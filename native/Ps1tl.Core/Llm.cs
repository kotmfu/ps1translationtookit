using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace Ps1tl;

public sealed class LlmException(string message) : Exception(message);

/// <summary>
/// One image + prompt -> structured JSON, via the Anthropic API (if ANTHROPIC_API_KEY is set) or the local
/// Claude Code CLI (`claude -p`, runs on the user's Claude subscription, no API account needed).
/// Output streams: set Live = (label, text) to watch requests as Claude writes them (text null = finished).
/// Requests run through RunParallel are labelled 'request k/n'.
/// </summary>
public static class Llm
{
    public static readonly Dictionary<string, string> Models = new()
    {
        ["opus"] = "claude-opus-5-5", ["sonnet"] = "claude-sonnet-5-5", ["haiku"] = "claude-haiku-4-5",
    };
    public const int Workers = 4;          // requests in flight at once
    public const int TimeoutSeconds = 900;
    public static Action<string, string?>? Live;
    static readonly AsyncLocal<string?> label = new();

    // the CLI otherwise loads the user's own Claude Code setup (hooks, plugins, CLAUDE.md, MCP) on every call:
    // twice as slow and thousands of extra tokens. Login is kept (unlike --bare, which drops subscription auth).
    static readonly string[] Lean = ["--setting-sources=", "--strict-mcp-config", "--disable-slash-commands"];

    static void Emit(string? text) => Live?.Invoke(label.Value ?? "request", text);

    public static string? ClaudePath()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var n in names)
            {
                try { var p = Path.Combine(dir.Trim('"'), n); if (File.Exists(p)) return p; }
                catch (ArgumentException) { }
            }
        return null;
    }

    /// <summary>"api", "cli" or null</summary>
    public static string? Backend() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? "api" : ClaudePath() != null ? "cli" : null;

    public static JsonElement Ask(byte[]? png, string prompt, string schema, string system = "", string model = "opus", string effort = "high") =>
        Backend() switch
        {
            "api" => Api(png, prompt, schema, system, model, effort),
            "cli" => Cli(png, prompt, schema, system, model, effort),
            _ => throw new LlmException("no Claude access: enter an API key, or install and log in to Claude Code (`claude`)"),
        };

    /// <summary>run tasks Workers at a time; onResult(index, result or LlmException) is called on the caller's
    /// thread as each finishes. After stop() no new tasks start; running ones finish.</summary>
    public static void RunParallel<T>(IList<Func<T>> tasks, Func<bool> stop, Action<int, object> onResult)
    {
        var done = new BlockingCollection<(int, object)>();
        int next = 0, running = 0;
        void Start()
        {
            if (next >= tasks.Count || stop()) return;
            int i = next++; running++;
            Task.Run(() =>
            {
                label.Value = $"request {i + 1}/{tasks.Count}";
                object r;
                try { r = tasks[i]()!; }
                catch (LlmException e) { r = e; }
                catch (Exception e) { r = new LlmException(e.Message); }
                finally { Emit(null); }
                done.Add((i, r));
            });
        }
        for (int k = 0; k < Workers; k++) Start();
        while (running > 0)
        {
            var (i, r) = done.Take();
            running--;
            onResult(i, r);
            Start();
        }
    }

    static JsonElement Api(byte[]? png, string prompt, string schema, string system, string model, string effort)
    {
        var client = new AnthropicClient();
        var schemaDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema)!;
        var p = new MessageCreateParams
        {
            Model = Models[model],
            MaxTokens = 16000,
            Messages = [new() { Role = Role.User, Content = png == null
                ? new List<ContentBlockParam> { new TextBlockParam { Text = prompt } }
                : new List<ContentBlockParam>
                {
                    new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng } },
                    new TextBlockParam { Text = prompt },
                } }],
            OutputConfig = model == "haiku"   // no effort setting on Haiku 4.5
                ? new OutputConfig { Format = new JsonOutputFormat { Schema = schemaDict } }
                : new OutputConfig { Effort = effort switch { "low" => Effort.Low, "medium" => Effort.Medium, "max" => Effort.Max, _ => Effort.High },
                                     Format = new JsonOutputFormat { Schema = schemaDict } },
        };
        if (system.Length > 0) p = p with { System = system };
        var text = new System.Text.StringBuilder();
        string? stop = null;
        try
        {
            Task.Run(async () =>
            {
                await foreach (var ev in client.Messages.CreateStreaming(p))
                {
                    if (ev.TryPickContentBlockDelta(out var d) && d.Delta.TryPickText(out var t)) { text.Append(t.Text); Emit(t.Text); }
                    else if (ev.TryPickDelta(out var md) && md.Delta.StopReason is { } sr) stop = sr.ToString();
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not LlmException) { throw new LlmException($"API: {e.Message}"); }
        if (stop != null && !stop.Contains("end_turn", StringComparison.OrdinalIgnoreCase) && !stop.Contains("EndTurn"))
            throw new LlmException($"stopped: {stop}");
        return JsonDocument.Parse(text.ToString()).RootElement.Clone();
    }

    static JsonElement Cli(byte[]? png, string prompt, string schema, string system, string model, string effort)
    {
        var content = new System.Text.Json.Nodes.JsonArray();
        if (png != null)
            content.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["type"] = "image", ["source"] = new System.Text.Json.Nodes.JsonObject
                { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(png) } });
        content.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = prompt });
        var msg = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "user",
            ["message"] = new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = content },
        }.ToJsonString();
        var psi = new ProcessStartInfo(ClaudePath()!)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,               // no console window per call
            WorkingDirectory = Path.GetTempPath(),                         // no project CLAUDE.md
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var x in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                                  "--include-partial-messages", "--json-schema", schema, "--no-session-persistence",
                                  "--tools", "", "--model", model, "--effort", effort }.Concat(Lean))
            psi.ArgumentList.Add(x);
        if (system.Length > 0) { psi.ArgumentList.Add("--system-prompt"); psi.ArgumentList.Add(system); }
        using var proc = Process.Start(psi) ?? throw new LlmException("cannot start claude CLI");
        var err = proc.StandardError.ReadToEndAsync();
        proc.StandardInput.Write(msg + "\n");
        proc.StandardInput.Close();
        bool killed = false;
        using var timer = new Timer(_ => { killed = true; try { proc.Kill(true); } catch { } }, null, TimeoutSeconds * 1000, Timeout.Infinite);
        JsonElement? result = null;
        bool thinking = false;
        var tail = new Queue<string>();
        string? line;
        while ((line = proc.StandardOutput.ReadLine()) != null)
        {
            JsonElement j;
            try { j = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { tail.Enqueue(line); if (tail.Count > 5) tail.Dequeue(); continue; }
            var type = j.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "result") result = j.Clone();
            else if (type == "stream_event" && j.GetProperty("event").TryGetProperty("delta", out var d) && d.TryGetProperty("type", out var dt))
            {
                switch (dt.GetString())
                {
                    case "thinking_delta" when !thinking: thinking = true; Emit("(thinking...)\n"); break;
                    case "text_delta": Emit(d.GetProperty("text").GetString()); break;
                    case "input_json_delta": Emit(d.GetProperty("partial_json").GetString()); break;
                }
            }
        }
        proc.WaitForExit();
        if (result is not { } r)
        {
            var why = (err.Result + string.Join("", tail)).Trim();
            throw new LlmException(killed ? "claude CLI timed out" : $"claude CLI failed: {why[Math.Max(0, why.Length - 300)..]}");
        }
        if ((r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True)
            || !r.TryGetProperty("structured_output", out var so) || so.ValueKind == JsonValueKind.Null)
            throw new LlmException($"claude CLI: {(r.TryGetProperty("result", out var res) ? res.ToString() : "no output")}");
        return so.Clone();
    }
}


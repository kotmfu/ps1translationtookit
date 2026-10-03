"""One image + prompt -> structured JSON, via the Anthropic API (if a key is set) or the local
Claude Code CLI (`claude -p`, runs on the user's Claude subscription, no API account needed).

Output is streamed: set LIVE = fn(label, text) to watch requests as Claude writes them (text None = request
finished). Requests run through parallel() are labelled 'request k/n'."""
import base64, json, os, shutil, subprocess, sys, tempfile, threading
from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED

MODELS = {'opus': 'claude-opus-5-5', 'sonnet': 'claude-sonnet-5-5', 'haiku': 'claude-haiku-4-5'}
WORKERS = 4      # requests in flight at once
TIMEOUT = 900    # seconds per request
LIVE = None      # fn(label, text chunk | None)

# no console window per `claude` call when running from a windowed app on Windows
NO_WINDOW = subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0
# the CLI otherwise loads the user's own Claude Code setup (hooks, plugins, CLAUDE.md, MCP) on every call:
# twice as slow and thousands of extra tokens. Login is kept (unlike --bare, which drops subscription auth).
LEAN = ['--setting-sources=', '--strict-mcp-config', '--disable-slash-commands']

_local = threading.local()


class LLMError(Exception):
    pass


def _emit(text):
    if LIVE: LIVE(getattr(_local, 'label', 'request'), text)


def parallel(tasks, stop=lambda: False, workers=WORKERS):
    """run callables `workers` at a time; yields (index, result) as each finishes, result is an LLMError on
    failure. After stop() no new tasks start; running ones finish."""
    tasks = list(tasks)

    def run(i, t):
        _local.label = f'request {i + 1}/{len(tasks)}'
        try: return t()
        finally: _emit(None)

    with ThreadPoolExecutor(workers) as ex:
        pending, queue = {}, iter(enumerate(tasks))

        def submit():
            for i, t in queue:
                if not stop(): pending[ex.submit(run, i, t)] = i
                return
        for _ in range(workers): submit()
        while pending:
            done, _ = wait(pending, return_when=FIRST_COMPLETED)
            for f in done:
                i = pending.pop(f)
                try: r = f.result()
                except LLMError as e: r = e
                yield i, r
                submit()


def backend():
    if os.environ.get('ANTHROPIC_API_KEY'): return 'api'
    if shutil.which('claude'): return 'cli'
    return None


def ask(png, prompt, schema, system='', model='opus', effort='high'):
    b = backend()
    if b == 'api': return _api(png, prompt, schema, system, model, effort)
    if b == 'cli': return _cli(png, prompt, schema, system, model, effort)
    raise LLMError('no Claude access: enter an API key, or install and log in to Claude Code (`claude`)')


def _content(png, prompt):
    return [{'type': 'image', 'source': {'type': 'base64', 'media_type': 'image/png', 'data': base64.standard_b64encode(png).decode()}},
            {'type': 'text', 'text': prompt}]


def _api(png, prompt, schema, system, model, effort):
    import anthropic
    client = anthropic.Anthropic()
    kw = dict(model=MODELS[model], max_tokens=16000, system=system or anthropic.NOT_GIVEN,
              messages=[{'role': 'user', 'content': _content(png, prompt)}])
    fmt = {'format': {'type': 'json_schema', 'schema': schema}}
    try:
        if model == 'haiku':   # no effort / server-side fallback on Haiku 4.5
            stream = client.messages.stream(output_config=fmt, **kw)
        else:
            stream = client.beta.messages.stream(betas=['server-side-fallback-2026-07-01'], fallbacks='default',
                                                 output_config={'effort': effort, **fmt}, **kw)
        with stream as st:
            for text in st.text_stream: _emit(text)
            resp = st.get_final_message()
    except anthropic.AuthenticationError:
        raise LLMError('invalid API key')
    except anthropic.APIConnectionError:
        raise LLMError('network problem, try again')
    except anthropic.APIStatusError as e:
        raise LLMError(f'API {e.status_code}: {e.message}')
    if resp.stop_reason != 'end_turn': raise LLMError(f'stopped: {resp.stop_reason}')
    return json.loads(next(b.text for b in resp.content if b.type == 'text'))


def _cli(png, prompt, schema, system, model, effort):
    msg = {'type': 'user', 'message': {'role': 'user', 'content': _content(png, prompt)}}
    cmd = ['claude', '-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose',
           '--include-partial-messages', '--json-schema', json.dumps(schema), '--no-session-persistence',
           '--tools', '', '--model', model, '--effort', effort] + LEAN
    if system: cmd += ['--system-prompt', system]
    try:
        p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                             encoding='utf-8', creationflags=NO_WINDOW, cwd=tempfile.gettempdir())  # no project CLAUDE.md
    except OSError as e:
        raise LLMError(f'cannot start claude CLI: {e}')
    killed = []
    timer = threading.Timer(TIMEOUT, lambda: (killed.append(1), p.kill())); timer.start()
    result, tail, thinking = None, [], False
    try:
        p.stdin.write(json.dumps(msg) + '\n'); p.stdin.close()
        for line in p.stdout:
            try: j = json.loads(line)
            except ValueError: tail = (tail + [line])[-5:]; continue
            t = j.get('type')
            if t == 'result': result = j
            elif t == 'stream_event':
                d = j['event'].get('delta') or {}
                if d.get('type') == 'thinking_delta' and not thinking: thinking = True; _emit('(thinking...)\n')
                elif d.get('type') == 'text_delta': _emit(d['text'])
                elif d.get('type') == 'input_json_delta': _emit(d['partial_json'])
        p.wait()
    finally:
        timer.cancel()
    if not result:
        raise LLMError('claude CLI timed out' if killed else f'claude CLI failed: {"".join(tail)[-300:].strip()}')
    if result.get('is_error') or result.get('structured_output') is None:
        raise LLMError(f'claude CLI: {str(result.get("result"))[:300]}')
    return result['structured_output']

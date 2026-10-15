namespace Ps1tl.Port;

/// <summary>
/// setjmp / longjmp, for games that switch between tasks with them (Gunparade March's scheduler: each task has
/// a jmp_buf; yielding is setjmp(own) then longjmp(next)). Recompiled code keeps guest calls on the C# stack, so
/// each guest task runs on its own host thread and a longjmp into another task hands control to that thread.
/// A longjmp within one task unwinds the C# stack (an exception) to the interpreted function that called
/// setjmp; the recompiler leaves functions that call setjmp to the interpreter so they can resume mid-function.
/// Only one thread ever runs guest code at a time.
/// jmp_buf: ra, sp, fp, s0-s7, gp (words 0-11), as Sony's libc lays it out.
/// </summary>
public sealed class LibC(Psx m)
{
    sealed class Task
    {
        public readonly SemaphoreSlim Go = new(0);
        public readonly Stack<int> Frames = new();   // interpreter frames running on this task's thread
        public uint Buf, Val;                        // what to resume with when woken
        public bool Exit;                            // woken to end: nothing can resume it any more
    }

    /// <summary>thrown to unwind to the interpreter frame that called setjmp (registers already restored)</summary>
    public sealed class LongJmp(int frame, uint pc) : Exception
    {
        public readonly int Frame = frame;
        public readonly uint Pc = pc;
    }

    sealed class TaskExit : Exception;

    Task cur = new();                       // the task whose thread runs guest code now
    [ThreadStatic] static Task? mine;       // the task this thread runs
    readonly Dictionary<uint, (Task Task, int Frame)> owner = new();
    int serial;
    public int Threads;

    public Dictionary<string, Action> Impl(IReadOnlyDictionary<string, uint> symbols) => new()
    {
        ["setjmp"] = SetJmp,
        ["longjmp"] = () => LongJmpTo(m.R[4], m.R[5] == 0 ? 1 : m.R[5]),
    };

    Task Mine => mine ??= cur;

    /// <summary>the interpreter entering / leaving a guest function on this thread; returns the frame id</summary>
    public int Enter() { int f = ++serial; Mine.Frames.Push(f); return f; }
    public void Leave() => Mine.Frames.Pop();

    void SetJmp()
    {
        uint b = m.R[4];
        m.Write32(b, m.R[31]); m.Write32(b + 4, m.R[29]); m.Write32(b + 8, m.R[30]);
        for (uint i = 0; i < 8; i++) m.Write32(b + 12 + i * 4, m.R[16 + i]);
        m.Write32(b + 44, m.R[28]);
        if (cur.Frames.Count == 0) Psx.Log($"setjmp({b:X8}) from recompiled code at {m.R[31]:X8}: it can't be resumed");
        Own(b, cur, cur.Frames.Count > 0 ? cur.Frames.Peek() : 0);
        m.R[2] = 0;
    }

    static bool Live((Task Task, int Frame) o) => o.Task.Frames.Contains(o.Frame);

    void Own(uint b, Task t, int frame)
    {
        owner.TryGetValue(b, out var old);
        owner[b] = (t, frame);
        // a parked thread none of whose buffers can be resumed any more: let it end
        if (old.Task is { } o && o != t && o != cur && !owner.Values.Any(v => v.Task == o && Live(v)))
        {
            o.Exit = true;
            o.Go.Release();
        }
    }

    void Load(uint b, uint val)
    {
        m.R[31] = m.Read32(b); m.R[29] = m.Read32(b + 4); m.R[30] = m.Read32(b + 8);
        for (uint i = 0; i < 8; i++) m.R[16 + i] = m.Read32(b + 12 + i * 4);
        m.R[28] = m.Read32(b + 44);
        m.R[2] = val;
    }

    void LongJmpTo(uint b, uint val)
    {
        if (owner.TryGetValue(b, out var o) && Live(o))
        {
            if (o.Task == cur) { Load(b, val); throw new LongJmp(o.Frame, m.R[31]); }
            SwitchTo(o.Task, b, val);
            return;
        }
        // a buffer whose setjmp frame is gone: a task set up to start fresh (its ra/sp point at the task's
        // entry and stack); give it a thread
        var t = new Task();
        Own(b, t, 0);
        Threads++;
        new Thread(() => Run(t), 16 << 20) { IsBackground = true, Name = $"guest task {b:X8}" }.Start();
        SwitchTo(t, b, val);
    }

    void SwitchTo(Task t, uint b, uint val)
    {
        var me = cur;
        (t.Buf, t.Val) = (b, val);
        cur = t;
        t.Go.Release();
        me.Go.Wait();
        if (me.Exit) throw new TaskExit();
        // someone longjmp'd into one of this thread's buffers
        Load(me.Buf, me.Val);
        throw new LongJmp(owner[me.Buf].Frame, m.R[31]);
    }

    void Run(Task t)
    {
        mine = t;
        t.Go.Wait();
        if (t.Exit) return;
        Load(t.Buf, t.Val);
        try
        {
            m.Interpret(m.R[31], 0xFFFFFFFF, 0);
            Psx.Log($"guest task {t.Buf:X8} returned");
            new SemaphoreSlim(0).Wait();
        }
        catch (TaskExit) { }
    }
}

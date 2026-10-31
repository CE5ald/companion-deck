using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CompanionDeck.Host.Actions;

/// <summary>
/// Records keyboard and mouse input system-wide with Windows low-level hooks, for as long as it
/// is told to and no longer.
///
/// This is a keylogger in all but intent, so it is built to be unable to become one by accident:
/// it runs only between an explicit Start and Stop (or Cancel, or the link dropping), is capped at
/// 60 seconds and 400 events, keeps everything in memory until the caller decides to save it, never
/// writes what it sees to the log, and ignores input the agent itself sent.
/// </summary>
public sealed class MacroRecorder : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_QUIT = 0x0012;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private readonly object _gate = new();
    private readonly List<MacroEvent> _events = new();
    private readonly Stopwatch _clock = new();
    // Held in fields: a hook procedure that is collected while installed crashes the process.
    private HookProc? _keyProc, _mouseProc;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _recording;
    private volatile bool _full;
    private int _lastMoveMs = -1000, _lastX = -1, _lastY = -1;

    /// <summary>For tests: accept events the agent injected itself, which are normally ignored.</summary>
    public bool AcceptInjected { get; set; }

    public bool Recording => _recording;
    public bool Full => _full;
    public int Count { get { lock (_gate) return _events.Count; } }
    public int ElapsedMs => _recording ? (int)_clock.ElapsedMilliseconds : 0;

    public bool Start(out string? error)
    {
        error = null;
        lock (_gate)
        {
            if (_recording) { error = "already recording"; return false; }
            _events.Clear();
            _full = false;
            _lastMoveMs = -1000; _lastX = _lastY = -1;
        }

        using var ready = new ManualResetEventSlim(false);
        string? failure = null;

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            IntPtr kb = IntPtr.Zero, ms = IntPtr.Zero;
            try
            {
                _keyProc = KeyHook;
                _mouseProc = MouseHook;
                var module = GetModuleHandleW(null);
                kb = SetWindowsHookExW(WH_KEYBOARD_LL, _keyProc, module, 0);
                ms = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, module, 0);
                if (kb == IntPtr.Zero || ms == IntPtr.Zero)
                {
                    failure = $"Windows refused the input hooks (error {Marshal.GetLastWin32Error()})";
                    return;
                }
                _clock.Restart();
                _recording = true;
                ready.Set();

                // Hooks are only called while their thread pumps messages.
                while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
            }
            finally
            {
                _recording = false;
                if (kb != IntPtr.Zero) UnhookWindowsHookEx(kb);
                if (ms != IntPtr.Zero) UnhookWindowsHookEx(ms);
                ready.Set();
            }
        }) { IsBackground = true, Name = "macro-recorder" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!ready.Wait(3000)) failure ??= "the recorder did not start";
        if (failure is not null || !_recording)
        {
            error = failure ?? "the recorder did not start";
            return false;
        }

        // Counts and times only, never what was pressed.
        Log.Info("macro recording started");
        return true;
    }

    /// <summary>Ends the recording and returns what it captured, or null if none was running.</summary>
    public List<MacroEvent>? Stop()
    {
        var thread = _thread;
        if (!_recording || thread is null) return null;

        PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
        _thread = null;

        lock (_gate)
        {
            var result = _events.ToList();
            _events.Clear();
            Log.Info($"macro recording stopped: {result.Count} event(s)");
            return result;
        }
    }

    /// <summary>Ends it and throws away what it captured.</summary>
    public void Cancel()
    {
        if (Stop() is not null) Log.Info("macro recording cancelled");
    }

    private void Add(MacroEvent e)
    {
        lock (_gate)
        {
            if (_events.Count >= MacroPlayer.MaxEvents || e.T > MacroPlayer.MaxMs) { _full = true; return; }
            _events.Add(e);
        }
    }

    private static (int x, int y) Normalise(int px, int py)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        return (Math.Clamp((int)((long)(px - vx) * 65535 / (vw - 1)), 0, 65535),
                Math.Clamp((int)((long)(py - vy) * 65535 / (vh - 1)), 0, 65535));
    }

    private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _recording)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is 0x100 or 0x104, up = msg is 0x101 or 0x105;
            if ((down || up) && (AcceptInjected || (k.flags & LLKHF_INJECTED) == 0) && KeyNames.IsRecordable((int)k.vkCode))
                Add(new MacroEvent { T = (int)_clock.ElapsedMilliseconds, K = down ? "kd" : "ku", V = (int)k.vkCode });
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _recording)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (AcceptInjected || (m.flags & LLMHF_INJECTED) == 0)
            {
                int t = (int)_clock.ElapsedMilliseconds;
                var (nx, ny) = Normalise(m.x, m.y);
                switch ((int)wParam)
                {
                    case 0x200:   // move: sampled, so a drag is a path rather than a thousand points
                        if (t - _lastMoveMs >= 30 && (Math.Abs(m.x - _lastX) + Math.Abs(m.y - _lastY)) >= 4)
                        {
                            _lastMoveMs = t; _lastX = m.x; _lastY = m.y;
                            Add(new MacroEvent { T = t, K = "mm", X = nx, Y = ny });
                        }
                        break;
                    case 0x201: Add(new MacroEvent { T = t, K = "md", V = 1, X = nx, Y = ny }); break;
                    case 0x202: Add(new MacroEvent { T = t, K = "mu", V = 1, X = nx, Y = ny }); break;
                    case 0x204: Add(new MacroEvent { T = t, K = "md", V = 2, X = nx, Y = ny }); break;
                    case 0x205: Add(new MacroEvent { T = t, K = "mu", V = 2, X = nx, Y = ny }); break;
                    case 0x207: Add(new MacroEvent { T = t, K = "md", V = 3, X = nx, Y = ny }); break;
                    case 0x208: Add(new MacroEvent { T = t, K = "mu", V = 3, X = nx, Y = ny }); break;
                    case 0x20A:   // wheel: the signed delta is in the high word
                    {
                        int delta = (short)((m.mouseData >> 16) & 0xFFFF);
                        if (delta != 0) Add(new MacroEvent { T = t, K = "mw", V = Math.Clamp(delta, -1200, 1200) });
                        break;
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose() => Cancel();
}

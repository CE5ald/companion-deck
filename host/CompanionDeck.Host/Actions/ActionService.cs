using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CompanionDeck.Host.Actions;

/// <summary>
/// The fixed set of things the tablet is allowed to do to Windows. This is an allow-list on
/// purpose: the tablet names an action, never a command line.
/// </summary>
public sealed class ActionService
{

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    /// <summary>
    /// All three arms must be declared even though only the keyboard one is used. SendInput
    /// validates the cbSize argument against the real INPUT size, and the union is sized by its
    /// largest member - MOUSEINPUT, at 32 bytes on x64, making INPUT 40.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData;
        public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg; public ushort wParamL; public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")] private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MapVkToVsc = 0;

    private const ushort VkLWin = 0x5B;
    private const ushort VkShift = 0x10;
    private const ushort VkCtrl = 0x11;
    private const ushort VkEsc = 0x1B;
    private const ushort VkAlt = 0x12;
    private const ushort VkTab = 0x09;
    private const ushort VkF4 = 0x73;
    private const ushort VkD = 0x44;
    private const ushort VkS = 0x53;
    private const ushort VkG = 0x47;
    private const ushort VkR = 0x52;
    private const ushort VkZ = 0x5A;
    /// <summary>Top-row digits. VK_1..VK_4 are contiguous from 0x31.</summary>
    private const ushort VkDigit1 = 0x31;

    /// <summary>Keys that live in the E0 range and must carry the extended flag.</summary>
    private static bool IsExtended(ushort vk) => vk is VkLWin or 0x5C or 0x2D or 0x2E
        or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28;

    private static INPUT Key(ushort vk, bool up)
    {
        uint flags = up ? KeyEventKeyUp : 0;
        if (IsExtended(vk)) flags |= KeyEventExtended;

        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    // The missing piece: without a real scan code the shell ignores the event.
                    wScan = (ushort)MapVirtualKeyW(vk, MapVkToVsc),
                    dwFlags = flags,
                },
            },
        };
    }

    /// <summary>
    /// Presses a chord. Key-downs and key-ups go in separate SendInput calls with a short
    /// gap: the shell debounces hotkeys, and a chord delivered as one atomic burst is
    /// sometimes discarded as a spurious repeat.
    /// </summary>
    private static bool Chord(params ushort[] keys)
    {
        var down = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) down[i] = Key(keys[i], false);

        var up = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) up[i] = Key(keys[keys.Length - 1 - i], true);

        int size = Marshal.SizeOf<INPUT>();
        uint sentDown = SendInput((uint)down.Length, down, size);
        Thread.Sleep(40);
        uint sentUp = SendInput((uint)up.Length, up, size);

        if (sentDown != down.Length || sentUp != up.Length)
        {
            // Most commonly UIPI: an elevated window has focus and refuses injected input.
            Log.Warn($"SendInput delivered {sentDown}/{down.Length} down, {sentUp}/{up.Length} up " +
                     $"(win32={Marshal.GetLastWin32Error()})");
            return false;
        }
        return true;
    }


    /// <summary>
    /// Taps a single key and releases it. Quick chat is a sequence of independent presses,
    /// not a chord: holding the first digit while pressing the second selects nothing.
    /// </summary>
    private static bool Tap(ushort vk)
    {
        int size = Marshal.SizeOf<INPUT>();
        var down = new[] { Key(vk, false) };
        var up = new[] { Key(vk, true) };

        if (SendInput(1, down, size) != 1) return false;
        Thread.Sleep(25);
        return SendInput(1, up, size) == 1;
    }


    /// <summary>
    /// Overlay and capture hotkeys. Named ids only - the chord for each one is fixed here,
    /// so the tablet cannot ask for an arbitrary key combination.
    /// </summary>
    public bool GameShortcut(string id) => id switch
    {
        // Xbox Game Bar, and its recorder. Win+Alt+R toggles, so one button both starts
        // and stops - the tablet labels it accordingly.
        "gamebar" => Chord(VkLWin, VkG),
        "record" => Chord(VkLWin, VkAlt, VkR),

        // NVIDIA's in-game overlay (GeForce Experience and the newer NVIDIA app both use
        // Alt+Z). The control panel is a separate desktop application, not an overlay.
        "nvoverlay" => Chord(VkAlt, VkZ),
        "nvpanel" => Shell("nvcplui.exe") || Shell("control.exe", "desk.cpl"),

        // Steam's overlay only exists inside a Steam-launched game; pressing it elsewhere
        // is harmless but does nothing, which is why the tablet marks it Steam-only.
        "steamoverlay" => Chord(VkShift, VkTab),

        _ => false,
    };


    public bool Execute(string action)
    {
        try
        {
            switch (action)
            {
                // Ctrl+Esc rather than the Win key. It is the original Start shortcut, it
                // is handled by the shell directly, and it avoids the Win key entirely.
                case "start": return Chord(VkCtrl, VkEsc);

                case "search": return Chord(VkLWin, VkS);
                case "desktop": return Chord(VkLWin, VkD);
                case "taskmgr": return Chord(VkCtrl, VkShift, VkEsc);

                // The snip overlay has a documented protocol handler. Launching it directly
                // is far more reliable than hoping Win+Shift+S is intercepted correctly.
                case "screenshot": return Shell("ms-screenclip:") || Chord(VkLWin, VkShift, VkS);

                case "explorer": return Shell("explorer.exe");
                case "calc": return Shell("calc.exe");
                case "settings": return Shell("ms-settings:");

                // Closes the focused window, exactly as the keyboard chord does. Destructive
                // enough to be gated behind a press-and-hold on the tablet: on the desktop
                // with nothing focused, the shell treats it as a shutdown prompt.
                case "closewindow": return Chord(VkAlt, VkF4);

                case "lock": return LockWorkStation();

                // Closes whatever transient shell surface is open (Start, search, snip).
                case "dismiss": return Chord(VkEsc);

                // Deliberate, destructive, and gated behind a press-and-hold on the tablet.
                case "sleep": return SetSuspendState(false, false, false);
                case "restart": return Shell("shutdown.exe", "/r /t 0");
                case "shutdown": return Shell("shutdown.exe", "/s /t 0");

                default:
                    Log.Warn($"rejected unknown action '{action}'");
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"action '{action}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Launches a catalogue entry. The target comes from our own scan, never the tablet.
    /// </summary>
    public bool Launch(string target)
    {
        if (LaunchAsUser(target)) return true;

        // Fallback: better to start it elevated than not at all. Warm launchers and plain
        // executables are fine this way; it is only a cold protocol handler that refuses.
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            Log.Debug($"launched '{target}' directly (shell hand-off unavailable)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"launch failed for '{target}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Asks the running desktop shell to open the target, so it starts as the signed-in user
    /// rather than as administrator. Passing the URI to explorer.exe on the command line does not
    /// work - Explorer accepts file paths and http(s), but ignores arbitrary protocol schemes.
    /// </summary>
    private static bool LaunchAsUser(string target)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;

            object? shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            try
            {
                shellType.InvokeMember("ShellExecute",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell,
                    new object?[] { target, "", "", "open", 1 });
                return true;
            }
            finally
            {
                if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"shell hand-off for '{target}': {ex.Message}");
            return false;
        }
    }

    private static bool Shell(string file, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (args is not null) psi.Arguments = args;
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"shell '{file}' failed: {ex.Message}");
            return false;
        }
    }
}

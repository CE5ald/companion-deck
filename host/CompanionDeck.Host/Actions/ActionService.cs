using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CompanionDeck.Host.Actions;

/// <summary>
/// The fixed set of things the tablet is allowed to do to Windows. This is an allow-list on
/// purpose: the tablet names an action, never a command line.
/// </summary>
public sealed class ActionService : IInputSink
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

    /// <summary>Keys that live in the E0 range and must carry the extended flag.</summary>
    private static bool IsExtended(ushort vk) => vk is VkLWin or 0x5C or 0x2D or 0x2E
        or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28;

    private static INPUT KeyInput(ushort vk, bool up)
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
        for (int i = 0; i < keys.Length; i++) down[i] = KeyInput(keys[i], false);

        var up = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) up[i] = KeyInput(keys[keys.Length - 1 - i], true);

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


    /// <summary>Presses a chord already parsed and validated by <see cref="KeyNames"/>.</summary>
    public bool Press(ushort[] keys) => keys.Length > 0 && Chord(keys);

    // ---- single events, for replaying a recorded macro. These are the IInputSink the player drives.

    private const uint MiMove = 0x0001, MiLeftDown = 0x0002, MiLeftUp = 0x0004,
                       MiRightDown = 0x0008, MiRightUp = 0x0010, MiMiddleDown = 0x0020,
                       MiMiddleUp = 0x0040, MiWheel = 0x0800, MiVirtualDesk = 0x4000,
                       MiAbsolute = 0x8000;
    private const uint InputMouse = 0;

    private static bool SendOne(INPUT input) =>
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;

    private static INPUT Mouse(uint flags, int dx, int dy, uint data) => new()
    {
        type = InputMouse,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
    };

    public bool Key(ushort vk, bool up) => SendOne(KeyInput(vk, up));

    /// <summary>Position in 0..65535 across the whole virtual desktop.</summary>
    public bool MouseMove(int x, int y) => SendOne(Mouse(MiMove | MiAbsolute | MiVirtualDesk, x, y, 0));

    public bool MouseButton(int button, bool up) => SendOne(Mouse(button switch
    {
        1 => up ? MiLeftUp : MiLeftDown,
        2 => up ? MiRightUp : MiRightDown,
        _ => up ? MiMiddleUp : MiMiddleDown,
    }, 0, 0, 0));

    public bool Wheel(int delta) => SendOne(Mouse(MiWheel, 0, 0, unchecked((uint)delta)));

    /// <summary>
    /// Overlay and capture hotkeys. Named ids only - the chord for each one is fixed here,
    /// so the tablet cannot ask for an arbitrary key combination.
    /// </summary>
    /// <summary>
    /// Which of the shortcuts below this PC can actually use. Offering an NVIDIA overlay on an AMD
    /// laptop, or Steam's on a PC with no Steam, is a button that can never work, so the tablet is
    /// told only about the ones whose software is present.
    /// </summary>
    public List<string> AvailableGameShortcuts(bool nvidiaGpu)
    {
        var list = new List<string> { "record", "gamebar" };   // Windows' own Game Bar, on every supported Windows

        if (nvidiaGpu)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            bool overlay = Directory.Exists(Path.Combine(pf, "NVIDIA Corporation", "NVIDIA app"))
                        || Directory.Exists(Path.Combine(pf, "NVIDIA Corporation", "NVIDIA GeForce Experience"))
                        || System.Diagnostics.Process.GetProcessesByName("NVIDIA Overlay").Length > 0;
            if (overlay) list.Add("nvoverlay");

            bool panel = File.Exists(Path.Combine(pf, "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe"))
                      || PackageInstalled("NVIDIACorp.NVIDIAControlPanel");
            if (panel) list.Add("nvpanel");
        }

        try
        {
            using var steam = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is string) list.Add("steamoverlay");
        }
        catch { /* not installed, or unreadable: not offered */ }

        return list;
    }

    private static bool PackageInstalled(string namePrefix)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(
                @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return k?.GetSubKeyNames().Any(n => n.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch { return false; }
    }

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

using System.Diagnostics;
using CompanionDeck.Host.Actions;
using CompanionDeck.Host.Server;
using CompanionDeck.Host.Transport;

namespace CompanionDeck.Host;

internal static class Program
{
    public const string Version = "1.0.0";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] list, uint count);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    /// <summary>
    /// The agent is a console program so that --selftest and the probes can print, but started by the
    /// logon task or a double-click that leaves a console window sitting on the desktop, and closing it
    /// ends the agent. When this process is the only one attached to its console, nobody is reading
    /// it, so it is hidden. Run from a terminal there is more than one process on the console and
    /// nothing is touched; --show-console keeps it for the rare case where it is wanted.
    /// </summary>
    private static void HideOwnConsole(string[] args)
    {
        if (args.Contains("--show-console") || args.Contains("--verbose") || args.Contains("--selftest")
            || args.Any(a => a.StartsWith("--probe", StringComparison.Ordinal)) || args.Contains("--set-output")) return;
        try
        {
            var window = GetConsoleWindow();
            if (window == IntPtr.Zero) return;
            if (GetConsoleProcessList(new uint[4], 4) == 1) ShowWindow(window, 0 /* SW_HIDE */);
        }
        catch { /* a console that cannot be hidden is only untidy */ }
    }

    private static async Task<int> Main(string[] args)
    {
        // Without this, a laptop set to 125 percent scaling reports the mouse in one coordinate
        // space to the input hooks and another to everything else, and a recorded macro clicks
        // somewhere else on replay. The agent draws nothing, so being exact costs nothing.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { /* an older Windows: no change */ }

        HideOwnConsole(args);
        Log.Level = args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Info;

        // Always keep a log on disk. When the agent runs from the Scheduled Task there is
        // no console attached, so without this there is no way to find out why a
        // capability did not come up.
        Log.OpenFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionDeck", "host.log"));

        if (args.Contains("--selftest"))
            return SelfTest();

        if (args.Contains("--probe-adb"))
            return await ProbeAdbAsync().ConfigureAwait(false);

        if (args.Contains("--probe-actions"))
            return ProbeActions();

        if (args.Contains("--probe-macro"))
            return ProbeMacro();

        if (args.Contains("--probe-notifications"))
            return ProbeNotifications(args);

        int fpsIdx = Array.IndexOf(args, "--probe-fps");
        if (fpsIdx >= 0)
            return ProbeFps(fpsIdx + 1 < args.Length ? args[fpsIdx + 1] : null);

        int outIdx = Array.IndexOf(args, "--set-output");
        if (outIdx >= 0)
            return SetOutput(outIdx + 1 < args.Length ? args[outIdx + 1] : null);

        return await RunAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        Log.Info($"===== Companion Deck host {Version} starting =====");
        Log.Info($"elevated: {Elevation.IsElevated}   user: {Environment.UserName}   " +
                 $"session: {Process.GetCurrentProcess().SessionId}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await using var server = new DeckServer();
        await server.InitializeAsync().ConfigureAwait(false);

        var adb = new AdbClient();
        var usb = new UsbTransport(adb, autoLaunchUi: !args.Contains("--no-launch"))
        {
            OnLink = link => server.ServeAsync(link, cts.Token),
        };

        // The fallback. It costs nothing while a cable is attached - it stops announcing
        // itself the moment USB is live - and it is the only route that needs nothing
        // enabled on the tablet, so it is on unless explicitly turned off.
        var wifi = new WifiTransport(() => usb.DeviceAttached)
        {
            OnLink = link => server.ServeAsync(link, cts.Token),
        };

        Log.Info("waiting for tablet (plug in the USB cable)…");

        // Either transport can be switched off. --no-usb is what lets the link test run
        // against a live agent: with a tablet on the cable the agent rightly refuses network
        // connections, so the test could never reach it.
        var transports = new List<Task>();
        if (!args.Contains("--no-usb")) transports.Add(usb.RunAsync(cts.Token));
        if (!args.Contains("--no-wifi")) transports.Add(wifi.RunAsync(cts.Token));
        if (transports.Count == 0) { Log.Error("both transports disabled; nothing to do"); return 1; }

        try { await Task.WhenAll(transports).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        Log.Info("host stopped");
        return 0;
    }

    /// <summary>
    /// Probes every hardware capability and prints what this machine can actually do. Nothing
    /// about this project works the same on two different machines - vendor interfaces, GPU
    /// vendor, elevation and installed extras all move the answer - so "what will I get?" has to
    /// be answerable without a tablet attached.
    /// </summary>
    private static int SelfTest()
    {
        static void Row(string name, bool ok, string? detail = null) =>
            Console.WriteLine($"  [{(ok ? "yes" : " no")}] {name,-22} {detail ?? ""}".TrimEnd());

        // Every probe logs what it found, which is the right behaviour for the agent and
        // useless noise interleaved through a report. The file sink keeps it all.
        Log.ToConsole = false;

        Console.WriteLine();
        Console.WriteLine($"  Companion Deck {Version} - capability self-test");
        Console.WriteLine($"  {Environment.MachineName}  /  Windows {Environment.OSVersion.Version}  /  " +
                          $"{(Elevation.IsElevated ? "elevated" : "not elevated")}");
        Console.WriteLine();

        var counters = new Telemetry.SystemCounters();
        counters.Initialize();
        Row("CPU + memory", true, $"{counters.TotalRamMb} MB RAM");
        Row("disk activity", counters.ReadDiskPercent() is not null);
        var (battery, _) = counters.ReadBattery();
        Row("battery", battery is not null, battery is null ? "desktop, or no battery reported" : $"{battery}%");

        // The same switch the agent honours: skip the vendor providers to see what a PC without them gets.
        bool generic = Environment.GetEnvironmentVariable("COMPANIONDECK_GENERIC") == "1";
        if (generic) Console.WriteLine("  (COMPANIONDECK_GENERIC=1: vendor providers skipped)" + Environment.NewLine);

        using var nvml = new Telemetry.Nvml();
        bool haveNvml = !generic && nvml.TryInitialize();
        Row("GPU via NVML", haveNvml, haveNvml ? nvml.DeviceName : "no NVIDIA driver");

        using var gpu = new Telemetry.GpuCounters();
        bool haveCounters = gpu.TryInitialize();
        if (haveCounters)
        {
            Thread.Sleep(1100);   // rate counters need two collections
            var s = gpu.Sample();
            Row("GPU via Windows", true,
                $"util {s?.UtilPercent?.ToString() ?? "?"}%, " +
                $"vram {s?.VramUsedMb?.ToString() ?? "?"}/{s?.VramTotalMb?.ToString() ?? "?"} MB");
        }
        else Row("GPU via Windows", false, "counters not published");

        var brightness = new Telemetry.Brightness();
        brightness.Probe();
        Row("brightness", brightness.CanWrite,
            brightness.CanRead && !brightness.CanWrite ? "readable, needs elevation to set"
            : brightness.CanRead ? null : "no internal panel");

        var zone = new Telemetry.ThermalZone();
        zone.Probe();
        var power = new Telemetry.PowerPlans();
        power.Probe();

        // Whether a vendor interface answered. Kept as plain locals rather than read
        // inline below, so the vendor block is one separable piece instead of a condition
        // threaded through every row.
        bool vendorTemp = false, vendorFans = false, vendorThermal = false;

        var ec = new Telemetry.LenovoEc();
        if (!generic) ec.Probe();
        vendorTemp = ec.HasCpuTemp; vendorFans = ec.HasFans; vendorThermal = ec.HasThermalMode;
        Row("vendor EC", ec.Available,
            ec.Available ? $"fans={ec.HasFans}, cpuTemp={ec.HasCpuTemp}, thermal={ec.HasThermalMode}"
            : ec.NeedsElevation ? "present, needs the elevated agent" : "not a supported Lenovo");

        using var sensors = new Telemetry.HardwareSensors();
        sensors.Probe(gpuToo: !haveNvml);
        var hw = sensors.Available ? sensors.Read() : null;

        Row("CPU temperature", vendorTemp || zone.Available || sensors.HasCpuTemp,
            vendorTemp ? "vendor sensor"
            : sensors.HasCpuTemp ? $"sensor chip, {hw?.CpuTempC?.ToString("F0")} C"
            : zone.Available ? $"ACPI zone, {zone.Read()} C"
            // Several routes refuse an ordinary caller, so "absent" and "not allowed to look" are the same
            // silence. Saying which it is matters: one is a hardware fact, the other is a thing the user can fix.
            : !Elevation.IsElevated ? "needs the elevated agent"
            : sensors.Problem.Length > 0 ? sensors.Problem : "no readable sensor on this machine");

        Row("fan speeds", vendorFans || sensors.HasFans,
            vendorFans ? "vendor interface"
            : sensors.HasFans ? string.Join(", ", hw!.Fans.Select(f => $"{f.Name} {f.Rpm}"))
            : ec.NeedsElevation || !Elevation.IsElevated ? "needs the elevated agent"
            : sensors.Problem.Length > 0 ? sensors.Problem : "this PC reports no fan");

        Row("GPU temperature", haveNvml || sensors.HasGpuTemp,
            haveNvml ? "NVIDIA driver" : sensors.HasGpuTemp ? "sensor chip" : "not published by this GPU's driver");

        Row("performance modes", vendorThermal || power.Available,
            vendorThermal ? "vendor thermal profiles" : power.Mechanism ?? "none available");

        using var fps = new Telemetry.FpsProvider();
        Row("FPS", fps.Available, fps.Available ? "Windows event tracing, no extra software" : fps.Detail);

        var audio = new Audio.AudioService();
        audio.Initialize();
        Row("audio mixer", true, audio.CanSwitchDevices ? "with device switching" : "volume only");

        var cast = new Audio.MicCastService();
        Row("mic cast", cast.Available, cast.Available ? null : "needs VB-Audio Virtual Cable");

        Console.WriteLine();
        Console.WriteLine("  Anything marked no is reported to the tablet as unavailable, and its");
        Console.WriteLine("  control is not drawn. See the README for what each one needs.");
        Console.WriteLine();
        Console.WriteLine("  Logic checks (these must all pass on every machine):");
        int failed = UnitChecks.Run((name, ok, detail) => Row(name, ok, detail));
        Console.WriteLine();

        cast.Dispose();
        audio.Dispose();
        counters.Dispose();
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Measures a process's frame rate for a few seconds through the same code the agent uses.
    /// Give it a process name or pid; with no argument it uses the foreground window.
    /// Needs elevation, because ETW does.
    /// </summary>
    private static int ProbeFps(string? target)
    {
        using var fps = new CompanionDeck.Host.Telemetry.FpsProvider();
        Log.Info($"elevated: {Elevation.IsElevated}");
        if (!fps.Available) { Log.Error($"FPS unavailable: {fps.Detail}"); return 1; }

        int pid;
        if (int.TryParse(target, out var explicitPid)) pid = explicitPid;
        else if (!string.IsNullOrWhiteSpace(target))
        {
            var match = Process.GetProcessesByName(target).FirstOrDefault();
            if (match is null) { Log.Error($"no process named '{target}'"); return 2; }
            pid = match.Id;
        }
        else
        {
            var h = GetForegroundWindow();
            GetWindowThreadProcessId(h, out pid);
        }

        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch { Log.Error($"pid {pid} is not running"); return 2; }

        Log.Info($"tracking {name} (pid {pid}) for 8 seconds - it must be rendering to produce frames");
        for (int i = 0; i < 8; i++)
        {
            fps.Track(pid);
            Thread.Sleep(1000);
            var r = fps.Sample();
            Log.Info(r is null
                ? $"  {i + 1}s: {fps.State}{(fps.Detail.Length > 0 ? " - " + fps.Detail : "")}"
                : $"  {i + 1}s: {r.Fps:F1} fps, 1% low {r.OnePercentLow:F1}");
        }

        fps.Stop();
        return 0;
    }

    /// <summary>
    /// Lists output endpoints, or switches to the first whose name contains the given text.
    /// Uses the same code path as the tablet, so it doubles as a way to check that path
    /// independently of the UI.
    /// </summary>
    private static int SetOutput(string? match)
    {
        using var audio = new CompanionDeck.Host.Audio.AudioService();
        if (!audio.Initialize()) { Log.Error("Core Audio unavailable"); return 1; }

        // Endpoint enumeration now runs in the background, so the first snapshot can come
        // back with an empty list. Wait for it rather than reporting "no such device".
        var state = audio.Snapshot();
        for (int i = 0; i < 40 && state.Outputs.Count == 0; i++)
        {
            Thread.Sleep(250);
            state = audio.Snapshot();
        }

        if (string.IsNullOrWhiteSpace(match))
        {
            Log.Info("Output endpoints:");
            foreach (var d in state.Outputs)
                Log.Info($"  {(d.IsDefault ? "*" : " ")} {d.Name}");

            // Repeat snapshots so the warm (cached) cost is visible separately from the
            // cold one; the publish loop only ever pays the warm cost.
            Log.Info("timing three further snapshots:");
            for (int i = 0; i < 3; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                audio.Snapshot();
                Log.Info($"  snapshot {i + 1}: {sw.ElapsedMilliseconds}ms");
            }
            return 0;
        }

        var target = state.Outputs.FirstOrDefault(
            d => d.Name.Contains(match, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            Log.Error($"No output matching '{match}'. Run --set-output with no argument to list them.");
            return 2;
        }

        bool ok = audio.SetDefaultEndpoint(target.Id);
        Log.Info($"switch to '{target.Name}' -> {(ok ? "ok" : "FAILED")}");

        // Confirm from a fresh read rather than trusting the call's return value.
        var after = audio.Snapshot().Outputs.FirstOrDefault(d => d.IsDefault);
        Log.Info($"default is now: {after?.Name ?? "unknown"}");
        return ok ? 0 : 3;
    }

    /// <summary>
    /// Fires the shell-hotkey actions and reports which window Windows actually brought to
    /// the front. Injected input fails silently, so the only honest way to know whether an
    /// action works is to observe its effect. Each probe dismisses itself with Escape.
    /// </summary>
    private static int ProbeActions()
    {
        var actions = new ActionService();

        Log.Info("Probing shell actions. The desktop will flicker briefly.");
        Log.Info($"starting foreground: {Foreground()}");

        foreach (var (name, expect) in new[]
                 {
                     ("start", "StartMenuExperienceHost / SearchHost"),
                     ("search", "SearchHost / StartMenuExperienceHost"),
                     ("screenshot", "ScreenClippingHost / SnippingTool"),
                 })
        {
            Thread.Sleep(700);
            bool sent = actions.Execute(name);
            Thread.Sleep(1400);
            var fg = Foreground();
            Log.Info($"  {name,-11} sent={sent,-5} foreground now = {fg}   (expected {expect})");

            // Dismiss whatever appeared so the desktop is left as we found it.
            actions.Execute("dismiss");
            Thread.Sleep(500);
        }

        Log.Info($"final foreground: {Foreground()}");
        return 0;
    }

    /// <summary>
    /// Shows whether the Windows notification database can be read here, and what it looks like,
    /// without printing a single notification: counts and column names only.
    /// </summary>
    private static int ProbeNotifications(string[] args)
    {
        Log.ToConsole = false;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications", "wpndatabase.db");
        Console.WriteLine();
        Console.WriteLine($"  database: {path}");
        Console.WriteLine($"  exists:   {File.Exists(path)}   size: {(File.Exists(path) ? new FileInfo(path).Length : 0)} bytes");
        foreach (var side in new[] { "-wal", "-shm" })
            Console.WriteLine($"  {side}:     {(File.Exists(path + side) ? new FileInfo(path + side).Length + " bytes" : "absent")}");

        using var db = Notify.MiniSqlite.OpenReadOnly(path, out var err);
        if (db is null) { Console.WriteLine($"  [ no] open: {err}"); return 1; }
        Console.WriteLine("  [yes] opened read-only");

        var tables = new List<string>();
        db.Query("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name", r => tables.Add(r.Text(0) ?? ""));
        Console.WriteLine($"  tables:   {string.Join(", ", tables)}");

        foreach (var t in new[] { "Notification", "NotificationHandler" })
        {
            var cols = new List<string>();
            db.Query($"PRAGMA table_info({t})", r => cols.Add(r.Text(1) ?? ""));
            Console.WriteLine($"  {t} columns: {string.Join(", ", cols)}");
        }

        long count = -1, newest = 0;
        db.Query("SELECT COUNT(*), COALESCE(MAX(ArrivalTime),0) FROM Notification WHERE Type='toast'", r => { count = r.Int(0); newest = r.Int(1); });
        Console.WriteLine($"  toast rows: {count}   newest arrived: {(newest > 0 ? DateTime.FromFileTime(newest).ToString("yyyy-MM-dd HH:mm:ss") : "never")}");
        if (db.Error is not null) Console.WriteLine($"  [ no] query: {db.Error}");

        // End to end, with a toast of our own so nobody else's is ever shown: post it the way an
        // application would, and see whether it comes back out of the database as readable text.
        if (args.Contains("--send-test-toast"))
        {
            var mirror = new Notify.NotificationMirror();
            mirror.Probe();
            Console.WriteLine($"  [{(mirror.Available ? "yes" : " no")}] reader accepts this Windows   {mirror.Problem}".TrimEnd());

            const string aumid = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";
            try
            {
                var xml = new Windows.Data.Xml.Dom.XmlDocument();
                xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>Companion Deck test</text>" +
                            "<text>If this reaches the tablet, the mirror works.</text></binding></visual></toast>");
                var toast = new Windows.UI.Notifications.ToastNotification(xml) { Tag = "cdtest", Group = "cdtest" };
                Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(aumid).Show(toast);
                Thread.Sleep(3000);

                var mine = mirror.Read(new HashSet<long>()).FirstOrDefault(i => i.Title == "Companion Deck test");
                Console.WriteLine($"  [{(mine is not null ? "yes" : " no")}] our toast came back out of the database" +
                                  (mine is null ? "" : $"   app '{mine.App}', body '{mine.Body}'"));

                Windows.UI.Notifications.ToastNotificationManager.History.Remove("cdtest", "cdtest", aumid);
                Thread.Sleep(2000);
                bool gone = !mirror.Read(new HashSet<long>()).Any(i => i.Title == "Companion Deck test");
                Console.WriteLine($"  [{(gone ? "yes" : " no")}] removing it removes it from what is read");
            }
            catch (Exception ex) { Console.WriteLine($"  [ no] could not post a test toast: {ex.Message}"); }
        }

        Console.WriteLine();
        return db.Error is null ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>
    /// Drives the real Windows hooks end to end without anyone touching the machine: records the agent's
    /// own harmless input (the F13 key and a small cursor move), checks what was captured, replays it, and
    /// puts the cursor back. The hooks normally ignore input the agent injected; this turns that off.
    /// </summary>
    private static int ProbeMacro()
    {
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "yes" : " no")}] {name,-42} {detail}".TrimEnd());
            if (!ok) failures++;
        }

        Log.ToConsole = false;
        Console.WriteLine();
        var actions = new ActionService();
        using var rec = new Actions.MacroRecorder { AcceptInjected = true };

        GetCursorPos(out var origin);
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int tx = Math.Clamp(origin.X + 40, vx, vx + vw - 2), ty = origin.Y;
        int nx = (int)((long)(tx - vx) * 65535 / (vw - 1)), ny = (int)((long)(ty - vy) * 65535 / (vh - 1));

        bool started = rec.Start(out var err);
        Check("hooks install", started, err ?? "");
        if (!started) return 1;
        Check("recording flag is on", rec.Recording);

        Thread.Sleep(250);
        actions.Key(0x7C, false); Thread.Sleep(40); actions.Key(0x7C, true);   // F13: nothing listens to it
        Thread.Sleep(80);
        actions.MouseMove(nx, ny);
        Thread.Sleep(250);

        var events = rec.Stop() ?? new();
        Check("recording stops", !rec.Recording);
        Check("captured the key press and release", events.Any(e => e.K == "kd" && e.V == 0x7C) && events.Any(e => e.K == "ku" && e.V == 0x7C),
              $"{events.Count} event(s)");

        var move = events.LastOrDefault(e => e.K == "mm");
        Check("captured the mouse move", move is not null);
        if (move is not null)
        {
            // Within a few thousandths of the full range: the pointer rounds to whole pixels.
            Check("normalised position matches the cursor", Math.Abs(move.X - nx) < 120 && Math.Abs(move.Y - ny) < 120,
                  $"got {move.X},{move.Y} wanted {nx},{ny}");
        }
        Check("timestamps rise and fit the limits", events.Zip(events.Skip(1), (a, b) => b.T >= a.T).All(x => x) && events.All(e => e.T < 5000));
        Check("what was captured passes the rules", MacroPlayer.Sanitise(events, out var why), why ?? "");

        // Put the cursor back, then let the macro put it where it was recorded.
        SetCursorPos(origin.X, origin.Y);
        Thread.Sleep(100);
        var replayErr = MacroPlayer.PlayAsync(events, actions, CancellationToken.None).GetAwaiter().GetResult();
        Check("replay is accepted by Windows", replayErr is null, replayErr ?? "");
        Thread.Sleep(100);
        GetCursorPos(out var after);
        Check("replay moved the cursor back to the recording", Math.Abs(after.X - tx) <= 2 && Math.Abs(after.Y - ty) <= 2,
              $"at {after.X},{after.Y}, recorded {tx},{ty}");
        SetCursorPos(origin.X, origin.Y);

        // And the hooks must be gone: a second recording starts and ends cleanly, and sees nothing.
        bool again = rec.Start(out err);
        Thread.Sleep(150);
        var none = rec.Stop();
        Check("a second recording starts and ends cleanly", again && none is not null);
        Console.WriteLine();
        return failures == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

    private static string Foreground()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "(none)";
        GetWindowThreadProcessId(h, out int pid);
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch { return $"(pid {pid})"; }
    }

    /// <summary>
    /// Verifies the USB path end to end: locate adb, talk the wire protocol, watch for the
    /// tablet, and confirm we can run a shell command on it.
    /// </summary>
    private static async Task<int> ProbeAdbAsync()
    {
        var path = AdbClient.LocateAdb();
        Log.Info($"adb binary: {path ?? "NOT FOUND"}");
        if (path is null) return 1;

        var adb = new AdbClient(path);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await adb.EnsureServerAsync(cts.Token).ConfigureAwait(false);
        Log.Info("adb server reachable");

        string? serial = null;
        var seen = new TaskCompletionSource();

        var tracker = Task.Run(async () =>
        {
            try
            {
                await adb.TrackDevicesAsync(devices =>
                {
                    Log.Info($"track-devices: [{string.Join(", ", devices.Select(d => $"{d.Serial}={d.State}"))}]");
                    var ready = devices.FirstOrDefault(d => d.IsReady);
                    if (ready is not null && serial is null)
                    {
                        serial = ready.Serial;
                        seen.TrySetResult();
                    }
                    return Task.CompletedTask;
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error("track-devices", ex); seen.TrySetException(ex); }
        });

        var completed = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(8), cts.Token))
                                  .ConfigureAwait(false);
        if (completed != seen.Task || serial is null)
        {
            Log.Warn("No ready device appeared within 8s.");
            return 2;
        }

        Log.Info($"device ready: {serial}");
        var model = (await adb.ShellAsync(serial, "getprop ro.product.model", cts.Token).ConfigureAwait(false)).Trim();
        Log.Info($"shell works, model = {model}");

        cts.Cancel();
        try { await tracker.ConfigureAwait(false); } catch { }
        return 0;
    }
}

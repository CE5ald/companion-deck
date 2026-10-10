using System.Diagnostics;
using CompanionDeck.Host.Actions;
using CompanionDeck.Host.Server;
using CompanionDeck.Host.Transport;

namespace CompanionDeck.Host;

internal static class Program
{
    public const string Version = "1.0.0";

    private static async Task<int> Main(string[] args)
    {
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

        using var nvml = new Telemetry.Nvml();
        bool haveNvml = nvml.TryInitialize();
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


        Row("CPU temperature", vendorTemp || zone.Available,
            vendorTemp ? "vendor sensor"
            : zone.Available ? $"ACPI zone, {zone.Read()} C"
            // Both routes are WMI classes that refuse an ordinary caller, so "absent" and
            // "not allowed to look" are the same silence. Saying which it is matters:
            // one is a hardware fact, the other is a thing the user can fix.
            : Elevation.IsElevated ? "no readable sensor on this machine"
            : "needs the elevated agent");

        Row("fan speeds", vendorFans, vendorFans ? null : "needs a vendor interface");

        Row("performance modes", vendorThermal || power.Available,
            vendorThermal ? "vendor thermal profiles" : power.Mechanism ?? "none available");

        using var fps = new Telemetry.FpsProvider();
        Row("FPS", fps.Available, fps.ExePath ?? "install PresentMon to enable");

        var audio = new Audio.AudioService();
        audio.Initialize();
        Row("audio mixer", true, audio.CanSwitchDevices ? "with device switching" : "volume only");

        var cast = new Audio.MicCastService();
        Row("mic cast", cast.Available, cast.Available ? null : "needs VB-Audio Virtual Cable");

        Console.WriteLine();
        Console.WriteLine("  Anything marked no is reported to the tablet as unavailable, and its");
        Console.WriteLine("  control is not drawn. See the README for what each one needs.");
        Console.WriteLine();

        cast.Dispose();
        audio.Dispose();
        counters.Dispose();
        return 0;
    }

    /// <summary>
    /// Tracks a process with PresentMon for a few seconds and reports the frame rate.
    /// Give it a process name or pid; with no argument it uses the foreground window.
    /// Needs elevation, because ETW does.
    /// </summary>
    private static int ProbeFps(string? target)
    {
        using var fps = new CompanionDeck.Host.Telemetry.FpsProvider();
        if (!fps.Available) { Log.Error("PresentMon not found"); return 1; }

        Log.Info($"PresentMon: {fps.ExePath}");
        Log.Info($"elevated: {Elevation.IsElevated}" + (Elevation.IsElevated ? "" : "  <-- ETW needs admin; expect this to fail"));

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
        fps.Track(pid);

        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(1000);
            var r = fps.Sample();
            Log.Info(r is null
                ? $"  {i + 1}s: no frames yet"
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

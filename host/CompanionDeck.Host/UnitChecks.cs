using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompanionDeck.Host.Actions;
using CompanionDeck.Host.Audio;
using CompanionDeck.Host.Clock;
using CompanionDeck.Host.Monitoring;
using CompanionDeck.Host.Notify;
using CompanionDeck.Host.Protocol;
using CompanionDeck.Host.Server;
using CompanionDeck.Host.Tiles;
using CompanionDeck.Host.Transport;

namespace CompanionDeck.Host;

/// <summary>
/// Checks for the logic that has no hardware behind it: the key vocabulary, tile validation,
/// calendar parsing and the timer clock. They run as part of the self-test, so a machine that
/// builds the agent also proves these hold on it.
/// </summary>
internal static class UnitChecks
{
    public static int Run(Action<string, bool, string?> row)
    {
        int failed = 0;
        void Check(string name, Func<string?> body)
        {
            string? problem;
            try { problem = body(); }
            catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; }
            row(name, problem is null, problem);
            if (problem is not null) failed++;
        }

        string dir = Path.Combine(Path.GetTempPath(), "companiondeck-checks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        Check("key chords", () =>
        {
            if (!KeyNames.TryParse("m+ctrl+shift", out var k) || !k.SequenceEqual(new ushort[] { 0x11, 0x10, 0x4D }))
                return "modifiers must come first";
            if (!KeyNames.TryParse("f13", out var f) || f[0] != 0x7C) return "f13";
            if (!KeyNames.TryParse("win+alt+prtsc", out _)) return "win+alt+prtsc";
            foreach (var bad in new[] { "", "ctrl+", "ctrl+ctrl", "banana", "f25", "ctrl+shift+alt+win+a+b", "ctrl+§" })
                if (KeyNames.TryParse(bad, out _)) return $"'{bad}' should be refused";
            return null;
        });

        Check("default browser parsing", () =>
        {
            string? Exe(string c) => Media.DefaultBrowser.ExeFromCommand(c);
            if (Exe("\"C:\\Program Files\\BraveSoftware\\Brave-Browser\\Application\\brave.exe\" --single-argument %1")
                != @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe") return "quoted path with arguments";
            if (Exe("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"")
                != @"C:\Program Files\Mozilla Firefox\firefox.exe") return "firefox style";
            if (Exe(@"C:\Windows\system32\notepad.exe %1") != @"C:\Windows\system32\notepad.exe") return "unquoted path";
            Environment.SetEnvironmentVariable("CD_CHECK_ROOT", @"D:\Apps");
            if (Exe("\"%CD_CHECK_ROOT%\\x\\y.exe\" %1") != @"D:\Apps\x\y.exe") return "environment variables";
            foreach (var bad in new[] { "", "   ", "\"unterminated", @"C:\a\b.bat x", "just words" })
                if (Exe(bad) is not null) return $"'{bad}' should give nothing";
            return null;
        });

        Check("a browser other than the default resolves by its registration", () =>
        {
            // Whatever is installed here, each registered handler must lead to its own program and name,
            // which is exactly the step that follows when the user changes their default.
            int found = 0;
            foreach (var (progId, exe, name) in new[]
                     { ("BraveHTML", "brave.exe", "Brave"), ("ChromeHTML", "chrome.exe", "Chrome"),
                       ("MSEdgeHTM", "msedge.exe", "Edge"), ("FirefoxURL", "firefox.exe", "Firefox") })
            {
                // Firefox registers with a per-install suffix; match the stable prefix.
                var info = Media.DefaultBrowser.FromProgId(progId);
                if (info is null) continue;
                found++;
                if (!info.Path.EndsWith(exe, StringComparison.OrdinalIgnoreCase)) return $"{progId} led to {info.Path}";
                if (!info.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)) return $"{progId} was named '{info.DisplayName}'";
            }
            // A PC with none of these (a bare server) simply has nothing to resolve; that is not a failure.
            return Media.DefaultBrowser.FromProgId("NoSuchBrowserHTML") is null ? null : "an unknown handler resolved to something";
        });

        Check("a game is recognised by its install folder", () =>
        {
            bool In(string p, string f) => Context.ProcessPaths.IsInside(p, f);
            if (!In(@"E:\steam\steamapps\common\Light Dude\LightDude.exe", @"E:\steam\steamapps\common\Light Dude")) return "inside";
            if (!In(@"e:/steam/steamapps/common/Light Dude/x.exe", @"E:\steam\steamapps\common\Light Dude\")) return "case, slashes and a trailing slash";
            if (In(@"E:\steam\steamapps\common\Light Dude 2\x.exe", @"E:\steam\steamapps\common\Light Dude")) return "a sibling folder with the same prefix is not inside";
            if (In(@"E:\steam\steam.exe", @"E:\steam\steamapps\common\Light Dude")) return "outside";
            if (In("", @"E:\x") || In(@"E:\x\y.exe", "")) return "empty input";
            return Context.ProcessPaths.PathOf(Environment.ProcessId) is { } me && me.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? null : "could not read this process's own path";
        });

        Check("calendar links: only safe ones are accepted", () =>
        {
            bool Ok(string s) => CalendarSubscriptions.TryNormalise(s, out _, out _);
            if (!Ok("https://calendar.google.com/calendar/ical/x%40gmail.com/private-abc/basic.ics")) return "a normal https link";
            if (!CalendarSubscriptions.TryNormalise("webcal://p01-caldav.icloud.com/published/2/abc", out var u, out _) || u.Scheme != "https") return "webcal becomes https";
            foreach (var bad in new[] { "", "   ", "http://example.com/a.ics", "ftp://example.com/a.ics", "file:///C:/secret.ics", "javascript:alert(1)", "not a link", "https://" })
                if (Ok(bad)) return $"'{bad}' should be refused";
            foreach (var ip in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.9", "172.31.255.1", "192.168.18.1", "169.254.1.1", "100.64.0.1", "0.0.0.0", "::1", "fe80::1", "fd00::1", "::ffff:192.168.1.1" })
                if (!CalendarSubscriptions.IsPrivate(System.Net.IPAddress.Parse(ip))) return $"{ip} should count as inside the network";
            foreach (var ip in new[] { "8.8.8.8", "142.250.1.1", "172.32.0.1", "192.169.0.1", "2607:f8b0:4004::1" })
                if (CalendarSubscriptions.IsPrivate(System.Net.IPAddress.Parse(ip))) return $"{ip} should count as public";
            return null;
        });

        Check("a subscribed calendar is never shown with its address", () =>
        {
            string dirCal = Path.Combine(dir, "cal"), file = Path.Combine(dir, "calendars-check.json");
            File.WriteAllText(file, "[{\"Id\":\"abc12345\",\"Url\":\"https://example.com/private-SECRET-token/basic.ics\",\"Name\":\"Work\",\"LastOkMs\":1,\"Error\":null}]");
            var subs = new CalendarSubscriptions(dirCal, file);
            subs.Load();
            var info = subs.Info();
            if (info.Count != 1 || info[0].Name != "Work" || info[0].Host != "example.com") return "the list is wrong";
            string json = System.Text.Json.JsonSerializer.Serialize(info);
            if (json.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || json.Contains("private-", StringComparison.OrdinalIgnoreCase)) return "the address leaked into what the tablet receives";
            if (!subs.Remove("abc12345") || subs.Info().Count != 0) return "removal";
            return subs.Remove("abc12345") ? "removing twice should say so" : null;
        });

        Check("suggestions follow your habits", () =>
        {
            var svc = new Launcher.LauncherService(new Assets.AssetCache(), Path.Combine(dir, "launcher-check.json"));
            Launcher.LauncherService.Entry E(string id, string kind) =>
                new() { Id = id, Name = id, Kind = kind, Source = "shortcut", Target = "x" };
            svc.SetEntries(new[] { E("alpha", "app"), E("bravo", "game"), E("charlie", "app"), E("delta", "game") });

            string Order() => string.Join(",", svc.Snapshot().Items.Select(i => i.Id));
            if (Order() != "bravo,delta,alpha,charlie") return "with no history: games, then alphabetical, got " + Order();

            svc.Prefs.RecordUse("charlie");
            if (!Order().StartsWith("charlie")) return "something launched should lead, got " + Order();

            string Played() => string.Join(",", svc.Snapshot(id => id == "delta" ? long.MaxValue : null).Items.Select(i => i.Id));
            if (!Played().StartsWith("delta")) return "a game played more recently should lead, got " + Played();

            svc.Prefs.SetFavourite("alpha", true);
            if (!Order().StartsWith("alpha")) return "a pin outranks use, got " + Order();

            // An id left over from an uninstalled program must not break anything.
            svc.Prefs.RecordUse("uninstalled-thing");
            return Order().StartsWith("alpha") ? null : "unknown ids in the history broke the order";
        });

        Check("tile validation", () =>
        {
            var store = new TileStore(Path.Combine(dir, "t1.json"));
            store.Load();
            if (store.Snapshot().Profiles.Count < 2) return "seed has too few profiles";

            string Try(string json)
            {
                using var d = JsonDocument.Parse(json);
                return store.SetProfile(d.RootElement, out var err) ? "ok" : err ?? "refused";
            }

            string Tile(string steps) =>
                "{\"id\":\"p1\",\"name\":\"P\",\"tiles\":[{\"id\":\"a\",\"name\":\"A\",\"steps\":[" + steps + "]}]}";

            if (Try(Tile("{\"do\":\"sys\",\"id\":\"lock\"}")) != "ok") return "a good tile was refused";
            if (Try(Tile("{\"do\":\"sys\",\"id\":\"format\"}")) == "ok") return "unknown system action accepted";
            if (Try(Tile("{\"do\":\"key\",\"keys\":\"banana\"}")) == "ok") return "bad chord accepted";
            if (Try(Tile("{\"do\":\"powershell\",\"id\":\"calc\"}")) == "ok") return "unknown step kind accepted";
            if (Try(Tile("{\"do\":\"wait\",\"ms\":999999}")) == "ok") return "huge wait accepted";
            if (Try(Tile(string.Join(",", Enumerable.Repeat("{\"do\":\"wait\",\"ms\":9000}", 4)))) == "ok") return "36 s of waiting accepted";
            if (Try(Tile(string.Join(",", Enumerable.Repeat("{\"do\":\"sys\",\"id\":\"lock\"}", 13)))) == "ok") return "13 steps accepted";
            if (Try(Tile("")) == "ok") return "empty tile accepted";
            if (Try("{\"id\":\"BAD ID\",\"name\":\"x\",\"tiles\":[]}") == "ok") return "bad profile id accepted";

            Try(Tile("{\"do\":\"sys\",\"id\":\"shutdown\"}"));
            var saved = store.Snapshot().Profiles.First(p => p.Id == "p1").Tiles[0];
            if (!saved.Confirm) return "a shutdown tile must require holding";

            if (store.DeleteProfile(TileStore.DefaultId, out _)) return "default profile was deleted";
            return null;
        });

        Check("tile profiles follow the foreground", () =>
        {
            var store = new TileStore(Path.Combine(dir, "t2.json"));
            store.Load();
            if (store.Resolve("explorer", "desktop").profile.Id != TileStore.DefaultId) return "default expected";
            if (store.Resolve("anything", "gaming").profile.Id != "gaming") return "gaming mode should select the gaming profile";

            using (var d = JsonDocument.Parse(
                "{\"id\":\"edit\",\"name\":\"Editing\",\"match\":[\"Premiere\"],\"tiles\":[{\"id\":\"a\",\"name\":\"A\",\"steps\":[{\"do\":\"key\",\"keys\":\"ctrl+s\"}]}]}"))
                store.SetProfile(d.RootElement, out _);
            if (store.Resolve("premiere", "desktop").profile.Id != "edit") return "process match is case-insensitive and ignores .exe";
            if (store.Resolve("Premiere.exe", "desktop").profile.Id != "edit") return ".exe suffix";

            store.Pin("gaming", out _);
            var (p, auto) = store.Resolve("premiere", "desktop");
            if (p.Id != "gaming" || auto) return "pinned profile must win";
            store.Pin(null, out _);

            // A second load must give the same layout back.
            var again = new TileStore(Path.Combine(dir, "t2.json"));
            again.Load();
            if (again.Snapshot().Profiles.All(x => x.Id != "edit")) return "layout did not survive a restart";
            return null;
        });

        Check("tile file repair", () =>
        {
            string path = Path.Combine(dir, "t3.json");
            File.WriteAllText(path, "{ not json");
            var store = new TileStore(path);
            store.Load();
            if (store.Snapshot().Profiles.Count == 0) return "no fallback after a corrupt file";
            if (!File.Exists(path + ".bad")) return "the unreadable file was not kept";
            return null;
        });

        Check("tile runner", () =>
        {
            var log = new List<string>();
            var runner = new TileRunner(async (s, ct) =>
            {
                if (s.Do == "wait") { await Task.Delay(s.Ms ?? 0, ct); return null; }
                lock (log) log.Add(s.Id ?? s.Do);
                return s.Id == "boom" ? "exploded" : null;
            });

            static TileStep Step(string id) => new() { Do = "sys", Id = id };
            static TileStep Wait(int ms) => new() { Do = "wait", Ms = ms };
            bool Idle(int ms)
            {
                var until = DateTime.UtcNow.AddMilliseconds(ms);
                while (DateTime.UtcNow < until) { if (runner.Running().Count == 0) return true; Thread.Sleep(10); }
                return runner.Running().Count == 0;
            }

            runner.Toggle("one", "One", new[] { Step("a"), Wait(50), Step("b") });
            if (!Idle(3000)) return "a short chain never finished";
            lock (log) if (!log.SequenceEqual(new[] { "a", "b" })) return "steps must run in order: " + string.Join(",", log);

            log.Clear();
            runner.Toggle("two", "Two", new[] { Step("boom"), Step("never") });
            if (!Idle(3000)) return "a failing chain never stopped";
            lock (log) if (log.Contains("never")) return "a step ran after one failed";
            if (runner.LastError?.message != "exploded") return "the failure was not reported";

            log.Clear();
            runner.Toggle("three", "Three", new[] { Wait(5000), Step("late") });
            Thread.Sleep(60);
            if (!runner.Running().Contains("three")) return "a running tile is not listed";
            runner.Toggle("three", "Three", Array.Empty<TileStep>());
            if (!Idle(1500)) return "pressing a running tile did not stop it";
            lock (log) if (log.Contains("late")) return "a stopped tile kept going";

            int accepted = 0;
            for (int i = 0; i < 6; i++)
                if (runner.Toggle("k" + i, "K", new[] { Wait(3000) })) accepted++;
            runner.CancelAll();
            if (accepted != TileRunner.MaxConcurrent) return $"expected {TileRunner.MaxConcurrent} at once, got {accepted}";
            if (!Idle(2000)) return "cancel-all left tiles running";
            return null;
        });

        Check("pairing handshake", () =>
        {
            var path = Path.Combine(dir, "pairing.json");
            var p = new Pairing(path);
            p.Load();
            if (Convert.FromBase64String(p.KeyBase64).Length != 32) return "the key should be 256 bits";

            string tn = Pairing.NewNonce(), hn = Pairing.NewNonce();
            if (tn == hn) return "nonces repeat";

            // What a tablet computes, written out independently of the class under test.
            string Tablet(byte[] key, string a, string b)
            {
                using var h = new HMACSHA256(key);
                return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes($"tablet|{a}|{b}")));
            }
            var key = Convert.FromBase64String(p.KeyBase64);
            string good = Tablet(key, hn, tn);

            if (!p.VerifyTablet(tn, hn, good)) return "a correct proof was refused";
            if (p.VerifyTablet(tn, hn, Tablet(RandomNumberGenerator.GetBytes(32), hn, tn))) return "a proof from the wrong key was accepted";
            if (p.VerifyTablet(tn, Pairing.NewNonce(), good)) return "a proof for another connection was accepted";
            if (p.VerifyTablet(tn, hn, p.HostProof(tn, hn))) return "the PC's own proof was accepted as a tablet's";
            if (p.VerifyTablet(null, hn, good) || p.VerifyTablet(tn, hn, "") || p.VerifyTablet(tn, hn, "not base64!!"))
                return "a missing or malformed proof was accepted";

            var again = new Pairing(path);
            again.Load();
            if (again.KeyBase64 != p.KeyBase64) return "the key did not survive a restart";

            again.Rotate();
            if (again.VerifyTablet(tn, hn, good)) return "an old key still works after the key was replaced";
            return null;
        });

        Check("macro rules", () =>
        {
            static MacroEvent E(int t, string k, int v = 0, int x = 0, int y = 0) => new() { T = t, K = k, V = v, X = x, Y = y };
            string? Err(List<MacroEvent> e) { MacroPlayer.Sanitise(e, out var err); return err; }

            if (Err(new()) is null) return "an empty macro was accepted";
            if (Err(new() { E(0, "kd", 0xAF) }) is null) return "a volume key was recordable";
            if (Err(new() { E(0, "kd", 0x5B), E(5, "ku", 0x5B) }) is not null) return "the Windows key should be recordable";
            if (Err(new() { E(0, "md", 4) }) is null) return "mouse button 4 accepted";
            if (Err(new() { E(0, "mw", 0) }) is null) return "a zero wheel step accepted";
            if (Err(new() { E(0, "mm", 0, 70000, 0) }) is null) return "an off-screen position accepted";
            if (Err(new() { E(50, "kd", 0x41), E(10, "ku", 0x41) }) is null) return "time running backwards accepted";
            if (Err(new() { E(0, "exec", 1) }) is null) return "an unknown event kind accepted";
            if (Err(Enumerable.Range(0, 401).Select(i => E(i, "mm", 0, 1, 1)).ToList()) is null) return "401 events accepted";

            var path = Path.Combine(dir, "macros.json");
            var store = new MacroStore(path);
            if (!store.Add("  Sign   off  ", new() { E(0, "kd", 0x41), E(30, "ku", 0x41) }, out _)) return "a good macro was refused";
            var again = new MacroStore(path);
            again.Load();
            var info = again.Summary().SingleOrDefault();
            if (info is null || info.Name != "Sign   off" || info.Events != 2) return "macro did not survive a restart";
            if (again.Find(info.Id) is null || !again.Delete(info.Id) || again.Find(info.Id) is not null) return "delete";
            for (int i = 0; i < MacroStore.MaxMacros; i++) store.Add(null, new() { E(0, "kd", 0x42), E(1, "ku", 0x42) }, out _);
            if (store.Add(null, new() { E(0, "kd", 0x42), E(1, "ku", 0x42) }, out _)) return "more than the limit accepted";
            return null;
        });

        Check("macro playback", () =>
        {
            var sink = new RecordingSink();
            var events = new List<MacroEvent>
            {
                new() { T = 0, K = "kd", V = 0x11 },            // ctrl down
                new() { T = 20, K = "kd", V = 0x43 },           // c down
                new() { T = 40, K = "md", V = 1, X = 100, Y = 200 },
            };
            // Neither the control key nor the mouse button is ever released by the macro itself.
            var err = MacroPlayer.PlayAsync(events, sink, CancellationToken.None).GetAwaiter().GetResult();
            if (err is not null) return err;
            var log = string.Join(" ", sink.Log);
            if (log != "kd:17 kd:67 mv:100,200 bd:1 ku:17 ku:67 bu:1")
                return "wrong order, or something was left held down: " + log;

            // Cancelled halfway, with a key down: it must be let go, and nothing later may run.
            sink = new RecordingSink();
            using var cts = new CancellationTokenSource(80);
            var slow = new List<MacroEvent>
            {
                new() { T = 0, K = "kd", V = 0x10 },
                new() { T = 3000, K = "kd", V = 0x41 },
            };
            try { MacroPlayer.PlayAsync(slow, sink, cts.Token).GetAwaiter().GetResult(); return "a cancelled macro reported success"; }
            catch (OperationCanceledException) { }
            if (string.Join(" ", sink.Log) != "kd:16 ku:16") return "cancel left a key held or ran a later step: " + string.Join(" ", sink.Log);

            // A refusal from Windows stops the macro and still lets go.
            sink = new RecordingSink { FailAfter = 1 };
            err = MacroPlayer.PlayAsync(new List<MacroEvent> { new() { T = 0, K = "kd", V = 0x10 }, new() { T = 1, K = "kd", V = 0x41 } }, sink, CancellationToken.None).GetAwaiter().GetResult();
            if (err is null) return "a refused key did not stop the macro";
            if (!sink.Log.Contains("ku:16")) return "a refused macro left shift held";
            return null;
        });

        Check("notification parsing", () =>
        {
            const string xml = "<toast><visual><binding template='ToastGeneric'><text>Alex</text><text>Are you free?</text><text>Tonight</text></binding></visual></toast>";
            var (t, b) = NotificationMirror.ParseToast(Encoding.UTF8.GetBytes(xml));
            if (t != "Alex" || b != "Are you free? - Tonight") return $"title/body read as '{t}' / '{b}'";

            var (t2, _) = NotificationMirror.ParseToast(Encoding.Unicode.GetBytes(xml));
            if (t2 != "Alex") return "a UTF-16 payload was not read";
            var (t3, _) = NotificationMirror.ParseToast(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray());
            if (t3 != "Alex") return "a byte-order mark broke the parse";

            foreach (var junk in new byte[]?[] { null, Array.Empty<byte>(), Encoding.UTF8.GetBytes("not xml at all"), new byte[] { 0, 0, 0 } })
                if (NotificationMirror.ParseToast(junk) != ("", "")) return "junk produced text";

            if (NotificationMirror.FriendlyApp("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App") != "WhatsAppDesktop") return "packaged app name";
            if (NotificationMirror.FriendlyApp(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe") != "powershell") return "desktop app name";
            if (NotificationMirror.FriendlyApp("Microsoft.WindowsStore_8wekyb3d8bbwe!App") != "WindowsStore") return "store app name";
            if (NotificationMirror.FriendlyApp(null) != "") return "null app name";

            var settings = new HostSettings(Path.Combine(dir, "settings.json"));
            settings.Load();
            if (settings.Notifications) return "notifications must be off until someone turns them on";
            settings.SetNotifications(true);
            var again = new HostSettings(Path.Combine(dir, "settings.json"));
            again.Load();
            return again.Notifications ? null : "the setting did not survive a restart";
        });

        Check("history keeps spikes", () =>
        {
            var h = new HistoryBuffer();
            long t0 = 1_000_000;
            for (int i = 0; i < 100; i++)
                h.Add(new Reading(t0 + i * 1000L, i == 40 ? 97 : 10, 20, 50, double.NaN, 60, i is >= 70 and < 80 ? 144 : double.NaN));

            var d = h.Query(60, 60, t0 + 99_000);
            if (d.Cpu.Length != 60) return "wrong number of points";
            if (d.Cpu.Max() != 97) return "the 97% spike was lost: " + d.Cpu.Max();
            if (d.Cpu.Count(v => v == 97) != 1) return "a one-second spike should be one point";
            if (d.CpuTemp.Any(v => v is not null)) return "a missing reading became a number";
            if (d.GpuTemp.Max() != 60) return "temperature";
            // One reading a second means a one-minute view cannot ask for finer than a second: finer buckets
            // are empty every other time, and a graph of unconnected dots draws as nothing at all.
            var fine = h.Query(60, 120, t0 + 99_000);
            if (fine.Cpu.Length > 60 || fine.Cpu.Any(v => v is null)) return "a one-minute view has empty buckets: " + fine.Cpu.Count(v => v is null) + " of " + fine.Cpu.Length;
            if (d.Fps.Count(v => v is not null) < 8 || d.Fps.Where(v => v is not null).Any(v => v != 144)) return "frame rate buckets";

            // A 30-minute buffer wraps without losing its newest readings.
            var big = new HistoryBuffer();
            for (int i = 0; i < HistoryBuffer.Capacity + 500; i++) big.Add(new Reading(i * 1000L, i % 100, 0, 0, 0, 0, double.NaN));
            if (big.Count != HistoryBuffer.Capacity) return "the ring did not stay at capacity";
            var q = big.Query(30, 30, (HistoryBuffer.Capacity + 499) * 1000L);
            if (q.Cpu.Last() != 99) return "newest reading missing after wrap: " + q.Cpu.Last();
            return null;
        });

        Check("alert rules", () =>
        {
            var e = new AlertEngine();
            var cfg = new AlertConfig { Enabled = true, CpuTemp = 90 };
            long t = 0;
            List<AlertEvent> Step(double temp) => e.Evaluate(cfg, t += 1000, temp, null, null, null);

            if (Step(95).Count + Step(95).Count != 0) return "fired before it was sustained";
            if (Step(95).Count != 1) return "did not fire on the third reading over";
            if (Step(96).Count != 0) return "fired again straight away";
            Step(88); Step(88); Step(96); Step(96);
            if (Step(96).Count != 0) return "fired again inside the quiet period without having cooled down";

            // Cooled down properly, and later hot again.
            for (int i = 0; i < 5; i++) Step(70);
            if (e.Active.Contains("cpuTemp")) return "still active after cooling";
            t += AlertEngine.ReminderMs;
            Step(95); Step(95);
            if (Step(95).Count != 1) return "did not fire again after cooling and a minute";

            // A condition that never clears reminds once a minute.
            int reminders = 0;
            for (int i = 0; i < 125; i++) reminders += Step(99).Count;
            if (reminders is < 2 or > 3) return $"expected a reminder a minute, got {reminders} in two minutes";

            cfg.Enabled = false;
            if (Step(120).Count != 0) return "a disabled alert fired";
            if (e.Active.Count != 0) return "state left over while disabled";
            cfg.Enabled = true; cfg.Fan = 5000;
            for (int i = 0; i < 2; i++) e.Evaluate(cfg, t += 1000, null, null, null, 5200);
            return e.Evaluate(cfg, t += 1000, null, null, null, 5200).Count == 1 ? null : "fan limit";
        });

        Check("frame statistics", () =>
        {
            var f = new FrameStats();
            for (int i = 0; i < 990; i++) f.Add(10);
            for (int i = 0; i < 10; i++) f.Add(50);
            if (Math.Abs(f.AverageFps!.Value - 96.2) > 0.1) return "average " + f.AverageFps;
            if (Math.Abs(f.LowFps(0.01)!.Value - 20) > 0.2) return "1% low " + f.LowFps(0.01);
            if (Math.Abs(f.LowFps(0.001)!.Value - 20) > 0.2) return "0.1% low " + f.LowFps(0.001);

            f.Add(200);   // one real hitch: the 0.1% low must find it exactly, not a bin edge
            if (Math.Abs(f.LowFps(0.001)!.Value - 5) > 0.05) return "0.1% low with a hitch " + f.LowFps(0.001);
            f.Add(0); f.Add(double.NaN); f.Add(5000);
            if (f.Count != 1001) return "nonsense frames were counted";
            return new FrameStats().LowFps(0.01) is null ? null : "an empty session reported a low";
        });

        Check("session recorder", () =>
        {
            var path = Path.Combine(dir, "sessions.json");
            var r = new SessionRecorder(path);
            long t = 0;
            void Run(string? game, int seconds, double fps = 60)
            {
                for (int i = 0; i < seconds; i++) r.Tick(t += 1000, game, game is null ? null : fps, 40, 70, 75);
            }

            Run("Neon Drift", 5); Run(null, 40);
            if (r.Items.Count != 0) return "a five second blip became a session";

            Run("Neon Drift", 20);
            Run(null, 6);                   // a few seconds in something that is not a game
            Run("Neon Drift", 40);
            Run(null, SessionRecorder.EndAfter + 2);
            var items = r.Items;
            if (items.Count != 1) return $"alt-tabbing split one evening into {items.Count} sessions";
            if (items[0].Game != "Neon Drift" || items[0].Seconds != 60) return $"wrong session: {items[0].Game} {items[0].Seconds}s";
            if (items[0].Series.Count == 0 || items[0].Series.Any(v => v != 60)) return "frame-rate series";
            if (items[0].MaxGpuTemp != 75 || items[0].AvgCpu != 40) return "load figures";

            // Frames feed the statistics.
            Run("Hollow Keep", 12);
            for (int i = 0; i < 600; i++) r.AddFrame(i % 100 == 0 ? 40 : 8);
            Run("Hollow Keep", 30);
            if (r.Live?.Game != "Hollow Keep") return "no live session";
            Run("Neon Drift", 40);          // a different game takes over
            Run(null, SessionRecorder.EndAfter + 2);
            var all = r.Items;
            if (all.Count != 3 || all[1].Game != "Hollow Keep") return "game switch not recorded";
            if (all[1].Low1 is null || all[1].AvgFps is null || all[1].Low1 >= all[1].AvgFps) return "lows must be below the average";

            var again = new SessionRecorder(path);
            again.Load();
            if (again.Items.Count != 3) return "sessions did not survive a restart";
            if (!again.Delete(again.Items[0].Id) || again.Items.Count != 2) return "delete";
            return null;
        });

        Check("playtime", () =>
        {
            var path = Path.Combine(dir, "playtime.json");
            var p = new Playtime(path);
            for (int i = 0; i < 125; i++) p.Add("game:one", 1, 1_000_000 + i * 1000L);
            p.Flush();
            var again = new Playtime(path);
            again.Load();
            var e = again.Get("game:one");
            if (e is null || e.Seconds != 125 || e.LastMs != 1_000_000 + 124 * 1000L) return "playtime or last played wrong";
            return again.Get("never") is null ? null : "an unplayed game has playtime";
        });

        Check("auto power rule", () =>
        {
            int reads = 0;
            string mode = "balanced";
            string? Read() { reads++; return mode; }

            var a = new AutoPower();
            (PowerStep, string?) Tick(bool enabled, bool gaming, bool ac = true) => a.Tick(enabled, "performance", gaming, ac, Read);

            for (int i = 0; i < AutoPower.ApplyAfter - 1; i++)
                if (Tick(true, true).Item1 != PowerStep.None) return "applied before the game had settled";
            var first = Tick(true, true);
            if (first != (PowerStep.Apply, "performance")) return "did not apply: " + first;
            mode = "performance";
            for (int i = 0; i < 600; i++) Tick(true, true);
            if (reads > 3) return $"read the mode {reads} times during one game";

            for (int i = 0; i < AutoPower.RestoreAfter - 1; i++)
                if (Tick(true, false).Item1 != PowerStep.None) return "restored before the game was over";
            var back = Tick(true, false);
            if (back != (PowerStep.Restore, "balanced")) return "did not put the old mode back: " + back;

            // On battery, nothing happens.
            mode = "balanced";
            var b = new AutoPower();
            for (int i = 0; i < 30; i++)
                if (b.Tick(true, "performance", true, false, Read).Item1 != PowerStep.None) return "changed the mode on battery";

            // A mode chosen by hand during the game is left alone afterwards.
            mode = "balanced";
            var c = new AutoPower();
            for (int i = 0; i < 6; i++) c.Tick(true, "performance", true, true, Read);
            mode = "quiet";   // the user changed it
            for (int i = 0; i < AutoPower.RestoreAfter + 1; i++)
                if (c.Tick(true, "performance", false, true, Read).Item1 == PowerStep.Restore) return "overrode a mode the user picked";

            // Switching the rule off mid-game gives the machine back.
            mode = "balanced";
            var d = new AutoPower();
            for (int i = 0; i < 6; i++) d.Tick(true, "performance", true, true, Read);
            mode = "performance";
            return d.Tick(false, "performance", true, true, Read) == (PowerStep.Restore, "balanced") ? null : "switching it off did not restore";
        });

        Check("per-process GPU names", () =>
        {
            if (!GpuProcessCounters.TryParse("pid_4120_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D", out int pid, out string t) || pid != 4120 || t != "3D")
                return "a normal instance name";
            if (!GpuProcessCounters.TryParse("pid_77_luid_0x0_0x1_phys_0_eng_3_engtype_VideoDecode", out pid, out t) || pid != 77 || t != "VideoDecode")
                return "a video engine";
            return GpuProcessCounters.TryParse("something_else", out _, out _) || GpuProcessCounters.TryParse("pid_x_luid", out _, out _)
                ? "junk was parsed" : null;
        });

        Check("spectrum bars", () =>
        {
            static float[] Tone(double hz, double amp, int rate = 48000) =>
                Enumerable.Range(0, SpectrumAnalyser.FftSize).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();
            static int Loudest(float[] bars) => Array.IndexOf(bars, bars.Max());

            var silence = SpectrumAnalyser.Compute(new float[SpectrumAnalyser.FftSize], 48000);
            if (silence.Length != SpectrumAnalyser.Bands || silence.Any(v => v > 1)) return "silence is not silent";

            foreach (var hz in new[] { 100.0, 440, 1000, 4000, 10000 })
            {
                var bars = SpectrumAnalyser.Compute(Tone(hz, 0.5), 48000);
                int want = SpectrumAnalyser.BandOf(hz);
                if (Math.Abs(Loudest(bars) - want) > 1) return $"{hz} Hz showed in band {Loudest(bars)}, expected {want}";
                if (bars.Max() < 70) return $"{hz} Hz at half scale only reached {bars.Max():F0}";
            }

            var quiet = SpectrumAnalyser.Compute(Tone(1000, 0.01), 48000).Max();
            var loud = SpectrumAnalyser.Compute(Tone(1000, 0.5), 48000).Max();
            if (quiet >= loud - 20) return "a loud tone should read well above a quiet one";

            // The same tone at 44.1 kHz lands in the same band: the bars are about hertz, not bins.
            var other = SpectrumAnalyser.Compute(Tone(1000, 0.5, 44100), 44100);
            return Math.Abs(Loudest(other) - SpectrumAnalyser.BandOf(1000)) <= 1 ? null : "sample rate shifted the bars";
        });

        Check("calendar parsing", () =>
        {
            string ics = string.Join("\r\n", new[]
            {
                "BEGIN:VCALENDAR",
                "BEGIN:VEVENT", "SUMMARY:Stand-up\\, daily", "DTSTART:20261026T090000Z", "DTEND:20261026T091500Z",
                "RRULE:FREQ=DAILY;COUNT=5", "EXDATE:20261027T090000Z", "END:VEVENT",
                "BEGIN:VEVENT", "SUMMARY:Gym", "DTSTART:20261026T180000Z", "DTEND:20261026T190000Z",
                "RRULE:FREQ=WEEKLY;BYDAY=MO,WE", "END:VEVENT",
                "BEGIN:VEVENT", "SUMMARY:Holiday", "DTSTART;VALUE=DATE:20261028", "DTEND;VALUE=DATE:20261030", "END:VEVENT",
                "BEGIN:VEVENT", "SUMMARY:Cancelled", "DTSTART:20261026T100000Z", "STATUS:CANCELLED", "END:VEVENT",
                "BEGIN:VEVENT", "SUMMARY:Folded ti", " tle", "DTSTART:20261031T100000Z", "END:VEVENT",
                "END:VCALENDAR",
            });
            var events = IcsParser.Parse(ics);
            if (events.Count != 4) return $"expected 4 events, got {events.Count}";
            if (events[0].Title != "Stand-up, daily") return "escaped comma";
            if (events.Any(e => e.Title == "Cancelled")) return "cancelled event kept";
            if (events.All(e => e.Title != "Folded title")) return "line folding";

            var from = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
            var occ = IcsParser.Expand(events, from, from.AddDays(7));

            int standups = occ.Count(o => o.Title.StartsWith("Stand-up"));
            if (standups != 4) return $"daily x5 minus one excluded should be 4, got {standups}";
            int gyms = occ.Count(o => o.Title == "Gym");
            if (gyms != 2) return $"Mon+Wed over the week from Monday the 26th should be 2 (the 26th and 28th), got {gyms}";
            var hol = occ.FirstOrDefault(o => o.Title == "Holiday");
            if (!hol.AllDay || (hol.End - hol.Start).TotalDays != 2) return "all-day event should span two days";
            return null;
        });

        Check("timers", () =>
        {
            long clock = 1_000_000;
            int alarms = 0;
            string path = Path.Combine(dir, "timers.json");
            using var t = new TimerService(path, () => clock, () => alarms++);
            t.Load();

            if (!t.StartCountdown(60, "tea")) return "countdown refused";
            if (t.StartCountdown(0, null) || t.StartCountdown(90_000, null)) return "out-of-range countdown accepted";
            clock += 30_000;
            var id = t.Snapshot().Items[0].Id;
            if (!t.Pause(id)) return "pause";
            if (t.Snapshot().Items[0].RemainingMs != 30_000) return "paused remaining should be 30 s";
            clock += 600_000;
            t.Tick();
            if (t.Snapshot().Items[0].State != "paused") return "a paused timer must not fire";
            if (!t.Resume(id)) return "resume";
            clock += 29_000; t.Tick();
            if (t.Snapshot().Items[0].State != "running") return "fired early";
            clock += 2_000; t.Tick();
            if (t.Snapshot().Items[0].State != "ringing" || alarms < 1) return "did not ring when due";

            // The saved file must bring a running timer back with its time intact.
            t.Remove(id);
            t.StartCountdown(120, "again");
            using var back = new TimerService(path, () => clock, () => { });
            back.Load();
            var item = back.Snapshot().Items.FirstOrDefault();
            if (item is null || item.State != "running" || item.EndsAtMs != clock + 120_000) return "timer did not survive a restart";

            t.Clear();
            t.StartStopwatch(null);
            clock += 5_000;
            var sw = t.Snapshot().Items[0].Id;
            t.Pause(sw);
            if (t.Snapshot().Items[0].ElapsedMs != 5_000) return "stopwatch elapsed";
            clock += 60_000; t.Resume(sw); clock += 1_000;
            var s = t.Snapshot().Items[0];
            if (clock - s.StartedAtMs != 6_000) return "stopwatch must not count the paused minute";

            t.Clear();
            t.StartPomodoro(1, 1, 2, 2, null);
            string Phase() => t.Snapshot().Items[0].Phase + "/" + t.Snapshot().Items[0].Round;
            clock += 61_000; t.Tick(); if (Phase() != "short/1") return "work then short break, got " + Phase();
            clock += 61_000; t.Tick(); if (Phase() != "work/2") return "short break then work, got " + Phase();
            clock += 61_000; t.Tick(); if (Phase() != "long/2") return "last round ends in a long break, got " + Phase();
            clock += 121_000; t.Tick(); if (t.Snapshot().Items[0].State != "ringing") return "cycle should finish ringing";
            return null;
        });

        Check("remembered volumes", () =>
        {
            string path = Path.Combine(dir, "volumes.json");
            var m = new VolumeMemory(path);
            m.Set("spotify", 140, true);
            var got = m.Get("spotify");
            if (got is null || got.Volume != 100 || !got.Muted) return "stored value should be clamped";
            var again = new VolumeMemory(path);
            again.Load();
            if (!again.Has("SPOTIFY")) return "lookup is case-insensitive and survives a restart";
            again.Forget("spotify");
            return again.Has("spotify") ? "forget" : null;
        });

        try { Directory.Delete(dir, recursive: true); } catch { }
        return failed;
    }
}

internal sealed class RecordingSink : IInputSink
{
    public List<string> Log { get; } = new();
    public int FailAfter { get; set; } = int.MaxValue;
    private int _count;

    private bool Take(string entry)
    {
        Log.Add(entry);
        return ++_count <= FailAfter;
    }

    public bool Key(ushort vk, bool up) => Take($"{(up ? "ku" : "kd")}:{vk}");
    public bool MouseMove(int x, int y) => Take($"mv:{x},{y}");
    public bool MouseButton(int button, bool up) => Take($"{(up ? "bu" : "bd")}:{button}");
    public bool Wheel(int delta) => Take($"wh:{delta}");
}

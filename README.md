# Companion Deck

Turn an old Android tablet into a control surface for your Windows PC.

Plug the tablet in over USB and it becomes a second screen you can actually touch: now
playing with artwork and a seek bar, a per-application volume mixer, CPU/GPU/RAM gauges,
your installed games and apps with real cover art, and a soundboard that can play into a
voice call.

It was built to make a 2016 tablet with 1 GB of RAM useful again, so the whole thing is
built around not wasting resources. The PC agent collects only what the tablet is
currently showing, at the rate it asks for, and stops entirely when the screen goes off.

![The Companion Deck home screen: now playing with artwork, CPU, GPU and RAM dials, and the audio mixer](docs/images/home.png)

## Download

Nothing to build and nothing else to install. Get the files from the
**[latest release](https://github.com/CE5ald/companion-deck/releases/latest)**:

| File | What it is |
|---|---|
| **`CompanionDeck-Setup-1.0.0.exe`** | The Windows installer. It contains the program (with its own .NET, so you install nothing first), fetches Google's ADB for the USB cable and checks it is signed by Google, and can set up the rest for you. |
| **`CompanionDeck-1.0.0.apk`** | The Android app, for installing on the tablet by hand. The installer can also put it on a plugged-in tablet for you. |
| `SHA256SUMS.txt` | Checksums, to check the downloads are intact. |

**In three steps**

1. Run the installer on your PC and leave the ticked options as they are. It starts Companion Deck when you
   sign in, opens the Wi-Fi link in the firewall for private networks only, and offers to install the app on a
   tablet that is plugged in.
2. On the tablet, turn on **USB debugging** (the app walks you through it and ticks each step itself). Plug it in.
3. That is all: the tablet app opens and connects. Windows' firewall and the optional CPU-temperature driver
   are the only extras, and the installer asks before touching either.

**What your PC and tablet need:** Windows 10 or 11 (64-bit) and an Android 5.0 or newer device with a recent
*Android System WebView*. It finds everything else itself, and any feature your hardware cannot do is simply
not drawn.

## Screenshots

Every page of the tablet UI, captured at 2x from the built-in demo data (no real machine appears
in any of them). `node tools/capture-screenshots.js` regenerates them.

<table>
<tr>
<td width="50%"><img src="docs/images/media.png" alt="Media page: full player with artwork, seek, shuffle and repeat, and one-tap sources"><br><b>Media</b><br>Full player with artwork, a draggable seek bar, and one-tap sources.</td>
<td width="50%"><img src="docs/images/mixer.png" alt="Per-application volume mixer with output and input device switching"><br><b>Mixer</b><br>Per-app volume and mute, and output and input switching.</td>
</tr>
<tr>
<td><img src="docs/images/voice.png" alt="Voice page: desktop audio streamed into the microphone, with live level meters and a rolling recorder"><br><b>Voice</b><br>Stream desktop audio into your microphone, with a rolling recorder.</td>
<td><img src="docs/images/soundboard.png" alt="Soundboard with loudness-matched clips routed to speakers, microphone or both"><br><b>Soundboard</b><br>Loudness-matched clips, routed to speakers, mic or both.</td>
</tr>
<tr>
<td><img src="docs/images/performance.png" alt="Performance page: CPU, GPU and RAM dials, frame rate, fans and network"><br><b>Performance</b><br>Live dials, fans, network and frame rate, only where the hardware reports them.</td>
<td><img src="docs/images/apps.png" alt="Running applications that can be closed or ended from the tablet"><br><b>Running apps</b><br>Tap closes an app politely; hold ends it.</td>
</tr>
<tr>
<td><img src="docs/images/launcher.png" alt="Launcher for games and applications"><br><b>Launcher</b><br>Games and apps with their own artwork.</td>
<td><img src="docs/images/actions.png" alt="Quick actions and display brightness"><br><b>Actions</b><br>Brightness and system shortcuts; destructive tiles must be held.</td>
</tr>
<tr>
<td><img src="docs/images/deck.png" alt="Deck page: a grid of tiles that switched to a gaming profile by itself"><br><b>Deck</b><br>Tiles you lay out, one set per app. It switches profile when a game or editor is in front.</td>
<td><img src="docs/images/deck-editor.png" alt="Tile editor: a chain of steps with a wait and a timer"><br><b>Tile editor</b><br>One tile can run a chain: shortcuts, apps, audio, media, timers and waits.</td>
</tr>
<tr>
<td><img src="docs/images/clock.png" alt="Clock page with a large clock, upcoming events and running timers"><br><b>Clock</b><br>The PC's time, your calendar, and timers that survive a tablet reboot.</td>
<td><img src="docs/images/outputs.png" alt="Voice tiles with LEDs, and an output row with a volume for each device"><br><b>Voice and outputs</b><br>Big mic and deafen tiles, and every output with its own volume.</td>
</tr>
<tr>
<td><img src="docs/images/macro-record.png" alt="Tile editor recording a macro, with a live event count"><br><b>Macros</b><br>Record keys and mouse on the PC from the tablet, then bind the result to a tile.</td>
<td><img src="docs/images/notifications.png" alt="Upcoming events and mirrored Windows notifications"><br><b>Notifications</b><br>Windows toasts on the tablet. Read-only, and off until you switch it on.</td>
</tr>
<tr>
<td><img src="docs/images/history.png" alt="Load, temperature and frame-rate history charts"><br><b>History</b><br>The last 1, 5 or 30 minutes, with spikes kept.</td>
<td><img src="docs/images/top-processes.png" alt="Top processes by memory with close and priority controls"><br><b>Top processes</b><br>What is using the PC, with lower priority, close, and a held end button.</td>
</tr>
<tr>
<td><img src="docs/images/sessions.png" alt="Gaming sessions with average and 1% and 0.1% low frame rates"><br><b>Sessions</b><br>Average, 1% low and 0.1% low for each game you play.</td>
<td><img src="docs/images/frame-times.png" alt="Live frame-time trace"><br><b>Frame times</b><br>Individual frames, so stutter shows where an average hides it.</td>
</tr>
<tr>
<td><img src="docs/images/media-display.png" alt="Media page with the cover-coloured card and a live visualizer"><br><b>Visualizer</b><br>The card takes its colour from the cover; the bars follow what is playing.</td>
<td><img src="docs/images/now-playing.png" alt="Ambient now-playing view"><br><b>Ambient</b><br>Just the track, large, for a tablet propped beside the screen.</td>
</tr>
<tr>
<td><img src="docs/images/pad-settings.png" alt="Soundboard pad settings with colour, folder, volume, routing and loop"><br><b>Soundboard pads</b><br>Folders, colours, per-pad volume and looping.</td>
<td><img src="docs/images/calendars.png" alt="Calendars subscribed by link, with a failed one showing why"><br><b>Calendars</b><br>Paste a private calendar link once; it refreshes itself. The link stays on the PC.</td>
</tr>
<tr>
<td><img src="docs/images/hold-to-confirm.png" alt="Hold-to-end panel with a filling ring, away from the finger"><br><b>Hold to confirm</b><br>Ending a program takes a 1.5 s hold, shown where your finger is not, and reports what happened.</td>
<td></td>
</tr>
<tr>
<td><img src="docs/images/setup.png" alt="First-run setup guide with steps that tick themselves"><br><b>First-run guide</b><br>Steps tick themselves as the tablet's own settings change.</td>
<td><img src="docs/images/phone-landscape.png" alt="The same interface on a landscape phone"><br><b>Any screen</b><br>The same UI, reflowed for a landscape phone.</td>
</tr>
</table>

## What you need

- **Windows 10 or 11, 64-bit.** The now-playing card uses the media-session API that arrived in
  Windows 10 version 2004 (build 19041); builds from 1809 up run without it. Earlier than 1809 is not supported.
- **An Android 5.0 device or newer**, in landscape, about 600dp wide or more, with *Android System WebView*
  version 80 or later (the Play Store keeps it current; the app tells you if it is too old). A 1024x600
  tablet is the sweet spot; a phone held sideways works.
- **A USB cable.** A network connection works as a fallback, including the one Android's
  USB tethering makes over the cable itself.

There is no account, no cloud service, and no IP address to type anywhere. The installer puts the
program in Program Files and removes it cleanly; your settings stay until you delete them.

## Building it yourself

The download above is all most people need. To build from source instead:

```bash
git clone https://github.com/CE5ald/companion-deck.git
```

Then double-click **`Setup.cmd`**.

It checks what the machine is missing, tells you the exact command to fix anything it
cannot do itself, builds both sides, installs the app on a tablet if one is plugged in,
and finishes by printing what your particular PC can and cannot do.

Afterwards, **`Deck.cmd`** is the everyday launcher. Leave it running and plug the tablet
in; it starts the app on the tablet and connects by itself.

### The one thing the tablet needs

There is no way for a PC to open a connection to a stock Android device over USB without
something being switched on first. Every route needs one of these, and this is the honest
comparison:

| Route | What it costs you | What you get |
|---|---|---|
| **USB debugging** (recommended) | Developer options on, once | The cable alone from then on. The app starts itself and connects; nothing to tap, ever again |
| **USB tethering** | A toggle in Settings, no developer options | Works over the cable, but many devices need the toggle flicked each time |
| Wi-Fi | Nothing, if both are on the same network | No cable at all, slightly higher latency |

**You do not have to work any of this out.** Open the app on the tablet and it shows a
three-step guide with a button for each step that opens the right Settings page. The tablet
can read its own developer-options and USB-debugging state, so each step ticks itself green
the moment it is done, and the guide disappears for good once a link has worked once.

After that first run, the promise holds literally: plug the cable in, and the tablet wakes
up and connects by itself.

## Make it fit your own PC and tablet

Companion Deck detects what your PC and tablet can do, and shows only that. If something you have is not
covered (your laptop maker's fan or performance controls, an odd screen size, a game launcher, a button you
want), you can change your own copy, and an AI coding assistant makes that practical even if you do not
program. Editing it for yourself is allowed; sharing the changed copy is not, without asking (see
[Licence](#licence)).

**1. Get the code.** Download it from this page (the green *Code* button, then *Download ZIP*) and unzip it,
or run `git clone https://github.com/CE5ald/companion-deck.git`. Then run `Setup.cmd` once so you can build
it. The installer from the Releases page is for using it; changing it needs the code.

**2. Open the folder in an AI coding agent.** Any one that can read and edit files and run commands works:
Codex, Cursor, Gemini CLI, GitHub Copilot's agent mode, Windsurf and many others. Open the
folder as the project, so it can see everything in it.

**3. Tell it about your hardware and what you want.** The more it knows, the better it does. Give it:

- your PC: the make and model, Windows version, and graphics card;
- your tablet: the model, Android version and screen size;
- what is missing or wrong, in plain words ("my laptop is an ASUS and the fan speeds show as not available");
- the report from `CompanionDeck.Host.exe --selftest` (what this PC supports) and the end of
  `%LOCALAPPDATA%\CompanionDeck\host.log`.

A prompt that works well as a starting point:

> This is Companion Deck, a Windows agent plus an Android tablet app. Read README.md and docs/USAGE.md
> first. My PC is *[make, model, Windows version, GPU]* and my tablet is *[model, Android version,
> resolution]*. I want *[what you want]*. Find out how this machine exposes it, and show me what you
> found before you change anything. Make read-only probes first. Only write to hardware after you have
> read back that the value changed. Then make the smallest change that works, add a check for it, and
> run `CompanionDeck.Host.exe --selftest` and `node tools/selftest-link.js`.

**4. Let it build and test, and try it on the real devices.** The agent can run the checks above. For the
tablet screens, `node tools/uiserve.js` previews the interface in your browser with demo data, with no tablet
needed. Build it with `Setup.cmd`, then try the change on the real PC and tablet. A change that passes its checks can still behave differently on real hardware.

**Where things live**

| To change... | Look in |
|---|---|
| How a sensor, fan or performance mode is read or set | `host/CompanionDeck.Host/Telemetry/` |
| Which games and apps are found, and how they are ordered | `host/CompanionDeck.Host/Launcher/` |
| Audio devices, mixer, soundboard, recording | `host/CompanionDeck.Host/Audio/` |
| What the tablet is told it can do | `host/CompanionDeck.Host/Server/DeckServer.cs` (the capabilities) |
| How the tablet looks and behaves | `tablet/app/src/main/assets/` (`index.html`, `app.css`, `app.js`) |

**Good first requests:** support for your laptop maker's performance mode or fan readings; a layout for your
tablet's screen; shortcuts for the software you actually use; a new tile on the Deck page.

**Stay safe**

- Work on a copy or a branch, and keep the installer you downloaded, so you can go back.
- Back up `%LOCALAPPDATA%\CompanionDeck` (your settings, tiles and pairing) before big changes.
- Be careful with anything that writes to firmware or the embedded controller. Ask the agent to read first,
  and to verify every write by reading the value back. Do not guess at registers on hardware it cannot check.
- If your change would help other people with the same hardware, open an issue or a pull request instead
  of publishing your own copy, or ask for permission.

## What works on which hardware

Every hardware feature is probed at startup, and the tablet is told what is genuinely
available, so a control is only ever drawn when it can actually do something. Run
`CompanionDeck.Host.exe --selftest` to see where your machine lands.

| Feature | Requirement |
|---|---|
| CPU, memory, disk, network, battery | Any PC |
| GPU load and VRAM | Any PC. NVIDIA cards also report temperature, power and clocks through NVML |
| Media control, artwork, seek | Any app that registers with Windows (Spotify, browsers, players) |
| Per-app volume, output and input switching | Any PC |
| Launcher with real cover art | Steam, Epic and Start Menu apps. Ordered by what you pin and launch, so the first page is your own |
| Web music sources | Whatever Windows' default browser is (read from Windows, never assumed). The service keeps its own identity; the browser is a small badge |
| Performance modes | Windows power modes. On Windows 11 and recent Windows 10 these are the "Power mode" settings |
| Screen brightness | A laptop panel, and the elevated agent |
| CPU temperature | The elevated agent, plus [PawnIO](https://pawnio.eu/) (`winget install namazso.PawnIO`) so the sensor library can read your CPU. Intel and AMD, laptop or desktop. A Lenovo laptop's own interface and the ACPI thermal zone are tried too |
| GPU temperature | NVIDIA through its driver. AMD and Intel graphics through the same sensor library as the CPU |
| Fan speeds | A vendor interface. Not available through any standard Windows API. Today: Lenovo Legion/LOQ-class laptops. Everything else shows "not available" instead of a made-up reading. A fan is drawn only once it reports, so a one-fan laptop shows one |
| Vendor performance profile | Lenovo laptops with the GameZone interface (Eco/Normal/Gaming, titled with the brand). Every other PC gets Windows' own power mode. More vendors need their firmware interface written and tested on that hardware; the hook is `Telemetry/` |
| Game overlay shortcuts | Only those whose software is installed: Game Bar always, NVIDIA overlay and control panel with an NVIDIA GPU and its software, Steam's overlay with Steam |
| Frames per second | The elevated agent (or your account in "Performance Log Users"). Read from Windows' own event tracing: any GPU, nothing to install. DirectX, OpenGL and Vulkan games |
| Cast desktop audio into your microphone | [VB-Audio Virtual Cable](https://vb-audio.com/Cable/) installed separately |
| Deck tiles, profiles that follow the foreground app | Any PC. Editing a layout needs the cable |
| Clock, timers, stopwatch, pomodoro | Any PC. Timers are kept on the PC and survive a tablet reboot |
| Calendar | Any PC. Paste a calendar's private `.ics` link once (Google, Outlook, iCloud and Fastmail all give one) and it refreshes itself; or drop an exported `.ics` file in the calendar folder. The link stays on the PC |
| Mic mute, deafen, per-output volume, remembered app levels | Any PC |
| Macro recorder | Any PC. Records and replays keys and mouse |
| Notification mirror | Windows 10 or 11. Read-only, off until switched on from the cable. See [the usage notes](docs/USAGE.md#notifications) |
| Night mode | The tablet's own screen. Nothing on the PC |
| History graphs, alerts, top processes | Any PC. GPU throttle reasons need an NVIDIA driver; per-process GPU needs Windows' GPU counters |
| Gaming sessions with 1% and 0.1% lows, frame-time graph | Any PC for the session record. Frame rates and lows need the same as Frames per second |
| Auto performance mode while a game runs | A PC with a Windows power mode control. AC power only |
| Cover colours, ambient view, visualizer | Any PC. The visualizer listens to the default output, only while shown |
| Soundboard folders, colours and loops | Any PC |

### Running elevated

Brightness, CPU temperature, fans and frame rates are refused by Windows to an ordinary process.
`scripts\Install-Elevated.ps1` registers a logon scheduled task that runs the agent with
full privileges, so those work and you are never prompted again. It is optional, and
everything else works without it.

A scheduled task rather than a service because the agent needs the interactive desktop
session: it synthesises keystrokes and reads the media session of the logged-in user,
neither of which a session-0 service can do.

## How the link works

The tablet listens on an abstract local socket, and the PC reaches it with
`adb forward`. That means the connection travels over the USB cable, nothing is exposed to
your network, and plugging the cable in is the entire connection ceremony.

If no cable is present, the PC broadcasts a small UDP beacon and the tablet dials back over
Wi-Fi. The PC stops beaconing the moment the cable returns.

The tablet never names a file, a path or a URL. It names an **id**, and the PC decides what
that id means by looking it up in its own list. A compromised or spoofed tablet therefore
cannot make the PC launch something that was not already there.

The same rule covers the deck. A tile is stored on the PC; pressing it sends only its id. A
layout change is checked on the PC against a fixed list of step kinds, keys and actions before
it is kept, and is refused outright unless the link is the USB cable.

### Pairing

The cable is the trust anchor. The first time a tablet connects over USB, the PC gives it a
random 256-bit key. After that the network fallback works without the cable, but only for a
tablet that holds the key: when one connects, the PC and the tablet each prove they know it
(an HMAC over fresh random values from both sides, so nothing replays and the key never crosses
the network), and until that is done the PC answers nothing and cannot be knocked off a working
link. A tablet that has never been on the cable is told to plug in once. **Forget other tablets**
on the Actions page replaces the key; the tablet on the cable keeps working.

Over the network a tablet can use everything except the things that watch or rewrite the PC:
recording a macro, switching on notification mirroring, editing a tile and unpairing are
refused unless the link is the cable.

## Layout

```
host/        Windows agent (.NET 8). Telemetry, audio, media, launcher, the link.
tablet/      Android app (Kotlin). A thin shell around a WebView.
  app/src/main/assets/   The UI itself: one HTML file, one CSS file, one JS file.
scripts/     Setup, launch, elevated install.
installer/   Builds the Windows installer and the signed APK that releases carry.
tools/       A static server for designing the UI in a desktop browser.
```

The tablet UI is a web page rather than Android views because a weak tablet GPU handles
GPU-composited CSS far more happily than a view hierarchy, and because the whole dashboard
can then be designed in a desktop browser without deploying anything.

## Development

```bash
node tools/uiserve.js
```

Serves the UI at `http://localhost:5173` with a mock-data harness, so every card and state
can be worked on without a tablet or even the PC agent running.

Testing the link without a tablet:

```bash
node tools/selftest-link.js
```

Pretends to be a tablet: finds the running agent over the network transport, handshakes,
subscribes to every section and sends every command the real tablet can send, then prints a
pass/fail table. It checks that advertised capabilities are actually backed by readings,
that an empty subscription stops the agent collecting, and that malformed input does not
kill the link.

Useful agent flags:

| Flag | What it does |
|---|---|
| `--selftest` | Prints what this PC can do and exits |
| `--verbose` | Debug-level logging |
| `--probe-adb` | Diagnoses tablet detection |
| `--no-wifi` | USB only; skips the network transport and its beacon |
| `--no-usb` | Network only; lets `tools/selftest-link.js` reach an agent that has a cable attached |

The log is always written to `%LOCALAPPDATA%\CompanionDeck\host.log`, including when the
agent runs from the scheduled task with no console attached.

## How it was built

This software was written with the help of AI, working under my direction. I made the decisions,
tested it on real hardware, and am responsible for the result.

## Licence

**Free to use and to change for yourself. Ask first to share or continue it.** Anyone may download, use and
edit Companion Deck for their own private use, for free, including with the help of AI tools. Sharing it,
publishing a changed version, or taking over its development needs the author's written permission: open an
issue titled "Permission request". Pull requests are welcome. The full terms are in
[LICENSE](LICENSE). Versions published before 31 October 2026 were released under the MIT License, and copies
received under it stay under it.

This is a source-available license, not an open-source one: you can read the code, use the program and change
it for yourself, but you cannot republish it.

### Third-party software

[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) reads
CPU temperature and fans, [TraceEvent](https://github.com/microsoft/perfview) (MIT) reads frame rates,
and [NAudio](https://github.com/naudio/NAudio) (MIT) handles audio. Each keeps its own license; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Reading CPU sensors needs the signed
[PawnIO](https://pawnio.eu/) driver, which is installed by you and never by this program.

# Version 1 release readiness

Written 2026-10-31 after a full review. Everything below says which kind of test backed it:
**real** means the Lenovo Legion and the Lenovo TB-7304I tablet this was built on, **simulated** means a
forced or mocked path, and **not tested** means exactly that. Passing on one laptop is not a
compatibility claim for other hardware, and nothing here makes one.

## Verdict

**Ready for a public GitHub release, with the limits listed under "Not verified" stated in the release
notes.** The architecture is now universal: vendor features are found at run time and drawn only where the
hardware has them. The two things a stranger will hit first are installing without the elevated agent
(several readings are then "not available", and the tablet says so) and a game that does not draw through
DirectX, OpenGL or Vulkan.

## Issues found, and what was done

| # | Found | Done |
|---|---|---|
| 1 | **FPS never worked.** It launched whichever PresentMon-like program it found first. On this PC that was NVIDIA FrameView's private build, which exits at once with code 1 when driven like a public PresentMon. Failure was logged at the wrong level, so it was silent, and `--selftest` said "FPS: yes" because a file existed. | Replaced with an in-process reader of Windows' own event tracing (DirectX present events, plus the graphics kernel's present event for OpenGL and Vulkan). Nothing to install, any GPU vendor. Reports `unavailable` with the reason instead of a number. Trace thread failures now recover. Self-test reports the truth. |
| 2 | The preferred browser was hard-coded to Brave. "Spotify Web" showed the browser's icon as its identity. | Default browser read from Windows (UserChoice, then the program its open command runs). Opened through the shell, so profile and all. The service draws as itself, the browser is a corner badge. |
| 3 | A game-specific chat macro and its auto-repeat loop. | Removed entirely: host command, tablet UI, CSS. |
| 4 | "Legion mode", and the Lenovo code existed only in a personal build split from a public one. | One build. Lenovo support is detected (maker check, then the firmware interface) and titled by brand: "Legion mode", "Lenovo mode", or "Power mode" for Windows' own. The personal/public markers are gone from all source. |
| 5 | Fan 1 and Fan 2 always drawn together. | A fan tile appears only once that fan has produced a reading. |
| 6 | Missing hardware simply produced no tile. | One quiet line says what this PC cannot report, and that some need the elevated agent. |
| 7 | NVIDIA, NV Panel and Steam shortcuts shown on every PC, merely dimmed. | The host reports which have their software installed; only those are drawn. |
| 8 | Suggestions were "pinned, then games A to Z". | Pinned, then what you actually launch or play (most recent first), then games, then the rest. A launch counter is kept per PC. A dead shortcut triggers a rescan. |
| 9 | Hold-to-end drew a 3 px bar under the fingertip. Four separate copies of the logic. | One shared control: 1.5 s hold, a panel on the half of the screen your finger is not on, a halo around the button, a "released too early" message, and the PC's real answer afterwards (ended, or why not). |
| 10 | "Could not be ended" for every kind of failure. | Distinct reasons: protected process, access denied (and what to do), already closed (a success), no longer listed. |
| 11 | Alerts description restated the title. Other hints were long. | Removed or cut to a few words. The stale "needs PresentMon" text replaced. |
| 12 | After switching audio device the tablet showed the old tick for several seconds: the host dropped its device list and re-read it, 3 to 4 s with a Bluetooth endpoint. | The default is corrected in place; the full re-read happens behind it. |
| 13 | Mic cast device listing ran inside the publish loop, so it stalled other sections and delayed the reply to arming by about 4 s. | Moved to the background. |
| 14 | A console window stayed on screen whenever the scheduled task started the agent, and closing it killed the agent. | Hidden when the agent owns its console. `--show-console` or any terminal run is untouched. |
| 15 | "laptop" in user-facing text. | "PC". |
| 16 | Docs described PresentMon and a personal/public split. | README, usage notes and export script updated. |

## Second round (2026-10-31, later)

| Found | Done |
|---|---|
| **History graphs drew nothing.** The one-minute view asked for half-second points but readings arrive once a second, so every other point was empty and the line broke at each gap. | Never finer than one second; the chart also bridges a missed sample or two. Checked on the tablet: real load and temperature lines. |
| Output card painted dotted noise; the Ambient button sat 4 px high. | Found on the tablet's Mali-T720 by switching CSS off live: `contain: paint` caused the noise, a missing compositing group the speckle. Button group is now a flex row. |
| CPU temperature, fans and AMD/Intel GPU temperature unavailable on most PCs. | LibreHardwareMonitor (needs the elevated agent and the user-installed PawnIO driver). CPU temperature now reads on this Legion (65 C). Fans are a list of any length, one tile each. Missing pieces are named on the tablet with their fix. |
| The media source list was built once at startup, so changing the default browser needed a restart. | Followed live (within about 30 s); a Brave registration resolves to Brave. |
| Steam games had no executable recorded and Epic's is often a launcher stub, so games were only recognised by "fullscreen and GPU above 45%". | Games are recognised by their install folder. FPS tracking now sticks to the game when the foreground flickers to Windows Search. |
| Calendar needed a manually exported file in a folder. | Subscribe by link (Paste from PC, or type it). https only, private addresses refused, 5 MB cap, address never sent to the tablet, refreshed every 30 min. Verified against a real public calendar over the cable. |
| Old Android WebView gave a black screen. | A plain message says to update Android System WebView. |
| Windows build check refused anything below 2004. | Builds 1809 to 2004 get a warning instead (untested). |
| A Lenovo mode switch was assumed to work. | Read back from the firmware; a model that ignores it is reported as not working. A second, read-only route to fans for newer Legion firmware is included but untested. |

**Real game test:** Light Dude Demo (Steam), launched from the tablet. 143 to 144 FPS, 1% low 107 to 120, session recorded
(average 143.8, 1% low 85, 0.1% low 35). This demo is capped to the 144 Hz display, so the figure alone does not
prove the counter on an uncapped game.

## Tested

**Real hardware (this Legion and tablet):**
- Functional audit now 132 of 132, including the second-round changes.
- FPS: 144.1 on this 144 Hz panel through both the DirectX path and the kernel path, in the elevated agent;
  an idle or dead target reads "waiting" and never a number; the trace stops about 6 s after it is not
  wanted; about 5% of one core while tracing. On the tablet: live frame-time trace, "144 fps", and a session
  recorded.
- Hold-to-confirm on the tablet: early release, full hold, and a real process ended (Notepad), confirmed in
  the process list and the host log.
- Default browser: Windows reports Edge here and the badge shows Edge.
- Output and input switching and restoring, soundboard play and stop, a recording saved as a valid WAV,
  mic cast arm and disarm with the real microphone restored, process close and kill, protected processes
  refused, unknown launch refused: **129 of 129** checks in `node tools/selftest-link.js --audit`.
- Start menu action opens.
- Agent idle: about 3% of a core, 121 MB, no trace session and no stray child process when nobody is watching.
- A clean export of the repository builds both halves and passes the self-test.

**Simulated:**
- A PC without Lenovo or NVIDIA: `COMPANIONDECK_GENERIC=1`. 109 of 109 link checks pass, and the capabilities
  agree with the readings (no fans, no CPU temperature, GPU through Windows counters, Windows power mode).
- Screen sizes 1280x800, 800x480, 667x375 and 568x320 in a browser: no horizontal overflow on any page.

## Not verified, or not supported

- **FPS in an actual game.** Verified on a DirectX program (a browser's GPU process), not a game. The
  OpenGL/Vulkan path was proven only by counting a DirectX program through the kernel event, not with a
  real OpenGL or Vulkan title. Frame generation (DLSS FG and similar) is not counted. UWP titles hosted
  by ApplicationFrameHost may need a closer look. Needs the elevated agent, or membership of
  "Performance Log Users".
- **Other vendors' performance profiles and fans** (ASUS, HP, Dell, MSI, Acer): not written. Writing firmware
  controls blind, for hardware nobody here owns, would be inventing them. They get Windows' power mode, and
  the extension point is `Telemetry/`.
- **AMD and Intel GPU temperature and power**: Windows publishes only load and memory; temperature, power
  and clocks come from NVIDIA's driver. AMD/Intel machines were not available.
- **CPU temperature**: needs an ACPI thermal zone and the elevated agent. This laptop has none.
- Other tablets and Android versions: only the 1024x552 Android 7 tablet was tried.
- Windows 10: only Windows 11 was tried.
- Clean install on another PC: the export builds, but `Setup.cmd` was not run on a machine without the
  toolchain.
- The Apps page "Dock": there is no separate dock in this app; the Running list is what was reviewed.

## Known issues

- `sessions.json` was left as an empty list with a byte-order mark by a cleanup of test data, so the agent
  logs one warning at its next start and carries on. It is rewritten on the first session.
- `--probe-actions` reports the foreground window unreliably on this Windows build (Start did open).
- The test-only `fps-debug` and `test-games.txt` files remain as developer hooks.

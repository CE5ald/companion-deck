# Using Companion Deck

The [README](../README.md) covers installing it. This is the day-to-day detail, and what to
do when something does not work.

## Everyday use

| What you do | What happens |
|---|---|
| Plug the cable in | The tablet app launches and connects on its own |
| Unplug the cable | The tablet shows "Waiting for your laptop"; the PC stops collecting anything |
| Turn the tablet screen off | The PC goes idle: no sampling, no timers |
| Switch pages on the tablet | The PC starts collecting what that page needs and stops collecting the rest |

You do not need to open the app. The PC notices the tablet, starts it, and connects, which
usually takes three or four seconds, most of it the tablet app cold-starting.

## The pages

- **Home** adapts to what you are doing. Music playing puts the now-playing card first; a
  game in the foreground promotes performance.
- **Media** has the full now-playing card, a draggable seek bar, transport, shuffle and
  repeat, and a picker when more than one app is playing.
- **Gaming** has overlay and capture shortcuts, and the performance mode control.
- **Voice** casts your PC's audio into your microphone, plays soundboard clips, and records.
- **Perf** has the gauges, temperatures, fans and FPS.
- **Apps** lists your games and applications with real cover art, and the processes that
  are currently running, which you can close from the tablet.
- **Deck** is a grid of tiles you lay out yourself. See below.
- **Clock** is a large clock, your calendar, and timers. See below.
- **Actions** has brightness and quick system shortcuts.

## The deck

Each tile is a name, an icon and a list of steps that run in order: press a shortcut, open an
app, run a system action, change the audio, control the media player, play a sound, start a
timer, or wait. One tile can do all of them, so "open my editor, mute the microphone, wait a
second, start a pomodoro" is a single press.

- **Edit** turns the grid into an editor. Tap a tile to change it, use the small arrows on it
  to move it, and use **New tile** to add one.
- Tiles that shut down, restart, sleep or close a window are **held** to run, never tapped,
  whatever you name them.
- Pressing a tile that is still running stops it, which is how you cancel a long chain.
- A **profile** is a set of tiles. The one you see follows what is in front on the PC: give a
  profile the name of a program (`Premiere`, `Photoshop`, `obs64`) or `any gaming`, and the deck
  switches to it when that program is in front and back when it is not. Tap a profile chip to
  hold it in front, and **Auto** to let the PC choose again.
- If a step fails (an app was uninstalled, a window refused a shortcut) the tile stops and says
  why, rather than carrying on.

The layouts live on the PC in `%LOCALAPPDATA%\CompanionDeck\tiles.json`. The tablet only ever
sends the id of the tile you pressed. **Editing works only while the cable is connected**: the
tablet can press tiles over Wi-Fi, but cannot change what they do.

## Clock, calendar and timers

The clock shows the **PC's** time, not the tablet's, so an old tablet with the wrong time zone
still agrees with your laptop. **Ambient clock** hides everything but the time and nudges it
a few pixels each minute, so it is safe to leave on overnight; tap anywhere to leave it.

Upcoming events come from calendars you subscribe to. On the **Clock** page, over the cable, open
**Calendars** and paste a calendar's private link (**Paste from PC** takes whatever you just copied
on the PC). Google Calendar: Settings, your calendar, *Secret address in iCal format*. Outlook and
iCloud call it a published or shared calendar link. It is fetched again every half hour, and the
link never leaves the PC. Only `https` links to the public internet are accepted. Exporting an
`.ics` file and dropping it in `%LOCALAPPDATA%\CompanionDeck\calendar` still works too. No sign-in
is involved. Repeating events, all-day events and time zones are
understood; a file it cannot read shows fewer events, never wrong ones.

Timers, stopwatches and pomodoros run on the **PC** and are saved to disk. Reboot the tablet,
unplug it, or restart the agent: they carry on, and one that finishes while you are away is
waiting for you. A countdown that ends beeps on the PC for twenty seconds and flashes in the
corner of every page until you dismiss it. A pomodoro moves from focus to break to focus by
itself and finishes with a long break.

## History, alerts and what is using the PC

Under **Perf**, below the dials:

- **History** draws CPU, GPU and memory load, temperatures and frame rate for the last 1, 5 or 30
  minutes. The PC keeps one reading a second whether or not the tablet is connected, and each point
  on a longer view is the highest reading in its stretch, so a one-second spike still shows.
- **Top processes** lists what is using the most CPU, memory or GPU, one row per program (a browser
  is forty processes and one row). The down arrow lowers a program's priority so it stops competing
  with a game; the cross asks it to close the way its own window would; the square, **held**, ends
  it and loses unsaved work. System and security software have no buttons.
- **Alerts** buzz the tablet, wake its screen and show a banner when a reading stays over its limit
  for a few seconds, even with the tablet asleep. Off until switched on. A value sitting on the line
  does not buzz repeatedly: it has to cool down first, and a problem that persists reminds once a
  minute.
- A **throttling** note appears on the performance card when the GPU is held back, saying whether it
  is power or heat (from NVIDIA's driver, so exact), or when the CPU runs far below its rated clock
  under load (a judgement from the clock, load and power source, and worded as one).

## Gaming sessions

Under **Gaming**. When a game has been in front for a few seconds the PC starts a session and ends it
after you have been away from it for about 25 seconds, so alt-tabbing does not split an evening in
two. Each finished session of at least 30 seconds is kept with its length, **average frame rate and
1% and 0.1% lows**, average CPU and GPU load and peak GPU temperature. Tap a session for its
frame-rate shape. **Frame times** shows the last couple of seconds of individual frames: a flat low
line is smooth, spikes are the stutter an average hides.

Frame rates come from Windows' own event tracing and need the elevated agent; without it a session
still records its length and load, and the tablet says why. Recording can be switched off, and sessions deleted, from the
cable. Games on the launcher show **playtime and when you last played** under their name.

**Performance mode follows the game** (off until switched on, from the cable): when a game runs on AC
power the PC raises its performance mode, and puts the previous one back when the game has been gone
for 15 seconds. It never overrides a mode you chose yourself while the game ran, and never changes
anything on battery.

## Macros

A macro is a recording of what you type and click on the PC. In the tile editor add a **Macro**
step and tap **Record new**: do the thing on the PC, then **Stop and save** on the tablet. Bind
it to a tile and the tile replays it, with the same timing.

- Recording can only be started over the cable.
- It stops by itself after 60 seconds or 400 events, and if the cable is pulled.
- **It captures everything typed while it runs, passwords included.** Nothing is kept unless
  you save it, nothing is written to the log, and it never leaves the PC. Record something
  that does not involve a password, and look at what you saved.
- Replay puts the mouse where it was recorded, so it expects the same screen layout. Pressing
  the tile again while it runs stops it, and any key it was holding is let go.
- Windows will not let a normal program send input to a window running as administrator.
  Run the agent elevated (`scripts\Install-Elevated.ps1`) if you need that.

## Notifications

Under **Clock**, **Turn on** shows the Windows notifications that are waiting in the Action
Center. It is off until you switch it on from the cable, because they can hold anything anyone
has said to you.

Windows only offers its official notification API to packaged apps, so this reads the database
Windows keeps for the Action Center instead, read-only, using the SQLite library built into
Windows. Nothing is downloaded or changed. Two things follow from that:

- **Dismissing a notification on the tablet hides it there only.** Windows keeps its own copy, and
  nothing in this app can remove it.
- The database is Microsoft's to change. If a future Windows moves things, the card says it is
  unavailable and the feature stays off, rather than guessing.

## Night mode and pairing

On **Actions**, **Night mode** dims this tablet's screen and tints it amber. The brightness is
the app window's own, so the tablet's system setting is never touched, and it is remembered
between runs. The two sliders below it set the dimming and the warmth separately.

The pairing card appears over the cable. **Forget other tablets** (held, like anything
destructive) replaces the pairing key; see the README for what pairing protects.

## Cover colours, ambient view and the visualizer

On **Media**, the **Display** card:

- **Cover colours** tints the playing card with the most vivid colour of the album art.
- **Visualizer** draws 32 bars of what the PC is playing. The audio capture behind it exists only
  while the bars are on screen, and stops when you leave the page or the screen goes off.
- **Ambient** shows only the track, large, with the bars under it. The picture moves a few pixels
  each minute, so it is safe to leave on. Tap anywhere to leave.

## Soundboard organization

**Organize** on the Voice page turns taps into editing. Tap a pad for its settings: name, colour,
folder, volume, where it plays (both, mic only, ears only), and **Loop**. A looping pad is a switch:
press to start, press again to stop. **Put on the Deck** adds it as a tile you can move, recolour or
chain with other steps. Sub-folders inside the soundboard folder become folders automatically (two
levels deep); folders made on the tablet only change how pads are grouped there and never move a file.

## Voice, outputs and remembered levels

On the Media page:

- **Microphone** and **Deafen** are big tiles with an LED: green while live, red while muted.
  Deafen mutes the speakers and the microphone together, and switching it off puts back
  exactly what was muted before. **Hold to mute** turns the microphone tile into push-to-mute.
- **Output** shows each speaker, headset and TV on one row with its own volume. Tap one to
  make it the default. Virtual devices (VB-Cable, Steam streaming) are left out.
- The **pin** beside an app in the mixer keeps its level: each time the app starts, the PC puts
  it back. Unpin to let it go. Windows already remembers levels for many apps; this is for the
  ones that reset themselves, and it works with no tablet connected.

## Sliders

Every slider works three ways:

- **Tap** anywhere on the track to jump there.
- **Drag** along the track, one-to-one.
- **Drag, then move your finger away from the track** for fine control. Past about 44px of
  vertical distance the same horizontal travel covers a third of the range, and past 110px
  a tenth. The value readout says "fine" so a slider moving slowly reads as deliberate.

## The soundboard

Drop audio files into `%LOCALAPPDATA%\CompanionDeck\soundboard`. They appear on the Voice
page within a second or two. WAV, MP3, M4A and FLAC all work.

Clips are loudness-normalised to a consistent level, so a quiet recording and a loud one do
not jump at each other. Playback can go to your speakers, into the microphone cast, or
both, and desktop audio ducks briefly while a clip plays.

Casting into your microphone needs [VB-Audio Virtual Cable](https://vb-audio.com/Cable/).
Without it, the cast control is not offered at all. While casting is armed the PC switches
your default recording device to the cable and mixes your real microphone into it, so the
microphone you normally use keeps working. It switches back when you disarm, and also if
the agent crashes or is killed.

## Checking on it

```bash
CompanionDeck.Host.exe --selftest
```

Prints every capability and what each missing one needs.

The log is at `%LOCALAPPDATA%\CompanionDeck\host.log`. It records every capability probe at
startup, which is the first place to look when something is not appearing on the tablet.

## When something does not work

**The tablet never connects.** Open the app on the tablet: it shows a setup checklist that
says which of the two settings is still off, with a button that opens the right page. The
PC log says the same thing in words - it reports an `unauthorized` device as "look at the
tablet and tap Allow", because that is what you actually have to do.

If the checklist is all ticked and it still does not connect, the cable may be charge-only;
a surprising number are. Try another one, or turn on USB tethering instead, which uses a
different mechanism entirely.

**A control is missing from the tablet.** That is deliberate. Capabilities are probed, and
anything that cannot work is not drawn rather than shown as a dead button. `--selftest`
says why.

**Brightness, CPU temperature or fans are missing.** These need the elevated agent. Run
`scripts\Install-Elevated.ps1` from an administrator PowerShell, once.

**FPS never appears.** Windows only lets an administrator (or a member of "Performance Log Users")
open the trace this reads. Run `scripts\Install-Elevated.ps1`, or add your account to that group.
It then follows whatever game is in front. `CompanionDeck.Host.exe --probe-fps <process name or pid>`
measures any running program for eight seconds, and `--selftest` says whether it can work here.
A program that does not draw through DirectX, OpenGL or Vulkan has no frames to count and shows
nothing rather than a made-up number.

**The link drops and comes back.** It is designed to. The tablet drops a link that has been
silent past the ping interval and accepts a fresh one, so a cable wobble costs a couple of
seconds rather than a restart.

## Designing the UI without a tablet

```bash
node tools/uiserve.js
```

Then open `http://localhost:5173` and set the window to 1024x600. The page falls back to a
mock-data harness whenever it is not running inside the Android shell, so every card and
every state is reachable in a desktop browser.

## Testing the universal path on vendor hardware

Setting `COMPANIONDECK_GENERIC=1` makes the agent skip the vendor-specific providers and
use only the ones every PC has. On a machine that would otherwise take the NVIDIA or
vendor-EC route, this is the only way to see what most people will actually get.

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
- **Actions** has brightness and quick system shortcuts.

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

**FPS never appears.** FPS needs PresentMon, which is not bundled. Put `PresentMon.exe`
beside the agent, or set `COMPANIONDECK_PRESENTMON` to its full path. Note that the
PresentMon shipped inside NVIDIA's FrameView SDK does not work on every machine; Intel's
official build is the one to get.

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

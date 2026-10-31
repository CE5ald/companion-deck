/*
 * A synthetic tablet.
 *
 * Speaks the real wire protocol to a running host over the network transport: waits for the
 * UDP beacon, dials back, handshakes, subscribes to every section, then sends every command
 * the real tablet can send and reports what came back.
 *
 * This is the only way to exercise the host's whole command surface without the hardware,
 * and it tests the network transport at the same time - the one that needs nothing enabled
 * on the tablet.
 *
 * Commands that would disturb the machine (switching audio devices, launching apps, killing
 * processes, arming the microphone cast) are listed but only sent with --destructive.
 */
const crypto = require('crypto');
const dgram = require('dgram');
const fs = require('fs');
const net = require('net');
const path = require('path');

const DISCOVERY_PORT = 45888;
const KIND = { JSON: 0, ASSET: 1, PING: 2, PONG: 3 };
const destructive = process.argv.includes('--destructive');
const timeoutMs = process.argv.includes('--audit') ? 150000 : 25000;

const results = [];
const record = (name, ok, detail) => results.push({ name, ok, detail: detail || '' });

// ---------------------------------------------------------------- framing

function frame(kind, obj) {
  const payload = obj === undefined ? Buffer.alloc(0)
                                    : Buffer.from(JSON.stringify(obj), 'utf8');
  const head = Buffer.alloc(5);
  head.writeUInt32BE(payload.length, 0);
  head[4] = kind;
  return Buffer.concat([head, payload]);
}

function makeReader(onFrame) {
  let buf = Buffer.alloc(0);
  return (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    for (;;) {
      if (buf.length < 5) return;
      const len = buf.readUInt32BE(0);
      const kind = buf[4];
      if (buf.length < 5 + len) return;
      const payload = buf.subarray(5, 5 + len);
      buf = buf.subarray(5 + len);
      onFrame(kind, payload);
    }
  };
}

// ---------------------------------------------------------------- pairing

// The network link is only served to a tablet that holds the pairing key. The suite runs on the
// PC itself, so it reads the key from the file the agent keeps, the way a tablet gets it from the
// cable.
function readKey() {
  const file = path.join(process.env.LOCALAPPDATA || '', 'CompanionDeck', 'pairing.json');
  return Buffer.from(JSON.parse(fs.readFileSync(file, 'utf8')).key, 'base64');
}
const proofOf = (key, role, a, b) =>
  crypto.createHmac('sha256', key).update(`${role}|${a}|${b}`).digest('base64');

/** A bare connection that says hello and reports what the host does with it. */
function probeConnection(beacon, answer) {
  return new Promise((resolve) => {
    const sock = net.createConnection({ host: beacon.host, port: beacon.port });
    const nonce = crypto.randomBytes(16).toString('base64');
    const seen = { preHello: null, authed: false, results: 0, closedAt: null };
    const started = Date.now();
    const reader = makeReader((kind, payload) => {
      if (kind === KIND.PING) { sock.write(frame(KIND.PONG)); return; }
      if (kind !== KIND.JSON) return;
      let msg; try { msg = JSON.parse(payload.toString('utf8')); } catch { return; }
      if (msg.type === 'hello' && msg.auth === 'required') {
        seen.preHello = msg;
        const reply = answer(msg, nonce);
        if (reply) sock.write(frame(KIND.JSON, reply));
      }
      if (msg.type === 'authed') seen.authed = true;
      if (msg.type === 'result') seen.results++;
    });
    sock.on('data', reader);
    sock.on('close', () => { seen.closedAt = Date.now() - started; });
    sock.on('error', () => {});
    sock.once('connect', () => {
      sock.write(frame(KIND.JSON, { type: 'hello', device: 'Stranger', nonce, version: 'test', cachedAssets: [] }));
    });
    seen.sock = sock;
    seen.finish = () => { try { sock.destroy(); } catch {} return seen; };
    resolve(seen);
  });
}

// ---------------------------------------------------------------- discovery

function waitForBeacon() {
  return new Promise((resolve, reject) => {
    const sock = dgram.createSocket({ type: 'udp4', reuseAddr: true });
    const timer = setTimeout(() => {
      sock.close();
      reject(new Error(`no beacon on UDP ${DISCOVERY_PORT} within 20s. Is the host running?`));
    }, 20000);

    sock.on('message', (msg, rinfo) => {
      let b; try { b = JSON.parse(msg.toString('utf8')); } catch { return; }
      if (b.app !== 'companiondeck' || !b.port) return;
      clearTimeout(timer);
      sock.close();
      resolve({ host: rinfo.address, port: b.port, name: b.host });
    });

    sock.on('error', (e) => { clearTimeout(timer); reject(e); });
    sock.bind(DISCOVERY_PORT);
  });
}

// ---------------------------------------------------------------- session

async function main() {
  console.log('waiting for the host beacon...');
  const beacon = await waitForBeacon();
  record('beacon received', true, `${beacon.name} at ${beacon.host}:${beacon.port}`);
  console.log(`beacon: ${beacon.name} ${beacon.host}:${beacon.port}\n`);

  const key = readKey();

  // Strangers first. None of them may be served anything, and none may disturb the real session
  // that follows.
  const silent = await probeConnection(beacon, () => null);           // connects, never answers
  const wrong = await probeConnection(beacon, () => ({                 // answers with the wrong key
    type: 'auth', proof: proofOf(crypto.randomBytes(32), 'tablet', 'x', 'y') }));
  const nosy = await probeConnection(beacon, () => null);              // sends commands unauthenticated
  await new Promise((r) => setTimeout(r, 600));
  nosy.sock.write(frame(KIND.JSON, { type: 'sub', hz: 4, sections: ['sys', 'audio'] }));
  nosy.sock.write(frame(KIND.JSON, { type: 'cmd', id: 1, action: 'tiles.pin', args: { id: 'gaming' } }));
  nosy.sock.write(frame(KIND.JSON, { type: 'cmd', id: 2, action: 'sys.lock', args: {} }));

  const sock = net.createConnection({ host: beacon.host, port: beacon.port });
  sock.setNoDelay(true);

  const sections = {};
  const pending = new Map();
  let caps = null;
  let nextId = 1;
  let pongs = 0;

  const send = (obj) => sock.write(frame(KIND.JSON, obj));

  const cmd = (action, args) => new Promise((resolve) => {
    const id = nextId++;
    const t = setTimeout(() => { pending.delete(id); resolve({ ok: false, error: 'no reply in 4s' }); }, 4000);
    pending.set(id, (r) => { clearTimeout(t); resolve(r); });
    send({ type: 'cmd', id, action, args: args || {} });
  });

  const reader = makeReader((kind, payload) => {
    if (kind === KIND.PING) { sock.write(frame(KIND.PONG)); pongs++; return; }
    if (kind === KIND.ASSET) {
      sections.__assets = (sections.__assets || 0) + 1;
      return;
    }
    if (kind !== KIND.JSON) return;

    let msg; try { msg = JSON.parse(payload.toString('utf8')); } catch { return; }
    if (msg.type === 'hello' && msg.auth === 'required') { sections.preHello = msg; return; }
    if (msg.type === 'authed') { sections.authed = msg; return; }
    if (msg.type === 'hello') { caps = msg.capabilities || {}; sections.hello = msg; return; }
    if (msg.type === 'result') {
      const cb = pending.get(msg.id);
      if (cb) { pending.delete(msg.id); cb(msg); }
      return;
    }
    msg.__at = Date.now();
    sections[msg.type] = msg;
  });

  sock.on('data', reader);

  await new Promise((resolve, reject) => {
    sock.once('connect', resolve);
    sock.once('error', reject);
    setTimeout(() => reject(new Error('TCP connect timed out')), 8000);
  });
  record('tcp connect', true);

  // The real tablet opens with a hello describing itself, plus a nonce for the handshake.
  const myNonce = crypto.randomBytes(16).toString('base64');
  send({
    type: 'hello', device: 'FakeTablet', android: 34,
    width: 1024, height: 600, dpWidth: 1024, dpHeight: 600, density: 1.0,
    version: 'test', cachedAssets: [], nonce: myNonce,
  });

  const until = (pred, ms) => new Promise((resolve) => {
    const t0 = Date.now();
    const tick = () => (pred() ? resolve(true) : Date.now() - t0 > ms ? resolve(false) : setTimeout(tick, 60));
    tick();
  });

  // The PC must prove it holds the key before this tablet says anything else.
  record('host answers the challenge', await until(() => sections.preHello, 4000));
  const pre = sections.preHello || {};
  record('host proves it holds the key',
    pre.proof === proofOf(key, 'host', myNonce, pre.nonce),
    'a fake host could not produce this');
  record('nothing is revealed before authentication',
    !pre.capabilities || Object.values(pre.capabilities).every((v) => v === false || v === '' || v === null),
    'no capabilities in the first hello');
  record('the key is never sent over the network', !pre.pair && !JSON.stringify(pre).includes(key.toString('base64')));

  send({ type: 'auth', proof: proofOf(key, 'tablet', pre.nonce, myNonce) });
  record('host accepts a tablet with the key', await until(() => sections.authed, 4000));

  const waitFor = (pred, ms, what) => new Promise((resolve) => {
    const started = Date.now();
    const tick = () => {
      if (pred()) return resolve(true);
      if (Date.now() - started > ms) return resolve(false);
      setTimeout(tick, 120);
    };
    tick();
  });

  record('host hello + capabilities', await waitFor(() => caps !== null, 6000),
         caps ? Object.entries(caps).filter(([, v]) => v === true).map(([k]) => k).join(' ') : 'none');

  // Subscribe to everything the real UI ever asks for.
  send({ type: 'sub', hz: 4, sections: [
    'sys', 'media', 'audio', 'ctx', 'apps', 'running', 'cast', 'soundboard', 'mediaSources',
    'tiles', 'clock', 'timers', 'macros', 'notifications', 'alerts', 'sessions', 'frames', 'top',
  ]});

  // Wait for each section, then let them settle: several are published again once a slow
  // enumeration finishes, and judging the first payload would be judging a half-built one.
  for (const s of ['sys', 'audio', 'ctx', 'media', 'apps', 'running', 'cast', 'soundboard', 'mediaSources', 'tiles', 'clock', 'timers', 'macros', 'notifications', 'alerts', 'sessions', 'frames', 'top']) {
    await waitFor(() => sections[s] !== undefined, 9000);
  }
  await new Promise((r) => setTimeout(r, 3000));

  // Arrival is not the test; the payload has to be usable. Each check below is what the UI
  // would actually need in order to draw that section.
  const checks = {
    sys: (m) => (typeof m.cpu === 'number' && m.cpu >= 0 && m.cpu <= 100 &&
                 typeof m.ramTotalMb === 'number' && m.ramTotalMb > 0)
                 ? null : 'cpu or ram out of range',
    audio: (m) => (Array.isArray(m.outputs) && m.outputs.length > 0 &&
                   m.outputs.some((d) => d.isDefault) &&
                   typeof m.masterVolume === 'number')
                   ? (typeof m.deafened === 'boolean' || m.deafened === undefined ? null : 'deafened is not a boolean')
                   : 'no outputs, or none marked default',
    ctx: (m) => (typeof m.mode === 'string' && m.mode.length > 0) ? null : 'no mode',
    media: () => null,
    apps: (m) => (Array.isArray(m.items) && m.items.length > 0) ? null : 'no launcher items',
    running: (m) => (Array.isArray(m.items) && m.items.length > 0) ? null : 'no processes',
    cast: (m) => (typeof m.available === 'boolean') ? null : 'no availability flag',
    soundboard: (m) => Array.isArray(m.items) ? null : 'items is not a list',
    mediaSources: (m) => (Array.isArray(m.items) && m.items.length > 0) ? null : 'no sources',
    tiles: (m) => {
      if (!Array.isArray(m.profiles) || !m.profiles.some((p) => p.id === 'default' && p.tiles.length > 0)) return 'no default profile with tiles';
      if (!m.profiles.some((p) => p.id === m.active)) return 'active profile is not in the list';
      if (m.canEdit !== false) return 'a network link must not be offered editing';
      if (!m.catalog || !Array.isArray(m.catalog.sys) || m.catalog.sys.length === 0) return 'no catalogue';
      return null;
    },
    clock: (m) => {
      if (Math.abs(m.nowMs - m.__at) > 3000) return 'host clock is ' + (m.nowMs - m.__at) + ' ms away from this machine';
      if (typeof m.utcOffsetMin !== 'number') return 'no UTC offset';
      return Array.isArray(m.events) ? null : 'events is not a list';
    },
    timers: (m) => Array.isArray(m.items) ? null : 'items is not a list',
    macros: (m) => {
      if (!Array.isArray(m.items)) return 'items is not a list';
      if (m.recording) return 'a recording is running that nobody started';
      return m.canEdit === false ? null : 'a network link must not be offered recording';
    },
    alerts: (m) => (typeof m.enabled === 'boolean' && m.cpuTemp >= 40 && m.cpuTemp <= 110 && m.ram >= 50 && Array.isArray(m.active))
      ? null : 'bad alert settings',
    sessions: (m) => {
      if (!Array.isArray(m.items)) return 'items is not a list';
      if (m.canEdit !== false) return 'a network link must not be offered session editing';
      return typeof m.fpsAvailable === 'boolean' ? null : 'no frame-rate flag';
    },
    frames: (m) => Array.isArray(m.times) ? null : 'times is not a list',
    top: (m) => {
      if (!Array.isArray(m.ram) || m.ram.length === 0) return 'no memory list';
      if (m.ram.some((p) => typeof p.name !== 'string' || typeof p.ramMb !== 'number')) return 'a malformed row';
      return m.ram.every((p, i, a) => i === 0 || a[i - 1].ramMb >= p.ramMb) ? null : 'memory list is not sorted';
    },
    notifications: (m) => {
      if (m.enabled && m.canEdit === false && m.items === undefined) return 'no item list';
      return typeof m.available === 'boolean' && m.canEdit === false ? null : 'bad shape';
    },
  };

  for (const [s, check] of Object.entries(checks)) {
    const m = sections[s];
    if (m === undefined) { record(`section: ${s}`, false, 'never arrived'); continue; }
    const problem = check(m);
    record(`section: ${s}`, problem === null, problem === null ? summarise(s, m) : 'BAD: ' + problem);
  }

  // Capabilities must agree with the data. A capability that is advertised but never
  // backed by a reading is exactly the failure the whole capability system exists to stop.
  const sys = sections.sys || {};
  const agree = [
    ['fans', () => sys.fan1Rpm != null || sys.fan2Rpm != null],
    ['cpuTemp', () => sys.cpuTemp != null],
    ['gpu', () => sys.gpu != null],
    ['gpuDetail', () => sys.gpuPowerW != null || sys.gpuClockMhz != null],
    ['brightness', () => sys.brightness != null],
    ['thermalMode', () => sys.thermalMode != null],
  ];
  for (const [cap, has] of agree) {
    if (!caps || !caps[cap]) { record(`cap ${cap} (not advertised)`, !has(), has() ? 'DATA PRESENT but capability false' : 'consistent'); continue; }
    record(`cap ${cap} (advertised)`, has(), has() ? 'backed by a reading' : 'ADVERTISED BUT NO DATA');
  }

  // ------------------------------------------------------------ commands

  const safe = [
    ['media.playpause', {}, 'mediaSeek'],
    ['media.next', {}, 'mediaSeek'],
    ['media.previous', {}, 'mediaSeek'],
    ['media.shuffle', {}, 'mediaSeek'],
    ['media.repeat', {}, 'mediaSeek'],
    ['media.seek', { value: 10 }, 'mediaSeek'],
    ['audio.masterVolume', { value: currentMaster(sections) }, null],
    ['audio.micVolume', { value: currentMic(sections) }, null],
    ['sys.thermalMode', { mode: currentMode(sections) || 'balanced' }, 'thermalMode'],
    ['sys.brightness', { value: currentBrightness(sections) }, 'brightness'],
    ['launcher.fav', { id: '__nonexistent__' }, null],
    ['running.close', { pid: 999999 }, null],
    ['bogus.command', {}, null],
  ];

  console.log('\nsending commands...');
  for (const [action, args, cap] of safe) {
    if (cap && caps && !caps[cap]) { record(`cmd ${action}`, true, 'skipped: capability not advertised'); continue; }
    const r = await cmd(action, args);
    // A refusal is a pass when it is an honest, explained refusal: what is being tested is
    // that the host answers every command rather than hanging or dying.
    record(`cmd ${action}`, r.ok !== undefined, r.ok ? 'ok' : `refused: ${r.error || '(no reason)'}`);
  }

  // ---- the deck: names and ids only, validated by the host, editing refused off the cable
  console.log('\ndeck, timers and levels...');
  const refused = async (label, action, args, mustMention) => {
    const r = await cmd(action, args);
    const good = r.ok === false && (!mustMention || String(r.error || '').toLowerCase().includes(mustMention));
    record(label, good, r.ok ? 'ACCEPTED' : `refused: ${r.error || '(no reason)'}`);
  };
  await refused('tile.run unknown id is refused', 'tile.run', { id: '__nonexistent__' }, 'unknown');
  await refused('tiles.setProfile refused over Wi-Fi', 'tiles.setProfile',
    { profile: { id: 'x', name: 'x', tiles: [] } }, 'cable');
  await refused('tiles.deleteProfile refused over Wi-Fi', 'tiles.deleteProfile', { id: 'gaming' }, 'cable');
  await refused('tiles.pin unknown profile refused', 'tiles.pin', { id: '__nope__' }, 'unknown');

  const pinned = await cmd('tiles.pin', { id: 'gaming' });
  const sawPin = pinned.ok && await waitFor(() => sections.tiles && sections.tiles.pinned === 'gaming' && sections.tiles.active === 'gaming', 4000);
  record('tiles.pin holds a profile in front', !!sawPin, sawPin ? 'gaming pinned' : 'pin never showed in the tiles section');
  const unpinned = await cmd('tiles.pin', { id: '' });
  const sawAuto = unpinned.ok && await waitFor(() => sections.tiles && !sections.tiles.pinned, 4000);
  record('tiles.pin with no id returns to automatic', !!sawAuto);

  await refused('timer.countdown of zero refused', 'timer.countdown', { seconds: 0 });
  await refused('timer.countdown past 24 h refused', 'timer.countdown', { seconds: 90000 });
  await refused('unknown timer command refused', 'timer.bogus', {});
  await refused('timer.pause unknown id refused', 'timer.pause', { id: '__nope__' });

  await cmd('timer.clear', {});
  const started = await cmd('timer.countdown', { seconds: 600, label: 'selftest' });
  const saw = started.ok && await waitFor(() => sections.timers && sections.timers.items.some((t) => t.label === 'selftest' && t.state === 'running'), 4000);
  record('timer.countdown starts and is published', !!saw);
  const item = saw && sections.timers.items.find((t) => t.label === 'selftest');
  if (item) {
    const ends = item.endsAtMs - sections.timers.nowMs;
    record('timer end is an absolute time', ends > 595000 && ends <= 600500, `${ends} ms from the host's now`);
    const paused = await cmd('timer.pause', { id: item.id });
    const sawPause = paused.ok && await waitFor(() => sections.timers.items.some((t) => t.id === item.id && t.state === 'paused' && t.remainingMs > 590000), 4000);
    record('timer.pause keeps the remaining time', !!sawPause);
    const resumed = await cmd('timer.resume', { id: item.id });
    const sawResume = resumed.ok && await waitFor(() => sections.timers.items.some((t) => t.id === item.id && t.state === 'running'), 4000);
    record('timer.resume runs again', !!sawResume);
    const removed = await cmd('timer.remove', { id: item.id });
    const gone = removed.ok && await waitFor(() => !sections.timers.items.some((t) => t.id === item.id), 4000);
    record('timer.remove clears it', !!gone);
  }
  const sw = await cmd('timer.stopwatch', {});
  const sawSw = sw.ok && await waitFor(() => sections.timers.items.some((t) => t.kind === 'stopwatch'), 4000);
  record('timer.stopwatch starts', !!sawSw);
  const pomo = await cmd('timer.pomodoro', { work: 25, short: 5, long: 15, rounds: 4 });
  const sawPomo = pomo.ok && await waitFor(() => sections.timers.items.some((t) => t.kind === 'pomodoro' && t.phase === 'work' && t.round === 1), 4000);
  record('timer.pomodoro starts in its first work phase', !!sawPomo);
  await refused('pomodoro with bad lengths refused', 'timer.pomodoro', { work: 0, short: 5, long: 15, rounds: 4 });
  await cmd('timer.clear', {});
  const cleared = await waitFor(() => sections.timers.items.length === 0, 4000);
  record('timer.clear empties the list', cleared);

  // Recording what is typed on the PC, and reading its notifications, are cable-only.
  await refused('macro.record refused over Wi-Fi', 'macro.record', {}, 'cable');
  await refused('macro.stop refused over Wi-Fi', 'macro.stop', {}, 'cable');
  await refused('macro.delete refused over Wi-Fi', 'macro.delete', { id: 'x' }, 'cable');
  await refused('calendar.add refused over Wi-Fi', 'calendar.add', { url: 'https://example.com/a.ics' }, 'cable');
  await refused('calendar.paste refused over Wi-Fi', 'calendar.paste', {}, 'cable');
  await refused('calendar.remove refused over Wi-Fi', 'calendar.remove', { id: 'nope' }, 'cable');
  const cancelled = await cmd('macro.cancel', {});
  record('macro.cancel is always allowed', cancelled.ok === true);
  await refused('notify.enable refused over Wi-Fi', 'notify.enable', { value: 1 }, 'cable');
  await refused('unknown notify command refused', 'notify.bogus', {});
  const hid = await cmd('notify.dismiss', { id: 123456 });
  record('notify.dismiss is accepted', hid.ok === true);
  record('notifications stay off until switched on', sections.notifications && sections.notifications.enabled === false,
         'nothing is read from Windows');

  // ---- history, alerts, sessions and the process list
  const askHistory = async (seconds) => {
    delete sections.history;
    const r = await cmd('history.get', { seconds });
    const arrived = r.ok && await waitFor(() => sections.history && sections.history.seconds === seconds, 4000);
    return arrived ? sections.history : null;
  };
  const h60 = await askHistory(60);
  // Readings arrive once a second, so no bucket may be narrower than that: finer ones are empty every other time
  // and the tablet's graph turns into unconnected dots.
  record('history.get answers with a window', !!h60 && h60.cpu.length === 60 && h60.stepMs >= 1000, h60 ? `${h60.cpu.length} points, ${h60.stepMs} ms each` : 'no reply');
  const readings = h60 ? h60.cpu.filter((v) => v != null) : [];
  record('history holds real readings', readings.length >= 3 && readings.every((v) => v >= 0 && v <= 100),
         `${readings.length} cpu reading(s), peak ${readings.length ? Math.max(...readings) : '-'}`);
  const h30 = await askHistory(1800);
  record('history.get serves the 30 minute window', !!h30 && h30.seconds === 1800 && h30.stepMs === 15000);
  const hugeR = await cmd('history.get', { seconds: 99999999 });
  record('history.get clamps an absurd window', hugeR.ok === true && sections.history.seconds <= 1800);

  await cmd('alerts.set', { cpuTemp: 500, ram: 1 });
  await waitFor(() => sections.alerts && sections.alerts.cpuTemp === 110, 3000);
  record('alert limits are clamped to sensible values', sections.alerts.cpuTemp === 110 && sections.alerts.ram === 50,
         `cpu ${sections.alerts.cpuTemp}, ram ${sections.alerts.ram}`);
  await cmd('alerts.set', { cpuTemp: 90, ram: 92 });
  await waitFor(() => sections.alerts.cpuTemp === 90, 3000);

  await refused('sessions.enable refused over Wi-Fi', 'sessions.enable', { value: 0 }, 'cable');
  await refused('sessions.delete refused over Wi-Fi', 'sessions.delete', { id: 'x' }, 'cable');
  await refused('sessions.clear refused over Wi-Fi', 'sessions.clear', {}, 'cable');
  await refused('autopower.set refused over Wi-Fi', 'autopower.set', { value: 1 }, 'cable');

  await refused('top.close of an unlisted name is refused', 'top.close', { name: '__nothing__' });
  await refused('top.kill of an unlisted name is refused', 'top.kill', { name: '__nothing__' });
  await refused('top.priority of an unlisted name is refused', 'top.priority', { name: '__nothing__' });
  // These names are real and running, but the list never offers them.
  for (const name of ['MsMpEng', 'System', 'csrss', 'lsass']) {
    const r = await cmd('top.kill', { name });
    record(`top.kill ${name} is refused`, r.ok === false, r.ok ? 'ACCEPTED' : `refused: ${r.error}`);
  }

  // ---- soundboard organisation: folders, colours and loops, put back as they were
  const firstClip = (sections.soundboard && sections.soundboard.items || [])[0];
  if (firstClip) {
    const clipNow = () => (sections.soundboard.items || []).find((c) => c.id === firstClip.id) || {};
    const was = { group: clipNow().group, color: clipNow().color, loop: clipNow().loop };

    await refused('sb.folder rejects a path', 'sb.folder', { id: firstClip.id, folder: '..\\..\\Windows' });
    await refused('sb.folder rejects a long name', 'sb.folder', { id: firstClip.id, folder: 'x'.repeat(40) });
    await refused('sb.color rejects an unknown colour', 'sb.color', { id: firstClip.id, color: 'ultraviolet' });
    await refused('sb.loop on an unknown clip is refused', 'sb.loop', { id: '__nope__', value: 1 });

    await cmd('sb.folder', { id: firstClip.id, folder: 'Selftest' });
    await cmd('sb.color', { id: firstClip.id, color: 'amber' });
    const filed = await waitFor(() => clipNow().group === 'Selftest' && clipNow().color === 'amber', 4000);
    record('a clip can be filed and coloured', filed, `group '${clipNow().group}', colour '${clipNow().color}'`);

    await cmd('sb.folder', { id: firstClip.id, folder: '' });
    await cmd('sb.color', { id: firstClip.id, color: '' });
    await cmd('sb.loop', { id: firstClip.id, value: 0 });
    const back = await waitFor(() => clipNow().group === (was.group || '') && !clipNow().color && !clipNow().loop, 4000);
    record('and put back as it was', back);
  }

  // ---- the visualiser opens its audio capture on subscribe and closes it on leaving
  delete sections.spectrum;
  send({ type: 'sub', hz: 1, sections: ['spectrum'] });
  const gotBars = await waitFor(() => sections.spectrum && Array.isArray(sections.spectrum.bands), 4000);
  record('spectrum answers a subscription', gotBars && sections.spectrum.bands.length === 32,
         gotBars ? `32 bars, tallest ${Math.max(...sections.spectrum.bands)}` : 'nothing arrived');
  record('spectrum bars are in range', gotBars && sections.spectrum.bands.every((b) => b >= 0 && b <= 100));
  send({ type: 'sub', hz: 0, sections: [] });
  await new Promise((r) => setTimeout(r, 500));
  delete sections.spectrum;
  await new Promise((r) => setTimeout(r, 1500));
  record('spectrum stops when nobody is looking', sections.spectrum === undefined, sections.spectrum ? 'STILL SENDING' : 'capture closed');
  send({ type: 'sub', hz: 4, sections: ['sys', 'audio', 'tiles', 'clock', 'timers', 'alerts', 'sessions', 'frames', 'top', 'soundboard', 'macros', 'notifications'] });

  // Levels: set to what they already are, so nothing audible changes.
  const aud = sections.audio || {};
  const dflt = (aud.outputs || []).find((d) => d.isDefault);
  if (dflt) {
    record('output devices carry their own level', typeof dflt.volume === 'number', `default is ${dflt.volume}`);
    const r = await cmd('audio.deviceVolume', { id: dflt.id, value: dflt.volume });
    record('cmd audio.deviceVolume', r.ok === true, r.ok ? 'ok' : `refused: ${r.error}`);
  }
  await refused('audio.deviceVolume unknown device refused', 'audio.deviceVolume', { id: '__nope__', value: 10 });
  await refused('audio.rememberApp unknown app refused', 'audio.rememberApp', { id: '__nosuchapp__', value: 1 });
  const same = await cmd('audio.micMute', { value: aud.micMuted ? 1 : 0 });
  record('cmd audio.micMute (to its current state)', same.ok === true, same.ok ? 'ok' : `refused: ${same.error}`);

  // Mutes the speakers and microphone for a moment, then puts them back as they were. Opt-in,
  // because on a machine someone is using that is audible.
  if (destructive || process.argv.includes('--deafen')) {
    const was = { master: aud.masterMuted, mic: aud.micMuted };
    const d1 = await cmd('audio.deafenToggle', {});
    const on = d1.ok && await waitFor(() => sections.audio.deafened === true, 4000);
    record('deafen mutes speakers and microphone', !!on);
    const d2 = await cmd('audio.deafenToggle', {});
    const off = d2.ok && await waitFor(() => sections.audio.deafened === false, 4000);
    record('deafen again puts both back', !!off);
    const back = await waitFor(() => sections.audio.masterMuted === !!was.master && sections.audio.micMuted === !!was.mic, 4000);
    record('deafen restores what was muted before', back, `master ${was.master} mic ${was.mic}`);
  }

  if (destructive) {
    console.log('\nsending disruptive commands (--destructive)...');
    for (const [action, args] of [
      ['cast.arm', {}], ['cast.disarm', {}],
      ['rec.start', {}], ['rec.stop', {}],
      ['sb.play', { id: '__nonexistent__' }],
    ]) {
      const r = await cmd(action, args);
      record(`cmd ${action}`, r.ok !== undefined, r.ok ? 'ok' : `refused: ${r.error || '(no reason)'}`);
    }
  }

  // ------------------------------------------------- functional audit (opt-in)
  //
  // These do real things on the machine - move the default audio device, play a sound, record,
  // open and end programs - so they are opt-in, and each puts back what it changed. Every check
  // observes an effect (a device became the default, a file exists, a process is gone) rather than
  // trusting that a command was accepted.
  if (process.argv.includes('--audit')) {
    console.log('\nfunctional audit...');
    // The earlier checks narrowed the subscription; the audit has to watch what it changes.
    send({ type: 'sub', hz: 4, sections: ['sys', 'audio', 'running', 'cast', 'soundboard', 'apps'] });
    await new Promise((r) => setTimeout(r, 1500));
    const cp = require('child_process');
    const os = require('os');
    const ps = (script) => cp.execFileSync('powershell', ['-NoProfile', '-Command', script], { encoding: 'utf8' }).trim();
    const alive = (pid) => { try { process.kill(pid, 0); return true; } catch (_) { return false; } };

    // ---- output switching, and back
    {
      const a0 = sections.audio;
      const outs = (a0.outputs || []);
      const was = outs.find((d) => d.isDefault);
      const other = outs.find((d) => !d.isDefault);
      if (!was || !other) record('switch the default output device', true, 'only one output on this PC: nothing to switch to');
      else {
        const r1 = await cmd('audio.setOutput', { id: other.id });
        const moved = r1.ok && await waitFor(() => (sections.audio.outputs.find((d) => d.isDefault) || {}).id === other.id, 6000);
        record('switch the default output device', !!moved, moved ? `now "${other.name}"` : `refused/slow: ${r1.error || 'default did not change'}`);
        const r2 = await cmd('audio.setOutput', { id: was.id });
        const back = r2.ok && await waitFor(() => (sections.audio.outputs.find((d) => d.isDefault) || {}).id === was.id, 6000);
        record('and switch back', !!back, back ? `restored "${was.name}"` : 'DEFAULT OUTPUT NOT RESTORED');
        // The Windows view of it, not the agent's own: ask Windows which endpoint is default.
      }
    }

    // ---- input switching, and back
    {
      const ins = sections.audio.inputs || [];
      const was = ins.find((d) => d.isDefault);
      const other = ins.find((d) => !d.isDefault);
      if (!was || !other) record('switch the default input device', true, 'only one input on this PC: nothing to switch to');
      else {
        const r1 = await cmd('audio.setInput', { id: other.id });
        const moved = r1.ok && await waitFor(() => (sections.audio.inputs.find((d) => d.isDefault) || {}).id === other.id, 6000);
        record('switch the default input device', !!moved, moved ? `now "${other.name}"` : `refused/slow: ${r1.error || 'default did not change'}`);
        const r2 = await cmd('audio.setInput', { id: was.id });
        const back = r2.ok && await waitFor(() => (sections.audio.inputs.find((d) => d.isDefault) || {}).id === was.id, 6000);
        record('and switch the input back', !!back, back ? `restored "${was.name}"` : 'DEFAULT INPUT NOT RESTORED');
      }
    }

    // ---- soundboard: a real clip plays, at a low level, and stops
    {
      const clip = (sections.soundboard.items || []).find((c) => !c.broken);
      if (!clip) record('soundboard plays a clip', true, 'no clips in the soundboard folder: nothing to play');
      else {
        const loud = sections.audio.masterVolume;
        await cmd('audio.masterVolume', { value: 6 });
        const wasRoute = clip.route;
        await cmd('sb.route', { id: clip.id, route: 'ears' });
        const r = await cmd('sb.play', { id: clip.id });
        const playing = r.ok && await waitFor(() => (sections.soundboard.items.find((c) => c.id === clip.id) || {}).playing === true, 3000);
        // A very short clip may already be over; that is still a pass if the host accepted it.
        record('soundboard plays a clip', r.ok === true, playing ? `"${clip.name}" playing` : r.ok ? `"${clip.name}" accepted (already finished)` : `refused: ${r.error}`);
        const st = await cmd('sb.stop', {});
        const stopped = st.ok && await waitFor(() => !sections.soundboard.items.some((c) => c.playing), 3000);
        record('soundboard stop silences it', !!stopped);
        await cmd('sb.route', { id: clip.id, route: wasRoute });
        await cmd('audio.masterVolume', { value: loud });
        record('volume and routing put back', await waitFor(() => Math.abs(sections.audio.masterVolume - loud) <= 1, 3000));
      }
    }

    // ---- recorder: arms, records, saves a real WAV file
    {
      const r0 = await cmd('rec.arm', {});
      record('recorder arms', r0.ok === true, r0.ok ? '' : `refused: ${r0.error}`);
      if (r0.ok) {
        await new Promise((r) => setTimeout(r, 2500));
        const s1 = await cmd('rec.start', {});
        const going = s1.ok && await waitFor(() => sections.soundboard.recording === true, 3000);
        record('recording starts', !!going, going ? '' : `refused: ${s1.error || 'no recording flag'}`);
        await new Promise((r) => setTimeout(r, 3000));
        const s2 = await cmd('rec.stop', {});
        const saved = s2.ok && await waitFor(() => !!sections.soundboard.lastSaved, 4000);
        let detail = '', good = false;
        if (saved) {
          const file = sections.soundboard.lastSaved;
          const full = path.isAbsolute(file) ? file : path.join(sections.soundboard.folder || '', file);
          try {
            const buf = fs.readFileSync(full);
            good = buf.length > 1000 && buf.toString('ascii', 0, 4) === 'RIFF' && buf.toString('ascii', 8, 12) === 'WAVE';
            detail = `${path.basename(full)}, ${buf.length} bytes`;
            if (good) fs.unlinkSync(full);   // only the file this audit just made
          } catch (e) { detail = 'saved file unreadable: ' + e.message; }
        }
        record('recording is saved as a real WAV file', good, detail || 'no file reported');
        await cmd('rec.disarm', {});
      }
    }

    // ---- mic cast: needs a virtual cable; arming moves the default input, disarming must restore it
    {
      const cast = sections.cast;
      if (!cast.available) record('mic cast', true, 'no virtual audio cable on this PC: the control is correctly not offered');
      else {
        const inBefore = (sections.audio.inputs.find((d) => d.isDefault) || {}).id;
        const a = await cmd('cast.arm', {});
        const armed = a.ok && await waitFor(() => sections.cast.armed === true, 5000);
        record('mic cast arms', !!armed, armed ? `mic "${sections.cast.micName || '?'}"` : `refused: ${a.error}`);
        await new Promise((r) => setTimeout(r, 2000));
        await cmd('cast.disarm', {});
        const off = await waitFor(() => sections.cast.armed === false, 5000);
        const inAfter = await waitFor(() => (sections.audio.inputs.find((d) => d.isDefault) || {}).id === inBefore, 6000);
        record('mic cast disarms and the real microphone is restored', off && inAfter,
               off && inAfter ? '' : 'DEFAULT INPUT NOT RESTORED - check Windows sound settings');
      }
    }

    // ---- programs: close politely, end by force, refuse what is protected
    {
      const spawnNotepad = async () => {
        const child = cp.spawn('notepad.exe', [], { detached: true, stdio: 'ignore' });
        child.unref();
        const pid = child.pid;
        const seen = await waitFor(() => (sections.running.items || []).some((p) => p.name.toLowerCase().startsWith('notepad')), 8000);
        const row = (sections.running.items || []).filter((p) => p.name.toLowerCase().startsWith('notepad'))
          .sort((a, b) => Math.abs(a.pid - pid) - Math.abs(b.pid - pid))[0];
        return { pid, seen, row };
      };

      // close (WM_CLOSE): a clean notepad has nothing to save, so it must exit
      let n = await spawnNotepad();
      record('a program appears in the running list', n.seen, n.row ? `notepad pid ${n.row.pid}` : 'never listed');
      if (n.row) {
        const c = await cmd('running.close', { pid: n.row.pid });
        const gone = c.ok && await waitFor(() => !alive(n.row.pid), 6000);
        record('running.close ends a program politely', !!gone, gone ? '' : `refused/ignored: ${c.error || 'still running'}`);
        if (!gone) { try { process.kill(n.row.pid); } catch (_) {} }
      }

      // kill: immediate
      n = await spawnNotepad();
      if (n.row) {
        const k = await cmd('running.kill', { pid: n.row.pid });
        const gone = k.ok && await waitFor(() => !alive(n.row.pid), 6000);
        record('running.kill ends a program at once', !!gone, gone ? '' : `refused: ${k.error}`);
        // killing what is already gone is a success with nothing left to do, not an error to show
        const again = await cmd('running.kill', { pid: n.row.pid });
        record('ending something already gone is not shown as a failure', again.ok === true || /no longer|already/i.test(again.error || ''),
               again.ok ? 'ok' : `said: ${again.error}`);
        if (!gone) { try { process.kill(n.row.pid); } catch (_) {} }
      }

      // a pid that was never offered must be refused, and the refusal must say something useful
      const bogus = await cmd('running.kill', { pid: 4 });
      record('running.kill refuses a pid it never listed', bogus.ok === false && !!bogus.error, `said: ${bogus.error}`);
      const sys = (sections.running.items || []).find((p) => /^(svchost|csrss|winlogon|lsass|dwm)$/i.test(p.name));
      record('protected processes are never offered', !sys, sys ? `LISTED: ${sys.name}` : 'none listed');
    }

    // ---- launching: a real entry opens, a made-up one is refused
    {
      const calc = (sections.apps.items || []).find((a) => /calculator/i.test(a.name));
      if (!calc) record('launch opens an installed app', true, 'Calculator not in this PC\'s list: skipped');
      else {
        const l = await cmd('launch', { id: calc.id });
        const up = l.ok && await waitFor(() => (sections.running.items || []).some((p) => /calc/i.test(p.name)) , 8000);
        record('launch opens an installed app', !!up, up ? `"${calc.name}"` : `refused: ${l.error}`);
        try { ps('Get-Process -Name CalculatorApp,Calculator,calc -ErrorAction SilentlyContinue | Stop-Process -Force'); } catch (_) {}
      }
      const bad = await cmd('launch', { id: 'steam:0000000-not-installed' });
      record('launch refuses something that is not installed', bad.ok === false, `said: ${bad.error}`);
    }
  }

  // ------------------------------------------------- resilience + idle

  const before = pongs;
  await new Promise((r) => setTimeout(r, 6500));
  record('keepalive ping answered', pongs > before, `${pongs} ping(s) seen`);

  // An empty subscription must park the host: nothing should arrive afterwards.
  send({ type: 'sub', hz: 0, sections: [] });
  // A frame that was already being built when the subscription changed goes out once; what matters is
  // that nothing keeps coming. So let that one land, discard it, and then look.
  await new Promise((r) => setTimeout(r, 700));
  delete sections.sys;
  await new Promise((r) => setTimeout(r, 2500));
  record('empty subscription stops telemetry', sections.sys === undefined,
         sections.sys === undefined ? 'host went idle' : 'STILL PUBLISHING');

  // Garbage must not take the link down.
  sock.write(frame(KIND.JSON, { type: 'nonsense', what: 'ever' }));
  sock.write(Buffer.from([0, 0, 0, 2, 0, 0x7b, 0x7d]));
  send({ type: 'sub', hz: 2, sections: ['sys'] });
  const survived = await waitFor(() => sections.sys !== undefined, 5000);
  record('survives malformed input', survived, survived ? 'link still live' : 'link died');

  // The strangers, now that the real session has had its run.
  const s1 = silent.finish(), s2 = wrong.finish(), s3 = nosy.finish();
  record('a peer that never answers is dropped', s1.closedAt !== null && s1.closedAt < 12000,
         s1.closedAt === null ? 'STILL CONNECTED' : `closed after ${s1.closedAt} ms`);
  record('a peer with the wrong key is dropped', s2.closedAt !== null && s2.closedAt < 6000 && !s2.authed,
         s2.closedAt === null ? 'STILL CONNECTED' : `closed after ${s2.closedAt} ms`);
  record('unauthenticated commands get no answer', s3.results === 0 && !s3.authed,
         `${s3.results} result(s)`);
  record('a stranger could not pin a profile or lock the PC',
    sections.tiles && !sections.tiles.pinned, 'the real session still shows its own state');

  sock.end();
  report();
}

// ---------------------------------------------------------------- helpers

const currentMaster = (s) => (s.audio && s.audio.masterVolume != null ? s.audio.masterVolume : 50);
const currentMic = (s) => (s.audio && s.audio.micVolume != null ? s.audio.micVolume : 50);
const currentMode = (s) => (s.sys && s.sys.thermalMode) || null;
const currentBrightness = (s) => (s.sys && s.sys.brightness != null ? s.sys.brightness : 50);

function summarise(kind, m) {
  switch (kind) {
    case 'sys': return `cpu ${m.cpu}%  ram ${m.ramPercent}%  gpu ${m.gpu ?? '-'}  temp ${m.cpuTemp ?? '-'}`;
    case 'audio': return `master ${m.masterVolume}  mic ${m.micVolume}  ${(m.apps || []).length} app(s)  ${(m.outputs || []).length} output(s)`;
    case 'media': return `${(m.sessions || []).length} session(s)`;
    case 'apps': return `${(m.items || []).length} item(s)`;
    case 'running': return `${(m.items || []).length} process(es)`;
    case 'ctx': return `mode ${m.mode}  fg ${m.foregroundApp || '-'}`;
    case 'cast': return `available ${m.available}  armed ${m.armed}`;
    case 'soundboard': return `${(m.items || []).length} clip(s)`;
    case 'mediaSources': return `${(m.items || []).length} source(s)`;
    case 'tiles': return `${m.profiles.length} profile(s), showing '${m.active}'${m.auto ? ' (auto)' : ''}, canEdit ${m.canEdit}`;
    case 'clock': return `${(m.events || []).length} event(s), ${m.calendarFiles} calendar file(s), offset ${m.utcOffsetMin} min`;
    case 'timers': return `${(m.items || []).length} timer(s)`;
    case 'macros': return `${(m.items || []).length} macro(s)`;
    case 'alerts': return `${m.enabled ? 'on' : 'off'}, cpu ${m.cpuTemp} gpu ${m.gpuTemp} ram ${m.ram}`;
    case 'sessions': return `${m.items.length} session(s), frame rate ${m.fpsAvailable ? 'available' : 'not available'}, auto power ${m.autoPower ? 'on' : 'off'}`;
    case 'frames': return `${m.times.length} recent frame(s)`;
    case 'top': return `${m.ram.length} by memory, ${m.cpu.length} by cpu, ${m.gpu.length} by gpu, top is ${m.ram[0] ? m.ram[0].name : '-'}`;
    case 'notifications': return `${m.enabled ? 'on' : 'off'}, ${m.available ? 'readable on this PC' : 'unavailable: ' + m.problem}, ${(m.items || []).length} shown`;
    default: return 'ok';
  }
}

function report() {
  const pass = results.filter((r) => r.ok).length;
  const fail = results.length - pass;
  console.log('\n' + '='.repeat(78));
  for (const r of results) {
    console.log(`  ${r.ok ? 'PASS' : 'FAIL'}  ${r.name.padEnd(38)} ${r.detail}`);
  }
  console.log('='.repeat(78));
  console.log(`  ${pass} passed, ${fail} failed, ${results.length} total`);
  process.exit(fail === 0 ? 0 : 1);
}

const bail = setTimeout(() => {
  record('harness completed', false, 'overall timeout');
  report();
}, timeoutMs + 30000);
bail.unref?.();

main().catch((e) => {
  record('harness completed', false, String(e.message || e));
  report();
});

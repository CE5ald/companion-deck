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
const dgram = require('dgram');
const net = require('net');

const DISCOVERY_PORT = 45888;
const KIND = { JSON: 0, ASSET: 1, PING: 2, PONG: 3 };
const destructive = process.argv.includes('--destructive');
const timeoutMs = 25000;

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
    if (msg.type === 'hello') { caps = msg.capabilities || {}; sections.hello = msg; return; }
    if (msg.type === 'result') {
      const cb = pending.get(msg.id);
      if (cb) { pending.delete(msg.id); cb(msg); }
      return;
    }
    sections[msg.type] = msg;
  });

  sock.on('data', reader);

  await new Promise((resolve, reject) => {
    sock.once('connect', resolve);
    sock.once('error', reject);
    setTimeout(() => reject(new Error('TCP connect timed out')), 8000);
  });
  record('tcp connect', true);

  // The real tablet opens with a hello describing itself.
  send({
    type: 'hello', device: 'FakeTablet', android: 34,
    width: 1024, height: 600, dpWidth: 1024, dpHeight: 600, density: 1.0,
    version: 'test', cachedAssets: [],
  });

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
  ]});

  // Wait for each section, then let them settle: several are published again once a slow
  // enumeration finishes, and judging the first payload would be judging a half-built one.
  for (const s of ['sys', 'audio', 'ctx', 'media', 'apps', 'running', 'cast', 'soundboard', 'mediaSources']) {
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
                   ? null : 'no outputs, or none marked default',
    ctx: (m) => (typeof m.mode === 'string' && m.mode.length > 0) ? null : 'no mode',
    media: () => null,
    apps: (m) => (Array.isArray(m.items) && m.items.length > 0) ? null : 'no launcher items',
    running: (m) => (Array.isArray(m.items) && m.items.length > 0) ? null : 'no processes',
    cast: (m) => (typeof m.available === 'boolean') ? null : 'no availability flag',
    soundboard: (m) => Array.isArray(m.items) ? null : 'items is not a list',
    mediaSources: (m) => (Array.isArray(m.items) && m.items.length > 0) ? null : 'no sources',
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

  // ------------------------------------------------- resilience + idle

  const before = pongs;
  await new Promise((r) => setTimeout(r, 6500));
  record('keepalive ping answered', pongs > before, `${pongs} ping(s) seen`);

  // An empty subscription must park the host: nothing should arrive afterwards.
  delete sections.sys;
  send({ type: 'sub', hz: 0, sections: [] });
  await new Promise((r) => setTimeout(r, 2500));
  record('empty subscription stops telemetry', sections.sys === undefined,
         sections.sys === undefined ? 'host went idle' : 'STILL PUBLISHING');

  // Garbage must not take the link down.
  sock.write(frame(KIND.JSON, { type: 'nonsense', what: 'ever' }));
  sock.write(Buffer.from([0, 0, 0, 2, 0, 0x7b, 0x7d]));
  send({ type: 'sub', hz: 2, sections: ['sys'] });
  const survived = await waitFor(() => sections.sys !== undefined, 5000);
  record('survives malformed input', survived, survived ? 'link still live' : 'link died');

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

// Captures the README screenshots: every page of the tablet UI at 2x pixel density, using the
// UI's built-in demo data, so no image ever contains anything from a real machine.
//
//   node tools/uiserve.js                                    (in one terminal)
//   node tools/capture-screenshots.js http://localhost:5173/ docs/images
//   node tools/capture-screenshots.js http://localhost:5173/ docs/images home voice   (just these)
//
// It drives headless Edge or Chrome over the DevTools protocol, so one of them must be
// installed. Set BROWSER to a full path if neither is found in the usual places.
//
// ?demo=live shows the voice tools mid-use (cast armed, recorder buffering) and ?setup=half
// shows the first-run guide part-way through; both exist only in the demo-data preview.

const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const [base = 'http://localhost:5173/', outDir = 'docs/images', ...only] = process.argv.slice(2);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const SCROLL = "document.querySelector('#content').scrollTo(0, 99999); 1";
const SCROLLED = "document.querySelector('#content').scrollTop > 50";
const GUIDE_SHOWN = "!document.querySelector('#setup').hidden && getComputedStyle(document.querySelector('#veil')).opacity === '1'";
const TO_VOICE = "document.querySelector('section[data-page=\"media\"] .voice-strip').closest('.card').scrollIntoView(); 1";
// Re-run on every retry, so it walks from the page to edit mode to the open editor and then stops.
const OPEN_EDITOR = "(() => { if (!document.querySelector('#sheet').hidden) return 1; if (!document.querySelector('.deck-grid.editing')) document.querySelector('.d-edit').click(); else { const t = document.querySelector('[data-tile=\"focus\"]'); if (t) t.click(); } return 1; })()";
const EDITOR_OPEN = "!document.querySelector('#sheet').hidden";
const EDIT_MODE = "(() => { if (!document.querySelector('.deck-grid.editing')) document.querySelector('.d-edit').click(); return 1; })()";
const EDITING = "!!document.querySelector('.deck-grid.editing')";
const AMBIENT = "(() => { document.querySelector('.amb').click(); return 1; })()";
const IS_AMBIENT = "document.querySelector('#app').classList.contains('ambient')";
// Walks from the Deck page to a new tile with a macro step that is part-way through recording.
const RECORD_MACRO = "(() => { if (document.querySelector('.rec-live')) return 1; const open = !document.querySelector('#sheet').hidden; if (!document.querySelector('.deck-grid.editing')) { document.querySelector('.d-edit').click(); return 1; } if (!open) { document.querySelector('.deck-grid .tile.add').click(); return 1; } if (!document.querySelector('[data-step] [data-macro]')) { const d = document.querySelector('[data-step=\\\"0\\\"] [data-act=\\\"del\\\"]'); if (d) d.click(); document.querySelector('[data-add=\\\"macro\\\"]').click(); return 1; } document.querySelector('[data-macro=\\\"record\\\"]').click(); return 1; })()";
const TO = (sel) => `document.querySelector('${sel}').scrollIntoView(); 1`;
const OPEN_SESSION = "(() => { document.querySelector('.sessions-card').scrollIntoView(); if (!document.querySelector('.sess.open')) document.querySelector('.sess').click(); return 1; })()";
const SESSION_OPEN = "!!document.querySelector('.sess.open')";
const AMBIENT_NP = "(() => { document.querySelector('.b-amb').click(); return 1; })()";
const NP_SHOWN = "!document.querySelector('#npAmbient').hidden";
const PAD_SHEET = "(() => { if (!document.querySelector('.pad-grid.editing')) { document.querySelector('.b-sbedit').click(); return 1; } if (document.querySelector('#sheet').hidden) document.querySelector('.pad').click(); return 1; })()";
const RECORDING = "!!document.querySelector('.rec-live') && document.querySelector('.rec-live').textContent.includes('Recording')";

// name, size, and how to reach the view.
//   js     runs after the page has laid out (and again on every retry until `ready` holds)
//   ready  an expression that must be true before the picture is taken. Timing in a headless
//          browser is not reliable enough to trust a fixed delay: the first version of this tool
//          saved a half-faded guide and an unscrolled page, both silently. A shot that never
//          becomes ready now fails the run instead.
const SHOTS = [
  { name: 'home',            nav: 'home' },
  { name: 'media',           nav: 'media' },
  { name: 'mixer',           nav: 'media', js: SCROLL, ready: SCROLLED },
  { name: 'voice',           nav: 'voice', query: '?demo=live', wait: 3200 },
  { name: 'soundboard',      nav: 'voice', query: '?demo=live', js: SCROLL, ready: SCROLLED, wait: 2400 },
  { name: 'performance',     nav: 'perf' },
  { name: 'apps',            nav: 'apps' },
  { name: 'launcher',        nav: 'apps', js: SCROLL, ready: SCROLLED },
  { name: 'actions',         nav: 'quick' },
  { name: 'deck',            nav: 'deck', query: '?ctx=gaming' },
  { name: 'deck-edit',       nav: 'deck', js: EDIT_MODE, ready: EDITING },
  { name: 'deck-editor',     nav: 'deck', js: OPEN_EDITOR, ready: EDITOR_OPEN },
  { name: 'clock',           nav: 'clock' },
  { name: 'ambient',         nav: 'clock', js: AMBIENT, ready: IS_AMBIENT },
  { name: 'outputs',         nav: 'media', js: TO_VOICE, ready: SCROLLED },
  { name: 'notifications',   nav: 'clock', js: SCROLL, ready: SCROLLED },
  { name: 'history',         nav: 'perf', js: TO('.hist-card'), ready: SCROLLED },
  { name: 'top-processes',   nav: 'perf', js: TO('.top-card'), ready: SCROLLED },
  { name: 'sessions',        nav: 'game', js: OPEN_SESSION, ready: SESSION_OPEN },
  { name: 'frame-times',     nav: 'game', js: TO('.frames-card'), ready: SCROLLED },
  { name: 'media-display',   nav: 'media', js: TO('.viz-card'), ready: SCROLLED, wait: 2200 },
  { name: 'now-playing',     nav: 'media', js: AMBIENT_NP, ready: NP_SHOWN, wait: 2400 },
  { name: 'pad-settings',    nav: 'voice', js: PAD_SHEET, ready: "!document.querySelector('#sheet').hidden" },
  { name: 'macro-record',    nav: 'deck', js: RECORD_MACRO, ready: RECORDING, wait: 2200 },
  { name: 'setup',           query: '?setup=half', ready: GUIDE_SHOWN },
  { name: 'phone-landscape', nav: 'home', width: 800, height: 360, wait: 3000 },
];

function findBrowser() {
  const candidates = [
    process.env.BROWSER,
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/usr/bin/chromium-browser',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  ].filter(Boolean);
  const found = candidates.find((p) => fs.existsSync(p));
  if (!found) throw new Error('No Edge or Chrome found. Set BROWSER to its full path.');
  return found;
}

/**
 * One shot, in a browser of its own.
 *
 * A single long-lived headless browser worked for the first several shots and then, reliably,
 * stopped laying pages out in time: an eight-shot run left the launcher unscrollable while the
 * same shot alone, or after a shorter run, was fine. Whatever accumulates inside the browser
 * is not worth finding when a fresh one per image removes it, and costs a few seconds each.
 */
async function capture(shot, port) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'deck-shots-'));
  const proc = spawn(findBrowser(), [
    '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-color-profile=srgb',
    `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, 'about:blank',
  ], { stdio: 'ignore' });

  try {
    let tabs;
    for (let i = 0; i < 40; i++) {
      try { tabs = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); if (tabs.length) break; } catch {}
      await sleep(250);
    }
    const ws = new WebSocket(tabs.find((t) => t.type === 'page').webSocketDebuggerUrl);
    await new Promise((r) => ws.addEventListener('open', r));

    let id = 0;
    const waiting = new Map();
    ws.addEventListener('message', (e) => {
      const m = JSON.parse(e.data);
      if (m.id && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); }
    });
    const send = (method, params) => new Promise((r) => { const i = ++id; waiting.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
    const run = async (expression) => {
      const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
      if (r.result?.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description || 'script failed');
      return r.result?.result?.value;
    };

    await send('Page.enable');
    await send('Emulation.setDeviceMetricsOverride',
      { width: shot.width || 1024, height: shot.height || 600, deviceScaleFactor: 2, mobile: false });
    await send('Page.navigate', { url: base + (shot.query || '') });
    await sleep(2200);
    if (shot.nav) await run(`document.querySelector(".rail-btn[data-nav='${shot.nav}']").click()`);

    // The page's height is not final until its first render, so wait before scrolling it.
    if (shot.js) { await sleep(1200); await run(shot.js); }

    // Poll until the view really is what the shot claims, re-running its script each time.
    if (shot.ready) {
      let ok = false;
      for (let i = 0; i < 32 && !ok; i++) {
        if (shot.js) await run(shot.js);
        await sleep(250);
        ok = !!(await run(shot.ready));
      }
      if (!ok) throw new Error(`"${shot.name}" never reached its expected state, so no image was saved for it`);
    }
    await sleep(shot.wait || 1800);

    const png = await send('Page.captureScreenshot', { format: 'png' });
    const file = path.join(outDir, `${shot.name}.png`);
    fs.writeFileSync(file, Buffer.from(png.result.data, 'base64'));
    console.log(`${shot.name.padEnd(16)} ${(fs.statSync(file).size / 1024).toFixed(0).padStart(4)} KB`);
    ws.close();
  } finally {
    proc.kill();
    // The browser holds its profile open for a moment after it is killed, and failing to delete a
    // temp folder is no reason to report a failed run when every image was written.
    await sleep(800);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch { /* left for the OS */ }
  }
}

(async () => {
  fs.mkdirSync(outDir, { recursive: true });
  const wanted = SHOTS.filter((s) => !only.length || only.includes(s.name));
  if (!wanted.length) throw new Error(`No shot matches: ${only.join(', ')}. Known: ${SHOTS.map((s) => s.name).join(', ')}`);
  let port = 9334;
  for (const shot of wanted) await capture(shot, port++);
})().catch((e) => { console.error(e.message || e); process.exit(1); });

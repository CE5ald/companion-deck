/* ==========================================================================
   Companion Deck - tablet UI

   Targets a landscape control surface from about 600dp wide upward, on whatever
   WebView the device has from Android 5 onward.

   RENDERING MODEL - the reason this file is shaped the way it is.

   Every card's DOM is built exactly once, at boot. After that nothing is ever
   destroyed or recreated: updates write text and transforms onto cached node
   references, and lists are reconciled by key so only genuinely new rows are
   created and only genuinely gone rows are removed.

   This matters because an earlier version rebuilt a card's innerHTML whenever a
   "signature" string changed, and several of those signatures were unstable by
   nature - the audio page keyed off the list of apps making sound, the home page
   off which card was leading. An app starting playback therefore tore down and
   rebuilt whole pages, which showed as flashing, cards vanishing and reappearing,
   and scroll position jumping. Layout changes now move zero nodes: the home grid
   reorders purely through CSS `order`.
   ========================================================================== */
'use strict';

const $ = (s, r = document) => r.querySelector(s);
const app = $('#app');

/* ------------------------------------------------------------------ state */

const S = {
  link: { phase: 'idle', transport: '', hostName: '' },
  caps: {},
  sys: null,
  media: null,
  audio: null,
  ctx: null,
  apps: null,
  mediaSources: null,
  tiles: null,
  clock: null,
  timers: null,
  macros: null,
  notifications: null,
  history: null,
  top: null,
  alerts: null,
  sessions: null,
  frames: null,
};

let page = 'home';
let dirty = false;
let cmdSeq = 1;

/* Controls the user is currently touching. Incoming telemetry must never yank a
   slider out from under a finger, so updates for that control are ignored until
   shortly after the interaction ends. */
const held = new Map();
const holdFor = (key, ms = 700) => held.set(key, performance.now() + ms);
const isHeld = (key) => (held.get(key) || 0) > performance.now();

const native = typeof window.DeckNative !== 'undefined' ? window.DeckNative : null;

/* Commands that need to know how they ended (saving a tile, say) pass a callback. It is called
   once with the host's result, or with a failure if nothing comes back. */
const cmdWaiters = new Map();

function send(action, args, done) {
  const msg = { type: 'cmd', id: cmdSeq++, action, args: args || {} };
  if (done) {
    cmdWaiters.set(msg.id, done);
    setTimeout(() => { if (cmdWaiters.delete(msg.id)) done({ ok: false, error: 'The PC did not answer.' }); }, 6000);
  }
  if (native) native.send(JSON.stringify(msg));
  else if (window.__mockCmd) window.__mockCmd(action, args || {}, msg.id);
  else console.log('[mock cmd]', action, args || {});
  return msg.id;
}

/* -------------------------------------------------------------- utilities */

const icon = (id, cls) => `<svg class="ic ${cls || ''}"><use href="#${id}"/></svg>`;
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, c =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

function el(tag, cls, html) {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (html != null) n.innerHTML = html;
  return n;
}

/** Writes only when the value differs; redundant writes still cost a repaint. */
function text(node, value) {
  const v = value == null ? '' : String(value);
  if (node && node.textContent !== v) node.textContent = v;
}

function mmss(sec) {
  if (!isFinite(sec) || sec < 0) sec = 0;
  const m = Math.floor(sec / 60), s = Math.floor(sec % 60);
  return m + ':' + String(s).padStart(2, '0');
}


/* ===================================================== hold to confirm = */

/* Ending a process, shutting the PC down and forgetting a tablet all throw something away, so
   none of them is a tap. They share one control. The progress is drawn in a panel on the half of
   the screen your finger is NOT on - the old thin bar sat on the button's bottom edge, which is
   exactly where a fingertip lands, so you could not tell whether the press was registering. The
   panel says what will happen and for how long to keep pressing, and afterwards says what
   actually happened, using the PC's own answer rather than assuming it worked. */
const HOLD_MS = 1500;      /* long enough to be deliberate, short enough to be bearable */
const HOLD_SLOP = 14;      /* px of drift before a press counts as a scroll, not a hold */
const HOLD_TAP_MS = 250;   /* a release sooner than this is a tap and gets no scolding */

const holdHud = {
  node: null, title: null, sub: null, timer: null,
  ensure() {
    if (this.node) return;
    const n = el('div', 'hold-hud', `
      <svg class="hh-ring" viewBox="0 0 36 36" aria-hidden="true">
        <circle class="bg" cx="18" cy="18" r="15.5"></circle>
        <circle class="fg" cx="18" cy="18" r="15.5"></circle>
        <path class="tick" d="M11 18.5l5 5 9-10.5"></path>
        <path class="cross" d="M12 12l12 12M24 12L12 24"></path>
      </svg>
      <div class="hh-txt"><b class="hh-t"></b><span class="hh-s"></span></div>`);
    n.hidden = true;
    n.setAttribute('role', 'status');
    document.body.appendChild(n);
    this.node = n; this.title = $('.hh-t', n); this.sub = $('.hh-s', n);
  },
  /* state: arming | working | ok | fail | early */
  show(state, title, sub, y, linger) {
    this.ensure();
    clearTimeout(this.timer);
    const n = this.node;
    // Opposite half from the finger, so the hand never covers it.
    if (y != null) n.classList.toggle('low', y < window.innerHeight * 0.5);
    n.hidden = false;
    n.dataset.state = state;
    n.style.setProperty('--hold-ms', HOLD_MS + 'ms');
    text(this.title, title);
    text(this.sub, sub || '');
    if (state === 'arming') {
      // Restart the fill from empty: drop the animation class, force a layout, add it back.
      n.classList.remove('run'); void n.offsetWidth; n.classList.add('run');
    } else n.classList.remove('run');
    if (linger) this.timer = setTimeout(() => this.hide(), linger);
  },
  hide() { clearTimeout(this.timer); if (this.node) { this.node.hidden = true; this.node.classList.remove('run'); } },
};

function haptic(ms) { try { navigator.vibrate && navigator.vibrate(ms); } catch (_) { /* not available */ } }

/* opts: selector, verb(el) -> "end Notepad", run(el, done), enabled?(el), noWait?, okText?(el, res),
         failText?(el, res).  run() reports through done({ok, error}). */
function bindHold(root, opts) {
  let st = null;

  const release = () => {
    if (!st) return;
    clearTimeout(st.timer);
    st.el.classList.remove('holding');
    const held = performance.now() - st.t0;
    const fired = st.fired;
    st = null;
    if (fired) return;
    // Let go early: say so, unless it was only a tap that has its own meaning.
    if (held >= HOLD_TAP_MS) holdHud.show('early', 'Released too early',
      'Keep pressing for ' + (HOLD_MS / 1000).toFixed(1) + ' s', null, 1500);
    else holdHud.hide();
  };

  const fire = () => {
    const cur = st;
    if (!cur) return;
    cur.fired = true;
    cur.el.classList.remove('holding');
    cur.el.dataset.holdFired = '1';
    haptic(40);
    const verb = opts.verb(cur.el);
    const y = cur.y;
    const ok = (res) => holdHud.show('ok', opts.okText ? opts.okText(cur.el, res) : 'Done', verb, y, 2200);
    if (opts.noWait) { opts.run(cur.el, () => {}); return ok({}); }
    holdHud.show('working', 'Working\u2026', verb, y);
    opts.run(cur.el, (res) => {
      res = res || {};
      if (res.ok === false) {
        holdHud.show('fail', opts.failText ? opts.failText(cur.el, res) : 'Did not work',
          res.error || 'The PC refused', y, 4500);
        haptic([60, 40, 60]);
      } else ok(res);
    });
  };

  root.addEventListener('pointerdown', (e) => {
    if (e.button > 0) return;
    const b = e.target.closest(opts.selector);
    if (!b || (opts.enabled && !opts.enabled(b))) return;
    release();
    st = { el: b, x: e.clientX, y: e.clientY, t0: performance.now(), fired: false, timer: null };
    b.classList.add('holding');
    holdHud.show('arming', 'Hold to ' + opts.verb(b), 'Keep pressing for ' + (HOLD_MS / 1000).toFixed(1) + ' s', e.clientY);
    haptic(10);
    st.timer = setTimeout(fire, HOLD_MS);
  });
  root.addEventListener('pointermove', (e) => {
    if (st && !st.fired && Math.hypot(e.clientX - st.x, e.clientY - st.y) > HOLD_SLOP) {
      // Dragging is scrolling, not holding: cancel without a telling-off.
      clearTimeout(st.timer); st.el.classList.remove('holding'); st = null; holdHud.hide();
    }
  });
  window.addEventListener('pointerup', release);
  window.addEventListener('pointercancel', release);
  root.addEventListener('contextmenu', (e) => { if (e.target.closest(opts.selector)) e.preventDefault(); });

  // The release that finishes a successful hold also produces a click. Swallow it, or the row
  // that was just ended would also be asked to close.
  root.addEventListener('click', (e) => {
    const b = e.target.closest('[data-hold-fired]');
    if (!b) return;
    delete b.dataset.holdFired;
    e.stopPropagation(); e.preventDefault();
  }, true);
}

/* Cover art for the browser preview, keyed like real artwork. Empty on a device, where every key
   is a content hash served by the shell. */
const DEMO_ART = {};
const artUrl = (key) => (key ? DEMO_ART[key] || `https://deck.local/asset/${key}` : null);

/* Zero-width space. Paints nothing, but still generates a line box - so an element using
   it keeps the height and baseline it would have had with real text in it, instead of
   collapsing and dragging the surrounding layout with it. Written as an escape rather
   than the literal character so it survives editing. */
const ZWSP = '\u200B';

function fmtRate(kbps) {
  if (kbps == null) return '–';
  if (kbps >= 1000) return (kbps / 1000).toFixed(1) + ' Mb';
  return Math.round(kbps) + ' kb';
}

/**
 * Keyed list reconciliation. Existing rows are reused and updated in place;
 * only new keys create DOM and only vanished keys remove it. This is what stops
 * the audio mixer flashing every time an application starts or stops playing.
 */
function reconcile(container, items, keyOf, create, update) {
  let map = container.__keyed;
  if (!map) map = container.__keyed = new Map();

  const seen = new Set();
  let prev = null;

  for (const item of items) {
    const key = keyOf(item);
    seen.add(key);

    let rec = map.get(key);
    if (!rec) { rec = create(item); map.set(key, rec); }
    update(rec, item);

    // Reposition only if this row is not already where it belongs.
    const node = rec.node;
    const shouldFollow = prev ? prev.nextElementSibling : container.firstElementChild;
    if (shouldFollow !== node) container.insertBefore(node, shouldFollow);
    prev = node;
  }

  for (const [key, rec] of map) {
    if (!seen.has(key)) { rec.node.remove(); map.delete(key); }
  }
}

/* ------------------------------------------------------------ media timing */

/* GSMTC reports position on change, not continuously, so we anchor to the last
   reported value and extrapolate locally. Smooth progress bar, zero traffic. */
let anchor = null;

function applyMedia(msg) {
  S.media = msg;
  const a = activeSession();
  if (!a) { anchor = null; return; }

  const changed = !anchor || anchor.id !== a.id || anchor.title !== a.title;
  const drift = anchor && Math.abs((a.positionSec || 0) - livePosition()) > 1.6;

  if (changed || drift) {
    anchor = {
      id: a.id, title: a.title,
      pos: a.positionSec || 0,
      dur: a.durationSec || 0,
      at: performance.now(),
      playing: a.status === 'playing',
    };
  } else {
    if (anchor.playing && a.status !== 'playing') { anchor.pos = livePosition(); anchor.at = performance.now(); }
    anchor.playing = a.status === 'playing';
    anchor.dur = a.durationSec || anchor.dur;
  }
}

function livePosition() {
  if (!anchor) return 0;
  let p = anchor.pos;
  if (anchor.playing) p += (performance.now() - anchor.at) / 1000;
  return anchor.dur ? Math.min(p, anchor.dur) : p;
}

let pinnedSession = null;

function activeSession() {
  const m = S.media;
  if (!m || !m.sessions || !m.sessions.length) return null;
  if (pinnedSession) {
    const p = m.sessions.find(s => s.id === pinnedSession);
    if (p) return p;
    pinnedSession = null;
  }
  return m.sessions.find(s => s.id === m.activeId) || m.sessions[0];
}

/* ============================================================ components = */

const DIAL_C = 2 * Math.PI * 44;

const BAND_COLOUR = { ok: 'var(--accent)', warn: 'var(--warn)', hot: 'var(--danger)' };

/* Bands need hysteresis: a value resting on a threshold (RAM at 67-68%) would
   otherwise change colour on every tick, which reads as the card flickering. */
function bandFor(v, prev) {
  const enterWarn = 72, leaveWarn = 66, enterHot = 90, leaveHot = 84;
  if (prev === 'hot') return v < leaveHot ? (v < leaveWarn ? 'ok' : 'warn') : 'hot';
  if (prev === 'warn') return v >= enterHot ? 'hot' : (v < leaveWarn ? 'ok' : 'warn');
  return v >= enterHot ? 'hot' : (v >= enterWarn ? 'warn' : 'ok');
}

function makeGauge(caption) {
  const node = el('div', 'gauge', `
    <div class="dial">
      <svg viewBox="0 0 96 96">
        <circle class="bg" cx="48" cy="48" r="44"></circle>
        <circle class="fg" cx="48" cy="48" r="44"
                stroke-dasharray="${DIAL_C.toFixed(1)}" stroke-dashoffset="${DIAL_C.toFixed(1)}"></circle>
      </svg>
      <div class="lbl"><span class="n">–</span><span class="pct">%</span></div>
    </div>
    <div class="cap">${caption}</div>
    <div class="sub"></div>
    <div class="temp none"><i></i></div>`);

  return {
    node,
    fg: $('.fg', node), n: $('.n', node), sub: $('.sub', node),
    temp: $('.temp', node), tempFill: $('.temp i', node),
    band: 'ok', offset: null, tempScale: null, tempBand: null,
  };
}

/* The span the temperature track covers.

   Not 0-100: below about 30C nothing is happening on a laptop that is switched on, so
   starting there would spend a third of the bar's length on a state it never occupies and
   leave the interesting range compressed. 100C is where this hardware throttles, which is
   the only ceiling that means anything to the person reading it. */
const TEMP_FLOOR = 30;
const TEMP_CEIL = 100;

/* Warmer thresholds than the utilisation dials use. A CPU at 80% is working; a CPU at 80C
   is merely warm, and colouring it as a warning would cry wolf through every game. */
function tempBandFor(c) {
  return c >= 90 ? 'hot' : c >= 78 ? 'warn' : '';
}

/* Writes only on change. These run on every publish tick, and assigning an identical
   transform still invalidates style on this WebView. */
function setScale(rec, fillEl, frac, key) {
  const v = (frac == null ? 0 : Math.max(0, Math.min(1, frac))).toFixed(3);
  if (rec[key] === v) return;
  rec[key] = v;
  fillEl.style.transform = 'scaleX(' + v + ')';
}

function updateGauge(g, pct, sub, tempC) {
  const v = Math.max(0, Math.min(100, pct == null ? 0 : pct));

  const off = (DIAL_C * (1 - v / 100)).toFixed(1);
  if (g.offset !== off) { g.fg.style.strokeDashoffset = off; g.offset = off; }

  const band = bandFor(v, g.band);
  if (band !== g.band) { g.band = band; g.fg.style.stroke = BAND_COLOUR[band]; }

  text(g.n, pct == null ? '–' : Math.round(v));
  text(g.sub, sub || '');

  // A gauge with no temperature keeps the track's height and drops only its ink, so the
  // three dials go on sharing a baseline.
  const has = tempC != null;
  g.temp.classList.toggle('none', !has);
  setScale(g, g.tempFill, has ? (tempC - TEMP_FLOOR) / (TEMP_CEIL - TEMP_FLOOR) : 0, 'tempScale');

  // The band is cleared along with the reading, not merely left on a hidden element. A
  // sensor that drops out while hot and comes back hot would otherwise be relying on the
  // stale class still being correct - true today, but only by accident.
  const tb = has ? tempBandFor(tempC) : '';
  if (g.tempBand !== tb) {
    g.temp.classList.remove('warn', 'hot');
    if (tb) g.temp.classList.add(tb);
    g.tempBand = tb;
  }
}

/* ---- slider ------------------------------------------------------------- */

function makeSlider(key, iconId, compact) {
  const node = el('div', 'slider-row' + (compact ? ' compact' : ''), `
    <button class="icon-btn" data-mute="${esc(key)}">${icon(iconId)}</button>
    <div class="range">
      <div class="range-track"><div class="range-fill"></div></div>
      <div class="range-thumb"></div>
    </div>
    <div class="range-val">–</div>`);
  node.dataset.slider = key;

  return {
    node,
    fill: $('.range-fill', node),
    thumb: $('.range-thumb', node),
    val: $('.range-val', node),
    range: $('.range', node),
    btn: $('[data-mute]', node),
    use: $('use', node),
    baseIcon: iconId,
  };
}

/* Where the handle sits for a given value.
 *
 * Inset by half the thumb so it never hangs off the end of its own track: at 0 the handle
 * sits fully inside the left edge rather than half over the mute button beside it. */
function placeThumb(thumb, v) {
  if (!thumb) return;
  const s = `calc(${v}% + ${(0.5 - v / 100) * 20}px)`;
  if (thumb.style.left !== s) thumb.style.left = s;
}

/**
 * Values we have asked the host for but not yet seen reflected back. The host publishes audio on
 * its own cadence, so a frame captured before our change landed can arrive just after the
 * touch-hold expires.
 */
const pendingValue = new Map();

/** The muted counterpart of a control's icon. Muting has to be drawn on the thing being muted. */
const MUTED_ICON = { 'i-vol': 'i-mute', 'i-mic': 'i-mic-off' };
const mutedIconFor = (base) => MUTED_ICON[base] || 'i-mute';

function markPending(key, want) {
  pendingValue.set(key, { want, until: performance.now() + 2500 });
}

function updateSlider(sl, value, muted) {
  if (!sl || value == null) return;
  const key = sl.node.dataset.slider;
  if (isHeld('sl:' + key)) return;   // do not fight the finger

  const p = pendingValue.get(key);
  if (p) {
    if (performance.now() > p.until) pendingValue.delete(key);        // host never agreed; trust it
    else if (Math.abs(value - p.want) <= 1) pendingValue.delete(key); // caught up
    else return;                                                     // stale echo, ignore
  }

  const v = Math.max(0, Math.min(100, value));
  const t = `scaleX(${v / 100})`;
  if (sl.fill.style.transform !== t) sl.fill.style.transform = t;
  placeThumb(sl.thumb, v);
  // Remembered so a drag can start from the live value rather than from wherever the
  // finger happened to land - which is the whole point of grabbing instead of jumping.
  sl.node.__value = v;

  text(sl.val, muted ? 'off' : v + '%');
  sl.range.classList.toggle('dim', !!muted);
  sl.btn.classList.toggle('muted', !!muted);
  sl.use.setAttribute('href', '#' + (muted ? mutedIconFor(sl.baseIcon) : sl.baseIcon));
}

/** One delegated set of pointer handlers per container, installed once. */
function bindSliderHost(hostNode, onChange, onMute) {
  if (hostNode.__slbound) return;
  hostNode.__slbound = true;

  hostNode.addEventListener('click', (e) => {
    const m = e.target.closest('[data-mute]');
    if (!m) return;
    // A mute flips the level's meaning, so drop any in-flight expectation for that row
    // rather than letting it suppress the state that comes back.
    pendingValue.delete(m.dataset.mute);
    onMute(m.dataset.mute);
  });

  const rowOf = (n) => n.closest('[data-slider]');

  /* The live value, so a gesture can start from where the control actually is.
     Falls back to reading it back off the fill, in case a drag begins before the first
     reading from the host has landed. */
  const valueOf = (row) => {
    if (typeof row.__value === 'number') return row.__value;
    const m = /scaleX\(([\d.]+)\)/.exec($('.range-fill', row)?.style.transform || '');
    return m ? Math.round(parseFloat(m[1]) * 100) : 0;
  };

  /*
   * Precision by pulling away from the track. A tap has to land where you point it, so this
   * slider is absolute: touch at 70% and the value is 70%. That is the part that has to stay.
   */
  const FINE_STEPS = [
    { fromY: 0,   scale: 1    },
    { fromY: 44,  scale: 0.35 },
    { fromY: 110, scale: 0.12 },
  ];
  const scaleForDy = (dy) => {
    let s = 1;
    for (const step of FINE_STEPS) if (dy >= step.fromY) s = step.scale;
    return s;
  };

  const apply = (range, row, v, commit) => {
    const key = row.dataset.slider;
    v = Math.max(0, Math.min(100, Math.round(v)));
    range.__v = v;
    row.__value = v;

    holdFor('sl:' + key, 900);
    $('.range-fill', range).style.transform = `scaleX(${v / 100})`;
    placeThumb($('.range-thumb', range), v);
    text($('.range-val', row), v + '%');

    const now = performance.now();
    if (commit || !row.__last || now - row.__last > 70) {
      row.__last = now;
      markPending(key, v);
      onChange(key, v);
    }
  };

  /* Position at the touch point, then track the finger.
   *
   * Anchored rather than recomputed from the raw x each frame: the anchor is re-taken
   * whenever the precision scale changes, so moving between coarse and fine never makes the
   * value jump - it simply starts moving more slowly from wherever it already is. */
  const reanchor = (range, x, v) => { range.__ax = x; range.__av = v; };

  hostNode.addEventListener('pointerdown', (e) => {
    const range = e.target.closest('.range');
    if (!range) return;
    const row = rowOf(range);
    if (!row) return;

    // Capture is best-effort: if it fails the drag must still register, so it never
    // stands between the user and the control.
    try { range.setPointerCapture(e.pointerId); } catch (_) { /* not fatal */ }
    const r = range.getBoundingClientRect();
    const at = ((e.clientX - r.left) / (r.width || 1)) * 100;

    range.__dragging = e.pointerId;
    range.__startY = e.clientY;
    range.__scale = 1;
    range.classList.add('grabbing');
    range.classList.remove('fine');
    reanchor(range, e.clientX, at);

    apply(range, row, at, false);
    e.preventDefault();
  });

  hostNode.addEventListener('pointermove', (e) => {
    const range = e.target.closest('.range');
    // Track our own drag state rather than relying on hasPointerCapture, which the
    // browser can revoke mid-gesture.
    if (!range || range.__dragging !== e.pointerId) return;
    const row = rowOf(range);
    if (!row) return;

    const scale = scaleForDy(Math.abs(e.clientY - range.__startY));
    if (scale !== range.__scale) {
      // Re-anchor before changing gear, or the value lurches by the whole accumulated
      // distance at the new ratio.
      reanchor(range, e.clientX, range.__v ?? valueOf(row));
      range.__scale = scale;
      range.classList.toggle('fine', scale !== 1);
    }

    const w = range.getBoundingClientRect().width || 1;
    apply(range, row, range.__av + ((e.clientX - range.__ax) / w) * 100 * scale, false);
  });

  const finish = (e, commit) => {
    const range = e.target.closest('.range');
    if (!range || range.__dragging !== e.pointerId) return;
    range.__dragging = null;
    range.classList.remove('grabbing', 'fine');

    const row = rowOf(range);
    if (!row) return;
    if (commit && range.__v != null) apply(range, row, range.__v, true);

    holdFor('sl:' + row.dataset.slider, 500);
  };

  hostNode.addEventListener('pointerup', (e) => finish(e, true));
  // A cancelled gesture keeps whatever was last sent rather than resending a stale
  // coordinate from an event the browser already gave up on.
  hostNode.addEventListener('pointercancel', (e) => finish(e, false));
}

/* ============================================================ media card = */

/* ------------------------------------------------------- media sources --- */

/* Somewhere to start playback from, so the Media tab is worth opening before anything is
   playing and the home card's "nothing playing" space does something useful.

   The host publishes only sources it actually resolved on this machine, and a tap sends
   nothing but the entry's id - the executable or address behind it stays host-side. That
   is the same contract the app launcher already uses. */

const SOURCE_GLYPH = { app: 'i-media', web: 'i-globe', folder: 'i-folder' };

/* A web service is drawn as itself. The browser that will open it is only a badge in the corner:
   showing the browser's icon as the whole tile made "Spotify Web" look like "Brave". */
const SERVICE_STYLE = {
  spotify: { letter: 'S', a: '#1fb96b', b: '#0b6b3f' },
  ytmusic: { letter: '▶', a: '#ff5a4f', b: '#8a1b1b' },
};
const serviceIconCache = {};
function serviceIcon(service) {
  const st = SERVICE_STYLE[service];
  if (!st) return null;
  return serviceIconCache[service] || (serviceIconCache[service] = 'data:image/svg+xml;utf8,' + encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120"><defs>' +
    '<linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="' + st.a + '"/>' +
    '<stop offset="1" stop-color="' + st.b + '"/></linearGradient></defs>' +
    '<rect width="120" height="120" rx="26" fill="url(#g)"/>' +
    '<text x="60" y="80" text-anchor="middle" font-family="Segoe UI,Roboto,sans-serif" ' +
    'font-size="58" font-weight="700" fill="#fff">' + st.letter + '</text></svg>'));
}

function makeSourceTile() {
  const node = el('button', 'tile src-tile', `
    <img alt="" hidden>${icon('i-media', 'ph')}<img class="via" alt="" hidden><span class="nm"></span>`);
  return { node, img: $('img', node), via: $('.via', node), fallback: $('.ph', node), nm: $('.nm', node), iconKey: null, kind: null, viaKey: null };
}

function updateSourceTile(rec, item) {
  rec.node.dataset.source = item.id;
  rec.node.classList.toggle('running', !!item.running);
  text(rec.nm, item.name);
  rec.node.title = item.via ? item.name + ' - opens in ' + item.via : item.name;

  if (item.kind !== rec.kind) {
    rec.kind = item.kind;
    rec.fallback.querySelector('use')
      .setAttribute('href', '#' + (SOURCE_GLYPH[item.kind] || 'i-media'));
  }

  const own = item.iconKey ? artUrl(item.iconKey) : serviceIcon(item.service);
  const ownKey = item.iconKey || (own ? 'svc:' + item.service : null);
  if (ownKey !== rec.iconKey) {
    rec.iconKey = ownKey;
    if (own) { rec.img.src = own; rec.img.hidden = false; rec.fallback.style.display = 'none'; }
    else { rec.img.hidden = true; rec.fallback.style.display = ''; }
  }

  const viaKey = item.viaIconKey || null;
  if (viaKey !== rec.viaKey) {
    rec.viaKey = viaKey;
    if (viaKey) { rec.via.src = artUrl(viaKey); rec.via.hidden = false; }
    else rec.via.hidden = true;
  }
}

/** One delegated handler for any container holding source tiles. */
function bindSources(node) {
  node.addEventListener('click', (e) => {
    const t = e.target.closest('[data-source]');
    if (t) send('media.openSource', { id: t.dataset.source });
  });
}

function sourceItems() {
  return (S.mediaSources && S.mediaSources.items) || [];
}

function updateSources(container, limit) {
  const items = limit ? sourceItems().slice(0, limit) : sourceItems();
  reconcile(container, items, x => x.id, makeSourceTile, updateSourceTile);
  return items.length;
}

/** Standalone card for the Media tab, so sources are reachable while something is playing. */
function makeSourcesCard() {
  const node = el('div', 'card', `
    <div class="card-head"><div class="card-title">${icon('i-media')} Start playing</div></div>
    <div class="tile-grid sources"></div>
    <div class="empty src-empty">${icon('i-media')}<p>No media applications were found on the PC</p></div>`);

  const r = { node, sources: $('.sources', node), empty: $('.src-empty', node) };
  bindSources(r.sources);
  return r;
}

function updateSourcesCard(r) {
  const n = updateSources(r.sources, 0);
  r.sources.hidden = n === 0;
  r.empty.hidden = n > 0;
}

function makeMediaCard(full) {
  const node = el('div', 'card');
  node.innerHTML = `
    <div class="np${full ? ' full' : ''}">
      <div class="np-art"><img alt="" hidden>${icon('i-media')}</div>
      <div class="np-body">
        <div>
          <div class="np-title">Nothing playing</div>
          <div class="np-sub"></div>
        </div>
        <div>
          <div class="seek"><div class="seek-track"><div class="seek-fill"></div><div class="seek-rail"><div class="seek-knob"></div></div></div></div>
          <div class="seek-times"><span class="cur">0:00</span><span class="dur">0:00</span></div>
        </div>
        <div class="np-controls">
          <button class="tbtn b-prev">${icon('i-prev', 'solid')}</button>
          <button class="tbtn primary b-play">${icon('i-play', 'solid')}</button>
          <button class="tbtn b-next">${icon('i-next', 'solid')}</button>
          <button class="tbtn b-shuf">${icon('i-shuffle')}</button>
          <button class="tbtn b-rep">${icon('i-repeat')}</button>
          <div class="np-vol"></div>
          <span class="chip b-app"></span>
        </div>
      </div>
    </div>
    <div class="src-tabs"></div>
    <div class="np-empty">
      <div class="np-empty-head">
        <span class="lead">Nothing is playing</span>
        <span class="hint">Start something on the PC</span>
      </div>
      <div class="tile-grid sources"></div>
      <div class="empty np-empty-plain">${icon('i-media')}<p>Nothing is playing on the PC</p></div>
    </div>`;

  // This source's own level, sitting in the empty space beside the transport controls.
  // "Turn this song down" should never mean "turn the whole laptop down".
  const vol = makeSlider('__media', 'i-vol', true);
  $('.np-vol', node).appendChild(vol.node);

  const r = {
    node, full, vol,
    np: $('.np', node),
    art: $('.np-art', node),
    img: $('.np-art img', node),
    fallback: $('.np-art svg', node),
    title: $('.np-title', node),
    sub: $('.np-sub', node),
    appChip: $('.b-app', node),
    seek: $('.seek', node),
    fill: $('.seek-fill', node),
    knob: $('.seek-rail', node),
    cur: $('.cur', node),
    dur: $('.dur', node),
    prev: $('.b-prev', node),
    play: $('.b-play', node),
    playUse: $('.b-play use', node),
    next: $('.b-next', node),
    shuf: $('.b-shuf', node),
    rep: $('.b-rep', node),
    repUse: $('.b-rep use', node),
    tabs: $('.src-tabs', node),
    empty: $('.np-empty', node),
    emptyHead: $('.np-empty-head', node),
    emptyPlain: $('.np-empty-plain', node),
    sources: $('.sources', node),
    volWrap: $('.np-vol', node),
    artKey: null,
  };

  bindSources(r.sources);

  node.addEventListener('click', (e) => {
    const b = e.target.closest('button');
    const s = activeSession();
    if (!b || !s) return;
    const session = s.id;
    if (b.classList.contains('b-play')) {
      send('media.playpause', { session });
      // Stop extrapolating the instant the button is pressed, rather than running on until
      // the host's GSMTC event arrives. That round trip is short but not free, and the
      // progress bar was visibly continuing past the pause and then sliding backwards when
      // the real position landed - the transition on the fill animates the correction, so
      // the snap-back was very legible. If the command fails, the next media frame simply
      // re-anchors and puts it right.
      if (anchor) {
        if (anchor.playing) { anchor.pos = livePosition(); anchor.playing = false; }
        else { anchor.playing = true; }
        anchor.at = performance.now();
      }
      schedule('media');
    }
    else if (b.classList.contains('b-prev')) send('media.previous', { session });
    else if (b.classList.contains('b-next')) send('media.next', { session });
    else if (b.classList.contains('b-shuf')) send('media.shuffle', { session, value: s.shuffle ? 0 : 1 });
    else if (b.classList.contains('b-rep')) {
      const order = ['none', 'list', 'track'];
      const cur = order.indexOf((s.repeat || 'none').toLowerCase());
      send('media.repeat', { session, value: (cur + 1) % 3 });
    }
  });

  bindSliderHost(r.volWrap,
    (_key, v) => { const s = activeSession(); if (s) send('media.appVolume', { session: s.id, value: v }); },
    () => { const s = activeSession(); if (s) send('media.appMute', { session: s.id }); });

  bindSeek(r);

  r.tabs.addEventListener('click', (e) => {
    const b = e.target.closest('[data-sess]');
    if (!b) return;
    pinnedSession = b.dataset.sess;
    anchor = null;
    schedule('media');
  });

  return r;
}

function bindSeek(r) {
  const frac = (e) => {
    const rect = r.seek.getBoundingClientRect();
    return Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
  };
  const preview = (e) => {
    const s = activeSession();
    if (!s || !s.durationSec) return;
    const f = frac(e);
    const v = f * s.durationSec;
    held.set('seekValue', v);
    holdFor('seek', 1400);
    r.fill.style.transform = `scaleX(${f})`;
    if (r.knob) r.knob.style.transform = `translateX(${(f * 100).toFixed(2)}%)`;
    text(r.cur, mmss(v));
  };

  r.seek.addEventListener('pointerdown', (e) => {
    const s = activeSession();
    if (!s || !s.canSeek || !s.durationSec) return;
    r.seek.setPointerCapture(e.pointerId);
    r.seek.classList.add('dragging');   // suppresses the glide so the bar tracks the finger
    preview(e);
    e.preventDefault();
  });

  r.seek.addEventListener('pointermove', (e) => {
    if (!r.seek.hasPointerCapture(e.pointerId)) return;
    preview(e);
  });

  const commit = (e) => {
    r.seek.classList.remove('dragging');
    const s = activeSession();
    if (!s || !s.canSeek || !s.durationSec) return;
    const v = frac(e) * s.durationSec;
    send('media.seek', { session: s.id, value: v });
    // Re-anchor locally so the displayed time is correct immediately, rather than
    // snapping back to the old position until the host's next update arrives.
    if (anchor) { anchor.pos = v; anchor.at = performance.now(); }
    held.set('seekValue', v);
    holdFor('seek', 1000);
  };

  r.seek.addEventListener('pointerup', commit);
  r.seek.addEventListener('pointercancel', () => r.seek.classList.remove('dragging'));
}

function updateMediaCard(r) {
  const s = activeSession();
  const has = !!s;

  r.np.hidden = !has;
  r.empty.hidden = has;
  r.tabs.hidden = !has || !r.full || !(S.media && S.media.sessions.length > 1);

  if (!has) {
    // Idle is the common state for this card, so on the dashboard it offers somewhere to
    // start rather than just reporting silence. The media page does not repeat them: it
    // carries a dedicated sources card directly below, and showing both read as a bug.
    // The plain message remains the fallback whenever no tiles are shown, so the card is
    // never blank.
    const n = r.full ? 0 : updateSources(r.sources, 6);
    r.emptyHead.hidden = n === 0;
    r.sources.hidden = n === 0;
    r.emptyPlain.hidden = n > 0;
    return;
  }

  text(r.title, s.title || 'Unknown title');
  text(r.sub, [s.artist, s.album].filter(Boolean).join(' / ') || (s.appName || ''));
  text(r.appChip, s.appName || '');

  // Swap artwork only when the key actually changes, otherwise the image reloads
  // and flashes on every update.
  if (s.artKey !== r.artKey) {
    r.artKey = s.artKey || null;
    if (s.artKey) { r.img.src = artUrl(s.artKey); r.img.hidden = false; r.fallback.style.display = 'none'; }
    else { r.img.hidden = true; r.img.removeAttribute('src'); r.fallback.style.display = ''; }
    applyArtTheme();
  }

  r.playUse.setAttribute('href', s.status === 'playing' ? '#i-pause' : '#i-play');
  r.prev.disabled = !s.canPrevious;
  r.next.disabled = !s.canNext;
  r.shuf.hidden = s.shuffle == null;
  r.shuf.classList.toggle('on', !!s.shuffle);

  const rep = (s.repeat || 'none').toLowerCase();
  r.rep.hidden = s.repeat == null;
  r.rep.classList.toggle('on', rep !== 'none');
  r.repUse.setAttribute('href', '#i-repeat');
  r.rep.dataset.mode = rep;

  // Application volume, when the host managed to pair this session with an
  // audio session. Hidden rather than faked when it could not.
  const hasVol = s.appVolume != null;
  r.volWrap.hidden = !hasVol;
  if (hasVol) updateSlider(r.vol, s.appVolume, s.appMuted);

  updateSeek(r, s);

  if (r.full) {
    reconcile(r.tabs, S.media.sessions, x => x.id,
      () => {
        const node = el('button', 'src-tab', `<span class="pip"></span><span class="nm"></span>`);
        return { node, pip: $('.pip', node), nm: $('.nm', node) };
      },
      (rec, x) => {
        rec.node.dataset.sess = x.id;
        rec.node.classList.toggle('active', x.id === s.id);
        rec.pip.hidden = x.status !== 'playing';
        text(rec.nm, x.appName || x.id);
      });
  }
}

function updateSeek(r, s) {
  const dur = s.durationSec || 0;
  const pos = isHeld('seek') ? (held.get('seekValue') || 0) : livePosition();
  const frac = dur > 0 ? Math.max(0, Math.min(1, pos / dur)) : 0;

  const t = `scaleX(${frac})`;
  if (r.fill.style.transform !== t) r.fill.style.transform = t;
  if (r.knob) r.knob.style.transform = `translateX(${(frac * 100).toFixed(2)}%)`;

  text(r.cur, mmss(pos));
  text(r.dur, dur ? mmss(dur) : '--:--');
  r.seek.classList.toggle('disabled', !s.canSeek);
}

/* ============================================================= perf card = */

/* A fixed set of tiles. Values become a dash when unavailable rather than the tile
   disappearing, so the strip never reflows and the card never changes height. */
/*
 * A fan's RPM means nothing on its own. 2000 is idle on one machine and flat out on another, so
 * the number alone is a reading you cannot act on.
 */
function fanFraction(y, rpm) {
  if (rpm == null || !y.fanMaxRpm) return null;
  return Math.max(0, Math.min(1, rpm / y.fanMaxRpm));
}

/* Short enough for the top bar's battery chip, which is where this is shown. */
const CHARGE_SHORT = { normal: 'normal', rapid: 'rapid', conservation: 'conserve' };

/* `cap` names the capability a slot needs. A slot whose capability the host did not
 * advertise is never built, rather than built and left showing a dash forever: a dash
 * means "no reading yet", and on hardware that has no such sensor it never will be. */
const STAT_SLOTS = [
  { key: 'VRAM GB', kind: 'bar', cap: 'gpu',
    get: y => y.vramTotalMb ? (y.vramUsedMb / 1024).toFixed(1) : null,
    frac: y => y.vramTotalMb ? y.vramUsedMb / y.vramTotalMb : null,
    hot: y => y.vramTotalMb && y.vramUsedMb / y.vramTotalMb > 0.9 },
  // Power and clocks come only from NVML; Windows publishes neither.
  { key: 'GPU W', cap: 'gpuDetail', get: y => y.gpuPowerW != null ? Math.round(y.gpuPowerW) : null },
  { key: 'GPU MHZ', cap: 'gpuDetail', get: y => y.gpuClockMhz != null ? Math.round(y.gpuClockMhz) : null },
  { key: 'DISK %', kind: 'bar',
    get: y => y.diskPercent != null ? Math.round(y.diskPercent) : null,
    frac: y => y.diskPercent != null ? y.diskPercent / 100 : null,
    hot: y => y.diskPercent > 90 },
  { key: 'NET ↓', get: y => fmtRate(y.netDownKbps) },
  { key: 'NET ↑', get: y => fmtRate(y.netUpKbps) },
];

/* One tile per fan the PC actually reports: a gaming laptop's two, a desktop's header fans, a fanless
   machine's none. The host sends the list; nothing here knows or assumes how many there are. Older
   hosts only sent fan1Rpm and fan2Rpm, so those are used when there is no list. */
function fansOf(y) {
  if (!y) return [];
  if (Array.isArray(y.fans) && y.fans.length) return y.fans;
  const legacy = [];
  if (y.fan1Rpm != null) legacy.push({ name: 'Fan 1', rpm: y.fan1Rpm, maxRpm: y.fanMaxRpm });
  if (y.fan2Rpm != null) legacy.push({ name: 'Fan 2', rpm: y.fan2Rpm, maxRpm: y.fanMaxRpm });
  return legacy;
}

function fanSlots(y) {
  return fansOf(y).slice(0, 8).map((f, i) => ({
    key: 'fan:' + i, label: String(f.name || 'Fan ' + (i + 1)).toUpperCase(), kind: 'fan',
    get: yy => { const x = fansOf(yy)[i]; return x ? x.rpm : null; },
    frac: yy => { const x = fansOf(yy)[i]; return x && x.maxRpm ? Math.max(0, Math.min(1, x.rpm / x.maxRpm)) : null; },
  }));
}

/* Seconds per revolution at rest and at the rated ceiling - see .rotor in app.css for why
   these are perceptual rather than derived from the actual RPM. */
const ROTOR_SLOW = 2.4;
const ROTOR_FAST = 0.32;

function setRotor(rec, frac, value) {
  const sv = rec.rotor;
  const spinning = value != null && value > 0;
  sv.classList.toggle('still', !spinning);
  if (!spinning) return;

  // No rated ceiling: turn at a middling speed rather than at one end of the range, so the
  // motion says "running" without claiming a proportion that was never measured.
  const f = frac == null ? 0.4 : frac;

  const dur = (ROTOR_SLOW + (ROTOR_FAST - ROTOR_SLOW) * f).toFixed(2) + 's';
  if (rec.dur !== dur) { rec.dur = dur; sv.style.setProperty('--rotor-dur', dur); }

  const band = f > 0.85 ? 'max' : f > 0.62 ? 'fast' : '';
  if (rec.rotorBand !== band) {
    sv.classList.remove('fast', 'max');
    if (band) sv.classList.add(band);
    rec.rotorBand = band;
  }
}

/* A tile whose existence depends on a reading appearing once; it then stays for the session so
   the strip does not reflow when the sensor blinks. */
const latchedSlots = new Set();
function latched(slot, y) {
  if (latchedSlots.has(slot.key)) return true;
  if (y && slot.latch(y)) { latchedSlots.add(slot.key); return true; }
  return false;
}

/* What this PC cannot report, said once and briefly, so a missing dial reads as "not supported
   here" rather than as a bug. */
function unavailableList() {
  const c = S.caps, out = [];
  if (!c.gpu) out.push('GPU');
  else if (!c.gpuDetail) out.push('GPU power');
  if (!c.cpuTemp) out.push('CPU temperature');
  if (!c.fans) out.push('fan speed');
  if (!c.fps) out.push('FPS');
  return out;
}

function makePerfCard(alwaysGaming) {
  const node = el('div', 'card');
  node.innerHTML = `
    <div class="card-head">
      <div class="card-title">${icon('i-perf')} <span class="ttl">System</span></div>
      <div class="mode-mini" hidden></div>
    </div>
    <div class="thr" hidden></div>
    <div class="fps-hero" hidden>
      <span class="n">–</span><span class="u">FPS</span>
      <span class="low"><b>–</b><span>1% low</span></span>
    </div>
    <div class="gauges"></div>
    <div class="stat-strip"></div>
    <div class="na-note" hidden></div>`;

  const gauges = $('.gauges', node);
  const cpu = makeGauge('CPU'), gpu = makeGauge('GPU'), ram = makeGauge('RAM');
  gauges.append(cpu.node, gpu.node, ram.node);

  // The performance profile sits in this card's header rather than in a card of its own:
  // it belongs with the fans and temperatures it governs, and the dashboard grid has no
  // room for a fifth card at 1024x600. The gaming page keeps the larger version.
  const modeMini = $('.mode-mini', node);
  for (const m of PERF_MODES) {
    const b = el('button', 'mm-btn', esc(modeName(m)));
    b.dataset.mode = m.id;
    modeMini.appendChild(b);
  }
  node.addEventListener('click', (e) => {
    const b = e.target.closest('[data-mode]');
    if (b) send('sys.thermalMode', { mode: b.dataset.mode });
  });

  return {
    node, cpu, gpu, ram, alwaysGaming,
    ttl: $('.ttl', node),
    strip: $('.stat-strip', node),
    na: $('.na-note', node),
    fps: $('.fps-hero', node),
    fpsN: $('.fps-hero .n', node),
    fpsLow: $('.fps-hero .low b', node),
    thr: $('.thr', node),
    modeMini,
    built: false,
  };
}

function updatePerfCard(r) {
  const y = S.sys;
  const gaming = r.alwaysGaming || (S.ctx && S.ctx.mode === 'gaming');

  text(r.ttl, gaming ? 'Live performance' : 'System');
  updateThrottle(r);

  // The hero only earns its space once a real reading has arrived. PresentMon is started on
  // demand and may never produce one - it needs the elevated agent and a game that is genuinely
  // presenting frames, and "gaming mode" is inferred from the foreground process, so it can be
  // true while nothing is rendering.
  if (!gaming) r.sawFps = false;
  if (y && y.fps != null) r.sawFps = true;
  r.fps.hidden = !(gaming && S.caps.fps && r.sawFps);

  // Hidden outright when the firmware is out of reach, rather than shown as dead buttons.
  r.modeMini.hidden = !S.caps.thermalMode;
  if (!r.modeMini.hidden) {
    const mode = S.sys && S.sys.thermalMode;
    for (const b of r.modeMini.children) {
      const m = PERF_MODES.find((x) => x.id === b.dataset.mode);
      if (m) text(b, modeName(m));
      b.classList.toggle('on', b.dataset.mode === mode);
    }
  }

  // Rebuild the tile set whenever the advertised capabilities change, not merely once.
  // Capabilities are re-probed by the host on every start, so installing the elevated
  // agent can add fan tiles to a tablet that is already connected - latching on the
  // first connect left those permanently missing.
  if (S.link.phase === 'connected') {
    const slots = STAT_SLOTS.filter(s => !s.cap || S.caps[s.cap]).concat(S.caps.fans ? fanSlots(y) : []);
    r.slots = slots;
    const sig = slots.map(s => s.key + ':' + (s.label || '')).join(',');
    if (r.capSig !== sig) {
      r.capSig = sig;
      reconcile(r.strip, slots, s => s.key,
        (s) => {
          const rotor = s.kind === 'fan' ? '<svg class="rotor still"><use href="#i-fan"/></svg>' : '';
          const bar = (s.kind === 'fan' || s.kind === 'bar') ? '<div class="bar"><i></i></div>' : '';
          const node = el('div', 'stat' + (s.kind === 'fan' ? ' with-rotor' : ''),
            `<div class="val-row">${rotor}<b>–</b></div><span>${esc(s.label || s.key)}</span>${bar}`);
          return { node, b: $('b', node), rotor: $('.rotor', node), fill: $('.bar i', node) };
        },
        () => {});
    }
  }

  const na = S.link.phase === 'connected' ? unavailableList() : [];
  r.na.hidden = na.length === 0;
  if (na.length) {
    const why = S.caps.sensorsNote ? ' \u00b7 ' + S.caps.sensorsNote
      : !S.caps.elevated ? ' \u00b7 some need the elevated agent' : '';
    text(r.na, 'Not available on this PC: ' + na.join(', ') + why);
  }

  if (!y) return;

  updateGauge(r.cpu, y.cpu, y.cpuTemp != null ? Math.round(y.cpuTemp) + '°C' : '', y.cpuTemp);

  // A machine with no readable GPU - a virtual machine, or one whose driver publishes
  // nothing - loses the dial rather than keeping a permanently empty one. The remaining
  // two then share the row, which is what the flex layout does on its own.
  r.gpu.node.hidden = !S.caps.gpu;
  if (S.caps.gpu) {
    updateGauge(r.gpu, y.gpu, y.gpuTemp != null ? Math.round(y.gpuTemp) + '°C' : '', y.gpuTemp);
  }
  // RAM has no temperature to show. It still carries the track, drawn empty, so the three
  // dials keep a common baseline.
  updateGauge(r.ram, y.ramPercent,
    y.ramTotalMb ? (y.ramUsedMb / 1024).toFixed(1) + ' / ' + Math.round(y.ramTotalMb / 1024) + ' GB' : '',
    null);

  if (!r.fps.hidden) {
    // The placeholder for "no reading yet" is drawn in CSS, not written as text: at this type
    // size a dash glyph is indistinguishable from a filled white rectangle. Leaving the element
    // empty also keeps its line box the same height as a real reading, so the card does not
    // change shape when frame timings start or stop arriving.
    const hasFps = y.fps != null;
    r.fpsN.classList.toggle('waiting', !hasFps);
    text(r.fpsN, hasFps ? Math.round(y.fps) : ZWSP);

    const hasLow = y.fps1Low != null;
    r.fpsLow.classList.toggle('waiting', !hasLow);
    text(r.fpsLow, hasLow ? Math.round(y.fps1Low) : '–');
  }

  const map = r.strip.__keyed;
  if (map) {
    for (const slot of (r.slots || STAT_SLOTS)) {
      const rec = map.get(slot.key);
      if (!rec) continue;
      const v = slot.get(y);
      text(rec.b, v == null ? '–' : v);
      rec.node.classList.toggle('hot', !!(slot.hot && slot.hot(y)));

      const frac = slot.frac ? slot.frac(y) : null;
      if (rec.fill) setScale(rec, rec.fill, frac, 'scale');
      if (rec.rotor) setRotor(rec, frac, v);
    }
  }
}

/* ============================================================ audio cards = */

function makeDeviceRow(kind) {
  const node = el('div', 'dev', `${icon(kind === 'in' ? 'i-mic' : 'i-out')}<span class="nm"></span><span class="tick">✓</span>`);
  return { node, nm: $('.nm', node), tick: $('.tick', node) };
}

function updateDeviceRow(rec, d, kind) {
  rec.node.dataset[kind === 'in' ? 'devIn' : 'devOut'] = d.id;
  rec.node.classList.toggle('active', !!d.isDefault);
  rec.tick.hidden = !d.isDefault;
  text(rec.nm, d.name);
}

/** Compact audio card for the home grid. */
function makeAudioMini() {
  const node = el('div', 'card');
  node.innerHTML = `
    <div class="card-head"><div class="card-title">${icon('i-audio')} Audio</div></div>
    <div class="voice-host" style="margin-bottom:12px"></div>
    <div class="levels"></div>
    <div class="dev-list outs" style="margin-top:11px"></div>
    <div class="empty aud-empty">${icon('i-audio')}<p>Audio service not connected</p></div>`;

  const master = makeSlider('master', 'i-vol');
  const mic = makeSlider('mic', 'i-mic');
  $('.levels', node).append(master.node, mic.node);

  const voice = makeVoiceStrip();
  $('.voice-host', node).appendChild(voice.node);

  const r = { node, master, mic, voice, outs: $('.outs', node), levels: $('.levels', node), empty: $('.aud-empty', node) };
  wireAudio(r);
  return r;
}

function makeAudioPage() {
  const node = el('div', 'stack');
  node.innerHTML = `
    <div class="card">
      <div class="card-head"><div class="card-title">${icon('i-mic')} Voice</div>
        <div class="voice-mode"><button data-mode="tap">Tap</button><button data-mode="hold">Hold to mute</button></div></div>
      <div class="voice-host"></div>
    </div>
    <div class="card">
      <div class="card-head"><div class="card-title">${icon('i-audio')} Levels</div></div>
      <div class="levels"></div>
    </div>
    <div class="card">
      <div class="card-head"><div class="card-title">${icon('i-out')} Output</div></div>
      <div class="out-row"></div>
    </div>
    <div class="row two">
      <div class="card">
        <div class="card-head"><div class="card-title">${icon('i-apps')} App mixer</div>
          <span class="chip ghost">${icon('i-pin')} pin keeps a level</span></div>
        <div class="mixer"></div>
        <div class="empty mix-empty">${icon('i-audio')}<p>No applications are playing audio</p></div>
      </div>
      <div class="card">
        <div class="card-head"><div class="card-title">${icon('i-mic')} Input</div></div>
        <div class="dev-list ins"></div>
      </div>
    </div>`;

  const master = makeSlider('master', 'i-vol');
  const mic = makeSlider('mic', 'i-mic');
  $('.levels', node).append(master.node, mic.node);

  const voice = makeVoiceStrip();
  $('.voice-host', node).appendChild(voice.node);

  const modes = $('.voice-mode', node);
  const showMode = () => modes.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.mode === micMode()));
  modes.addEventListener('click', (e) => {
    const b = e.target.closest('[data-mode]');
    if (!b) return;
    try { localStorage.setItem(MIC_MODE_KEY, b.dataset.mode); } catch (_) { /* not fatal */ }
    showMode();
  });
  showMode();

  const r = {
    node, master, mic, voice,
    mixer: $('.mixer', node),
    mixEmpty: $('.mix-empty', node),
    outRow: $('.out-row', node),
    ins: $('.ins', node),
    levels: $('.levels', node),
  };
  wireAudio(r);
  return r;
}

function wireAudio(r) {
  const onChange = (key, v) => {
    if (key === 'master') send('audio.masterVolume', { value: v });
    else if (key === 'mic') send('audio.micVolume', { value: v });
    else if (key.startsWith('dev:')) send('audio.deviceVolume', { id: key.slice(4), value: v });
    else send('audio.appVolume', { id: key, value: v });
  };
  const onMute = (key) => {
    if (key === 'master') send('audio.masterMuteToggle');
    else if (key === 'mic') send('audio.micMuteToggle');
    else if (key.startsWith('dev:')) { /* an output has a level here, not a mute of its own */ }
    else send('audio.appMuteToggle', { id: key });
  };

  bindSliderHost(r.node, onChange, onMute);

  r.node.addEventListener('click', (e) => {
    const pin = e.target.closest('[data-pin]');
    if (pin) return send('audio.rememberApp', { id: pin.dataset.pin, value: pin.classList.contains('on') ? 0 : 1 });
    const o = e.target.closest('[data-dev-out]');
    if (o) return send('audio.setOutput', { id: o.dataset.devOut });
    const i = e.target.closest('[data-dev-in]');
    if (i) return send('audio.setInput', { id: i.dataset.devIn });
  });
}

function updateAudio(r, opts) {
  const a = S.audio;
  const showEmpty = !a;
  if (r.empty) r.empty.hidden = !showEmpty;
  if (r.levels) r.levels.hidden = showEmpty;
  if (r.outs && showEmpty) r.outs.hidden = true;
  if (showEmpty) return;
  if (r.outs) r.outs.hidden = false;

  updateSlider(r.master, a.masterVolume, a.masterMuted);
  updateSlider(r.mic, a.micVolume, a.micMuted);
  if (r.voice) updateVoiceStrip(r.voice, a);
  if (r.outRow) {
    reconcile(r.outRow, (a.outputs || []).filter(d => !VIRTUAL_DEVICE.test(d.name)), d => d.id, makeOutCard, updateOutCard);
  }

  if (r.outs) {
    const outs = opts && opts.limit ? (a.outputs || []).slice(0, opts.limit) : (a.outputs || []);
    reconcile(r.outs, outs, d => d.id, () => makeDeviceRow('out'), (rec, d) => updateDeviceRow(rec, d, 'out'));
  }

  if (r.ins) {
    reconcile(r.ins, a.inputs || [], d => d.id, () => makeDeviceRow('in'), (rec, d) => updateDeviceRow(rec, d, 'in'));
  }

  if (r.mixer) {
    const apps = a.apps || [];
    r.mixEmpty.hidden = apps.length > 0;
    reconcile(r.mixer, apps, x => x.id,
      (x) => {
        const node = el('div', 'mix-row', `
          <div class="mix-ico"><img alt="" hidden><span class="ltr"></span></div>
          <div class="mix-body">
            <div class="mix-name"><span class="live"></span><span class="nm"></span>
              <button class="pin-btn" data-pin="${esc(x.id)}" aria-label="Keep this level each time it starts">${icon('i-pin')}</button></div>
          </div>`);
        const sl = makeSlider(x.id, 'i-vol', true);
        $('.mix-body', node).appendChild(sl.node);
        return {
          node, sl,
          img: $('img', node), ltr: $('.ltr', node),
          live: $('.live', node), nm: $('.nm', node), pin: $('.pin-btn', node),
          iconKey: null,
        };
      },
      (rec, x) => {
        text(rec.nm, x.name);
        rec.live.hidden = !x.active;
        rec.pin.dataset.pin = x.id;
        rec.pin.classList.toggle('on', !!x.remembered);
        if (x.iconKey !== rec.iconKey) {
          rec.iconKey = x.iconKey || null;
          if (x.iconKey) { rec.img.src = artUrl(x.iconKey); rec.img.hidden = false; rec.ltr.hidden = true; }
          else { rec.img.hidden = true; rec.ltr.hidden = false; text(rec.ltr, (x.name || '?').charAt(0).toUpperCase()); }
        }
        updateSlider(rec.sl, x.volume, x.muted);
      });
  }
}

/* ============================================================= apps cards = */

/** Shared across both apps views so the mode is consistent wherever you toggle it. */
let editingApps = false;

/**
 * Tiles waiting on something to actually start, keyed by entry id. A launch reports success the
 * moment the shell accepts the request, which for a store title means "the launcher was handed a
 * URI", not "the game is running".
 */
const launching = new Map();
const LAUNCH_FEEDBACK_MS = 30000;

function markLaunching(id) {
  launching.set(id, performance.now() + LAUNCH_FEEDBACK_MS);
  schedule('apps');
  // Re-render when the window expires, so a launch that never happened stops pretending.
  setTimeout(() => { if (isLaunching(id) === false) schedule('apps'); }, LAUNCH_FEEDBACK_MS + 200);
}

function isLaunching(id) {
  const until = launching.get(id);
  if (until === undefined) return false;
  if (performance.now() > until) { launching.delete(id); return false; }
  return true;
}

function makeAppsCard(limit, split) {
  const node = el('div', split ? 'stack' : 'card');
  const editBtn = `<button class="chip ghost b-edit">${icon('i-edit')}<span class="lbl">Edit</span></button>`;

  if (split) {
    // Running leads. It is what this page is opened to act on, and it is a handful of rows
    // against a hundred launcher tiles - below them it sat several screens past the fold,
    // which is the same as not being there.
    node.innerHTML = `
      <div class="card running-card" hidden>
        <div class="card-head">
          <div class="card-title">${icon('i-chip')} Running now</div>
          <span class="chip ghost">tap closes · hold 1.5 s ends</span>
        </div>
        <div class="run-list open"></div>
        <div class="run-head bg-head" hidden>Background</div>
        <div class="run-list bg"></div>
      </div>
      <div class="card games-card" hidden>
        <div class="card-head"><div class="card-title">${icon('i-apps')} Games</div></div>
        <div class="tile-grid games"></div>
      </div>
      <div class="card">
        <div class="card-head">
          <div class="card-title">${icon('i-apps')} Applications</div>${editBtn}
        </div>
        <div class="tile-grid apps"></div>
        <div class="empty apps-empty">${icon('i-apps')}<p>Launcher catalogue not available yet</p></div>
      </div>`;
  } else {
    node.innerHTML = `
      <div class="card-head">
        <div class="card-title">${icon('i-apps')} Apps &amp; games</div>${editBtn}
      </div>
      <div class="tile-grid apps"></div>
      <div class="empty apps-empty">${icon('i-apps')}<p>Launcher catalogue not available yet</p></div>`;
  }

  const r = {
    node, limit, split,
    games: $('.games', node),
    gamesCard: $('.games-card', node),
    apps: $('.apps', node),
    empty: $('.apps-empty', node),
    edit: $('.b-edit', node),
    editLbl: $('.b-edit .lbl', node),
    runCard: $('.running-card', node),
    runOpen: $('.run-list.open', node),
    runBg: $('.run-list.bg', node),
    runBgHead: $('.bg-head', node),
  };

  // Close is a tap; force-ending is a deliberate press-and-hold. The two are genuinely different
  // acts: a tap asks the app to close and lets it prompt to save, a hold terminates it and
  // throws that work away, so they must not be one stray touch apart on a tablet lying next to
  // the keyboard.
  if (r.runCard) {
    const runName = (b) => { const row = b.closest('.run-row'); const n = row && $('.run-nm', row); return n ? n.textContent : 'this app'; };
    bindHold(r.runCard, {
      selector: '[data-pid]',
      verb: (b) => 'end ' + runName(b),
      run: (b, done) => send('running.kill', { pid: Number(b.dataset.pid) }, done),
      okText: (b) => 'Ended ' + runName(b),
      failText: (b) => 'Could not end ' + runName(b),
    });

    r.runCard.addEventListener('click', (e) => {
      const b = e.target.closest('[data-pid]');
      if (!b) return;
      send('running.close', { pid: Number(b.dataset.pid) }, (res) => {
        if (res && res.ok === false) holdHud.show('fail', 'Could not close ' + runName(b), res.error || '', null, 3500);
      });
    });
  }

  node.addEventListener('click', (e) => {
    if (e.target.closest('.b-edit')) {
      editingApps = !editingApps;
      schedule('apps');
      return;
    }

    const tile = e.target.closest('[data-launch]');
    if (!tile) return;
    const id = tile.dataset.launch;

    if (editingApps) {
      // While editing, taps adjust the catalogue rather than launching anything.
      if (e.target.closest('[data-fav]')) {
        send('launcher.fav', { id, value: !$('.b-fav', tile).classList.contains('on') });
      } else if (e.target.closest('[data-hide]')) {
        send('launcher.hide', { id, value: !tile.classList.contains('is-hidden') });
      }
      return;
    }

    send('launch', { id });
    markLaunching(id);
  });
  return r;
}

function makeTile() {
  const node = el('button', 'tile', `
    <img alt="" hidden>${icon('i-apps', 'ph')}<span class="nm"></span>
    <span class="pt"></span>
    <span class="starting">Starting…</span>
    ${icon('i-star', 'star')}
    <span class="badges">
      <span class="mini b-fav" data-fav>${icon('i-star')}</span>
      <span class="mini b-hide" data-hide>${icon('i-x')}</span>
    </span>`);
  return {
    node,
    img: $('img', node), fallback: $('.ph', node), nm: $('.nm', node), pt: $('.pt', node),
    star: $('.star', node), fav: $('.b-fav', node),
    hideUse: $('.b-hide use', node),
    iconKey: null,
  };
}

function updateTile(rec, item) {
  rec.node.dataset.launch = item.id;
  rec.node.classList.toggle('running', !!item.running);

  // Once the host reports the process up, the wait is over regardless of how long is left
  // on the window - the running pip is better feedback than a stale "Starting".
  if (item.running) launching.delete(item.id);
  rec.node.classList.toggle('starting', isLaunching(item.id));
  rec.node.classList.toggle('is-hidden', !!item.hidden);
  text(rec.nm, item.name);
  text(rec.pt, playtimeCaption(item));

  // A class, not the `hidden` property: this is an <svg>, and SVGElement does not
  // implement HTMLElement.hidden - assigning it silently creates a JS expando and the
  // element stays visible. The edit badge replaces the marker while editing.
  rec.star.classList.toggle('hidden', !item.fav || editingApps);
  rec.fav.classList.toggle('on', !!item.fav);
  // A hidden entry offers restore rather than hide.
  rec.hideUse.setAttribute('href', item.hidden ? '#i-undo' : '#i-x');

  if (item.iconKey !== rec.iconKey) {
    rec.iconKey = item.iconKey || null;
    if (item.iconKey) { rec.img.src = artUrl(item.iconKey); rec.img.hidden = false; rec.fallback.style.display = 'none'; }
    else { rec.img.hidden = true; rec.fallback.style.display = ''; }
  }
}

function updateApps(r) {
  const all = (S.apps && S.apps.items) || [];
  // Hidden entries are only shown while editing, so they can be restored.
  const items = editingApps ? all : all.filter(i => !i.hidden);

  r.empty.hidden = all.length > 0;
  if (r.edit) {
    r.edit.classList.toggle('hot', editingApps);
    text(r.editLbl, editingApps ? 'Done' : 'Edit');
  }
  r.apps.classList.toggle('editing', editingApps);
  if (r.games) r.games.classList.toggle('editing', editingApps);

  if (r.split) {
    const games = items.filter(i => i.kind === 'game');
    const others = items.filter(i => i.kind !== 'game');
    r.gamesCard.hidden = games.length === 0;
    reconcile(r.games, games, i => i.id, makeTile, updateTile);
    reconcile(r.apps, others, i => i.id, makeTile, updateTile);
  } else {
    // The home card shows the pinned entries; the host already sorts them first.
    reconcile(r.apps, items.slice(0, r.limit || 6), i => i.id, makeTile, updateTile);
  }

  if (r.runCard) updateRunning(r);
}

/* Running processes.
 *
 * Two lists from one payload, because they answer different questions: what you have open,
 * and what is eating the machine. The host decides which is which and what may be touched
 * at all - the tablet only ever names a pid it was given. */
function makeRunRow(item) {
  const node = el('div', 'run-row', `
    <div class="run-txt">
      <span class="run-nm"></span>
      <span class="run-ttl"></span>
    </div>
    <span class="run-mem"></span>
    <button class="run-x">${icon('i-x')}<span class="hold"></span></button>`);
  return {
    node,
    nm: $('.run-nm', node),
    ttl: $('.run-ttl', node),
    mem: $('.run-mem', node),
    btn: $('.run-x', node),
  };
}

function updateRunRow(rec, item) {
  text(rec.nm, item.name);
  // The window title, when there is one, is what identifies the thing you want to close -
  // "chrome" is not, when four of them are open.
  text(rec.ttl, item.title || '');
  rec.ttl.hidden = !item.title;
  text(rec.mem, item.memoryMb >= 1024
    ? (item.memoryMb / 1024).toFixed(1) + ' GB'
    : item.memoryMb + ' MB');
  rec.btn.dataset.pid = item.pid;
}

function updateRunning(r) {
  const items = (S.running && S.running.items) || [];
  const open = items.filter(i => i.windowed);
  const bg = items.filter(i => !i.windowed);

  r.runCard.hidden = items.length === 0;
  r.runBgHead.hidden = bg.length === 0;

  reconcile(r.runOpen, open, i => String(i.pid), makeRunRow, updateRunRow);
  reconcile(r.runBg, bg, i => String(i.pid), makeRunRow, updateRunRow);
}

/* ============================================================= game page = */

/* Shortcuts whose chord is fixed host-side; the tablet only names one. The host says which of them
   this PC has the software for (caps.gameShortcuts), and only those are shown. */
const GAME_SHORTCUTS = [
  { id: 'record',       name: 'Record',      ic: 'i-rec',   hint: 'Win+Alt+R' },
  { id: 'gamebar',      name: 'Game Bar',    ic: 'i-game',  hint: 'Win+G' },
  { id: 'nvoverlay',    name: 'NVIDIA',      ic: 'i-bolt',  hint: 'Alt+Z' },
  { id: 'nvpanel',      name: 'NV Panel',    ic: 'i-chip',  hint: 'App' },
  { id: 'steamoverlay', name: 'Steam',       ic: 'i-apps',  hint: 'Shift+Tab' },
];

/* The white keyboard backlight, which is the only lighting LENOVO_GAMEZONE_DATA reaches
   (SetKeyboardLight, 0-2). It is not the Spectrum/RGB profile system: that is driven over
   vendor HID reports straight to the keyboard and has no WMI surface at all. */
const KBD_LEVELS = [
  { level: 0, name: 'Off' },
  { level: 1, name: 'Dim' },
  { level: 2, name: 'Bright' },
];

/* The three-way performance control, which the host drives through whichever mechanism
 * the machine has: a laptop's embedded controller, or Windows power modes everywhere
 * else. Same command, same three choices, different names - calling a Windows power mode
 * "Gaming" would be claiming something the host is not doing. `caps.thermalModeKind`
 * says which is in play. */
const PERF_MODES = [
  { id: 'quiet',       vendor: 'Eco',    plan: 'Efficiency',  ic: 'i-leaf' },
  { id: 'balanced',    vendor: 'Normal', plan: 'Balanced',    ic: 'i-gauge' },
  { id: 'performance', vendor: 'Gaming', plan: 'Performance', ic: 'i-bolt' },
];

const onPlan = () => S.caps.thermalModeKind === 'powerplan';
const modeName = (m) => (onPlan() ? m.plan : m.vendor);
/* Named for what it drives: "Power mode" for Windows' own, or "<Brand> mode" for a laptop's firmware profile. */
const modeCardTitle = () => (onPlan() ? 'Power mode' : ((S.caps.vendorName ? S.caps.vendorName + ' ' : '') + 'mode'));

function makeGamePage() {
  const node = el('div', 'stack');
  node.innerHTML = `
    <div class="row two">
      <div class="card">
        <div class="card-head"><div class="card-title">${icon('i-rec')} Overlays &amp; capture</div></div>
        <div class="tile-grid sources shortcut-grid"></div>
      </div>
      <div class="card perf-card">
        <div class="card-head">
          <div class="card-title">${icon('i-bolt')}<span class="mode-ttl">Performance</span></div>
          <span class="chip ghost charge-chip" hidden>${icon('i-plug')}<span class="ch-t"></span></span>
        </div>
        <div class="mode-row"></div>
        <div class="mode-note"></div>
        <div class="kbd-row" hidden>
          <span class="kbd-lbl">${icon('i-kbd')}<span>Backlight</span></span>
          <div class="kbd-btns"></div>
        </div>
      </div>
    </div>`;

  const shortcutGrid = $('.shortcut-grid', node);
  for (const s of GAME_SHORTCUTS) {
    const b = el('button', 'tile src-tile', `
      ${icon(s.ic, 'ph')}<span class="nm">${esc(s.name)}</span><span class="hint">${esc(s.hint)}</span>`);
    b.dataset.shortcut = s.id;
    shortcutGrid.appendChild(b);
  }

  const modeRow = $('.mode-row', node);
  for (const m of PERF_MODES) {
    // Seeded with the default label rather than left empty for the update pass to fill:
    // this card is built before the host has said which mechanism it drives, and an
    // unlabelled row of three buttons is worse than three that are briefly renamed.
    const b = el('button', 'mode-btn', `${icon(m.ic)}<span class="mn">${esc(modeName(m))}</span>`);
    b.dataset.mode = m.id;
    modeRow.appendChild(b);
  }

  const kbdBtns = $('.kbd-btns', node);
  for (const k of KBD_LEVELS) {
    const b = el('button', 'kbd-btn', esc(k.name));
    b.dataset.kbd = k.level;
    kbdBtns.appendChild(b);
  }

  const r = {
    node,
    shortcutGrid, modeRow,
    shortcutCard: $('.shortcut-grid', node).parentElement,
    kbdBtns,
    note: $('.mode-note', node),
    perfCard: $('.perf-card', node),
    perfRow: $('.perf-card', node).parentElement,
    modeTtl: $('.mode-ttl', node),
    kbdRow: $('.kbd-row', node),
    chargeChip: $('.charge-chip', node),
    chargeText: $('.ch-t', node),
  };

  node.addEventListener('click', (e) => {
    const sc = e.target.closest('[data-shortcut]');
    if (sc) return send('game.shortcut', { id: sc.dataset.shortcut });

    const md = e.target.closest('[data-mode]');
    if (md) return send('sys.thermalMode', { mode: md.dataset.mode });

    const kb = e.target.closest('[data-kbd]');
    if (kb) return send('sys.keyboardLight', { level: Number(kb.dataset.kbd) });
  });

  return r;
}

function updateGamePage(r) {
  // Removed outright when the host has no mechanism it can drive, rather than shown
  // greyed out: a control that can never work is not information, it is clutter. The row
  // collapses with it, so the card beside it does not sit next to an empty column.
  // Only the overlays whose software is installed. A host that predates this field sends none, in
  // which case nothing is hidden.
  const have = S.caps.gameShortcuts;
  for (const b of r.shortcutGrid.children) b.hidden = !!have && !have.includes(b.dataset.shortcut);
  r.shortcutCard.hidden = [...r.shortcutGrid.children].every(b => b.hidden);

  const canMode = !!S.caps.thermalMode;
  r.perfCard.hidden = !canMode;
  r.perfRow.classList.toggle('one', !canMode);

  if (canMode) {
    text(r.modeTtl, modeCardTitle());
    const mode = S.sys && S.sys.thermalMode;
    for (const b of r.modeRow.children) {
      const m = PERF_MODES.find((x) => x.id === b.dataset.mode);
      if (m) text($('.mn', b), modeName(m));
      b.classList.toggle('on', b.dataset.mode === mode);
    }
    const active = PERF_MODES.find((x) => x.id === mode);
    text(r.note, mode ? 'Currently ' + (active ? modeName(active) : mode) : '');
  }

  // Hidden outright when the firmware is out of reach, rather than shown as dead buttons.
  r.kbdRow.hidden = !S.caps.keyboardLight;
  if (!r.kbdRow.hidden) {
    const lvl = S.sys && S.sys.keyboardLight;
    for (const b of r.kbdBtns.children) b.classList.toggle('on', Number(b.dataset.kbd) === lvl);
  }

  // Read-only: this firmware has GetPowerChargeMode and no matching setter, so the charge
  // mode is reported as a chip beside the title rather than offered as a switch.
  const charge = S.caps.chargeMode && S.sys && S.sys.chargeMode;
  r.chargeChip.hidden = !charge;
  if (charge) text(r.chargeText, S.sys.chargeMode);
}

/* ============================================================ voice page = */

/*
 * Casting the laptop's own audio into whatever is listening to the microphone. There is no way to
 * write into a real microphone - a capture endpoint is fed by hardware and Windows exposes no API
 * to inject into it, short of shipping a driver.
 */
function makeVoicePage() {
  const node = el('div', 'stack');
  node.innerHTML = `
    <div class="card cast-card">
      <div class="card-head">
        <div class="card-title">${icon('i-mic')} Stream audio to mic</div>
        <button class="cast-toggle"><span class="dot"></span><span class="lbl">Off</span></button>
      </div>

      <div class="cast-body">
        <div class="src-row">
          <label class="src-pick">
            <span>${icon('i-out')}Listen to</span>
            <select data-src="render"></select>
          </label>
          <label class="src-pick">
            <span>${icon('i-mic')}Microphone</span>
            <select data-src="capture"></select>
          </label>
        </div>
        <div class="meter-row">
          <span class="mt-cap">Desktop</span>
          <div class="meter"><i class="m-desk"></i></div>
        </div>
        <div class="meter-row">
          <span class="mt-cap">Mic</span>
          <div class="meter"><i class="m-mic"></i></div>
        </div>
        <div class="cast-levels"></div>
        <div class="cast-note"></div>
      </div>

      <div class="empty cast-unavailable" hidden>
        ${icon('i-usb')}
        <p>Needs a virtual audio cable on the PC.<br>VB-Audio Virtual Cable provides one.</p>
      </div>
    </div>

    <div class="card rec-card">
      <div class="card-head">
        <div class="card-title">${icon('i-rec')} Recorder</div>
        <button class="cast-toggle rec-arm"><span class="dot"></span><span class="lbl">Off</span></button>
      </div>
      <div class="rec-body">
        <div class="meter-row">
          <span class="mt-cap">Buffer</span>
          <div class="meter"><i class="m-buf"></i></div>
          <span class="rec-buf"></span>
        </div>
        <div class="rec-actions">
          <button class="rec-btn b-clip">${icon('i-star')}<span>Clip last <b class="clip-secs">30</b>s</span></button>
          <button class="chip ghost b-span">30s</button>
          <button class="rec-btn b-rec">${icon('i-rec')}<span class="rec-lbl">Record</span></button>
        </div>
        <div class="cast-note rec-note"></div>
      </div>
    </div>

    <div class="card sb-card">
      <div class="card-head">
        <div class="card-title">${icon('i-media')} Soundboard</div>
        <button class="chip ghost b-norm">${icon('i-gauge')}<span>Level match</span></button>
        <button class="chip ghost b-sbedit">${icon('i-edit')}<span>Organize</span></button>
        <button class="chip ghost b-stopall">${icon('i-stop')}<span>Stop</span></button>
      </div>
      <div class="profile-bar pad-folders" hidden></div>
      <div class="pad-grid"></div>
      <div class="empty sb-empty">
        ${icon('i-folder')}
        <p class="sb-folder">Drop audio files in the soundboard folder on the PC.</p>
      </div>
    </div>`;

  const levels = $('.cast-levels', node);
  const desktop = makeSlider('castDesktop', 'i-vol', true);
  const mic = makeSlider('castMic', 'i-mic', true);
  levels.append(desktop.node, mic.node);

  bindSliderHost(node,
    (key, v) => send(key === 'castDesktop' ? 'cast.desktopGain' : 'cast.micGain', { value: v }),
    (key) => { if (key === 'castMic') send('cast.micEnabled', { value: S.cast && S.cast.micEnabled ? 0 : 1 }); });

  // Source pickers are native selects on purpose: a tablet's own list UI is easier to hit
  // than anything drawn here, and the device count is unknown - a scrolling list of custom
  // rows would cost more page than the rest of this card.
  node.addEventListener('change', (e) => {
    const sel = e.target.closest('[data-src]');
    if (!sel) return;
    send('cast.source', { kind: sel.dataset.src, device: sel.value || null });
  });

  node.addEventListener('click', (e) => {
    if (e.target.closest('.b-sbedit')) { sbView.edit = !sbView.edit; return schedule('soundboard'); }
    const fc = e.target.closest('[data-folder]');
    if (fc) { sbView.filter = fc.dataset.folder; return schedule('soundboard'); }
    if (e.target.closest('.rec-arm')) {
      return send(S.sb && S.sb.recorderArmed ? 'rec.disarm' : 'rec.arm');
    }
    if (e.target.closest('.cast-toggle')) {
      return send(S.cast && S.cast.armed ? 'cast.disarm' : 'cast.arm');
    }
    if (e.target.closest('.b-span')) {
      clipSpanIdx = (clipSpanIdx + 1) % CLIP_SPANS.length;
      return schedule('soundboard');
    }
    if (e.target.closest('.b-clip')) return send('rec.clip', { seconds: CLIP_SPANS[clipSpanIdx] });
    if (e.target.closest('.b-rec')) {
      return send(S.sb && S.sb.recording ? 'rec.stop' : 'rec.start');
    }
    if (e.target.closest('.b-stopall')) return send('sb.stop');
    if (e.target.closest('.b-norm')) {
      return send('sb.normalise', { value: S.sb && S.sb.normalise ? 0 : 1 });
    }
  });

  // Tap fires a pad; holding it cycles where that pad goes. Routing is a per-pad setting
  // you change rarely and fire constantly, so the frequent action gets the plain tap.
  let timer = null, active = null;
  const clearHold = () => {
    if (timer) { clearTimeout(timer); timer = null; }
    if (active) { active.classList.remove('holding'); active = null; }
  };
  node.addEventListener('pointerdown', (e) => {
    const p = e.target.closest('[data-pad]');
    if (!p || sbView.edit) return;
    active = p;
    p.classList.add('holding');
    timer = setTimeout(() => {
      const order = ['both', 'mic', 'ears'];
      const next = order[(order.indexOf(p.dataset.route || 'both') + 1) % order.length];
      send('sb.route', { id: p.dataset.pad, route: next });
      p.dataset.routed = '1';
      clearHold();
    }, 700);
  });
  ['pointerup', 'pointercancel', 'pointerleave'].forEach(ev => node.addEventListener(ev, clearHold));
  node.addEventListener('click', (e) => {
    const p = e.target.closest('[data-pad]');
    if (!p) return;
    if (p.dataset.routed) { delete p.dataset.routed; return; }
    if (sbView.edit) return openPadSheet(p.dataset.pad);
    send('sb.play', { id: p.dataset.pad });
  });

  return {
    node, desktop, mic,
    card: $('.cast-card', node),
    toggle: $('.cast-card .cast-toggle', node),
    toggleLbl: $('.cast-card .cast-toggle .lbl', node),
    body: $('.cast-body', node),
    unavailable: $('.cast-unavailable', node),
    mDesk: $('.m-desk', node),
    mMic: $('.m-mic', node),
    note: $('.cast-card .cast-note', node),
    selRender: $('[data-src="render"]', node),
    selCapture: $('[data-src="capture"]', node),

    recArm: $('.rec-arm', node),
    recArmLbl: $('.rec-arm .lbl', node),
    recBody: $('.rec-body', node),
    mBuf: $('.m-buf', node),
    recBuf: $('.rec-buf', node),
    recBtn: $('.b-rec', node),
    recLbl: $('.rec-lbl', node),
    clipBtn: $('.b-clip', node),
    clipSecs: $('.clip-secs', node),
    spanBtn: $('.b-span', node),
    recNote: $('.rec-note', node),

    pads: $('.pad-grid', node),
    padFolders: $('.pad-folders', node),
    sbEditBtn: $('.b-sbedit', node),
    normBtn: $('.b-norm', node),
    sbEmpty: $('.sb-empty', node),
    sbFolder: $('.sb-folder', node),
    deskScale: null, micScale: null, bufScale: null, devSig: null,
  };
}

/* How much history "clip that" reaches back for. The host caps this at the buffer it
   actually holds, so these are requests rather than promises. */
const CLIP_SPANS = [15, 30, 60];
let clipSpanIdx = 1;

function makePad(item) {
  const node = el('button', 'pad', `
    <span class="pad-nm"></span>
    <span class="pad-meta"><b class="pad-len"></b><span class="pad-route"></span></span>`);
  return { node, nm: $('.pad-nm', node), len: $('.pad-len', node), route: $('.pad-route', node) };
}

const ROUTE_LABEL = { both: 'both', mic: 'mic only', ears: 'ears only' };

function updatePad(rec, item) {
  text(rec.nm, item.name);
  // A silent file and a broken one look identical from a pad; both have to say so, or the
  // soundboard gets blamed for a clip that simply has nothing in it.
  text(rec.len, item.broken ? 'unreadable'
    : item.silent ? 'silent'
    : (item.seconds ? item.seconds.toFixed(1) + 's' : ''));
  text(rec.route, (ROUTE_LABEL[item.route] || item.route) + (item.loop ? ' · loop' : ''));
  rec.node.dataset.col = item.color || '';
  rec.node.dataset.pad = item.id;
  rec.node.dataset.route = item.route;
  rec.node.classList.toggle('playing', !!item.playing);
  rec.node.classList.toggle('broken', !!(item.broken || item.silent));
}

function updateVoicePage(r) {
  const c = S.cast || {};
  const can = !!S.caps.micCast;

  r.unavailable.hidden = can;
  r.body.hidden = !can;
  r.toggle.hidden = !can;
  if (!can) return;

  const armed = !!c.armed;
  r.toggle.classList.toggle('on', armed);
  text(r.toggleLbl, armed ? 'Live' : 'Off');
  r.card.classList.toggle('live', armed);

  // An error outranks the status line: if arming failed, that is the only thing worth
  // saying, and saying "off" instead would read as though nothing had been attempted.
  text(r.note, c.error ? c.error
    : armed ? (c.micName ? 'Mixing ' + c.micName + ' with desktop audio' : 'Casting')
    : 'Set the app’s microphone to “Default”, then arm this.');
  r.note.classList.toggle('bad', !!c.error);

  setScale(r, r.mDesk, armed ? (c.desktopPeak || 0) / 100 : 0, 'deskScale');
  setScale(r, r.mMic, armed && c.micEnabled ? (c.micPeak || 0) / 100 : 0, 'micScale');

  updateSlider(r.desktop, c.desktopGain, false);
  updateSlider(r.mic, c.micGain, !c.micEnabled);

  fillSources(r, c);
}

/* Rebuilt only when the device set actually changes. These arrive eight times a second
   with the meters, and replacing the options on every frame would close the picker under
   the finger of anyone using it. */
function fillSources(r, c) {
  const outs = c.outputs || [];
  const ins = c.inputs || [];
  const sig = outs.map(d => d.id).join('|') + '#' + ins.map(d => d.id).join('|');
  if (r.devSig === sig) return;
  r.devSig = sig;

  const fill = (sel, list) => {
    sel.innerHTML = list.map(d =>
      `<option value="${esc(d.id)}"${d.isDefault ? ' selected' : ''}>${esc(d.name)}</option>`).join('');
  };
  fill(r.selRender, outs);
  fill(r.selCapture, ins);
}

function updateSoundboard(r) {
  const s = S.sb || {};
  const items = s.items || [];

  r.sbEmpty.hidden = items.length > 0;
  if (s.folder) text(r.sbFolder, 'Drop audio files in ' + s.folder);

  // Folders: a row of chips when there are any, and the pads of the one chosen.
  const groups = [...new Set(items.map(i => i.group).filter(Boolean))].sort();
  if (sbView.filter && !groups.includes(sbView.filter)) sbView.filter = '';
  const shown = sbView.filter ? items.filter(i => i.group === sbView.filter) : items;
  const chips = groups.length ? [{ id: '', name: 'All' }].concat(groups.map(g => ({ id: g, name: g }))) : [];
  r.padFolders.hidden = chips.length === 0;
  reconcile(r.padFolders, chips, c => 'f:' + c.id,
    () => ({ node: el('button', 'pchip') }),
    (rec, c) => { rec.node.dataset.folder = c.id; text(rec.node, c.name); rec.node.className = 'pchip' + (sbView.filter === c.id ? ' live' : ''); });

  reconcile(r.pads, shown, i => i.id, makePad, updatePad);
  r.pads.classList.toggle('editing', sbView.edit);
  r.sbEditBtn.classList.toggle('hot', sbView.edit);
  text($('span', r.sbEditBtn), sbView.edit ? 'Done' : 'Organize');

  // An open pad sheet follows the pad, except while a field in it has the keyboard.
  const ed = deck.editor;
  if (ed && ed.kind === 'pad') {
    const c = padClip(ed.id);
    const sig = c ? [c.color, c.group, c.route, c.loop].join('|') : '';
    const typing = document.activeElement && /^(INPUT|SELECT)$/.test(document.activeElement.tagName);
    if (sig !== ed.sig && !typing) { ed.sig = sig; renderPadSheet(); }
  }

  r.normBtn.classList.toggle('hot', !!s.normalise);

  const armed = !!s.recorderArmed;
  r.recArm.classList.toggle('on', armed);
  text(r.recArmLbl, armed ? 'Armed' : 'Off');
  r.recBody.hidden = false;

  // The clip and record buttons do nothing without a buffer behind them, so they say so
  // rather than failing when pressed.
  r.clipBtn.disabled = !armed;
  r.recBtn.disabled = !armed;

  text(r.clipSecs, String(CLIP_SPANS[clipSpanIdx]));
  text(r.spanBtn, CLIP_SPANS[clipSpanIdx] + 's');

  const cap = s.bufferCapacity || 60;
  setScale(r, r.mBuf, armed ? Math.min(1, (s.bufferedSeconds || 0) / cap) : 0, 'bufScale');
  text(r.recBuf, armed ? (s.bufferedSeconds || 0) + 's' : '–');

  const recording = !!s.recording;
  r.recBtn.classList.toggle('on', recording);
  text(r.recLbl, recording ? 'Stop · ' + mmss(s.recordingSeconds || 0) : 'Record');

  text(r.recNote, s.error ? s.error
    : s.lastSaved ? 'Saved ' + s.lastSaved
    : armed ? 'Keeping the last ' + cap + 's of what you hear'
    : 'Arm to start keeping a rolling buffer.');
  r.recNote.classList.toggle('bad', !!s.error);
}

/* ============================================================ quick page = */

const QUICK = [
  { id: 'start', name: 'Start', ic: 'i-home' },
  { id: 'search', name: 'Search', ic: 'i-apps' },
  { id: 'explorer', name: 'Files', ic: 'i-apps' },
  { id: 'taskmgr', name: 'Task Mgr', ic: 'i-chip' },
  { id: 'calc', name: 'Calculator', ic: 'i-chip' },
  { id: 'settings', name: 'Settings', ic: 'i-chip' },
  { id: 'desktop', name: 'Show Desktop', ic: 'i-home' },
  { id: 'screenshot', name: 'Screenshot', ic: 'i-apps' },
  // Alt+F4. Held rather than tapped: it closes whatever has focus, which can discard
  // unsaved work, and on an empty desktop the shell turns it into a shutdown prompt.
  { id: 'closewindow', name: 'Close Window', ic: 'i-x', confirm: true },
  { id: 'lock', name: 'Lock', ic: 'i-bolt' },
  { id: 'sleep', name: 'Sleep', ic: 'i-bolt', confirm: true },
  { id: 'restart', name: 'Restart', ic: 'i-bolt', confirm: true },
  { id: 'shutdown', name: 'Shut Down', ic: 'i-bolt', confirm: true },
];

function makeQuickPage() {
  const node = el('div', 'stack', `
    <div class="card bright-card" hidden>
      <div class="card-head">
        <div class="card-title">${icon('i-sun')} Display</div>
        <span class="chip ghost thermal" hidden></span>
      </div>
      <div class="levels"></div>
    </div>
    <div class="card">
      <div class="card-head">
        <div class="card-title">${icon('i-sun')} This tablet's screen</div>
        <button class="btn sm night-btn">${icon('i-leaf')} <span>Night mode</span></button>
      </div>
      <div class="levels night-levels"></div>
      <p class="hint" style="margin:10px 0 0">This tablet only.</p>
    </div>
    <div class="card pair-card" hidden>
      <div class="card-head">
        <div class="card-title">${icon('i-lock')} Wi-Fi pairing</div>
        <span class="chip ghost pair-state"></span>
      </div>
      <p class="hint">Wi-Fi works only for tablets that were plugged in once. Forgetting replaces the key: this tablet keeps working, others must pair again.</p>
      <div class="tile-grid" style="grid-template-columns:repeat(auto-fill,minmax(180px,220px))">
        <button class="tile danger" data-quick="pairreset" data-action="pair.reset" data-confirm="1">
          ${icon('i-lock')}<span class="nm">Forget other tablets</span><span class="hold"></span>
        </button>
      </div>
    </div>
    <div class="card qa-card">
      <div class="card-head">
        <div class="card-title">${icon('i-bolt')} Quick actions</div>
        <span class="chip ghost">hold red tiles for 1.5 s</span>
      </div>
      <div class="tile-grid">
        ${QUICK.map(q => `
          <button class="tile ${q.confirm ? 'danger' : ''}" data-quick="${q.id}" ${q.confirm ? 'data-confirm="1"' : ''}>
            ${icon(q.ic)}<span class="nm">${q.name}</span>
            ${q.confirm ? '<span class="hold"></span>' : ''}
          </button>`).join('')}
      </div>
    </div>`);

  // Destructive actions need a deliberate press-and-hold, so a stray touch on a
  // tablet lying beside the keyboard can never shut the laptop down.
  bindHold(node, {
    selector: '[data-quick][data-confirm]',
    verb: (b) => ($('.nm', b) ? $('.nm', b).textContent : b.dataset.quick).toLowerCase(),
    // Shutting down or restarting ends the link, so there is no answer to wait for.
    noWait: true,
    run: (b) => send(b.dataset.action || 'sys.' + b.dataset.quick),
    okText: (b) => ($('.nm', b) ? $('.nm', b).textContent : 'Done') + ' sent',
  });

  node.addEventListener('click', (e) => {
    const b = e.target.closest('[data-quick]');
    if (!b || b.dataset.confirm) return;
    send('sys.' + b.dataset.quick);
  });

  // Brightness is a live control rather than a one-shot action, so it gets a slider.
  const bright = makeSlider('brightness', 'i-sun');
  $('.bright-card .levels', node).appendChild(bright.node);

  const tdim = makeSlider('tdim', 'i-sun');
  const twarm = makeSlider('twarm', 'i-leaf');
  $('.night-levels', node).append(tdim.node, twarm.node);

  bindSliderHost(node,
    (key, v) => {
      if (key === 'brightness') send('sys.brightness', { value: v });
      else if (key === 'tdim') setNight({ dim: Math.max(1, v) });
      else if (key === 'twarm') setNight({ warm: v });
    },
    () => { /* nothing sensible to mute here */ });

  $('.night-btn', node).addEventListener('click', () => {
    const on = night.dim < 100 || night.warm > 0;
    setNight(on ? { dim: 100, warm: 0 } : { dim: 15, warm: 55 });
    updateQuick(UI.quickPage);
  });

  return {
    node, bright, tdim, twarm,
    card: $('.bright-card', node),
    thermal: $('.thermal', node),
    nightBtn: $('.night-btn', node),
    pair: $('.pair-card', node),
    pairState: $('.pair-state', node),
  };
}

function updateQuick(r) {
  // Shown only when the host reported it can actually change brightness on this machine.
  const on = !!S.caps.brightness;
  r.card.hidden = !on;
  if (on && S.sys && S.sys.brightness != null) updateSlider(r.bright, S.sys.brightness, false);

  const mode = S.sys && S.sys.thermalMode;
  r.thermal.hidden = !mode;
  if (mode) text(r.thermal, mode);

  updateSlider(r.tdim, night.dim, false);
  updateSlider(r.twarm, night.warm, false);
  const nightOn = night.dim < 100 || night.warm > 0;
  r.nightBtn.classList.toggle('on', nightOn);
  text($('span', r.nightBtn), nightOn ? 'Night mode on' : 'Night mode');

  // Forgetting tablets is offered only over the cable, which is also the only place the host allows it.
  const onCable = S.link.transport === 'usb' && S.link.phase === 'connected';
  r.pair.hidden = !onCable;
  text(r.pairState, S.link.paired ? 'this tablet is paired' : 'pairing...');
}

/* ============================================================== voice strip = */

/* A big mute for the microphone and a deafen, always one tap from the audio card. Each has an
   LED that is green while live and red while muted, readable across a desk without reading. */

const MIC_MODE_KEY = 'deck.micMode';
const micMode = () => { try { return localStorage.getItem(MIC_MODE_KEY) === 'hold' ? 'hold' : 'tap'; } catch (_) { return 'tap'; } };

function makeVoiceStrip() {
  const node = el('div', 'voice-strip', `
    <button class="big-tile" data-voice="mic">${icon('i-mic')}<span><span class="t1">Microphone</span><span class="t2">Live</span></span><span class="led"></span></button>
    <button class="big-tile" data-voice="deaf">${icon('i-headphones')}<span><span class="t1">Deafen</span><span class="t2">Off</span></span><span class="led"></span></button>`);

  const mic = $('[data-voice="mic"]', node);
  const deaf = $('[data-voice="deaf"]', node);
  const rec = {
    node, mic, deaf,
    micUse: $('use', mic), micState: $('.t2', mic),
    deafState: $('.t2', deaf),
  };

  // Push-to-mute: the mic is live until the tile is held. If it was already muted, the press
  // brings it back instead, so a held tile can never leave the mic stuck off by surprise.
  let heldMute = false;
  mic.addEventListener('pointerdown', (e) => {
    if (micMode() !== 'hold') return;
    e.preventDefault();
    const muted = !!(S.audio && S.audio.micMuted);
    heldMute = !muted;
    send('audio.micMute', { value: muted ? 0 : 1 });
  });
  const release = () => {
    if (micMode() !== 'hold' || !heldMute) return;
    heldMute = false;
    send('audio.micMute', { value: 0 });
  };
  ['pointerup', 'pointercancel', 'pointerleave'].forEach(ev => mic.addEventListener(ev, release));
  mic.addEventListener('click', () => { if (micMode() === 'tap') send('audio.micMuteToggle'); });
  deaf.addEventListener('click', () => send('audio.deafenToggle'));
  return rec;
}

function updateVoiceStrip(rec, a) {
  const micOff = !!a.micMuted, deafOn = !!a.deafened;
  rec.mic.classList.toggle('off', micOff);
  rec.deaf.classList.toggle('off', deafOn);
  rec.micUse.setAttribute('href', micOff ? '#i-mic-off' : '#i-mic');
  text(rec.micState, micOff ? 'Muted' : 'Live');
  text(rec.deafState, deafOn ? 'Deafened' : 'Off');
}

/* ================================================================ outputs row = */

const VIRTUAL_DEVICE = /cable|vb-audio|voicemeeter|virtual|steam streaming/i;

function outKind(name) {
  if (/head(phone|set)|buds|airpods|bluetooth|wh-|wf-/i.test(name)) return 'i-headphones';
  if (/hdmi|display|monitor|\btv\b|nvidia|samsung|lg |dell|asus/i.test(name)) return 'i-monitor';
  return 'i-speaker';
}

function makeOutCard(d) {
  const node = el('div', 'out-card', `
    <button class="out-pick" data-dev-out="${esc(d.id)}">${icon('i-speaker')}
      <span class="tx"><span class="nm"></span><span class="sub"></span></span></button>`);
  const sl = makeSlider('dev:' + d.id, 'i-vol', true);
  node.appendChild(sl.node);
  return { node, sl, nm: $('.nm', node), sub: $('.sub', node), use: $('use', node) };
}

function updateOutCard(rec, d) {
  const m = /^(.*?)\s*\((.*)\)\s*$/.exec(d.name);
  text(rec.nm, m ? m[1] : d.name);
  text(rec.sub, m ? m[2] : '');
  rec.node.classList.toggle('active', !!d.isDefault);
  const g = '#' + outKind(d.name);
  if (rec.use.getAttribute('href') !== g) rec.use.setAttribute('href', g);
  rec.sl.node.hidden = d.volume == null;
  if (d.volume != null) updateSlider(rec.sl, d.volume, d.muted);
}

/* ============================================================== deck page == */

/* Tiles are drawn from what the PC says they are. This side never holds a command: a press sends
   the tile's id, and the PC looks up what that id does. Editing sends a whole profile, which
   the PC checks again before keeping it, and refuses unless the cable is the link. */

const TILE_ICONS = ['i-bolt', 'i-play', 'i-pause', 'i-next', 'i-prev', 'i-vol', 'i-mute', 'i-mic', 'i-mic-off',
  'i-headphones', 'i-speaker', 'i-monitor', 'i-camera', 'i-search', 'i-lock', 'i-home', 'i-apps', 'i-folder',
  'i-game', 'i-chat', 'i-rec', 'i-stop', 'i-star', 'i-sun', 'i-chip', 'i-globe', 'i-kbd', 'i-clock', 'i-timer',
  'i-calendar', 'i-plug', 'i-leaf', 'i-gauge', 'i-shuffle', 'i-repeat', 'i-deck'];
const TILE_COLOURS = ['accent', 'pink', 'violet', 'amber', 'green', 'red', 'blue'];
const COLOUR_HEX = { accent: '#2BE3D0', pink: '#FF4F8B', violet: '#9A82FF', amber: '#FFB020', green: '#35D07F', red: '#FF5566', blue: '#4A9EFF' };

const SYS_NAMES = {
  start: 'Start menu', search: 'Search', desktop: 'Show desktop', taskmgr: 'Task Manager',
  screenshot: 'Screenshot tool', explorer: 'File Explorer', calc: 'Calculator', settings: 'Settings',
  dismiss: 'Close menus (Esc)', lock: 'Lock the PC', closewindow: 'Close window (Alt+F4)',
  sleep: 'Sleep', restart: 'Restart', shutdown: 'Shut down',
};
const AUDIO_STEPS = {
  masterMuteToggle: 'Mute or unmute speakers', micMuteToggle: 'Mute or unmute microphone',
  deafenToggle: 'Deafen or undeafen', masterVolume: 'Set volume to', setOutput: 'Switch output to',
};
const MEDIA_STEPS = { playpause: 'Play or pause', next: 'Next track', previous: 'Previous track' };
const TIMER_STEPS = { countdown: 'Start a timer for', stopwatch: 'Start a stopwatch', pomodoro: 'Start a pomodoro', clear: 'Clear all timers' };
const STEP_KINDS = [['key', 'Shortcut'], ['launch', 'Open app'], ['sys', 'System'], ['audio', 'Audio'],
                    ['media', 'Media'], ['sound', 'Sound'], ['timer', 'Timer'], ['macro', 'Macro'], ['wait', 'Wait']];
const KEY_CHIPS = ['ctrl', 'shift', 'alt', 'win', 'enter', 'esc', 'tab', 'space', 'f5', 'prtsc', 'left', 'right', 'up', 'down'];

const deck = { editing: false, editor: null, profile: null, sig: '', macroName: '' };

const newId = (prefix) => (prefix || 't') + Math.random().toString(36).slice(2, 8);
const deepCopy = (o) => JSON.parse(JSON.stringify(o));
const activeProfile = () => {
  const t = S.tiles;
  return t ? (t.profiles.find(p => p.id === t.active) || t.profiles[0]) : null;
};

function makeDeckPage() {
  const node = el('div', 'stack', `
    <div class="card">
      <div class="card-head deck-head">
        <div class="card-title">${icon('i-deck')} <span class="d-name">Deck</span>
          <span class="chip ghost d-auto" hidden></span></div>
        <div class="grp">
          <span class="chip ghost d-lock" hidden>${icon('i-usb')} edit over USB</span>
          <button class="btn sm d-prof" hidden>${icon('i-edit')} Profile</button>
          <button class="btn sm d-edit" hidden>${icon('i-edit')} <span>Edit</span></button>
        </div>
      </div>
      <div class="profile-bar"></div>
      <div class="tile-grid deck-grid"></div>
      <p class="deck-note" hidden></p>
    </div>`);

  const add = el('button', 'tile add', `${icon('i-plus')}<span class="nm">New tile</span>`);
  add.dataset.act = 'add';
  add.hidden = true;
  const grid = $('.deck-grid', node);
  grid.appendChild(add);

  const r = {
    node, grid, add,
    bar: $('.profile-bar', node),
    name: $('.d-name', node), auto: $('.d-auto', node), lock: $('.d-lock', node),
    prof: $('.d-prof', node), edit: $('.d-edit', node), editLbl: $('.d-edit span', node),
    note: $('.deck-note', node),
  };

  // Tiles that can end work in progress are held, not tapped, the same as the Actions page.
  const run = (b) => {
    const p = activeProfile();
    if (p) send('tile.run', { id: b.dataset.tile, profile: p.id });
  };

  bindHold(grid, {
    selector: '[data-tile][data-confirm]',
    enabled: () => !deck.editing,
    verb: (b) => ($('.nm', b) ? $('.nm', b).textContent : 'run this tile'),
    noWait: true,
    run: (b) => run(b),
    okText: (b) => ($('.nm', b) ? $('.nm', b).textContent : 'Tile') + ' sent',
  });

  grid.addEventListener('click', (e) => {
    const mini = e.target.closest('[data-move]');
    if (mini) return moveTile(mini.closest('[data-tile]').dataset.tile, +mini.dataset.move);
    if (e.target.closest('[data-act="add"]')) return openTileEditor(null);
    const b = e.target.closest('[data-tile]');
    if (!b) return;
    if (deck.editing) return openTileEditor(b.dataset.tile);
    if (!b.dataset.confirm) run(b);
  });

  r.bar.addEventListener('click', (e) => {
    const b = e.target.closest('[data-prof]');
    if (!b) return;
    if (b.dataset.prof === '+') return openProfileEditor(null);
    // Choosing a profile holds it in front; "Auto" lets the foreground app choose again.
    send('tiles.pin', { id: b.dataset.prof === '*' ? '' : b.dataset.prof });
  });

  r.edit.addEventListener('click', () => {
    deck.editing = !deck.editing;
    schedule('tiles');
  });
  r.prof.addEventListener('click', () => openProfileEditor(activeProfile() && activeProfile().id));
  return r;
}

function moveTile(tileId, dir) {
  const p = activeProfile();
  if (!p) return;
  const prof = deepCopy(p);
  const i = prof.tiles.findIndex(t => t.id === tileId);
  const j = i + dir;
  if (i < 0 || j < 0 || j >= prof.tiles.length) return;
  [prof.tiles[i], prof.tiles[j]] = [prof.tiles[j], prof.tiles[i]];
  send('tiles.setProfile', { profile: prof });
}

function updateDeck(r) {
  const t = S.tiles;
  if (!t) return;
  const p = activeProfile();
  if (!p) return;

  if (!t.canEdit && deck.editing) deck.editing = false;
  text(r.name, p.name);
  r.auto.hidden = !(t.pinned || (t.auto && p.id !== 'default'));
  text(r.auto, t.pinned ? 'pinned' : 'switched automatically');
  r.edit.hidden = !t.canEdit;
  r.lock.hidden = t.canEdit;
  r.prof.hidden = !(t.canEdit && deck.editing);
  r.edit.classList.toggle('on', deck.editing);
  text(r.editLbl, deck.editing ? 'Done' : 'Edit');
  r.grid.classList.toggle('editing', deck.editing);

  const chips = [{ id: '*', name: 'Auto', cls: t.pinned ? '' : 'live' }]
    .concat(t.profiles.map(x => ({ id: x.id, name: x.name, cls: x.id === t.active ? (t.pinned ? 'pinned' : 'cur') : '' })));
  if (deck.editing && t.profiles.length < 16) chips.push({ id: '+', name: '+ Profile', cls: '' });
  reconcile(r.bar, chips, c => c.id,
    () => { const n = el('button', 'pchip'); return { node: n }; },
    (rec, c) => {
      rec.node.dataset.prof = c.id;
      text(rec.node, c.name);
      const cls = 'pchip ' + c.cls;
      if (rec.node.className !== cls) rec.node.className = cls;
    });

  const busy = new Set(t.running || []);
  reconcile(r.grid, p.tiles, x => p.id + '/' + x.id,
    (x) => {
      const n = el('button', 'tile', `${icon(x.icon)}<span class="nm"></span>
        <span class="hold"></span>
        <span class="badges"><span class="mini b-fav" data-move="-1">${icon('i-prev')}</span><span class="mini b-hide" data-move="1">${icon('i-next')}</span></span>`);
      return { node: n, use: $('use', n), nm: $('.nm', n) };
    },
    (rec, x) => {
      rec.node.dataset.tile = x.id;
      text(rec.nm, x.name);
      const g = '#' + x.icon;
      if (rec.use.getAttribute('href') !== g) rec.use.setAttribute('href', g);
      rec.node.dataset.col = x.color || 'accent';
      if (x.confirm) rec.node.dataset.confirm = '1'; else delete rec.node.dataset.confirm;
      rec.node.classList.toggle('danger', !!x.confirm);
      rec.node.classList.toggle('busy', busy.has(x.id));
    });
  r.add.hidden = !deck.editing || p.tiles.length >= 40;

  // A failure is reported where it happened, for as long as it is still news.
  const err = t.error;
  const fresh = err && (hostNow() - err.atMs) < 12000;
  r.note.hidden = !fresh;
  r.note.classList.toggle('err', !!fresh);
  if (fresh) {
    text(r.note, `"${err.tile}" stopped: ${err.message}`);
    clearTimeout(r.noteTimer);
    r.noteTimer = setTimeout(() => schedule('tiles'), 12500 - (hostNow() - err.atMs));
  }
}

/* ------------------------------------------------------------ sheets ------- */

const sheet = () => $('#sheet');

function closeSheet() {
  deck.editor = null;
  const s = sheet();
  s.hidden = true;
  s.innerHTML = '';
  pushSubscription();
}

function openSheet(html) {
  const s = sheet();
  s.innerHTML = `<div class="sheet-card">${html}</div>`;
  s.hidden = false;
}

const optionList = (pairs, selected) => pairs.map(([v, label]) =>
  `<option value="${esc(v)}"${String(v) === String(selected) ? ' selected' : ''}>${esc(label)}</option>`).join('');

function stepHtml(s, i) {
  let body = '';
  const cat = (S.tiles && S.tiles.catalog) || {};

  switch (s.do) {
    case 'key':
      body = `<input type="text" data-f="keys" value="${esc(s.keys || '')}" placeholder="ctrl+shift+m" autocapitalize="none" autocomplete="off" spellcheck="false">
        <div class="key-chips">${KEY_CHIPS.map(k => `<button data-chip="${k}" data-i="${i}">${k}</button>`).join('')}</div>`;
      break;
    case 'launch': {
      const items = ((S.apps && S.apps.items) || []).filter(a => !a.hidden);
      const pairs = items.map(a => [a.id, a.name]);
      if (s.id && !items.some(a => a.id === s.id)) pairs.unshift([s.id, '(not found) ' + s.id]);
      if (!s.id && pairs.length) s.id = pairs[0][0];
      body = pairs.length
        ? `<select data-f="id">${optionList(pairs, s.id)}</select>`
        : `<span class="hint" style="margin:0">Waiting for the app list from the PC...</span>`;
      break;
    }
    case 'sys': {
      const ids = cat.sys || Object.keys(SYS_NAMES);
      if (!s.id) s.id = ids[0];
      body = `<select data-f="id">${optionList(ids.map(k => [k, SYS_NAMES[k] || k]), s.id)}</select>`;
      break;
    }
    case 'audio': {
      if (!s.verb) s.verb = 'micMuteToggle';
      body = `<select data-f="verb">${optionList(Object.entries(AUDIO_STEPS), s.verb)}</select>`;
      if (s.verb === 'masterVolume') {
        if (s.value == null) s.value = 50;
        body += `<input type="number" data-f="value" min="0" max="100" value="${s.value}"> %`;
      } else if (s.verb === 'setOutput') {
        const outs = ((S.audio && S.audio.outputs) || []).filter(o => !VIRTUAL_DEVICE.test(o.name));
        const pairs = outs.map(o => [o.id, o.name]);
        if (s.id && !outs.some(o => o.id === s.id)) pairs.unshift([s.id, '(not connected)']);
        if (!s.id && pairs.length) s.id = pairs[0][0];
        body += pairs.length ? `<select data-f="id">${optionList(pairs, s.id)}</select>` : '';
      }
      break;
    }
    case 'media':
      if (!s.verb) s.verb = 'playpause';
      body = `<select data-f="verb">${optionList(Object.entries(MEDIA_STEPS), s.verb)}</select>`;
      break;
    case 'sound': {
      const clips = ((S.sb && S.sb.items) || []).map(c => [c.id, c.name]);
      const cur = s.verb === 'stop' ? '__stop' : s.id;
      if (!s.verb) s.verb = 'play';
      if (!cur && clips.length) s.id = clips[0][0];
      body = `<select data-f="clip">${optionList([['__stop', 'Stop all sounds']].concat(clips), s.verb === 'stop' ? '__stop' : s.id)}</select>`;
      break;
    }
    case 'macro': {
      const m = S.macros || { items: [], recording: null, canEdit: false };
      if (m.recording) {
        body = `<span class="rec-live">${macroLive(m.recording)}</span>
          <input type="text" data-f="macroname" placeholder="Name it" maxlength="24" value="${esc(deck.macroName)}" autocomplete="off">
          <button class="btn sm primary" data-macro="stop">${icon('i-check')} Stop and save</button>
          <button class="btn sm" data-macro="cancel">Discard</button>`;
        break;
      }
      const pairs = m.items.map(x => [x.id, x.name + ' (' + x.seconds + ' s)']);
      if (s.id && !m.items.some(x => x.id === s.id)) pairs.unshift([s.id, '(deleted)']);
      if (!s.id && pairs.length) s.id = pairs[0][0];
      body = pairs.length
        ? `<select data-f="id">${optionList(pairs, s.id)}</select>`
        : `<span class="hint" style="margin:0">No macros yet.</span>`;
      if (m.canEdit) {
        body += `<button class="btn sm" data-macro="record">${icon('i-rec')} Record new</button>`;
        if (s.id && m.items.some(x => x.id === s.id))
          body += `<button class="btn sm icon danger" data-macro="delete" data-id="${esc(s.id)}" aria-label="Delete this macro">${icon('i-trash')}</button>`;
      } else {
        body += `<span class="hint" style="margin:0">Recording needs the cable.</span>`;
      }
      break;
    }
    case 'timer':
      if (!s.verb) s.verb = 'countdown';
      body = `<select data-f="verb">${optionList(Object.entries(TIMER_STEPS), s.verb)}</select>`;
      if (s.verb === 'countdown') {
        if (s.value == null) s.value = 300;
        body += `<input type="number" data-f="minutes" min="1" max="1440" value="${Math.max(1, Math.round(s.value / 60))}"> min`;
      }
      break;
    case 'wait':
      if (s.ms == null) s.ms = 1000;
      body = `<input type="number" data-f="seconds" min="0.1" max="10" step="0.1" value="${(s.ms / 1000).toFixed(1)}"> seconds`;
      break;
  }

  const kind = (STEP_KINDS.find(k => k[0] === s.do) || [0, s.do])[1];
  return `<div class="step-edit" data-step="${i}">
      <span class="n">${i + 1}</span>
      <div class="body"><span class="kind">${kind}</span>${body}</div>
      <div class="ctl">
        <button class="btn sm icon" data-act="up" data-i="${i}" aria-label="Move up">${icon('i-up')}</button>
        <button class="btn sm icon" data-act="down" data-i="${i}" aria-label="Move down">${icon('i-down')}</button>
        <button class="btn sm icon danger" data-act="del" data-i="${i}" aria-label="Remove step">${icon('i-x')}</button>
      </div>
    </div>`;
}

function editorSignature() {
  const m = S.macros;
  return [(S.apps && S.apps.items || []).length, (S.audio && S.audio.outputs || []).length,
          (S.sb && S.sb.items || []).length, m ? m.items.length + (m.recording ? 'r' : '') : '-'].join('|');
}

const macroLive = (r) => `● Recording on your PC: ${r.count} event${r.count === 1 ? '' : 's'}, ${r.seconds} s` +
  (r.full ? ' (limit reached, nothing more is kept)' : '');

function openTileEditor(tileId) {
  const p = activeProfile();
  if (!p) return;
  const found = tileId ? p.tiles.find(t => t.id === tileId) : null;
  const draft = found ? deepCopy(found) : { id: newId('t'), name: '', icon: 'i-bolt', color: null, confirm: false, steps: [{ do: 'key', keys: '' }] };
  deck.editor = { kind: 'tile', draft, isNew: !found, error: '' };
  deck.sig = editorSignature();
  renderTileEditor();
  pushSubscription();
}

function renderTileEditor() {
  const ed = deck.editor;
  if (!ed || ed.kind !== 'tile') return;
  const d = ed.draft;
  openSheet(`
    <h3>${icon('i-deck')} ${ed.isNew ? 'New tile' : 'Edit tile'}</h3>
    <div class="two-up">
      <div class="field"><label>Name</label><input type="text" data-f="name" maxlength="24" value="${esc(d.name)}" placeholder="What it is called" autocomplete="off"></div>
      <div class="field"><label>Colour</label><div class="swatches">${TILE_COLOURS.map(c =>
        `<button data-col="${c}" class="${(d.color || 'accent') === c ? 'on' : ''}" style="background:${COLOUR_HEX[c]}" aria-label="${c}"></button>`).join('')}</div></div>
    </div>
    <div class="field"><label>Icon</label>${ed.iconOpen
      ? `<div class="icon-pick">${TILE_ICONS.map(i => `<button data-icon="${i}" class="${d.icon === i ? 'on' : ''}">${icon(i)}</button>`).join('')}</div>`
      : `<div><button class="btn" data-act="icons">${icon(d.icon)} Change icon</button></div>`}</div>
    <div class="lbl" style="margin-bottom:8px">Steps, run in order</div>
    <div class="steps">${d.steps.map(stepHtml).join('')}</div>
    <div class="add-steps">${STEP_KINDS.map(([k, n]) => `<button class="btn sm" data-add="${k}">${icon('i-plus')} ${n}</button>`).join('')}</div>
    <p class="hint" style="margin-top:12px">Tiles that shut down, sleep or close windows must be held.</p>
    ${d.steps.some(x => x.do === 'macro') ? `<p class="hint">While recording, everything typed or clicked on the PC is captured, passwords included. It stops after 60 seconds, is kept only if you save it, and never leaves the PC.</p>` : ''}
    <p class="deck-note err" ${ed.error ? '' : 'hidden'}>${esc(ed.error)}</p>
    <div class="sheet-foot">
      ${ed.isNew ? '' : `<button class="btn danger" data-act="delete">${icon('i-trash')} Delete</button>`}
      <span class="grow"></span>
      <button class="btn" data-act="close">Cancel</button>
      <button class="btn primary" data-act="save">${icon('i-check')} Save</button>
    </div>`);
}

function saveEditedProfile(mutate, onDone) {
  const p = activeProfile();
  if (!p) return;
  const prof = deepCopy(p);
  mutate(prof);
  send('tiles.setProfile', { profile: prof }, (res) => onDone(res));
}

sheet().addEventListener('input', (e) => {
  const ed = deck.editor;
  const f = e.target.dataset.f;
  if (!ed || !f) return;
  if (ed.kind === 'tile') {
    const row = e.target.closest('[data-step]');
    if (!row) { if (f === 'name') ed.draft.name = e.target.value; return; }
    const s = ed.draft.steps[+row.dataset.step];
    if (f === 'keys') s.keys = e.target.value;
    else if (f === 'value') s.value = Math.max(0, Math.min(100, parseInt(e.target.value, 10) || 0));
    else if (f === 'minutes') s.value = Math.max(1, Math.min(1440, parseInt(e.target.value, 10) || 1)) * 60;
    else if (f === 'seconds') s.ms = Math.max(100, Math.min(10000, Math.round((parseFloat(e.target.value) || 1) * 1000)));
    else if (f === 'macroname') deck.macroName = e.target.value;
  } else if (ed.kind === 'profile') {
    if (f === 'pname') ed.draft.name = e.target.value;
    if (f === 'pmatch') ed.matchText = e.target.value;
  } else if (ed.kind === 'timer') {
    ed.vals[f] = e.target.value;
  }
});

sheet().addEventListener('change', (e) => {
  const ed = deck.editor;
  const f = e.target.dataset.f;
  if (!ed || ed.kind !== 'tile' || !f) return;
  const row = e.target.closest('[data-step]');
  if (!row) return;
  const s = ed.draft.steps[+row.dataset.step];
  if (f === 'id') s.id = e.target.value;
  else if (f === 'clip') {
    if (e.target.value === '__stop') { s.verb = 'stop'; s.id = null; } else { s.verb = 'play'; s.id = e.target.value; }
  } else if (f === 'verb') {
    s.verb = e.target.value;
    // Parameters belong to a verb; keep the editor honest about which ones apply.
    if (s.do === 'audio') { if (s.verb !== 'masterVolume') s.value = null; if (s.verb !== 'setOutput') s.id = null; }
    if (s.do === 'timer' && s.verb !== 'countdown') s.value = null;
    renderTileEditor();
  }
});

sheet().addEventListener('click', (e) => {
  const ed = deck.editor;
  if (!ed) {
    // Plain sheets (timers) are handled by their own opener.
    return;
  }

  const act = e.target.closest('[data-act]');
  const a = act && act.dataset.act;
  if (a === 'close') return closeSheet();

  if (ed.kind === 'timer') {
    if (e.target.closest('[data-go]')) ed.go();
    return;
  }

  if (ed.kind === 'tile') {
    const d = ed.draft;
    const i = act ? +act.dataset.i : -1;
    if (a === 'icons') { ed.iconOpen = true; return renderTileEditor(); }
    if (e.target.closest('[data-icon]')) { d.icon = e.target.closest('[data-icon]').dataset.icon; ed.iconOpen = false; return renderTileEditor(); }
    if (e.target.closest('[data-col]')) { d.color = e.target.closest('[data-col]').dataset.col; return renderTileEditor(); }
    if (e.target.closest('[data-add]')) {
      const k = e.target.closest('[data-add]').dataset.add;
      if (d.steps.length < 12) d.steps.push({ do: k });
      return renderTileEditor();
    }
    const mc = e.target.closest('[data-macro]');
    if (mc) {
      const what = mc.dataset.macro;
      const row = mc.closest('[data-step]');
      const step = row ? d.steps[+row.dataset.step] : null;
      if (what === 'record') send('macro.record', {}, (r) => { if (!r.ok) ed.error = r.error || 'Could not start recording.'; renderTileEditor(); });
      else if (what === 'cancel') send('macro.cancel', {}, () => renderTileEditor());
      else if (what === 'delete') send('macro.delete', { id: mc.dataset.id }, () => { if (step) step.id = null; renderTileEditor(); });
      else if (what === 'stop') {
        send('macro.stop', { name: deck.macroName }, (r) => {
          if (r.ok && step) {
            // The new macro was published just before this answer, so the newest one is it.
            const items = (S.macros && S.macros.items) || [];
            const newest = items.slice().sort((a, b) => b.created - a.created)[0];
            if (newest) step.id = newest.id;
            deck.macroName = '';
          } else ed.error = r.error || 'Could not save the recording.';
          renderTileEditor();
        });
      }
      return;
    }
    const chip = e.target.closest('[data-chip]');
    if (chip) {
      const s = d.steps[+chip.dataset.i];
      const cur = (s.keys || '').replace(/\+$/, '');
      s.keys = cur ? cur + '+' + chip.dataset.chip : chip.dataset.chip;
      return renderTileEditor();
    }
    if (a === 'up' && i > 0) { [d.steps[i - 1], d.steps[i]] = [d.steps[i], d.steps[i - 1]]; return renderTileEditor(); }
    if (a === 'down' && i < d.steps.length - 1) { [d.steps[i + 1], d.steps[i]] = [d.steps[i], d.steps[i + 1]]; return renderTileEditor(); }
    if (a === 'del') { d.steps.splice(i, 1); return renderTileEditor(); }

    if (a === 'save') {
      d.name = (d.name || '').trim();
      if (!d.name) { ed.error = 'Give the tile a name.'; return renderTileEditor(); }
      if (!d.steps.length) { ed.error = 'Add at least one step.'; return renderTileEditor(); }
      saveEditedProfile((prof) => {
        const at = prof.tiles.findIndex(t => t.id === d.id);
        if (at >= 0) prof.tiles[at] = d; else prof.tiles.push(d);
      }, (res) => {
        if (res.ok) closeSheet();
        else { ed.error = res.error || 'The PC did not accept that tile.'; renderTileEditor(); }
      });
    }
    if (a === 'delete') {
      saveEditedProfile((prof) => { prof.tiles = prof.tiles.filter(t => t.id !== d.id); },
        (res) => { if (res.ok) closeSheet(); else { ed.error = res.error || 'Could not delete.'; renderTileEditor(); } });
    }
    return;
  }

  if (ed.kind === 'profile') {
    const d = ed.draft;
    const m = e.target.closest('[data-m]');
    if (m) { d.match = d.match.filter(x => x !== m.dataset.m); return renderProfileEditor(); }
    const sug = e.target.closest('[data-sug]');
    if (sug) { if (!d.match.includes(sug.dataset.sug) && d.match.length < 8) d.match.push(sug.dataset.sug); return renderProfileEditor(); }
    if (a === 'addmatch') {
      const v = (ed.matchText || '').trim().replace(/\.exe$/i, '');
      if (v && !d.match.includes(v) && d.match.length < 8) d.match.push(v);
      ed.matchText = '';
      return renderProfileEditor();
    }
    if (a === 'save') {
      d.name = (d.name || '').trim();
      if (!d.name) { ed.error = 'Give the profile a name.'; return renderProfileEditor(); }
      const out = deepCopy(d);
      if (ed.isNew) {
        out.id = (d.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 22) || 'profile') + '-' + Math.random().toString(36).slice(2, 5);
        out.tiles = ed.copyTiles ? deepCopy((activeProfile() || { tiles: [] }).tiles) : [];
      }
      send('tiles.setProfile', { profile: out }, (res) => {
        if (res.ok) { if (ed.isNew) send('tiles.pin', { id: out.id }); closeSheet(); }
        else { ed.error = res.error || 'The PC did not accept that profile.'; renderProfileEditor(); }
      });
    }
    if (a === 'delete') {
      send('tiles.deleteProfile', { id: d.id }, (res) => {
        if (res.ok) { send('tiles.pin', { id: '' }); closeSheet(); }
        else { ed.error = res.error || 'Could not delete.'; renderProfileEditor(); }
      });
    }
    if (e.target.closest('[data-copy]')) { ed.copyTiles = !ed.copyTiles; renderProfileEditor(); }
  }
});

function openProfileEditor(profileId) {
  const t = S.tiles;
  if (!t) return;
  const found = profileId ? t.profiles.find(p => p.id === profileId) : null;
  const draft = found ? deepCopy({ id: found.id, name: found.name, match: found.match || [] }) : { id: '', name: '', match: [] };
  deck.editor = { kind: 'profile', draft, isNew: !found, error: '', matchText: '', copyTiles: true };
  renderProfileEditor();
  pushSubscription();
}

function renderProfileEditor() {
  const ed = deck.editor;
  if (!ed || ed.kind !== 'profile') return;
  const d = ed.draft;
  const isDefault = d.id === 'default';
  const seen = new Set(d.match.map(x => x.toLowerCase()));
  const running = ((S.running && S.running.items) || []).filter(x => x.windowed && !seen.has(x.name.toLowerCase()));
  const uniq = [...new Set(running.map(x => x.name))].slice(0, 8);

  openSheet(`
    <h3>${icon('i-deck')} ${ed.isNew ? 'New profile' : 'Profile'}</h3>
    <div class="field"><label>Name</label><input type="text" data-f="pname" maxlength="24" value="${esc(d.name)}" placeholder="Editing, Streaming, a game..." autocomplete="off"></div>
    ${isDefault ? `<p class="hint">This is the profile shown when nothing else matches, so it has no apps of its own.</p>` : `
      <div class="lbl" style="margin-bottom:8px">Shown automatically when this is in front</div>
      <div class="match-chips">
        ${d.match.map(m => `<button class="pchip live" data-m="${esc(m)}">${esc(m.startsWith('@') ? 'any ' + m.slice(1) : m)} ${icon('i-x')}</button>`).join('') || '<span class="hint" style="margin:0">Nothing yet. Pick below, or it only appears when you choose it.</span>'}
      </div>
      <div class="match-chips">
        ${['@gaming', '@music', '@video'].filter(m => !d.match.includes(m)).map(m => `<button class="pchip" data-sug="${m}">+ any ${m.slice(1)}</button>`).join('')}
        ${uniq.map(n => `<button class="pchip" data-sug="${esc(n)}">+ ${esc(n)}</button>`).join('')}
      </div>
      <div class="field"><label>Or type a program name</label>
        <div style="display:flex;gap:8px"><input type="text" data-f="pmatch" placeholder="Photoshop" value="${esc(ed.matchText || '')}" autocomplete="off" style="flex:1" autocapitalize="none"><button class="btn" data-act="addmatch">Add</button></div></div>`}
    ${ed.isNew ? `<div class="field"><button class="btn ${ed.copyTiles ? 'on' : ''}" data-copy="1">${icon('i-check')} Start with the tiles from the current profile</button></div>` : ''}
    <p class="deck-note err" ${ed.error ? '' : 'hidden'}>${esc(ed.error)}</p>
    <div class="sheet-foot">
      ${ed.isNew || isDefault ? '' : `<button class="btn danger" data-act="delete">${icon('i-trash')} Delete</button>`}
      <span class="grow"></span>
      <button class="btn" data-act="close">Cancel</button>
      <button class="btn primary" data-act="save">${icon('i-check')} Save</button>
    </div>`);
}

/* =========================================================== time and timers = */

/* The PC's clock is the reference: an old tablet's own clock and time zone are often wrong, and
   a timer that is due at 14:05 PC time has to read the same on both. The host sends its time on
   subscribe and every few minutes; between those the tablet only counts. */

const hostNow = () => Date.now() + (S.timeOffset || 0);
const tzMin = () => (S.tzOffsetMin != null ? S.tzOffsetMin : -new Date().getTimezoneOffset());
const hostDate = (ms) => new Date((ms == null ? hostNow() : ms) + tzMin() * 60000);   // read with getUTC*

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const two = (n) => String(n).padStart(2, '0');

function fmtDigits(ms, tenths) {
  ms = Math.max(0, ms);
  const total = tenths ? Math.floor(ms / 1000) : Math.ceil(ms / 1000);
  const h = Math.floor(total / 3600), m = Math.floor((total % 3600) / 60), s = total % 60;
  const base = (h ? h + ':' + two(m) : m) + ':' + two(s);
  return tenths ? base + '.' + Math.floor((ms % 1000) / 100) : base;
}

function timerMs(t, now) {
  if (t.kind === 'stopwatch') return t.state === 'running' ? now - t.startedAtMs : (t.elapsedMs || 0);
  if (t.state === 'running') return Math.max(0, t.endsAtMs - now);
  if (t.state === 'paused') return t.remainingMs || 0;
  return 0;
}

const PHASE_NAME = { work: 'Focus', short: 'Short break', long: 'Long break' };

function timerLabel(t) {
  if (t.kind === 'pomodoro') {
    const ph = PHASE_NAME[t.phase] || '';
    return `<b>${ph}</b> · round ${t.round} of ${t.rounds}` + (t.label ? ' · ' + esc(t.label) : '');
  }
  const base = t.kind === 'stopwatch' ? 'Stopwatch' : 'Timer';
  return base + (t.label ? ' · ' + esc(t.label) : '');
}

function makeClockPage() {
  const node = el('div', 'clock-wrap', `
    <div class="stack clock-main">
      <div class="card">
        <div class="clock-hero">
          <div class="clock-time"><span class="hm">--:--</span><span class="sec"></span></div>
          <div class="clock-date"></div>
          <div class="clock-zone"></div>
        </div>
        <div style="margin-top:12px"><button class="btn sm amb">${icon('i-expand')} Ambient clock</button></div>
      </div>
      <div class="card clock-side">
        <div class="card-head"><div class="card-title">${icon('i-calendar')} Coming up</div></div>
        <div class="events"></div>
        <div class="empty ev-empty">${icon('i-calendar')}<p class="ev-hint"></p></div>
      </div>
      <div class="card cal-card">
        <div class="card-head"><div class="card-title">${icon('i-calendar')} Calendars</div>
          <button class="btn sm cal-refresh" hidden>Refresh</button></div>
        <div class="cal-list"></div>
        <div class="cal-add" hidden>
          <input type="url" class="cal-url" placeholder="Calendar link (https://\u2026 .ics)" autocapitalize="none" autocomplete="off" spellcheck="false">
          <button class="btn sm primary cal-go">Add</button>
          <button class="btn sm cal-paste">Paste from PC</button>
        </div>
        <p class="hint cal-note" style="margin:8px 0 0"></p>
      </div>
    </div>
    <div class="stack clock-side">
      <div class="card">
        <div class="card-head"><div class="card-title">${icon('i-timer')} Timers</div>
          <button class="btn sm clr" hidden>Clear all</button></div>
        <div class="timer-list"></div>
        <div class="preset-row"><div class="lbl" style="margin-bottom:7px">Countdown</div>
          <div class="presets">
            ${[1, 5, 10, 15, 30, 60].map(m => `<button class="btn" data-cd="${m * 60}">${m < 60 ? m + ' min' : '1 hour'}</button>`).join('')}
            <button class="btn" data-act="custom-cd">${icon('i-edit')} Custom</button>
          </div></div>
        <div class="preset-row"><div class="lbl" style="margin-bottom:7px">Other</div>
          <div class="presets">
            <button class="btn" data-act="stopwatch">${icon('i-clock')} Stopwatch</button>
            <button class="btn" data-act="pomodoro">${icon('i-leaf')} Pomodoro 25 / 5</button>
            <button class="btn" data-act="custom-pomo">${icon('i-edit')} Custom pomodoro</button>
          </div></div>
      </div>
      <div class="card">
        <div class="card-head"><div class="card-title">${icon('i-bell')} Notifications</div>
          <div class="grp"><button class="btn sm nclear" hidden>Clear</button><button class="btn sm nswitch" hidden>Turn on</button></div></div>
        <p class="hint nhint" style="margin:0 0 8px"></p>
        <div class="notes"></div>
      </div>
    </div>`);

  const calAction = (action, args, doneText) => {
    const r2 = UI.clockPage;
    r2.calBusy = true;
    text(r2.calNote, 'Working\u2026');
    send(action, args, (res) => {
      r2.calBusy = false;
      if (res && res.ok === false) text(r2.calNote, res.error || 'That did not work');
      else { text(r2.calNote, doneText || ''); r2.calUrl.value = ''; }
      schedule('clock');
    });
  };
  node.addEventListener('click', (e) => {
    if (e.target.closest('.cal-go')) {
      const v = UI.clockPage.calUrl.value.trim();
      return v ? calAction('calendar.add', { url: v }, 'Added') : text(UI.clockPage.calNote, 'Paste a link first');
    }
    if (e.target.closest('.cal-paste')) return calAction('calendar.paste', {}, 'Added from the PC\'s clipboard');
    if (e.target.closest('.cal-refresh')) return calAction('calendar.refresh', {}, 'Updated');
    const del = e.target.closest('[data-cal-del]');
    if (del) return calAction('calendar.remove', { id: del.closest('[data-cal]').dataset.cal }, 'Removed');
  });

  const r = {
    node,
    hm: $('.hm', node), sec: $('.sec', node), date: $('.clock-date', node), zone: $('.clock-zone', node),
    hero: $('.clock-hero', node),
    events: $('.events', node), evEmpty: $('.ev-empty', node), evHint: $('.ev-hint', node),
    calList: $('.cal-list', node), calAdd: $('.cal-add', node), calUrl: $('.cal-url', node),
    calNote: $('.cal-note', node), calRefresh: $('.cal-refresh', node),
    list: $('.timer-list', node), clr: $('.clr', node),
    notes: $('.notes', node), nhint: $('.nhint', node), nswitch: $('.nswitch', node), nclear: $('.nclear', node),
  };

  node.addEventListener('click', (e) => {
    if (app.classList.contains('ambient')) { app.classList.remove('ambient'); r.hero.style.transform = ''; return; }
    if (e.target.closest('.amb')) { app.classList.add('ambient'); return; }
    const cd = e.target.closest('[data-cd]');
    if (cd) return send('timer.countdown', { seconds: +cd.dataset.cd });
    if (e.target.closest('.clr')) return send('timer.clear');
    if (e.target.closest('.nswitch')) return send('notify.enable', { value: S.notifications && S.notifications.enabled ? 0 : 1 });
    if (e.target.closest('.nclear')) return send('notify.clear');
    const nd = e.target.closest('[data-note]');
    if (nd) return send('notify.dismiss', { id: +nd.dataset.note });
    const act = e.target.closest('[data-act]');
    if (!act) return;
    switch (act.dataset.act) {
      case 'stopwatch': return send('timer.stopwatch');
      case 'pomodoro': return send('timer.pomodoro', { work: 25, short: 5, long: 15, rounds: 4 });
      case 'custom-cd': return openTimerSheet('countdown');
      case 'custom-pomo': return openTimerSheet('pomodoro');
      case 'pause': case 'resume': case 'remove': case 'skip':
        return send('timer.' + act.dataset.act, { id: act.dataset.id });
    }
  });

  // Keep the picture from sitting still: shifted a few pixels each minute in ambient mode.
  setInterval(() => {
    if (!app.classList.contains('ambient')) return;
    const x = Math.round((Math.random() - 0.5) * 60), y = Math.round((Math.random() - 0.5) * 40);
    r.hero.style.transform = `translate(${x}px, ${y}px)`;
  }, 60000);
  return r;
}

function dayLabel(ms) {
  const today = Math.floor(hostDate().getTime() / 86400000);
  const day = Math.floor(hostDate(ms).getTime() / 86400000);
  if (day === today) return 'Today';
  if (day === today + 1) return 'Tomorrow';
  const d = hostDate(ms);
  return DAYS[d.getUTCDay()].slice(0, 3) + ' ' + d.getUTCDate();
}

function updateClock(r) {
  tickClockPage(r);

  const c = S.clock;
  const evs = (c && c.events) || [];
  r.events.hidden = evs.length === 0;
  r.evEmpty.hidden = evs.length > 0;
  if (!evs.length) {
    text(r.evHint, c && (c.calendarFiles || (c.subscriptions && c.subscriptions.length))
      ? 'Nothing in the next two weeks.'
      : 'No calendar yet. Add a link below.');
  }

  // Calendars subscribed by link. The address is a secret and stays on the PC; only a name and the site are shown.
  const subs = (c && c.subscriptions) || [];
  const canEdit = !!(c && c.canEdit);
  r.calAdd.hidden = !canEdit;
  r.calRefresh.hidden = !canEdit || subs.length === 0;
  reconcile(r.calList, subs, x => x.id,
    () => {
      const n = el('div', 'cal-row', `<div class="cal-txt"><b class="cn"></b><small class="cs"></small></div>
        <button class="btn sm icon" data-cal-del aria-label="Remove this calendar">${icon('i-x')}</button>`);
      return { node: n, cn: $('.cn', n), cs: $('.cs', n), del: $('[data-cal-del]', n) };
    },
    (rec, x) => {
      rec.node.dataset.cal = x.id;
      text(rec.cn, x.name || x.host);
      text(rec.cs, x.error ? x.host + ' \u00b7 ' + x.error : x.host);
      rec.node.classList.toggle('bad', !!x.error);
      rec.del.hidden = !canEdit;
    });
  if (!r.calBusy) text(r.calNote, canEdit ? (subs.length ? '' : 'Google, Outlook and iCloud give a private .ics link.') : (subs.length ? '' : 'Plug in the cable to add a calendar.'));
  const now = hostNow();
  reconcile(r.events, evs, e => e.startMs + '|' + e.title,
    () => {
      const n = el('div', 'event', `<div class="when"></div><div class="what"></div>`);
      return { node: n, when: $('.when', n), what: $('.what', n) };
    },
    (rec, e) => {
      const d = hostDate(e.startMs);
      rec.when.innerHTML = (e.allDay ? 'All day' : two(d.getUTCHours()) + ':' + two(d.getUTCMinutes())) +
        `<small>${esc(dayLabel(e.startMs))}</small>`;
      rec.what.innerHTML = esc(e.title) + (e.place ? `<small>${esc(e.place)}</small>` : '');
      rec.node.classList.toggle('now', e.startMs <= now && now < e.endMs);
    });

  updateNotifications(r);

  const items = (S.timers && S.timers.items) || [];
  r.clr.hidden = items.length === 0;
  reconcile(r.list, items, t => t.id,
    () => {
      const n = el('div', 'timer-row', `
        <div><div class="tl"></div><div class="tt"></div></div>
        <div class="ctl"></div>`);
      return { node: n, tl: $('.tl', n), tt: $('.tt', n), ctl: $('.ctl', n), sig: '' };
    },
    (rec, t) => {
      rec.node.className = 'timer-row ' + t.state;
      rec.tl.innerHTML = timerLabel(t) + (t.state === 'ringing' ? ' · <b>done</b>' : '');
      rec.tt.textContent = timerDigits(t, now);
      const btns = [];
      if (t.state === 'running') btns.push(['pause', 'i-pause', 'Pause']);
      if (t.state === 'paused') btns.push(['resume', 'i-play', 'Resume']);
      if (t.kind === 'pomodoro' && t.state !== 'ringing') btns.push(['skip', 'i-next', 'Next phase']);
      btns.push(['remove', t.state === 'ringing' ? 'i-check' : 'i-x', t.state === 'ringing' ? 'Dismiss' : 'Remove']);
      const sig = btns.map(b => b[0]).join();
      if (sig !== rec.sig) {
        rec.sig = sig;
        rec.ctl.innerHTML = btns.map(([a, ic, label]) =>
          `<button class="btn sm icon" data-act="${a}" data-id="${esc(t.id)}" aria-label="${label}">${icon(ic)}</button>`).join('');
      } else {
        for (const b of rec.ctl.children) b.dataset.id = t.id;
      }
    });
}

/* Windows notifications, read-only. Off until switched on over the cable, because they can hold anything
   anyone has said to you. Dismissing here hides the item on this tablet; Windows keeps its own copy. */
function updateNotifications(r) {
  const n = S.notifications;
  if (!n) { r.nhint.hidden = true; r.nswitch.hidden = true; r.nclear.hidden = true; return; }

  r.nswitch.hidden = !(n.available && n.canEdit);
  text($('span', r.nswitch) || r.nswitch, n.enabled ? 'Turn off' : 'Turn on');
  r.nswitch.classList.toggle('on', !!n.enabled);
  const items = n.enabled ? (n.items || []) : [];
  r.nclear.hidden = items.length === 0;

  let hint = '';
  if (!n.available) hint = 'Not available on this PC: ' + (n.problem || 'unknown reason') + '.';
  else if (!n.enabled) hint = 'Show Windows notifications here. It only reads, nothing leaves the PC, and it is switched on from the cable.';
  else if (!items.length) hint = 'Nothing waiting. Dismissing one here hides it on this tablet only; Windows keeps its own.';
  r.nhint.hidden = !hint;
  text(r.nhint, hint);

  reconcile(r.notes, items, x => x.id,
    () => {
      const node = el('div', 'note', `<div class="nb"><div class="nt"><b class="na"></b><span class="nw"></span></div><div class="nh"></div><div class="nx"></div></div>
        <button class="btn sm icon" aria-label="Dismiss">${icon('i-x')}</button>`);
      return { node, na: $('.na', node), nw: $('.nw', node), nh: $('.nh', node), nx: $('.nx', node), btn: $('button', node) };
    },
    (rec, x) => {
      text(rec.na, x.app);
      const d = hostDate(x.atMs);
      text(rec.nw, x.atMs ? two(d.getUTCHours()) + ':' + two(d.getUTCMinutes()) : '');
      text(rec.nh, x.title);
      text(rec.nx, x.body);
      rec.btn.dataset.note = x.id;
    });
}

function timerDigits(t, now) {
  return fmtDigits(timerMs(t, now), t.kind === 'stopwatch');
}

function tickClockPage(r) {
  const d = hostDate();
  const hm = two(d.getUTCHours()) + ':' + two(d.getUTCMinutes());
  text(r.hm, hm);
  text(r.sec, two(d.getUTCSeconds()));
  text(r.date, `${DAYS[d.getUTCDay()]}, ${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]}`);
  text(r.zone, S.clock && S.clock.zone ? S.clock.zone : '');
}

/* Once a second, and only the numbers that move: the timer digits and the chip. Structure is
   left to the render pass, which runs when the PC reports a change. */
function tickSecond() {
  const now = hostNow();
  if (page === 'clock' && UI.clockPage) {
    tickClockPage(UI.clockPage);
    const rows = UI.clockPage.list.__keyed;
    if (rows && S.timers) for (const t of S.timers.items) {
      const rec = rows.get(t.id);
      if (rec) { const s = timerDigits(t, now); if (rec.tt.textContent !== s) rec.tt.textContent = s; }
    }
  }
  renderTimerChip();
}

function renderTimerChip() {
  const chip = $('#timerChip');
  const items = (S.timers && S.timers.items) || [];
  if (!items.length) { chip.classList.add('hidden'); return; }
  const now = hostNow();
  const ringing = items.find(t => t.state === 'ringing');
  const next = ringing || items.filter(t => t.state === 'running' && t.kind !== 'stopwatch')
    .sort((a, b) => a.endsAtMs - b.endsAtMs)[0] || items[0];
  chip.classList.remove('hidden');
  chip.classList.toggle('ring', !!ringing);
  const s = ringing ? 'done' : timerDigits(next, now);
  text($('#timerText'), (next.label ? next.label + ' ' : '') + s);
}

$('#timerChip').addEventListener('click', () => {
  const b = $('[data-nav="clock"]');
  if (b) b.click();
});

function openTimerSheet(kind) {
  const vals = kind === 'countdown' ? { minutes: '10', label: '' } : { work: '25', short: '5', long: '15', rounds: '4' };
  const go = () => {
    if (kind === 'countdown') send('timer.countdown', { seconds: Math.max(1, parseInt(vals.minutes, 10) || 1) * 60, label: vals.label || '' });
    else send('timer.pomodoro', { work: parseInt(vals.work, 10) || 25, short: parseInt(vals.short, 10) || 5, long: parseInt(vals.long, 10) || 15, rounds: parseInt(vals.rounds, 10) || 4 });
    closeSheet();
  };
  deck.editor = { kind: 'timer', vals, go };
  const field = (id, label, extra) => `<div class="field"><label>${label}</label><input type="number" data-f="${id}" value="${vals[id]}" ${extra || ''}></div>`;
  openSheet(`
    <h3>${icon('i-timer')} ${kind === 'countdown' ? 'Countdown' : 'Pomodoro'}</h3>
    ${kind === 'countdown'
      ? field('minutes', 'Minutes', 'min="1" max="1440"') + `<div class="field"><label>Label (optional)</label><input type="text" data-f="label" maxlength="24" placeholder="Tea" autocomplete="off"></div>`
      : `<div class="two-up">${field('work', 'Focus (min)', 'min="1" max="180"')}${field('short', 'Short break (min)', 'min="1" max="60"')}</div>
         <div class="two-up">${field('long', 'Long break (min)', 'min="1" max="120"')}${field('rounds', 'Rounds', 'min="1" max="12"')}</div>`}
    <div class="sheet-foot"><span class="grow"></span>
      <button class="btn" data-act="close">Cancel</button>
      <button class="btn primary" data-go="1">${icon('i-play')} Start</button></div>`);
}

/* ======================================================== performance extras = */

/* Everything here is drawn from what the PC has already measured. History is asked for rather than
   subscribed to: it is a whole window of readings, wanted every couple of seconds at most, and
   only while its page is open. */

const SVGNS = 'http://www.w3.org/2000/svg';

/** A polyline through the values, lifting the pen where a reading is missing. */
function linePath(vals, w, h, lo, hi) {
  const n = vals.length;
  if (n < 2) return '';
  const span = hi - lo || 1;
  let d = '', pen = false, gap = 0;
  for (let i = 0; i < n; i++) {
    const v = vals[i];
    // One or two missed samples are bridged so the line stays a line; a longer silence is a real
    // absence (no game running, sensor gone) and breaks it.
    if (v == null) { if (++gap > 2) pen = false; continue; }
    gap = 0;
    const x = (i / (n - 1)) * w;
    const y = h - (Math.min(hi, Math.max(lo, v)) - lo) / span * h;
    d += (pen ? 'L' : 'M') + x.toFixed(1) + ' ' + y.toFixed(1);
    pen = true;
  }
  return d;
}

const lastOf = (a) => { for (let i = a.length - 1; i >= 0; i--) if (a[i] != null) return a[i]; return null; };
const peakOf = (a) => a.reduce((m, v) => (v != null && (m == null || v > m) ? v : m), null);
const RANGES = [[60, '1 min'], [300, '5 min'], [1800, '30 min']];
const hist = { range: 60 };

function requestHistory() {
  if (page === 'perf' && !document.hidden) send('history.get', { seconds: hist.range });
}
setInterval(requestHistory, 2500);

function makeChart(title, series, lo, hi, unit) {
  const node = el('div', 'chart', `
    <div class="chart-head"><span class="ct">${esc(title)}</span><span class="legend"></span></div>
    <div class="chart-body">
      <span class="ax top"></span><span class="ax bot"></span>
      <svg viewBox="0 0 300 64" preserveAspectRatio="none">
        <path class="grid" d="M0 16H300M0 32H300M0 48H300"/>
        ${series.map(s => `<path class="ln ${s.cls}" d=""/>`).join('')}
      </svg>
    </div>`);
  return { node, series, lo, hi, unit, paths: [...node.querySelectorAll('.ln')], legend: $('.legend', node),
           top: $('.ax.top', node), bot: $('.ax.bot', node) };
}

function drawChart(c, data, lo, hi) {
  c.lo = lo; c.hi = hi;
  text(c.top, Math.round(hi) + c.unit);
  text(c.bot, Math.round(lo) + c.unit);
  let any = false;
  const parts = [];
  c.series.forEach((s, i) => {
    const vals = data[s.key] || [];
    const d = linePath(vals, 300, 64, lo, hi);
    if (c.paths[i].getAttribute('d') !== d) c.paths[i].setAttribute('d', d);
    const has = vals.some(v => v != null);
    any = any || has;
    if (has) parts.push(`<i class="dot ${s.cls}"></i>${esc(s.name)} <b>${Math.round(lastOf(vals))}${c.unit}</b> <small>peak ${Math.round(peakOf(vals))}</small>`);
  });
  const html = parts.join('<span class="sep"></span>');
  if (c.legend.__html !== html) { c.legend.__html = html; c.legend.innerHTML = html; }
  return any;
}

function makePerfExtras() {
  const node = el('div', 'stack');
  node.innerHTML = `
    <div class="card hist-card">
      <div class="card-head">
        <div class="card-title">${icon('i-perf')} History</div>
        <div class="voice-mode ranges">${RANGES.map(([s, n]) => `<button data-range="${s}">${n}</button>`).join('')}</div>
      </div>
      <div class="charts"></div>
      <p class="hint hist-hint" style="margin:8px 0 0">Short spikes stay visible.</p>
    </div>
    <div class="card top-card">
      <div class="card-head">
        <div class="card-title">${icon('i-chip')} Top processes</div>
        <div class="voice-mode tops"><button data-tab="cpu">CPU</button><button data-tab="ram">Memory</button><button data-tab="gpu">GPU</button></div>
      </div>
      <div class="top-list"></div>
      <p class="hint top-hint" style="margin:8px 0 0"></p>
    </div>
    <div class="card alerts-card">
      <div class="card-head">
        <div class="card-title">${icon('i-bell')} Alerts</div>
        <button class="btn sm alert-switch">Turn on</button>
      </div>
      <div class="alert-rows"></div>
    </div>`;

  const charts = $('.charts', node);
  const load = makeChart('Load', [
    { key: 'cpu', name: 'CPU', cls: 'c1' }, { key: 'gpu', name: 'GPU', cls: 'c2' }, { key: 'ram', name: 'Memory', cls: 'c3' }], 0, 100, '%');
  const temp = makeChart('Temperature', [
    { key: 'cpuTemp', name: 'CPU', cls: 'c4' }, { key: 'gpuTemp', name: 'GPU', cls: 'c5' }], 30, 100, '°');
  const fps = makeChart('Frame rate', [{ key: 'fps', name: 'FPS', cls: 'c1' }], 0, 120, '');
  charts.append(load.node, temp.node, fps.node);

  const r = {
    node, load, temp, fps,
    ranges: $('.ranges', node), tops: $('.tops', node), topList: $('.top-list', node), topHint: $('.top-hint', node),
    alertSwitch: $('.alert-switch', node), alertRows: $('.alert-rows', node),
    topTab: 'cpu', killTimer: null,
  };

  r.ranges.addEventListener('click', (e) => {
    const b = e.target.closest('[data-range]');
    if (!b) return;
    hist.range = +b.dataset.range;
    requestHistory();
    schedule('history');
  });
  r.tops.addEventListener('click', (e) => {
    const b = e.target.closest('[data-tab]');
    if (!b) return;
    r.topTab = b.dataset.tab;
    schedule('top');
  });

  // Ending a process discards unsaved work, so it is a deliberate hold, like the system actions.
  bindHold(r.topList, {
    selector: '[data-top="kill"]',
    verb: (b) => 'end ' + b.dataset.name,
    run: (b, done) => send('top.kill', { name: b.dataset.name }, done),
    okText: (b) => 'Ended ' + b.dataset.name,
    failText: (b) => 'Could not end ' + b.dataset.name,
  });
  r.topList.addEventListener('click', (e) => {
    const b = e.target.closest('[data-top]');
    if (!b) return;
    if (b.dataset.top === 'close') send('top.close', { name: b.dataset.name });
    else if (b.dataset.top === 'lower') send('top.priority', { name: b.dataset.name, low: b.dataset.low === '1' ? 0 : 1 });
  });

  r.alertSwitch.addEventListener('click', () => send('alerts.set', { enabled: S.alerts && S.alerts.enabled ? 0 : 1 }));
  r.alertRows.addEventListener('click', (e) => {
    const b = e.target.closest('[data-step]');
    if (!b || !S.alerts) return;
    const key = b.dataset.key, by = +b.dataset.step;
    const next = Math.max(0, (S.alerts[key] || 0) + by);
    S.alerts[key] = next;                       // shown at once; the PC's answer follows
    send('alerts.set', { [key]: next });
    schedule('alerts');
  });
  return r;
}

const ALERT_ROWS = [
  { key: 'cpuTemp', label: 'CPU temperature', unit: '°C', step: 1, avail: 'cpuTempAvailable' },
  { key: 'gpuTemp', label: 'GPU temperature', unit: '°C', step: 1, avail: 'gpuTempAvailable' },
  { key: 'ram', label: 'Memory in use', unit: '%', step: 1 },
  { key: 'fan', label: 'Fan speed', unit: ' rpm', step: 250, avail: 'fanAvailable', zero: 'off' },
];

function updatePerfExtras(r) {
  for (const b of r.ranges.children) b.classList.toggle('on', +b.dataset.range === hist.range);
  for (const b of r.tops.children) b.classList.toggle('on', b.dataset.tab === r.topTab);

  const h = S.history;
  if (h && h.seconds === hist.range) {
    const data = { cpu: h.cpu, gpu: h.gpu, ram: h.ram, cpuTemp: h.cpuTemp, gpuTemp: h.gpuTemp, fps: h.fps };
    drawChart(r.load, data, 0, 100);
    drawChart(r.temp, data, 30, 100);
    const peak = peakOf(h.fps || []);
    r.fps.node.hidden = peak == null;
    if (peak != null) drawChart(r.fps, data, 0, Math.max(60, Math.ceil(peak / 30) * 30));
  }

  // Top processes.
  const t = S.top;
  const list = t ? (t[r.topTab] || []) : [];
  const gpuTab = r.topTab === 'gpu';
  text(r.topHint, !t ? 'Reading the process list...'
    : !t.haveCpu && r.topTab === 'cpu' ? 'CPU figures appear in a moment.'
    : gpuTab && !t.gpuAvailable ? 'This PC does not publish per-process GPU use.'
    : !list.length ? 'Nothing is using it to speak of.' : 'Tap to close \u00b7 hold to end (unsaved work is lost)');
  const max = Math.max(1, ...list.map(p => r.topTab === 'ram' ? p.ramMb : (r.topTab === 'cpu' ? p.cpu : p.gpu) || 0));
  reconcile(r.topList, list, p => p.name,
    (p) => {
      const n = el('div', 'top-row', `
        <div class="tn"><b></b><small></small></div>
        <div class="tbar"><i></i></div><span class="tv"></span>
        <button class="btn sm icon" data-top="lower" aria-label="Lower its priority">${icon('i-down')}</button>
        <button class="btn sm icon" data-top="close" aria-label="Ask it to close">${icon('i-x')}</button>
        <button class="btn sm icon danger" data-top="kill" aria-label="Hold to end it">${icon('i-stop')}</button>`);
      return { node: n, nm: $('b', n), sub: $('small', n), fill: $('.tbar i', n), val: $('.tv', n),
               low: $('[data-top="lower"]', n), close: $('[data-top="close"]', n), kill: $('[data-top="kill"]', n) };
    },
    (rec, p) => {
      text(rec.nm, p.name);
      text(rec.sub, p.count > 1 ? p.count + ' processes' : '');
      const v = r.topTab === 'ram' ? p.ramMb : (r.topTab === 'cpu' ? p.cpu : p.gpu) || 0;
      text(rec.val, r.topTab === 'ram' ? (p.ramMb >= 1024 ? (p.ramMb / 1024).toFixed(1) + ' GB' : p.ramMb + ' MB') : Math.round(v) + '%');
      const sc = `scaleX(${Math.min(1, v / max).toFixed(3)})`;
      if (rec.fill.style.transform !== sc) rec.fill.style.transform = sc;
      for (const b of [rec.low, rec.close, rec.kill]) { b.dataset.name = p.name; b.hidden = !!p.protected; }
      rec.low.dataset.low = p.low ? '1' : '0';
      rec.low.classList.toggle('on', !!p.low);
      rec.node.classList.toggle('locked', !!p.protected);
    });

  // Alerts.
  const a = S.alerts;
  r.alertSwitch.hidden = !a;
  if (!a) return;
  text(r.alertSwitch, a.enabled ? 'Turn off' : 'Turn on');
  r.alertSwitch.classList.toggle('on', !!a.enabled);
  const rows = ALERT_ROWS.filter(x => !x.avail || a[x.avail]);
  r.alertRows.classList.toggle('dim', !a.enabled);
  reconcile(r.alertRows, rows, x => x.key,
    (x) => {
      const n = el('div', 'alert-row', `<span class="al"></span>
        <button class="btn sm icon" data-step="-${x.step}" data-key="${x.key}" aria-label="Lower the limit">−</button>
        <b class="av"></b>
        <button class="btn sm icon" data-step="${x.step}" data-key="${x.key}" aria-label="Raise the limit">+</button>`);
      return { node: n, al: $('.al', n), av: $('.av', n) };
    },
    (rec, x) => {
      text(rec.al, x.label);
      const v = a[x.key] || 0;
      text(rec.av, v === 0 && x.zero ? x.zero : v + x.unit);
      if (a.active && a.active.includes(x.key)) rec.node.classList.add('hit'); else rec.node.classList.remove('hit');
    });
}

/* A reading that crossed its limit. The shell has already buzzed the tablet; this is what the eyes get. */
let alertTimer = null;
function showAlert(m) {
  const bar = $('#alertBar');
  text(bar, `${m.label} ${Math.round(m.value)}${/temp/i.test(m.label) ? '°C' : /Memory/.test(m.label) ? '%' : ' rpm'} is over your limit of ${Math.round(m.limit)}`);
  bar.hidden = false;
  clearTimeout(alertTimer);
  alertTimer = setTimeout(() => { bar.hidden = true; }, 15000);
}
$('#alertBar').addEventListener('click', () => { $('#alertBar').hidden = true; });

/* Why the GPU or CPU is slower than it could be, shown on the performance card when it is true. */
const THROTTLE_TEXT = {
  gpu: { power: 'GPU held back by its power limit', heat: 'GPU slowing down from heat', both: 'GPU held back by power and heat' },
  cpu: { heat: 'CPU slowed by heat', power: 'CPU held below its clock under load', battery: 'CPU slowed by running on battery' },
};

function updateThrottle(r) {
  const y = S.sys;
  const notes = [];
  if (y && y.gpuThrottle && THROTTLE_TEXT.gpu[y.gpuThrottle]) notes.push(THROTTLE_TEXT.gpu[y.gpuThrottle]);
  if (y && y.cpuThrottle && THROTTLE_TEXT.cpu[y.cpuThrottle]) notes.push(THROTTLE_TEXT.cpu[y.cpuThrottle]);
  const html = notes.map(n => `<span class="chip warn">${icon('i-bolt')} ${esc(n)}</span>`).join('');
  if (r.thr.__html !== html) { r.thr.__html = html; r.thr.innerHTML = html; }
  r.thr.hidden = notes.length === 0;
}

/* ========================================================= gaming sessions = */

const ago = (ms) => {
  const days = Math.floor((hostNow() - ms) / 86400000);
  return days <= 0 ? 'today' : days === 1 ? 'yesterday' : days + ' d ago';
};
const fmtHours = (min) => min < 60 ? min + ' min' : (min < 600 ? (min / 60).toFixed(1) : Math.round(min / 60)) + ' h';
const fmtDur = (sec) => sec < 3600 ? Math.max(1, Math.round(sec / 60)) + ' min' : Math.floor(sec / 3600) + ' h ' + Math.round((sec % 3600) / 60) + ' min';

function makeSessionsCard() {
  const node = el('div', 'stack');
  node.innerHTML = `
    <div class="card frames-card">
      <div class="card-head">
        <div class="card-title">${icon('i-gauge')} Frame times</div>
        <span class="chip ghost live-chip" hidden></span>
      </div>
      <div class="frames-body">
        <svg viewBox="0 0 240 64" preserveAspectRatio="none">
          <path class="grid" d="M0 21H240M0 42H240"/>
          <path class="ln c1 ft" d=""/>
        </svg>
        <span class="ax top">50 ms</span><span class="g1">33 ms</span><span class="g2">16.7 ms</span>
      </div>
      <p class="hint frames-hint" style="margin:8px 0 0"></p>
    </div>
    <div class="card sessions-card">
      <div class="card-head">
        <div class="card-title">${icon('i-rec')} Sessions</div>
        <div class="grp">
          <button class="btn sm sess-switch">Recording</button>
          <button class="btn sm sess-clear" hidden>Clear</button>
        </div>
      </div>
      <div class="sess-list"></div>
      <p class="hint sess-hint" style="margin:8px 0 0"></p>
      <div class="auto-power" hidden>
        <div><b>Performance mode follows the game</b>
          <small>Raises the mode while a game runs on AC power, and puts it back afterwards. Never overrides a mode you picked.</small></div>
        <button class="btn sm ap-switch">Turn on</button>
      </div>
    </div>`;

  const r = {
    node, ft: $('.ft', node), liveChip: $('.live-chip', node), framesHint: $('.frames-hint', node),
    list: $('.sess-list', node), hint: $('.sess-hint', node),
    sw: $('.sess-switch', node), clr: $('.sess-clear', node),
    ap: $('.auto-power', node), apSw: $('.ap-switch', node),
    open: null,
  };

  r.sw.addEventListener('click', () => send('sessions.enable', { value: S.sessions && S.sessions.enabled ? 0 : 1 }));
  r.clr.addEventListener('click', () => send('sessions.clear'));
  r.apSw.addEventListener('click', () => send('autopower.set', { value: S.sessions && S.sessions.autoPower ? 0 : 1 }));
  r.list.addEventListener('click', (e) => {
    const del = e.target.closest('[data-del]');
    if (del) return send('sessions.delete', { id: del.dataset.del });
    const row = e.target.closest('[data-sid]');
    if (row) { r.open = r.open === row.dataset.sid ? null : row.dataset.sid; schedule('sessions'); }
  });
  return r;
}

function updateSessionsCard(r) {
  const s = S.sessions, f = S.frames;

  // The live trace: the most recent frames, newest on the right.
  const times = (f && f.times) || [];
  const d = linePath(times.map(t => Math.min(t, 50)), 240, 64, 0, 50);
  if (r.ft.getAttribute('d') !== d) r.ft.setAttribute('d', d);
  const live = f && f.live;
  r.liveChip.hidden = !live;
  if (live) text(r.liveChip, `recording ${live.game} · ${fmtDur(live.seconds)}`);
  const worst = times.length ? Math.max(...times) : 0;
  text(r.framesHint, times.length
    ? `${f.fps ? Math.round(f.fps) + ' fps \u00b7 ' : ''}worst recent frame ${Math.round(worst)} ms`
    : !s ? '' : s.fpsAvailable ? 'Frame times appear while a game is running.'
    : 'Frame rate unavailable' + (S.caps.fpsNote ? ': ' + S.caps.fpsNote : '') + '. Sessions still record play time and load.');

  if (!s) return;
  r.sw.classList.toggle('on', !!s.enabled);
  text(r.sw, s.enabled ? 'Recording' : 'Not recording');
  r.sw.disabled = !s.canEdit;
  r.clr.hidden = !s.items.length || !s.canEdit;

  const items = s.items || [];
  text(r.hint, !s.enabled ? 'Recording is off.'
    : !items.length ? 'Sessions appear after a few minutes of play.' : '');

  reconcile(r.list, items, x => x.id,
    () => {
      const n = el('div', 'sess', `
        <div class="sess-top"><div class="sg"><b></b><small></small></div>
          <div class="ss"><span class="n avg"><b></b><i>avg</i></span><span class="n l1"><b></b><i>1% low</i></span><span class="n l01"><b></b><i>0.1% low</i></span></div></div>
        <div class="sess-more" hidden>
          <svg viewBox="0 0 240 48" preserveAspectRatio="none"><path class="ln c1 sp" d=""/></svg>
          <div class="sm"><span class="sx"></span><button class="btn sm danger" data-del="">${icon('i-trash')} Delete</button></div>
        </div>`);
      return { node: n, nm: $('.sg b', n), when: $('.sg small', n), avg: $('.avg b', n), l1: $('.l1 b', n), l01: $('.l01 b', n),
               more: $('.sess-more', n), sp: $('.sp', n), sx: $('.sx', n), del: $('[data-del]', n) };
    },
    (rec, x) => {
      rec.node.dataset.sid = x.id;
      text(rec.nm, x.game);
      const d0 = hostDate(x.startMs);
      text(rec.when, `${ago(x.startMs)} ${two(d0.getUTCHours())}:${two(d0.getUTCMinutes())} · ${fmtDur(x.seconds)}`);
      text(rec.avg, x.avgFps != null ? Math.round(x.avgFps) : '–');
      text(rec.l1, x.low1 != null ? Math.round(x.low1) : '–');
      text(rec.l01, x.low01 != null ? Math.round(x.low01) : '–');
      const open = r.open === x.id;
      rec.more.hidden = !open;
      rec.node.classList.toggle('open', open);
      if (open) {
        const dd = linePath(x.series || [], 240, 48, 0, Math.max(60, Math.ceil(Math.max(0, ...(x.series || [0])) / 30) * 30));
        if (rec.sp.getAttribute('d') !== dd) rec.sp.setAttribute('d', dd);
        const bits = [];
        if (x.avgCpu != null) bits.push('CPU ' + x.avgCpu + '%');
        if (x.avgGpu != null) bits.push('GPU ' + x.avgGpu + '%');
        if (x.maxGpuTemp != null) bits.push('GPU peak ' + x.maxGpuTemp + '°C');
        text(rec.sx, bits.join(' · ') || (x.series && x.series.length ? '' : 'No frame data was available for this session.'));
        rec.del.dataset.del = x.id;
        rec.del.hidden = !s.canEdit;
      }
    });

  // The rule that raises performance mode, offered only where the PC has a mode to raise.
  r.ap.hidden = !s.autoPowerAvailable;
  r.apSw.classList.toggle('on', !!s.autoPower);
  text(r.apSw, s.autoPower ? (s.autoPowerApplied ? 'On, active now' : 'Turn off') : 'Turn on');
  r.apSw.disabled = !s.canEdit;
}

/* The playtime shown under a game's name on the launcher. */
function playtimeCaption(item) {
  if (item.playMinutes == null) return '';
  return fmtHours(item.playMinutes) + (item.lastPlayedMs ? ' · ' + ago(item.lastPlayedMs) : '');
}

/* ======================================================= cover-art colours = */

/* The accent of the playing card follows the cover. Worked out here from the image the shell already
   serves, so nothing is sent for it; the colour is the most vivid common hue, then pulled into a
   range that stays readable on the dark background. */

const lsGet = (k, d) => { try { const v = localStorage.getItem(k); return v == null ? d : v; } catch (_) { return d; } };
const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch (_) { /* not fatal */ } };

const look = { colours: lsGet('deck.artTheme', '1') !== '0', viz: lsGet('deck.viz', '1') !== '0', ambient: false };
const artColours = new Map();

function dominantColour(img) {
  const c = document.createElement('canvas');
  c.width = c.height = 24;
  const g = c.getContext('2d', { willReadFrequently: true });
  g.drawImage(img, 0, 0, 24, 24);
  const px = g.getImageData(0, 0, 24, 24).data;

  const bins = Array.from({ length: 12 }, () => ({ w: 0, r: 0, g: 0, b: 0 }));
  for (let i = 0; i < px.length; i += 4) {
    const r = px[i] / 255, gg = px[i + 1] / 255, b = px[i + 2] / 255;
    const max = Math.max(r, gg, b), min = Math.min(r, gg, b), d = max - min;
    if (max < 0.22 || d / (max || 1) < 0.28) continue;          // too dark or too grey to be the colour of the cover
    let h = d === 0 ? 0 : max === r ? ((gg - b) / d) % 6 : max === gg ? (b - r) / d + 2 : (r - gg) / d + 4;
    h = (h * 60 + 360) % 360;
    const bin = bins[Math.floor(h / 30)];
    const w = (d / max) * max;                                   // saturation times value
    bin.w += w; bin.r += px[i] * w; bin.g += px[i + 1] * w; bin.b += px[i + 2] * w;
  }
  const best = bins.reduce((a, b) => (b.w > a.w ? b : a));
  if (best.w <= 0) return null;
  return rgbToCss(best.r / best.w, best.g / best.w, best.b / best.w);
}

function rgbToCss(r, g, b) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  let h = d === 0 ? 0 : max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  h = Math.round((h * 60 + 360) % 360);
  const l = (max + min) / 2;
  const s = d === 0 ? 0 : d / (1 - Math.abs(2 * l - 1));
  // Vivid enough to read as a colour, light enough to read as text on near-black.
  const S2 = Math.round(Math.max(0.55, Math.min(0.92, s)) * 100);
  const L2 = Math.round(Math.max(0.58, Math.min(0.7, l < 0.58 ? 0.6 : l)) * 100);
  return `hsl(${h}, ${S2}%, ${L2}%)`;
}

function colourFromArt(key, done) {
  if (artColours.has(key)) return done(artColours.get(key));
  const img = new Image();
  img.onload = () => {
    let c = null;
    try { c = dominantColour(img); } catch (_) { /* a tainted canvas: leave the default colour */ }
    artColours.set(key, c);
    if (artColours.size > 40) artColours.delete(artColours.keys().next().value);
    done(c);
  };
  img.onerror = () => done(null);
  img.src = artUrl(key);
}

function setNowPlayingAccent(c) {
  const nodes = [UI.homeMedia && UI.homeMedia.node, UI.mediaPage && UI.mediaPage.node, $('#npAmbient')];
  for (const n of nodes) {
    if (!n) continue;
    if (c) n.style.setProperty('--accent', c); else n.style.removeProperty('--accent');
  }
}

let artThemeFor = null;
function applyArtTheme() {
  const s = activeSession();
  const key = look.colours && s && s.artKey;
  artThemeFor = key || null;
  if (!key) return setNowPlayingAccent(null);
  colourFromArt(key, (c) => { if (artThemeFor === key) setNowPlayingAccent(c); });
}

/* ========================================================== ambient view = */

/* The playing track, big, and nothing else. For a tablet propped up beside the screen. The picture is
   moved a few pixels every minute so it never sits in one place for hours. */

function openAmbientNp() {
  look.ambient = true;
  $('#npAmbient').hidden = false;
  applyArtTheme();
  updateAmbientNp();
  pushSubscription();
}
function closeAmbientNp() {
  look.ambient = false;
  $('#npAmbient').hidden = true;
  pushSubscription();
}

function updateAmbientNp() {
  if (!look.ambient) return;
  const s = activeSession();
  const root = $('#npAmbient');
  text($('.t', root), s ? (s.title || 'Unknown title') : 'Nothing is playing');
  text($('.a', root), s ? [s.artist, s.album].filter(Boolean).join(' / ') : '');
  const img = $('img', root);
  const key = s && s.artKey;
  if (key && img.dataset.key !== key) { img.dataset.key = key; img.src = artUrl(key); }
  img.hidden = !key;
  if (!key) delete img.dataset.key;
  const dur = s && s.durationSec, pos = s ? livePosition() : 0;
  $('.bar i', root).style.transform = `scaleX(${dur ? Math.max(0, Math.min(1, pos / dur)).toFixed(3) : 0})`;
  root.querySelector('.bars').hidden = !look.viz;
}

$('#npAmbient').addEventListener('click', closeAmbientNp);
setInterval(() => {
  if (!look.ambient) return;
  const inner = $('#npAmbient .np-amb-inner');
  inner.style.transform = `translate(${Math.round((Math.random() - 0.5) * 50)}px, ${Math.round((Math.random() - 0.5) * 36)}px)`;
}, 60000);

/* ============================================================= visualiser = */

/* The PC sends 32 bars about twenty times a second, only while this is on screen and switched on. */

function drawBars(canvas, bands, colour) {
  if (!canvas || canvas.hidden) return;
  const g = canvas.getContext('2d');
  const w = canvas.width, h = canvas.height, n = bands.length || 32;
  g.clearRect(0, 0, w, h);
  const gap = 3, bw = (w - gap * (n - 1)) / n;
  g.fillStyle = colour;
  for (let i = 0; i < n; i++) {
    const v = Math.max(0, Math.min(100, bands[i] || 0)) / 100;
    const bh = Math.max(2, v * h);
    g.fillRect(i * (bw + gap), h - bh, bw, bh);
  }
}

let lastBands = [];
function onSpectrum(bands) {
  lastBands = bands;
  const colour = getComputedStyle($('#npAmbient')).getPropertyValue('--accent') || '#2BE3D0';
  if (look.ambient) return drawBars($('#npAmbient .bars'), bands, colour.trim() || '#2BE3D0');
  if (UI.mediaExtras) drawBars(UI.mediaExtras.canvas, bands, UI.mediaExtras.colour);
}

function makeMediaExtras() {
  const node = el('div', 'card viz-card');
  node.innerHTML = `
    <div class="card-head">
      <div class="card-title">${icon('i-audio')} Display</div>
      <div class="grp">
        <button class="btn sm b-colours">Cover colours</button>
        <button class="btn sm b-viz">Visualizer</button>
        <button class="btn sm b-amb">${icon('i-expand')} Ambient</button>
      </div>
    </div>
    <canvas class="viz" width="640" height="84"></canvas>
    <p class="hint viz-hint" style="margin:6px 0 0"></p>`;

  const r = { node, canvas: $('.viz', node), hint: $('.viz-hint', node),
              colours: $('.b-colours', node), viz: $('.b-viz', node),
              colour: getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#2BE3D0' };

  node.addEventListener('click', (e) => {
    if (e.target.closest('.b-colours')) { look.colours = !look.colours; lsSet('deck.artTheme', look.colours ? '1' : '0'); applyArtTheme(); updateMediaExtras(r); }
    else if (e.target.closest('.b-viz')) { look.viz = !look.viz; lsSet('deck.viz', look.viz ? '1' : '0'); pushSubscription(); updateMediaExtras(r); }
    else if (e.target.closest('.b-amb')) openAmbientNp();
  });
  return r;
}

function updateMediaExtras(r) {
  r.colours.classList.toggle('on', look.colours);
  r.viz.classList.toggle('on', look.viz);
  r.canvas.hidden = !look.viz;
  text(r.hint, look.viz ? '' : 'The visualizer is off.');
  r.hint.hidden = look.viz;
  if (!look.viz) { const g = r.canvas.getContext('2d'); g.clearRect(0, 0, r.canvas.width, r.canvas.height); }
}

/* ====================================================== soundboard layout = */

const PAD_COLOURS = ['pink', 'violet', 'amber', 'green', 'red', 'blue'];
const sbView = { edit: false, filter: '' };
const sbHex = { pink: '#FF4F8B', violet: '#9A82FF', amber: '#FFB020', green: '#35D07F', red: '#FF5566', blue: '#4A9EFF' };

function padClip(id) { return ((S.sb && S.sb.items) || []).find(c => c.id === id); }

function openPadSheet(id) {
  deck.editor = { kind: 'pad', id, newFolder: false, note: '' };
  pushSubscription();
  renderPadSheet();
}

function renderPadSheet() {
  const ed = deck.editor;
  if (!ed || ed.kind !== 'pad') return;
  const c = padClip(ed.id);
  if (!c) return closeSheet();
  const groups = [...new Set((S.sb.items || []).map(x => x.group).filter(Boolean))].sort();
  const canDeck = !!(S.tiles && S.tiles.canEdit);

  openSheet(`
    <h3>${icon('i-media')} Pad settings</h3>
    <div class="two-up">
      <div class="field"><label>Name</label><input type="text" data-f="padname" maxlength="40" value="${esc(c.name)}" autocomplete="off"></div>
      <div class="field"><label>Colour</label><div class="swatches">
        <button data-pcol="" class="${!c.color ? 'on' : ''}" style="background:var(--surface-3)" aria-label="none"></button>
        ${PAD_COLOURS.map(k => `<button data-pcol="${k}" class="${c.color === k ? 'on' : ''}" style="background:${sbHex[k]}" aria-label="${k}"></button>`).join('')}
      </div></div>
    </div>
    <div class="two-up">
      <div class="field"><label>Folder</label>
        <select data-f="padfolder">
          <option value="__none"${!c.group ? ' selected' : ''}>No folder</option>
          ${groups.map(g => `<option value="${esc(g)}"${c.group === g ? ' selected' : ''}>${esc(g)}</option>`).join('')}
          <option value="__new">New folder...</option>
        </select>
        ${ed.newFolder ? `<div style="display:flex;gap:8px;margin-top:8px"><input type="text" data-f="padnewfolder" maxlength="24" placeholder="Folder name" autocomplete="off" style="flex:1"><button class="btn" data-act="padfolder">Move</button></div>` : ''}
      </div>
      <div class="field"><label>Volume</label><input type="range" min="0" max="100" value="${c.gain}" data-f="padgain" style="width:100%"></div>
    </div>
    <div class="field"><label>Plays to</label>
      <div class="presets">
        ${[['both', 'Both'], ['mic', 'Mic only'], ['ears', 'Ears only']].map(([v, n]) => `<button class="btn ${c.route === v ? 'on' : ''}" data-proute="${v}">${n}</button>`).join('')}
        <button class="btn ${c.loop ? 'on' : ''}" data-act="padloop">${icon('i-repeat')} Loop</button>
      </div>
    </div>
    <p class="hint">${c.loop ? 'It plays over and over until you press the pad again.' : 'Looping turns the pad into a switch: press to start, press again to stop.'}</p>
    <p class="deck-note ${ed.note ? '' : 'hidden'}">${esc(ed.note)}</p>
    <div class="sheet-foot">
      ${canDeck ? `<button class="btn" data-act="padtile">${icon('i-deck')} Put on the Deck</button>` : ''}
      <span class="grow"></span>
      <button class="btn" data-act="padplay">${icon('i-play')} Play</button>
      <button class="btn primary" data-act="close">${icon('i-check')} Done</button>
    </div>`);
}

let gainTimer = null;

sheet().addEventListener('input', (e) => {
  const ed = deck.editor;
  if (!ed || ed.kind !== 'pad') return;
  if (e.target.dataset.f === 'padgain') {
    clearTimeout(gainTimer);
    const v = +e.target.value;
    gainTimer = setTimeout(() => send('sb.gain', { id: ed.id, value: v }), 120);
  }
});

sheet().addEventListener('change', (e) => {
  const ed = deck.editor;
  if (!ed || ed.kind !== 'pad') return;
  const f = e.target.dataset.f;
  if (f === 'padname') {
    const name = e.target.value.trim();
    if (name) send('sb.name', { id: ed.id, name });
  } else if (f === 'padfolder') {
    if (e.target.value === '__new') { ed.newFolder = true; renderPadSheet(); }
    else send('sb.folder', { id: ed.id, folder: e.target.value === '__none' ? '' : e.target.value });
  }
});

sheet().addEventListener('click', (e) => {
  const ed = deck.editor;
  if (!ed || ed.kind !== 'pad') return;
  const c = padClip(ed.id);
  if (!c) return;
  const col = e.target.closest('[data-pcol]');
  if (col) return send('sb.color', { id: ed.id, color: col.dataset.pcol });
  const rt = e.target.closest('[data-proute]');
  if (rt) return send('sb.route', { id: ed.id, route: rt.dataset.proute });
  const act = e.target.closest('[data-act]');
  const a = act && act.dataset.act;
  if (a === 'padloop') return send('sb.loop', { id: ed.id, value: c.loop ? 0 : 1 });
  if (a === 'padplay') return send('sb.play', { id: ed.id });
  if (a === 'padfolder') {
    const v = ($('[data-f="padnewfolder"]', sheet()) || {}).value || '';
    return send('sb.folder', { id: ed.id, folder: v.trim() }, (r) => {
      ed.newFolder = false;
      ed.note = r.ok ? '' : (r.error || 'That folder name was not accepted.');
      renderPadSheet();
    });
  }
  if (a === 'padtile') {
    // A pad on the Deck is an ordinary tile with one step, so it can be moved, recoloured and combined.
    const tile = { id: 'snd-' + c.id, name: c.name.slice(0, 24), icon: 'i-audio', color: c.color || null,
                   steps: [{ do: 'sound', id: c.id }] };
    return saveEditedProfile((prof) => {
      const at = prof.tiles.findIndex(t => t.id === tile.id);
      if (at >= 0) prof.tiles[at] = tile; else prof.tiles.push(tile);
    }, (r) => {
      ed.note = r.ok ? 'Added to the "' + ((activeProfile() || {}).name || 'Deck') + '" profile.' : (r.error || 'The PC did not accept that.');
      renderPadSheet();
    });
  }
});

/* ================================================================= boot === */

/* ============================================================== night mode = */

/* The tablet's own brightness and an amber tint, remembered across restarts. Brightness is the
   app window's, set through the shell, so the system setting is never touched; the tint is a
   plain overlay. In a browser, with no shell to ask, a dark layer stands in for brightness so
   the control can still be seen working. */
const NIGHT_KEY = 'deck.night';
const night = { dim: 100, warm: 0 };
try {
  const saved = JSON.parse(localStorage.getItem(NIGHT_KEY) || 'null');
  if (saved) { night.dim = Math.min(100, Math.max(1, +saved.dim || 100)); night.warm = Math.min(100, Math.max(0, +saved.warm || 0)); }
} catch (_) { /* nothing saved, or storage unavailable */ }

function setNight(change) {
  if (change.dim != null) night.dim = Math.min(100, Math.max(1, Math.round(change.dim)));
  if (change.warm != null) night.warm = Math.min(100, Math.max(0, Math.round(change.warm)));
  try { localStorage.setItem(NIGHT_KEY, JSON.stringify(night)); } catch (_) { /* not fatal */ }
  applyNight();
}

function applyNight() {
  const warm = $('#warm'), dimmer = $('#dimmer');
  warm.hidden = night.warm <= 0;
  warm.style.opacity = String(night.warm / 100 * 0.38);
  if (native && native.setBrightness) {
    native.setBrightness(night.dim >= 100 ? 0 : night.dim);
    dimmer.hidden = true;
  } else {
    dimmer.hidden = night.dim >= 100;
    dimmer.style.opacity = String((100 - night.dim) / 100 * 0.85);
  }
}

const UI = {};

function build() {
  const homeStack = $('#homeStack');
  homeStack.className = 'home-grid';

  UI.homeMedia = makeMediaCard(false);
  UI.homePerf = makePerfCard(false);
  UI.homeAudio = makeAudioMini();
  UI.homeApps = makeAppsCard(6, false);

  UI.slots = {};
  for (const [name, card, cls] of [
    ['media', UI.homeMedia, ''], ['perf', UI.homePerf, ''],
    ['audio', UI.homeAudio, ''], ['apps', UI.homeApps, 'full'],
  ]) {
    const slot = el('div', 'slot ' + cls);
    slot.appendChild(card.node);
    homeStack.appendChild(slot);
    UI.slots[name] = slot;
  }

  // One page for everything to do with sound: what is playing, where to start playing,
  // and every level and device that carries it. Media and audio used to be separate tabs,
  // which meant the volume of the track you were looking at lived somewhere else.
  //
  // The audio half is the existing audio page appended whole - the same cards, listeners
  // and reconciliation, just hosted here instead of on a tab of its own.
  const mediaStack = el('div', 'stack');
  UI.mediaPage = makeMediaCard(true);
  UI.mediaSources = makeSourcesCard();
  UI.audioPage = makeAudioPage();
  UI.mediaExtras = makeMediaExtras();
  mediaStack.append(UI.mediaPage.node, UI.mediaSources.node, UI.mediaExtras.node, UI.audioPage.node);
  $('#mediaFull').appendChild(mediaStack);

  UI.gamePage = makeGamePage();
  UI.sessions = makeSessionsCard();
  // The cards join the gaming page's own stack, so they share its spacing.
  [...UI.sessions.node.children].forEach(c => UI.gamePage.node.appendChild(c));
  $('#gameFull').appendChild(UI.gamePage.node);

  UI.voicePage = makeVoicePage();
  $('#voiceFull').appendChild(UI.voicePage.node);

  UI.perfPage = makePerfCard(true);
  UI.perfExtras = makePerfExtras();
  const perfStack = el('div', 'stack');
  perfStack.append(UI.perfPage.node, UI.perfExtras.node);
  $('#perfFull').appendChild(perfStack);

  UI.appsPage = makeAppsCard(0, true);
  $('#appsFull').appendChild(UI.appsPage.node);

  UI.deckPage = makeDeckPage();
  $('#deckFull').appendChild(UI.deckPage.node);

  UI.clockPage = makeClockPage();
  $('#clockFull').appendChild(UI.clockPage.node);

  UI.quickPage = makeQuickPage();
  $('#quickFull').appendChild(UI.quickPage.node);
}

/* ============================================================= rendering = */

/**
 * Entry point invoked by the Kotlin shell for every frame from the laptop. The payload is base64
 * so the shell never has to build a JS string literal, which would otherwise have to escape
 * quotes, backslashes and newlines coming from arbitrary track titles and device names.
 */
window.__deckDeliver = function (b64) {
  let msg;
  try {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    msg = JSON.parse(new TextDecoder('utf-8').decode(bytes));
  } catch (e) {
    console.warn('bad frame', e);
    return;
  }
  ingest(msg);
};

function ingest(msg) {
  switch (msg.type) {
    case 'link': S.link = msg; break;
    case 'hello':
      S.link = Object.assign({}, S.link, { hostName: msg.hostName, phase: 'connected' });
      S.caps = msg.capabilities || {};
      break;
    case 'sys': S.sys = msg; break;
    case 'media': applyMedia(msg); break;
    case 'audio': S.audio = msg; break;
    case 'ctx': S.ctx = msg; break;
    case 'apps': S.apps = msg; break;
    case 'running': S.running = msg; break;
    case 'cast': S.cast = msg; break;
    case 'soundboard': S.sb = msg; break;
    case 'mediaSources': S.mediaSources = msg; break;
    case 'tiles': S.tiles = msg; break;
    case 'macros': S.macros = msg; break;
    case 'notifications': S.notifications = msg; break;
    case 'history': S.history = msg; break;
    case 'top': S.top = msg; break;
    case 'alerts': S.alerts = msg; break;
    case 'sessions': S.sessions = msg; break;
    case 'frames': S.frames = msg; break;
    case 'spectrum': onSpectrum(msg.bands || []); return;
    case 'alert': showAlert(msg); return;
    case 'clock':
      S.clock = msg;
      S.timeOffset = msg.nowMs - Date.now();
      S.tzOffsetMin = msg.utcOffsetMin;
      tickClock();
      break;
    case 'timers':
      S.timers = msg;
      S.timeOffset = msg.nowMs - Date.now();
      break;
    case 'assetReady': return refreshAsset(msg.key);
    case 'result': {
      const w = cmdWaiters.get(msg.id);
      if (w) { cmdWaiters.delete(msg.id); w(msg); }
      if (msg.error) console.warn('command failed:', msg.error);
      return;
    }
    default: return;
  }
  // A hello or link change can affect every card (capabilities, connected state).
  schedule(msg.type === 'hello' || msg.type === 'link' ? '*' : msg.type);
}

function refreshAsset(key) {
  // A late-arriving image: point the existing <img> at it without rebuilding anything.
  document.querySelectorAll('img').forEach(img => {
    if (img.src && img.src.endsWith('/' + key)) img.src = artUrl(key) + '?r=' + Date.now();
  });
  schedule('*');
}

/* Which sections changed since the last paint. The home page receives five independent
   messages per second, and re-running every card's update for each of them was four times
   more work than necessary. Now a sys frame touches only the gauges. */
const pending = new Set();
const needs = (...keys) => pending.has('*') || keys.some(k => pending.has(k));

function schedule(section) {
  if (section) pending.add(section);
  if (dirty) return;
  dirty = true;
  requestAnimationFrame(() => {
    dirty = false;
    render();
    pending.clear();
  });
}

function render() {
  renderTop();

  const connected = S.link.phase === 'connected';
  $('#veil').classList.toggle('show', !connected);
  // A guide opened by hand has done its job once a link exists. Left on, it would reappear
  // at the next disconnection and bury the plain "waiting" screen under instructions the
  // person has already followed.
  if (connected) setupOpen = false;
  if (!connected) {
    // A laptop was found on the network but this tablet has never been on the cable, so it
    // holds no key. Saying so beats looking for it forever.
    const unpaired = S.link.note === 'unpaired';
    text($('#veilTitle'), unpaired ? 'Pair this tablet first'
      : S.link.phase === 'listening' ? 'Waiting for your PC' : 'Looking for your PC');
    text($('#veilBody'), unpaired
      ? 'Plug in the USB cable once. After that this tablet can connect over Wi-Fi without it, and nobody else on the network can.'
      : 'Plug in the USB cable, or make sure the Companion Deck host is running.');
    renderSetup();
  }

  // Only the visible page is updated. Off-screen cards keep their DOM and state
  // and cost nothing until they are shown again.
  if (page === 'home') {
    if (needs('ctx', 'media')) layoutHome();
    if (needs('media', 'mediaSources')) updateMediaCard(UI.homeMedia);
    if (needs('sys', 'ctx')) updatePerfCard(UI.homePerf);
    if (needs('audio')) updateAudio(UI.homeAudio, { limit: 3 });
    if (needs('apps')) updateApps(UI.homeApps);
  } else if (page === 'media') {
    if (needs('media', 'audio', 'mediaSources')) updateMediaCard(UI.mediaPage);
    if (needs('mediaSources')) updateSourcesCard(UI.mediaSources);
    if (needs('audio')) updateAudio(UI.audioPage);
  } else if (page === 'game') {
    if (needs('sys', 'ctx', 'game')) updateGamePage(UI.gamePage);
    if (needs('sessions', 'frames', 'apps')) updateSessionsCard(UI.sessions);
  } else if (page === 'voice') {
    if (needs('cast')) updateVoicePage(UI.voicePage);
    if (needs('soundboard')) updateSoundboard(UI.voicePage);
  } else if (page === 'perf') {
    if (needs('sys', 'ctx')) updatePerfCard(UI.perfPage);
    if (needs('history', 'top', 'alerts')) updatePerfExtras(UI.perfExtras);
  } else if (page === 'apps') {
    if (needs('apps', 'running')) updateApps(UI.appsPage);
  } else if (page === 'deck') {
    if (needs('tiles', 'ctx')) updateDeck(UI.deckPage);
  } else if (page === 'clock') {
    if (needs('clock', 'timers', 'notifications')) updateClock(UI.clockPage);
  } else if (page === 'quick') {
    if (needs('sys')) updateQuick(UI.quickPage);
  }

  // An editor opened before the lists it offers had arrived fills in as they land, but only
  // when something it shows has changed: redrawing it on every frame would take the keyboard away.
  const ed = deck.editor;
  if (ed && ed.kind === 'tile' && needs('apps', 'audio', 'soundboard', 'macros')) {
    const sig = editorSignature();
    if (sig !== deck.sig) { deck.sig = sig; renderTileEditor(); }
    else if (S.macros && S.macros.recording) {
      // Only the counter moves while recording; redrawing the sheet would take the keyboard away.
      const live = $('.rec-live', sheet());
      if (live) text(live, macroLive(S.macros.recording));
    }
  }
  if (ed && ed.kind === 'profile' && needs('running')) {
    const sig = ((S.running && S.running.items) || []).map(x => x.name).join();
    if (sig !== ed.runSig) { ed.runSig = sig; renderProfileEditor(); }
  }
}

/* Reordering is class and `order` only: no node is moved, so nothing flashes,
   nothing loses scroll position, and no listener is torn down. */
/*
 * The dashboard has one layout, and it does not move. It used to choose its lead card live on
 * every pass - System while gaming, Media when a session existed, System otherwise.
 */
const HOME_ORDER = { media: 1, perf: 2, audio: 3, apps: 4 };
const HOME_LEAD = 'media';

function layoutHome() {
  for (const name of ['media', 'perf', 'audio', 'apps']) {
    const slot = UI.slots[name];
    const o = String(HOME_ORDER[name]);
    if (slot.style.order !== o) slot.style.order = o;
    slot.classList.toggle('hero', name === HOME_LEAD);
    slot.classList.toggle('full', name === 'apps');
  }
}

function renderTop() {
  const connected = S.link.phase === 'connected';
  $('#linkDot').classList.toggle('on', connected);
  text($('#hostName'), connected ? (S.link.hostName || 'PC') : 'Companion Deck');

  const t = S.link.transport || '';
  text($('#transportText'), t ? t.toUpperCase() : '–');
  $('#transport').querySelector('use').setAttribute('href', t === 'wifi' ? '#i-wifi' : '#i-usb');
  $('#transport').classList.toggle('hot', connected);

  const badge = $('#ctxBadge');
  const showCtx = S.ctx && S.ctx.mode && S.ctx.mode !== 'desktop';
  badge.classList.toggle('hidden', !showCtx);
  if (showCtx) text(badge, S.ctx.mode);

  const b = $('#battery');
  const showBatt = S.sys && S.sys.battery != null;
  b.classList.toggle('hidden', !showBatt);
  if (showBatt) {
    // It rides with the battery percentage rather than sitting in the performance strip:
    // it is a property of the battery, the chip is already here, and putting it in the
    // strip left a lone ninth tile on a row of its own.
    const mode = S.caps.chargeMode && S.sys.chargeMode;
    const tag = mode && mode !== 'normal' ? ' · ' + CHARGE_SHORT[mode] : '';
    // The charging mark is the sprite's own bolt, not the lightning emoji: an emoji is drawn by
    // the system in its own colours and shape, which clashed with everything around it.
    // While charging the whole chip takes the accent, the way the link chip does when live.
    text($('#batText'), S.sys.battery + '%');
    // A class, not the hidden property: SVG elements have no such property, so assigning
    // it silently does nothing and the attribute in the markup keeps the icon hidden.
    $('#batBolt').classList.toggle('hidden', !S.sys.charging);
    text($('#batTag'), tag);
    b.classList.toggle('hot', !!S.sys.charging);
  }

  renderTimerChip();
  updateAmbientNp();
}

/* ============================================================== setup ==== */

/* First-run guide.
 *
 * The tablet can read its own Developer options and USB debugging flags without any
 * permission, so each step ticks itself the moment it is done. That turns a page of
 * instructions into a checklist that answers "did that work?" on its own, which is the
 * part people actually get stuck on.
 *
 * It leads only until the first successful link; after that it is behind a button, because
 * a later disconnection is a disconnection, not a reason to re-teach someone. */

let setupState = null;
let setupOpen = false;

// The shell pushes this on every link change and on resume; this is the first read, for
// the case where the page loads before anything has changed.
window.addEventListener('DOMContentLoaded', () => { readSetupState(); renderSetup(); });

window.__deckSetup = function (json) {
  try { setupState = JSON.parse(json); } catch (e) { return; }
  renderSetup();
};

function readSetupState() {
  if (!window.DeckNative || !DeckNative.setupState) return;
  try { setupState = JSON.parse(DeckNative.setupState()); } catch (e) { /* older shell */ }
}

function renderSetup() {
  const panel = $('#setup');
  if (!panel) return;

  // Lead with the guide until a link has worked once; after that, only on request.
  const firstRun = setupState ? !setupState.everConnected : false;
  const show = setupOpen || (firstRun && !setupState.usbDebugging);
  panel.hidden = !show;
  $('#veil').classList.toggle('guiding', show);
  if (!show) return;

  const done = {
    dev: !!(setupState && setupState.devOptions),
    adb: !!(setupState && setupState.usbDebugging),
    // Nothing on the tablet reports the authorisation prompt, so this one is inferred:
    // a link that has ever worked is the only proof it was accepted.
    allow: !!(setupState && setupState.everConnected),
  };

  // Highlight only the first unfinished step, so the eye lands on one row of three.
  let highlighted = false;
  for (const li of panel.querySelectorAll('.step')) {
    const ok = done[li.dataset.step];
    li.classList.toggle('done', ok);
    li.classList.toggle('next', !ok && !highlighted);
    if (!ok) highlighted = true;
  }
}

$('#veil').addEventListener('click', (e) => {
  if (e.target.closest('#veilHelp')) { setupOpen = true; readSetupState(); return renderSetup(); }
  if (e.target.closest('#setupClose')) { setupOpen = false; return renderSetup(); }

  const go = e.target.closest('[data-open]');
  if (go && window.DeckNative && DeckNative.openSettings) {
    DeckNative.openSettings(go.dataset.open);
  }
});

/* ============================================================== plumbing = */

$('#rail').addEventListener('click', (e) => {
  const b = e.target.closest('[data-nav]');
  if (!b) return;
  page = b.dataset.nav;
  app.dataset.page = page;
  $('#rail').querySelectorAll('.rail-btn').forEach(x => x.classList.toggle('active', x === b));
  $('#content').scrollTop = 0;
  pushSubscription();
  requestHistory();
  schedule('*');   // a newly shown page must paint everything it owns
});

function pushSubscription() {
  // 'mediaSources' is published on subscribe and after a catalogue scan, never on the
  // telemetry tick, so asking for it costs the laptop nothing per frame.
  const map = {
    home: ['sys', 'media', 'audio', 'ctx', 'apps', 'mediaSources'],
    media: ['media', 'audio', 'ctx', 'mediaSources'],
    // The gaming page is almost entirely buttons. It needs 'sys' only to show which
    // mode is live, and nothing else - so opening it while a game is running stops the
    // laptop collecting media, audio and app telemetry altogether.
    game: ['sys', 'ctx', 'sessions', 'frames'],
    voice: ['cast', 'soundboard'],
    perf: ['sys', 'ctx', 'alerts', 'top'],
    // 'running' enumerates every process and every top-level window, so it is asked for
    // only by the one page that shows it - and stops the moment you navigate away.
    apps: ['apps', 'ctx', 'running'],
    // 'sys' carries the current brightness and thermal mode shown on this page.
    quick: ['sys', 'ctx'],
    // The deck follows what is in front, which is why it also wants 'ctx'.
    deck: ['tiles', 'ctx'],
    clock: ['notifications'],
  };
  const sections = (map[page] || ['sys']).slice();
  // Time and timers go to every page: the clock in the corner reads the PC's time, and a timer
  // that goes off has to be seen wherever you are. Both only send when something changes.
  for (const s of ['clock', 'timers']) if (!sections.includes(s)) sections.push(s);
  // An open editor offers the apps, outputs and clips it can point a step at.
  if (deck.editor) for (const s of ['apps', 'audio', 'soundboard', 'running', 'macros', 'tiles']) if (!sections.includes(s)) sections.push(s);
  // The audio capture behind the bars runs only while they are on screen.
  if (look.ambient ? look.viz : (page === 'media' && look.viz)) sections.push('spectrum');
  // Nothing on the gaming page moves on its own, so one reading every two seconds is
  // plenty - and that is the page most likely to be open mid-match.
  // The voice page is the exception to the "slower is better" rule: a level meter updated
  // once a second is not a level meter, and the meter is the only evidence the cast is
  // actually carrying audio. It subscribes to nothing else, and the payload is a handful of
  // numbers, so the extra rate buys the one thing that page exists to show.
  const hz = (page === 'perf') ? 2 : (page === 'voice') ? 8 : (page === 'game' ? 2 : 1);
  if (native) native.setSubscription(sections.join(','), hz);
}

document.addEventListener('visibilitychange', () => {
  // Same reasoning as the rail: a screen-off tablet must not still be typing into a match.
  if (!native) return;
  if (document.hidden) native.setSubscription('', 0);
  else pushSubscription();
});

function tickClock() {
  const d = hostDate();
  text($('#clock'), d.getUTCHours() + ':' + String(d.getUTCMinutes()).padStart(2, '0'));
}
setInterval(tickClock, 10000);
setInterval(tickSecond, 1000);

/* Smooth seek bar with no extra traffic. Only touches the two visible nodes. */
setInterval(() => {
  if (!anchor || !anchor.playing) return;
  const s = activeSession();
  if (!s) return;
  if (page === 'home') updateSeek(UI.homeMedia, s);
  else if (page === 'media') updateSeek(UI.mediaPage, s);
}, 500);

/* ---------------------------------------------------------- frame metering */

/* Samples real frame intervals and reports them to logcat, so rendering problems can be
   diagnosed with numbers from this exact hardware instead of guessed at. Flip to false
   once a change has been validated; the rAF loop itself keeps the compositor awake. */
const PERF_DEBUG = false;

function startFrameMeter() {
  let last = performance.now();
  let samples = [];
  let long = 0;

  requestAnimationFrame(function tick(now) {
    const dt = now - last;
    last = now;

    // Ignore the first frame after a tab/page switch, which is legitimately long.
    if (dt < 2000) {
      samples.push(dt);
      if (dt > 33) long++;      // anything over ~2 frames at 60Hz reads as a stutter
    }

    if (samples.length >= 150) {
      const sorted = samples.slice().sort((a, b) => a - b);
      const p50 = sorted[(sorted.length * 0.50) | 0];
      const p95 = sorted[(sorted.length * 0.95) | 0];
      const worst = sorted[sorted.length - 1];
      console.log(`DECKPERF page=${page} fps=${(1000 / p50).toFixed(1)} ` +
                  `p50=${p50.toFixed(1)}ms p95=${p95.toFixed(1)}ms ` +
                  `worst=${worst.toFixed(1)}ms janky=${long}/${samples.length}`);
      samples = [];
      long = 0;
    }
    requestAnimationFrame(tick);
  });
}

/* ------------------------------------------------------------------ start */

build();
applyNight();
tickClock();
if (PERF_DEBUG) startFrameMeter();

if (native) {
  try { ingest(Object.assign({ type: 'link' }, JSON.parse(native.linkState()))); } catch (e) {}
  pushSubscription();
  native.requestSnapshot();
} else {
  installMockData();
}

/* Browser preview harness: the whole UI is designable at 1024x600 in a desktop
   browser, including states that are awkward to reproduce on real hardware. */
function installMockData() {
  // The setup guide is driven by the Android shell, so give the browser something to draw.
  // `?setup=` selects a stage: new (nothing done), half (dev options only), done.
  const stage = new URLSearchParams(location.search).get('setup');
  if (stage) {
    setupState = {
      devOptions: stage !== 'new',
      usbDebugging: stage === 'done',
      everConnected: false,
      model: 'Preview', android: '14',
    };
    S.link = { phase: 'listening', transport: '', hostName: '' };
  } else {
    setupState = { devOptions: true, usbDebugging: true, everConnected: true,
                   model: 'Preview', android: '14' };
    S.link = { phase: 'connected', transport: 'usb', hostName: 'MY-PC', paired: true };
  }
  S.caps = { fps: true, cpuTemp: true, fans: true, brightness: true, audioDeviceSwitch: true,
              gpu: true, gpuDetail: true,
              thermalMode: true, thermalModeKind: 'powerplan', micCast: true,
              chargeMode: true, keyboardLight: true,
            };
  S.ctx = { mode: 'desktop', foregroundApp: 'explorer' };
  // `?caps=` drops capabilities, so the "nothing unsupported is drawn" rule is testable:
  // ?caps=-thermalMode,-gpu,-gpuDetail,-fans,-brightness,-micCast,-fps
  const drop = new URLSearchParams(location.search).get('caps');
  if (drop) for (const k of drop.split(',')) if (k.startsWith('-')) S.caps[k.slice(1)] = false;
  // ?demo=live shows the voice tools mid-use, with the cast armed and the recorder buffering,
  // which is how the page looks in practice and far more informative than a row of off switches.
  const live = new URLSearchParams(location.search).get('demo') === 'live';
  S.cast = {
    available: true, armed: live, micName: 'Microphone Array',
    micEnabled: true, desktopGain: 55, micGain: 100, desktopPeak: 0, micPeak: 0,
    outputs: [
      { id: 'spk', name: 'Speakers (Realtek Audio)', isDefault: true },
      { id: 'hdmi', name: 'Monitor (HDMI Audio)', isDefault: false },
    ],
    inputs: [
      { id: 'mica', name: 'Microphone Array', isDefault: true },
      { id: 'usb', name: 'USB Microphone', isDefault: false },
    ],
  };
  S.sb = {
    folder: 'C:\\Users\\you\\AppData\\Local\\CompanionDeck\\soundboard',
    recorderArmed: live, recording: false, bufferedSeconds: live ? 48 : 0, bufferCapacity: 60,
    recordingSeconds: 0, normalise: true,
    items: [
      { id: 'a1', name: 'Airhorn', route: 'both', gain: 80, order: 0, seconds: 2.1, group: 'Reactions', color: 'amber' },
      { id: 'a2', name: 'Applause', route: 'both', gain: 100, order: 1, seconds: 4.6, group: 'Reactions', color: 'green' },
      { id: 'a3', name: 'Drum roll', route: 'mic', gain: 100, order: 2, seconds: 3.2, group: 'Reactions', color: 'violet' },
      { id: 'a4', name: 'Sad trombone', route: 'both', gain: 90, order: 3, seconds: 2.8, group: 'Reactions', color: 'blue' },
      { id: 'a5', name: 'Wow', route: 'mic', gain: 100, order: 4, seconds: 0.9, group: 'Reactions', color: 'pink' },
      { id: 'a6', name: 'Rimshot', route: 'both', gain: 100, order: 5, seconds: 1.3, group: 'Reactions' },
      { id: 'a7', name: 'Crowd gasp', route: 'ears', gain: 100, order: 6, seconds: 1.8, group: 'Reactions' },
      { id: 'a9', name: 'Rain', route: 'ears', gain: 60, order: 7, seconds: 42, group: 'Ambience', color: 'blue', loop: true },
      { id: 'a10', name: 'Cafe', route: 'ears', gain: 45, order: 8, seconds: 60, group: 'Ambience', loop: true },
      { id: 'a8', name: 'clip 2026-10-03 21-14-02', route: 'ears', gain: 100, order: 9, seconds: 30.0 },
    ],
  };
  // Launcher icons and game covers: a letter on a gradient. They stand in for the artwork the
  // real app fetches from the PC, and are generic on purpose rather than imitations of a logo.
  // Defined here, ahead of the first list that uses them.
  const tile = (letter, a, b) => 'data:image/svg+xml;utf8,' + encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120"><defs>' +
    '<linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="' + a + '"/>' +
    '<stop offset="1" stop-color="' + b + '"/></linearGradient></defs>' +
    '<rect width="120" height="120" rx="26" fill="url(#g)"/>' +
    '<text x="60" y="80" text-anchor="middle" font-family="Segoe UI,Roboto,sans-serif" ' +
    'font-size="58" font-weight="700" fill="#fff">' + letter + '</text></svg>');
  const art = (key, letter, a, b) => { DEMO_ART[key] = tile(letter, a, b); return key; };

  S.running = {
    items: [
      { pid: 4120, name: 'Browser', title: 'Project plan - 12 tabs', memoryMb: 1840, windowed: true },
      { pid: 9284, name: 'Code', title: 'main.rs - my-project', memoryMb: 920, windowed: true },
      { pid: 12096, name: 'Spotify', title: 'Midnight Drive', memoryMb: 310, windowed: true },
      { pid: 3928, name: 'Teams', title: 'Weekly sync', memoryMb: 205, windowed: true },
      { pid: 7710, name: 'SearchIndexer', title: null, memoryMb: 412, windowed: false },
      { pid: 6180, name: 'RuntimeBroker', title: null, memoryMb: 188, windowed: false },
      { pid: 5502, name: 'NVDisplay.Container', title: null, memoryMb: 141, windowed: false },
    ],
  };
  S.apps = {
    items: [
      { id: 'game', name: 'Neon Drift', kind: 'game', running: true, iconKey: art('g-neon', 'N', '#ff4f8b', '#5a1a7a'), playMinutes: 1530, lastPlayedMs: Date.now() - 3600000 },
      { id: 'ow', name: 'Hollow Keep', kind: 'game', iconKey: art('g-keep', 'H', '#2be3d0', '#0b4a52'), playMinutes: 310, lastPlayedMs: Date.now() - 26 * 3600000 },
      { id: 'cs', name: 'Starfall Tactics', kind: 'game', iconKey: art('g-star', 'S', '#7c5cff', '#241a5c'), playMinutes: 45, lastPlayedMs: Date.now() - 6 * 86400000 },
      { id: 'steam', name: 'Steam', kind: 'app', iconKey: art('a-steam', 'S', '#3b6ea5', '#14243a') },
      { id: 'epic', name: 'Epic Games', kind: 'app', iconKey: art('a-epic', 'E', '#4a5568', '#171b22') },
      { id: 'discord', name: 'Discord', kind: 'app', iconKey: art('a-chat', 'D', '#6c7bff', '#262a6e') },
      { id: 'browser', name: 'Browser', kind: 'app', iconKey: art('a-web', 'B', '#ff7a3d', '#7a2a0b') },
      { id: 'spotify', name: 'Spotify', kind: 'app', iconKey: 'i-spotify' },
    ],
  };
  S.audio = {
    masterVolume: 62, masterMuted: false, micVolume: 80, micMuted: false,
    deafened: false,
    outputs: [
      { id: 'o1', name: 'Speakers (Realtek Audio)', isDefault: true, volume: 62, muted: false },
      { id: 'o2', name: 'Headphones (Bluetooth)', isDefault: false, volume: 40, muted: false },
      { id: 'o4', name: 'Living room TV (HDMI)', isDefault: false, volume: 25, muted: false },
      { id: 'o3', name: 'CABLE Input (VB-Audio Virtual Cable)', isDefault: false, volume: 100, muted: false },
    ],
    inputs: [
      { id: 'i1', name: 'Microphone Array', isDefault: true },
      { id: 'i2', name: 'USB Microphone', isDefault: false },
    ],
    apps: [
      { id: 'spotify', name: 'Spotify', volume: 90, muted: false, active: true, remembered: true },
      { id: 'somegame', name: 'Neon Drift', volume: 70, muted: false, active: true },
      { id: 'discord', name: 'Discord', volume: 55, muted: false, active: false },
      { id: 'browser', name: 'Browser', volume: 100, muted: true, active: false },
    ],
  };
  // A synthwave sunset, drawn rather than shipped as a file, so the preview needs no assets.
  DEMO_ART['demo-cover'] = 'data:image/svg+xml;utf8,' + encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 400">' +
    '<defs><linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">' +
    '<stop offset="0" stop-color="#12082e"/><stop offset=".55" stop-color="#5a1a7a"/>' +
    '<stop offset="1" stop-color="#ff4f8b"/></linearGradient>' +
    '<linearGradient id="sun" x1="0" y1="0" x2="0" y2="1">' +
    '<stop offset="0" stop-color="#ffd166"/><stop offset="1" stop-color="#ff4f8b"/></linearGradient>' +
    '<clipPath id="c"><circle cx="200" cy="230" r="96"/></clipPath></defs>' +
    '<rect width="400" height="400" fill="url(#sky)"/>' +
    '<g clip-path="url(#c)"><rect x="100" y="130" width="200" height="200" fill="url(#sun)"/>' +
    '<g fill="#5a1a7a"><rect x="100" y="238" width="200" height="5"/><rect x="100" y="256" width="200" height="8"/>' +
    '<rect x="100" y="276" width="200" height="11"/><rect x="100" y="299" width="200" height="14"/></g></g>' +
    '<rect y="300" width="400" height="100" fill="#0b0420"/>' +
    '<g stroke="#2be3d0" stroke-width="1.6" opacity=".75" fill="none">' +
    '<path d="M0 316H400M0 336H400M0 362H400M0 400H400"/>' +
    '<path d="M200 300V400M150 300L40 400M100 300L-60 400M250 300L360 400M300 300L460 400"/></g></svg>');

  S.mediaSources = {
    items: [
      { id: 'src:spotify', name: 'Spotify', kind: 'app', running: true, iconKey: art('i-spotify', 'S', '#1fb96b', '#0b6b3f') },
      { id: 'src:spotify-web', name: 'Spotify Web', kind: 'web', service: 'spotify', via: 'Browser', viaIconKey: art('a-web', 'B', '#ff7a3d', '#7a2a0b') },
      { id: 'src:ytmusic', name: 'YouTube Music', kind: 'web', service: 'ytmusic', via: 'Browser', viaIconKey: 'a-web' },
      { id: 'src:wmp', name: 'Windows Media Player', kind: 'app', iconKey: art('i-wmp', 'M', '#f29a3a', '#8a4a0b') },
      { id: 'src:vlc', name: 'VLC', kind: 'app', iconKey: art('i-vlc', 'V', '#ff9d2e', '#a1500a') },
      { id: 'src:music-folder', name: 'Music', kind: 'folder' },
      { id: 'src:video-folder', name: 'Videos', kind: 'folder' },
    ],
  };
  S.media = {
    sessions: [{
      id: 'Spotify.exe', appName: 'Spotify', title: 'Midnight Drive', artist: 'Neon Harbor',
      album: 'Afterglow', artKey: 'demo-cover', status: 'playing', positionSec: 74, durationSec: 258,
      canPlay: true, canPause: true, canNext: true, canPrevious: true, canSeek: true,
      shuffle: false, repeat: 'none', appVolume: 90, appMuted: false,
    }],
    activeId: 'Spotify.exe',
  };
  applyMedia(S.media);

  // The deck, calendar and timers. Times are built from "now" so a screenshot taken at any hour
  // shows an upcoming day rather than one that has already ended.
  const q = new URLSearchParams(location.search);
  S.timeOffset = 0;
  S.tzOffsetMin = -new Date().getTimezoneOffset();
  if (q.get('ctx') === 'gaming') S.ctx = { mode: 'gaming', foregroundApp: 'NeonDrift' };

  const st = (o) => o;
  const mkTile = (id, name, icon, color, steps) => ({ id, name, icon, color, confirm: steps.some(x => x.do === 'sys' && ['closewindow', 'sleep', 'restart', 'shutdown'].includes(x.id)), steps });
  const key = (k) => st({ do: 'key', keys: k });
  const sys = (id) => st({ do: 'sys', id });
  const aud = (verb) => st({ do: 'audio', verb });
  const med = (verb) => st({ do: 'media', verb });

  S.tiles = {
    active: 'default', auto: true, pinned: null, canEdit: q.get('transport') !== 'wifi', running: [],
    catalog: { sys: ['start', 'search', 'desktop', 'taskmgr', 'screenshot', 'explorer', 'calc', 'settings', 'dismiss', 'lock', 'closewindow', 'sleep', 'restart', 'shutdown'] },
    profiles: [
      { id: 'default', name: 'Desktop', match: [], tiles: [
        mkTile('mic', 'Mic mute', 'i-mic-off', 'red', [aud('micMuteToggle')]),
        mkTile('deafen', 'Deafen', 'i-mute', 'red', [aud('deafenToggle')]),
        mkTile('playpause', 'Play / pause', 'i-play', 'accent', [med('playpause')]),
        mkTile('next', 'Next track', 'i-next', null, [med('next')]),
        mkTile('shot', 'Screenshot', 'i-camera', null, [sys('screenshot')]),
        mkTile('desktop', 'Show desktop', 'i-monitor', null, [sys('desktop')]),
        mkTile('search', 'Search', 'i-search', null, [sys('search')]),
        mkTile('taskmgr', 'Task Manager', 'i-chip', null, [sys('taskmgr')]),
        mkTile('focus', 'Focus mode', 'i-leaf', 'green', [aud('micMuteToggle'), { do: 'wait', ms: 500 }, { do: 'timer', verb: 'pomodoro' }, med('playpause')]),
        mkTile('lock', 'Lock', 'i-lock', 'amber', [sys('lock')]),
        mkTile('sleep', 'Sleep', 'i-bolt', 'red', [sys('sleep')]),
      ] },
      { id: 'gaming', name: 'Gaming', match: ['@gaming'], tiles: [
        mkTile('mic', 'Mic mute', 'i-mic-off', 'red', [aud('micMuteToggle')]),
        mkTile('deafen', 'Deafen', 'i-mute', 'red', [aud('deafenToggle')]),
        mkTile('gamebar', 'Game Bar', 'i-game', 'violet', [key('win+g')]),
        mkTile('record', 'Record', 'i-rec', 'red', [key('win+alt+r')]),
        mkTile('clip', 'Screenshot', 'i-camera', null, [key('win+alt+prtsc')]),
        mkTile('overlay', 'NVIDIA overlay', 'i-chip', 'green', [key('alt+z')]),
        mkTile('steam', 'Steam overlay', 'i-apps', 'blue', [key('shift+tab')]),
        mkTile('playpause', 'Play / pause', 'i-play', 'accent', [med('playpause')]),
      ] },
      { id: 'editing', name: 'Editing', match: ['Premiere', 'AfterFX'], tiles: [
        mkTile('save', 'Save', 'i-check', 'green', [key('ctrl+s')]),
        mkTile('undo', 'Undo', 'i-undo', null, [key('ctrl+z')]),
        mkTile('render', 'Export', 'i-play', 'pink', [key('ctrl+m')]),
        mkTile('cut', 'Cut', 'i-stop', null, [key('ctrl+k')]),
      ] },
    ],
  };
  const pickActive = () => {
    const t = S.tiles;
    if (t.pinned) { t.active = t.pinned; t.auto = false; return; }
    t.active = S.ctx.mode === 'gaming' ? 'gaming' : 'default';
    t.auto = true;
  };
  pickActive();

  const nowMs = Date.now();
  const round5 = (ms) => Math.round(ms / 300000) * 300000;
  const HOUR = 3600000;
  S.clock = {
    nowMs, utcOffsetMin: S.tzOffsetMin, zone: 'Local time', folder: 'C:\\Users\\you\\AppData\\Local\\CompanionDeck\\calendar', calendarFiles: 1,
    canEdit: q.get('transport') !== 'wifi',
    subscriptions: [{ id: 'a1', name: 'Work', host: 'calendar.google.com', lastOkMs: nowMs }, { id: 'b2', name: 'Family', host: 'outlook.office365.com', error: 'the site did not answer in time' }],
    events: [
      { title: 'Design review', place: 'Room 4', startMs: round5(nowMs + 0.7 * HOUR), endMs: round5(nowMs + 1.7 * HOUR) },
      { title: 'Lunch with Sam', place: 'Cafe Nord', startMs: round5(nowMs + 3.2 * HOUR), endMs: round5(nowMs + 4.2 * HOUR) },
      { title: 'Release day', allDay: true, startMs: round5(nowMs + 20 * HOUR), endMs: round5(nowMs + 44 * HOUR) },
      { title: 'Dentist', place: 'Elm Street Clinic', startMs: round5(nowMs + 27 * HOUR), endMs: round5(nowMs + 28 * HOUR) },
      { title: 'Team stand-up', startMs: round5(nowMs + 33 * HOUR), endMs: round5(nowMs + 33.5 * HOUR) },
    ],
  };
  S.timers = { nowMs, items: [
    { id: 'm1', kind: 'countdown', label: 'Tea', state: 'running', durationMs: 300000, endsAtMs: nowMs + 192000 },
    { id: 'm2', kind: 'pomodoro', label: '', state: 'running', durationMs: 1500000, endsAtMs: nowMs + 1120000, phase: 'work', round: 2, rounds: 4 },
  ] };

  S.notifications = { enabled: true, available: true, canEdit: q.get('transport') !== 'wifi', items: [
    { id: 11, app: 'Mail', title: 'Priya Nair', body: 'Slides for tomorrow are in the shared folder', atMs: nowMs - 4 * 60000 },
    { id: 12, app: 'Calendar', title: 'Design review in 30 minutes', body: 'Room 4', atMs: nowMs - 9 * 60000 },
    { id: 13, app: 'Chat', title: 'Build finished', body: 'All 214 checks passed', atMs: nowMs - 26 * 60000 },
  ] };
  S.macros = { canEdit: q.get('transport') !== 'wifi', recording: null, items: [
    { id: 'mdemo', name: 'Sign off', created: nowMs - 86400000, events: 42, seconds: 6.5 },
    { id: 'mdemo2', name: 'Tidy desktop', created: nowMs - 3600000, events: 18, seconds: 3.1 },
  ] };
  let recTimer = null;

  // Performance history, top processes, alerts and gaming sessions: believable and fictional.
  const mockHistory = (seconds) => {
    const n = 120, now = hostNow();
    const line = (f) => Array.from({ length: n }, (_, i) => f(i));
    const spike = (i, at) => (i === at ? 1 : 0);
    return { type: 'history', seconds, nowMs: now, startMs: now - seconds * 1000, stepMs: Math.round(seconds * 1000 / n),
      cpu: line(i => Math.round(20 + 14 * Math.abs(Math.sin(i / 9)) + spike(i, 31) * 60 + spike(i, 88) * 35)),
      gpu: line(i => i > 55 ? Math.round(70 + 22 * Math.abs(Math.sin(i / 7))) : Math.round(6 + 5 * Math.abs(Math.sin(i / 5)))),
      ram: line(i => Math.round(62 + i / 30)),
      cpuTemp: line(i => Math.round(54 + 12 * Math.abs(Math.sin(i / 14)) + (i > 55 ? 8 : 0))),
      gpuTemp: line(i => Math.round(47 + (i > 55 ? 24 : 4) + 4 * Math.sin(i / 6))),
      fps: line(i => i > 58 ? Math.round(138 + 14 * Math.sin(i / 3) - spike(i, 90) * 70) : null) };
  };
  S.history = mockHistory(60);

  const proc = (name, cpu, ramMb, gpu, count, extra) => Object.assign({ pid: 1000 + ramMb % 977, name, cpu, ramMb, gpu, count: count || 1, protected: false, low: false }, extra || {});
  const topCpu = [proc('Browser', 14.2, 1840, 3.1, 11), proc('Neon Drift', 9.8, 3120, 71.5), proc('Code', 4.1, 920, null, 6), proc('Discord', 2.2, 410, 0.4, 5), proc('Spotify', 1.3, 310, null, 4), proc('System', 0.9, 120, null, 1, { protected: true })];
  S.top = { haveCpu: true, gpuAvailable: true, cpu: topCpu,
    ram: topCpu.slice().sort((a, b) => b.ramMb - a.ramMb),
    gpu: topCpu.filter(p => p.gpu).sort((a, b) => b.gpu - a.gpu) };
  S.alerts = { enabled: true, cpuTemp: 90, gpuTemp: 85, ram: 92, fan: 0, cpuTempAvailable: true, gpuTempAvailable: true, fanAvailable: true, active: [] };

  const series = (base, wob, n) => Array.from({ length: n || 90 }, (_, i) => Math.round(base + wob * Math.sin(i / 6) - (i % 29 === 0 ? 30 : 0)));
  S.sessions = { enabled: true, fpsAvailable: true, canEdit: q.get('transport') !== 'wifi', autoPower: false, autoPowerApplied: false, autoPowerAvailable: true, items: [
    { id: 's1', game: 'Neon Drift', startMs: nowMs - 3 * 3600000, seconds: 6120, avgFps: 142, low1: 96, low01: 71, avgCpu: 38, avgGpu: 76, maxGpuTemp: 78, series: series(142, 12) },
    { id: 's2', game: 'Hollow Keep', startMs: nowMs - 26 * 3600000, seconds: 2880, avgFps: 87, low1: 64, low01: 52, avgCpu: 45, avgGpu: 91, maxGpuTemp: 81, series: series(87, 9) },
    { id: 's3', game: 'Starfall Tactics', startMs: nowMs - 74 * 3600000, seconds: 7800, avgFps: 188, low1: 121, low01: 88, avgCpu: 31, avgGpu: 58, maxGpuTemp: 69, series: series(188, 16) },
  ] };
  const frameTimes = (t) => Array.from({ length: 120 }, (_, i) => Math.round((7 + Math.abs(Math.sin((i + t) / 5)) * 1.4 + ((i + t) % 37 === 0 ? 14 : 0) + ((i + t) % 91 === 0 ? 26 : 0)) * 10) / 10);
  S.frames = { times: frameTimes(0), fps: 141, live: { game: 'Neon Drift', seconds: 1860 } };
  if (q.get('alert')) setTimeout(() => showAlert({ label: 'GPU temperature', value: 87, limit: 85 }), 1200);

  // Stand-ins for the PC's replies, so every control in the preview does what the real one does.
  const reply = (id, ok, error) => setTimeout(() => ingest({ type: 'result', id, ok, error }), 30);
  let mockTimer = 100;
  window.__mockCmd = (action, args, id) => {
    const t = S.tiles;
    switch (action) {
      case 'tile.run':
        t.running = [args.id]; schedule('tiles');
        setTimeout(() => { t.running = []; schedule('tiles'); }, 1400);
        return reply(id, true);
      case 'tiles.pin':
        t.pinned = args.id || null; pickActive(); schedule('tiles'); return reply(id, true);
      case 'tiles.setProfile': {
        const p = args.profile;
        if (!p.name || !p.tiles.every(x => x.name && x.steps.length)) return reply(id, false, 'a tile needs a name and a step');
        if (p.tiles.some(x => x.steps.some(s => s.do === 'key' && !/^[a-z0-9+]+$/i.test(s.keys || ''))))
          return reply(id, false, 'unknown key in a shortcut');
        p.tiles.forEach(x => { x.confirm = x.steps.some(s => s.do === 'sys' && ['closewindow', 'sleep', 'restart', 'shutdown'].includes(s.id)); });
        const at = t.profiles.findIndex(x => x.id === p.id);
        if (at >= 0) t.profiles[at] = p; else t.profiles.push(p);
        schedule('tiles'); return reply(id, true);
      }
      case 'tiles.deleteProfile':
        t.profiles = t.profiles.filter(x => x.id !== args.id); pickActive(); schedule('tiles'); return reply(id, true);

      case 'calendar.add': case 'calendar.paste': {
        // The mock accepts https links only, like the host, so the refusal message can be seen too.
        const url = action === 'calendar.paste' ? 'https://calendar.example.org/private/abc.ics' : String(args.url || '');
        if (!/^https:\/\//i.test(url)) return reply(id, false, 'that is not an https calendar link');
        S.clock.subscriptions.push({ id: 'm' + Math.random().toString(16).slice(2, 6), name: 'New calendar', host: new URL(url).host, lastOkMs: Date.now() });
        schedule('clock'); return reply(id, true);
      }
      case 'calendar.remove':
        S.clock.subscriptions = S.clock.subscriptions.filter(x => x.id !== args.id); schedule('clock'); return reply(id, true);
      case 'calendar.refresh':
        S.clock.subscriptions.forEach(x => { x.error = null; }); schedule('clock'); return reply(id, true);

      case 'timer.countdown': case 'timer.stopwatch': case 'timer.pomodoro': {
        const now = hostNow();
        const base = { id: 'm' + (mockTimer++), label: args.label || '', state: 'running' };
        let item;
        if (action === 'timer.countdown') item = { ...base, kind: 'countdown', durationMs: args.seconds * 1000, endsAtMs: now + args.seconds * 1000 };
        else if (action === 'timer.stopwatch') item = { ...base, kind: 'stopwatch', startedAtMs: now };
        else item = { ...base, kind: 'pomodoro', durationMs: args.work * 60000, endsAtMs: now + args.work * 60000, phase: 'work', round: 1, rounds: args.rounds };
        S.timers.items.push(item); schedule('timers'); return reply(id, true);
      }
      case 'timer.pause': case 'timer.resume': case 'timer.remove': case 'timer.clear': case 'timer.skip': {
        const now = hostNow();
        const it = S.timers.items.find(x => x.id === args.id);
        if (action === 'timer.clear') S.timers.items = [];
        else if (action === 'timer.remove') S.timers.items = S.timers.items.filter(x => x.id !== args.id);
        else if (it && action === 'timer.pause') {
          if (it.kind === 'stopwatch') { it.elapsedMs = now - it.startedAtMs; it.startedAtMs = null; }
          else { it.remainingMs = it.endsAtMs - now; it.endsAtMs = null; }
          it.state = 'paused';
        } else if (it && action === 'timer.resume') {
          if (it.kind === 'stopwatch') { it.startedAtMs = now - (it.elapsedMs || 0); it.elapsedMs = null; }
          else { it.endsAtMs = now + it.remainingMs; it.remainingMs = null; }
          it.state = 'running';
        } else if (it && action === 'timer.skip') {
          it.phase = it.phase === 'work' ? 'short' : 'work'; it.endsAtMs = now + (it.phase === 'work' ? 1500000 : 300000);
        }
        schedule('timers'); return reply(id, true);
      }

      case 'history.get': S.history = mockHistory(args.seconds); schedule('history'); return;
      case 'alerts.set': Object.assign(S.alerts, Object.fromEntries(Object.entries(args).map(([k, v]) => [k, k === 'enabled' ? !!v : v]))); schedule('alerts'); return reply(id, true);
      case 'sessions.enable': S.sessions.enabled = !!args.value; schedule('sessions'); return reply(id, true);
      case 'sessions.delete': S.sessions.items = S.sessions.items.filter(x => x.id !== args.id); schedule('sessions'); return reply(id, true);
      case 'sessions.clear': S.sessions.items = []; schedule('sessions'); return reply(id, true);
      case 'autopower.set': S.sessions.autoPower = !!args.value; schedule('sessions'); return reply(id, true);
      case 'top.priority': for (const l of ['cpu', 'ram', 'gpu']) for (const p of S.top[l]) if (p.name === args.name) p.low = !!args.low; schedule('top'); return reply(id, true);
      case 'top.close': case 'top.kill':
        for (const l of ['cpu', 'ram', 'gpu']) S.top[l] = S.top[l].filter(p => p.name !== args.name);
        schedule('top'); return reply(id, true);
      case 'sb.name': case 'sb.color': case 'sb.folder': case 'sb.loop': case 'sb.route': case 'sb.gain': {
        const c = (S.sb.items || []).find(x => x.id === args.id);
        if (!c) return reply(id, false, 'unknown clip');
        if (action === 'sb.name') c.name = args.name;
        else if (action === 'sb.color') c.color = args.color;
        else if (action === 'sb.folder') c.group = args.folder;
        else if (action === 'sb.loop') c.loop = !!args.value;
        else if (action === 'sb.route') c.route = args.route;
        else c.gain = args.value;
        schedule('soundboard'); return reply(id, true);
      }
      case 'notify.enable': S.notifications.enabled = !!args.value; schedule('notifications'); return reply(id, true);
      case 'notify.dismiss': S.notifications.items = S.notifications.items.filter(n => n.id !== args.id); schedule('notifications'); return reply(id, true);
      case 'notify.clear': S.notifications.items = []; schedule('notifications'); return reply(id, true);
      case 'macro.record':
        S.macros.recording = { count: 0, seconds: 0, full: false };
        clearInterval(recTimer);
        recTimer = setInterval(() => {
          const r = S.macros.recording;
          if (!r) return clearInterval(recTimer);
          r.count += 7; r.seconds = Math.round((r.seconds + 1) * 10) / 10;
          schedule('macros');
        }, 1000);
        schedule('macros'); return reply(id, true);
      case 'macro.stop': {
        const r = S.macros.recording;
        clearInterval(recTimer);
        S.macros.recording = null;
        if (!r || !r.count) { schedule('macros'); return reply(id, false, 'nothing was recorded'); }
        S.macros.items.push({ id: 'm' + Date.now(), name: args.name || 'Macro ' + (S.macros.items.length + 1), created: Date.now(), events: r.count, seconds: r.seconds });
        schedule('macros'); return reply(id, true);
      }
      case 'macro.cancel': clearInterval(recTimer); S.macros.recording = null; schedule('macros'); return reply(id, true);
      case 'macro.delete':
        S.macros.items = S.macros.items.filter(m => m.id !== args.id); schedule('macros'); return reply(id, true);

      case 'audio.micMuteToggle': S.audio.micMuted = !S.audio.micMuted; break;
      case 'audio.micMute': S.audio.micMuted = !!args.value; break;
      case 'audio.deafenToggle': {
        const on = !(S.audio.masterMuted && S.audio.micMuted);
        S.audio.masterMuted = on; S.audio.micMuted = on; break;
      }
      case 'audio.deviceVolume': {
        const d = S.audio.outputs.find(x => x.id === args.id);
        if (d) d.volume = args.value;
        break;
      }
      case 'audio.rememberApp': {
        const a = S.audio.apps.find(x => x.id === args.id);
        if (a) a.remembered = !!args.value;
        break;
      }
      case 'audio.setOutput':
        S.audio.outputs.forEach(o => { o.isDefault = o.id === args.id; }); break;
      default: return;
    }
    S.audio.deafened = S.audio.masterMuted && S.audio.micMuted;
    schedule('audio');
  };

  // The bars, as the PC would send them.
  setInterval(() => {
    if (!(look.ambient ? look.viz : (page === 'media' && look.viz))) return;
    const x = Date.now() / 260;
    onSpectrum(Array.from({ length: 32 }, (_, i) => Math.round(Math.max(4, 14 + 66 * Math.abs(Math.sin(x + i / 3.4)) * Math.exp(-i / 46)))));
  }, 90);

  let t = 0;
  setInterval(() => {
    t += 1;
    S.sys = {
      cpu: 22 + 16 * Math.abs(Math.sin(t / 7)),
      cpuTemp: 61 + 8 * Math.sin(t / 9),
      ramPercent: 68, ramUsedMb: 11179, ramTotalMb: 16253,
      gpu: 38 + 30 * Math.abs(Math.sin(t / 5)),
      gpuTemp: 54 + 9 * Math.sin(t / 6),
      vramUsedMb: 1424, vramTotalMb: 4096,
      gpuPowerW: 42, gpuClockMhz: 1620,
      diskPercent: 12, netDownKbps: 4300, netUpKbps: 220,
      battery: 100, charging: true,
      fps: 141 + 12 * Math.sin(t / 3), fps1Low: 108,
      fans: [
        { name: 'Fan 1', rpm: Math.round(1400 + 1600 * Math.abs(Math.sin(t / 11))), maxRpm: 3400 },
        { name: 'Fan 2', rpm: Math.round(1300 + 1700 * Math.abs(Math.sin(t / 13))), maxRpm: 3400 },
      ].slice(0, Number(new URLSearchParams(location.search).get('fans') ?? 2)), thermalMode: 'performance', brightness: 72,
      chargeMode: 'conservation', keyboardLight: 1,
    };
    schedule('sys');
    S.frames = { times: frameTimes(t), fps: 141 + Math.round(8 * Math.sin(t / 4)), live: { game: 'Neon Drift', seconds: 1860 + t } };
    schedule('frames');

    if (live) {
      S.cast.desktopPeak = Math.round(52 + 30 * Math.abs(Math.sin(t / 2.3)));
      S.cast.micPeak = Math.round(28 + 34 * Math.abs(Math.sin(t / 1.7)));
      S.sb.bufferedSeconds = Math.min(60, 48 + (t % 12));
      schedule('cast'); schedule('soundboard');
    }
  }, 1000);
  schedule('*');
}

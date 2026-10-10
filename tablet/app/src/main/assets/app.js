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

function send(action, args) {
  const msg = { type: 'cmd', id: cmdSeq++, action, args: args || {} };
  if (native) native.send(JSON.stringify(msg));
  else console.log('[mock cmd]', action, args || {});
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

function makeSourceTile() {
  const node = el('button', 'tile src-tile', `
    <img alt="" hidden>${icon('i-media', 'ph')}<span class="nm"></span>`);
  return { node, img: $('img', node), fallback: $('.ph', node), nm: $('.nm', node), iconKey: null, kind: null };
}

function updateSourceTile(rec, item) {
  rec.node.dataset.source = item.id;
  rec.node.classList.toggle('running', !!item.running);
  text(rec.nm, item.name);

  if (item.kind !== rec.kind) {
    rec.kind = item.kind;
    rec.fallback.querySelector('use')
      .setAttribute('href', '#' + (SOURCE_GLYPH[item.kind] || 'i-media'));
  }

  if (item.iconKey !== rec.iconKey) {
    rec.iconKey = item.iconKey || null;
    if (item.iconKey) { rec.img.src = artUrl(item.iconKey); rec.img.hidden = false; rec.fallback.style.display = 'none'; }
    else { rec.img.hidden = true; rec.fallback.style.display = ''; }
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
    <div class="empty src-empty">${icon('i-media')}<p>No media applications were found on the laptop</p></div>`);

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
        <span class="hint">Start something on the laptop</span>
      </div>
      <div class="tile-grid sources"></div>
      <div class="empty np-empty-plain">${icon('i-media')}<p>Nothing is playing on the laptop</p></div>
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
  { key: 'FAN 1', kind: 'fan', cap: 'fans',
    get: y => y.fan1Rpm ?? null, frac: y => fanFraction(y, y.fan1Rpm) },
  { key: 'FAN 2', kind: 'fan', cap: 'fans',
    get: y => y.fan2Rpm ?? null, frac: y => fanFraction(y, y.fan2Rpm) },
];

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

function makePerfCard(alwaysGaming) {
  const node = el('div', 'card');
  node.innerHTML = `
    <div class="card-head">
      <div class="card-title">${icon('i-perf')} <span class="ttl">System</span></div>
      <div class="mode-mini" hidden></div>
    </div>
    <div class="fps-hero" hidden>
      <span class="n">–</span><span class="u">FPS</span>
      <span class="low"><b>–</b><span>1% low</span></span>
    </div>
    <div class="gauges"></div>
    <div class="stat-strip"></div>`;

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
    fps: $('.fps-hero', node),
    fpsN: $('.fps-hero .n', node),
    fpsLow: $('.fps-hero .low b', node),
    modeMini,
    built: false,
  };
}

function updatePerfCard(r) {
  const y = S.sys;
  const gaming = r.alwaysGaming || (S.ctx && S.ctx.mode === 'gaming');

  text(r.ttl, gaming ? 'Live performance' : 'System');

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
    const slots = STAT_SLOTS.filter(s => !s.cap || S.caps[s.cap]);
    const sig = slots.map(s => s.key).join(',');
    if (r.capSig !== sig) {
      r.capSig = sig;
      reconcile(r.strip, slots, s => s.key,
        (s) => {
          const rotor = s.kind === 'fan' ? '<svg class="rotor still"><use href="#i-fan"/></svg>' : '';
          const bar = (s.kind === 'fan' || s.kind === 'bar') ? '<div class="bar"><i></i></div>' : '';
          const node = el('div', 'stat' + (s.kind === 'fan' ? ' with-rotor' : ''),
            `<div class="val-row">${rotor}<b>–</b></div><span>${esc(s.key)}</span>${bar}`);
          return { node, b: $('b', node), rotor: $('.rotor', node), fill: $('.bar i', node) };
        },
        () => {});
    }
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
    for (const slot of STAT_SLOTS) {
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
    <div class="levels"></div>
    <div class="dev-list outs" style="margin-top:11px"></div>
    <div class="empty aud-empty">${icon('i-audio')}<p>Audio service not connected</p></div>`;

  const master = makeSlider('master', 'i-vol');
  const mic = makeSlider('mic', 'i-mic');
  $('.levels', node).append(master.node, mic.node);

  const r = { node, master, mic, outs: $('.outs', node), levels: $('.levels', node), empty: $('.aud-empty', node) };
  wireAudio(r);
  return r;
}

function makeAudioPage() {
  const node = el('div', 'stack');
  node.innerHTML = `
    <div class="card">
      <div class="card-head"><div class="card-title">${icon('i-audio')} Levels</div></div>
      <div class="levels"></div>
    </div>
    <div class="row two">
      <div class="card">
        <div class="card-head"><div class="card-title">${icon('i-apps')} App mixer</div></div>
        <div class="mixer"></div>
        <div class="empty mix-empty">${icon('i-audio')}<p>No applications are playing audio</p></div>
      </div>
      <div class="card">
        <div class="card-head"><div class="card-title">${icon('i-out')} Output</div></div>
        <div class="dev-list outs"></div>
        <div class="card-head" style="margin:15px 0 10px"><div class="card-title">${icon('i-mic')} Input</div></div>
        <div class="dev-list ins"></div>
      </div>
    </div>`;

  const master = makeSlider('master', 'i-vol');
  const mic = makeSlider('mic', 'i-mic');
  $('.levels', node).append(master.node, mic.node);

  const r = {
    node, master, mic,
    mixer: $('.mixer', node),
    mixEmpty: $('.mix-empty', node),
    outs: $('.outs', node),
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
    else send('audio.appVolume', { id: key, value: v });
  };
  const onMute = (key) => {
    if (key === 'master') send('audio.masterMuteToggle');
    else if (key === 'mic') send('audio.micMuteToggle');
    else send('audio.appMuteToggle', { id: key });
  };

  bindSliderHost(r.node, onChange, onMute);

  r.node.addEventListener('click', (e) => {
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

  const outs = opts && opts.limit ? (a.outputs || []).slice(0, opts.limit) : (a.outputs || []);
  reconcile(r.outs, outs, d => d.id, () => makeDeviceRow('out'), (rec, d) => updateDeviceRow(rec, d, 'out'));

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
            <div class="mix-name"><span class="live"></span><span class="nm"></span></div>
          </div>`);
        const sl = makeSlider(x.id, 'i-vol', true);
        $('.mix-body', node).appendChild(sl.node);
        return {
          node, sl,
          img: $('img', node), ltr: $('.ltr', node),
          live: $('.live', node), nm: $('.nm', node),
          iconKey: null,
        };
      },
      (rec, x) => {
        text(rec.nm, x.name);
        rec.live.hidden = !x.active;
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
          <span class="chip ghost">tap closes · hold ends</span>
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

  // Close is a tap; force-ending is a deliberate press-and-hold, the same gate the Actions
  // page puts on Shut Down. The two are genuinely different acts: a tap asks the app to
  // close and lets it prompt to save, a hold terminates it and throws that work away, so
  // they must not be one stray touch apart on a tablet lying next to the keyboard.
  if (r.runCard) {
    let timer = null, active = null;
    const clear = () => {
      if (timer) { clearTimeout(timer); timer = null; }
      if (active) { active.classList.remove('holding'); active = null; }
    };

    r.runCard.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('[data-pid]');
      if (!b) return;
      active = b;
      b.classList.add('holding');
      timer = setTimeout(() => {
        send('running.kill', { pid: Number(b.dataset.pid) });
        // Suppress the click that follows this pointerup, or the row would also be asked
        // to close the process it has just been asked to kill.
        b.dataset.killed = '1';
        clear();
      }, 1100);
    });
    ['pointerup', 'pointercancel', 'pointerleave'].forEach(ev =>
      r.runCard.addEventListener(ev, clear));

    r.runCard.addEventListener('click', (e) => {
      const b = e.target.closest('[data-pid]');
      if (!b) return;
      if (b.dataset.killed) { delete b.dataset.killed; return; }
      send('running.close', { pid: Number(b.dataset.pid) });
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
    <span class="starting">Starting…</span>
    ${icon('i-star', 'star')}
    <span class="badges">
      <span class="mini b-fav" data-fav>${icon('i-star')}</span>
      <span class="mini b-hide" data-hide>${icon('i-x')}</span>
    </span>`);
  return {
    node,
    img: $('img', node), fallback: $('.ph', node), nm: $('.nm', node),
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


/* Shortcuts whose chord is fixed host-side; the tablet only names one.
   `conditional` marks the ones that genuinely do nothing without the software they drive -
   a Steam-launched game, or an NVIDIA driver - so they read as conditional rather than
   broken on a machine that does not have it. */
const GAME_SHORTCUTS = [
  { id: 'record',       name: 'Record',      ic: 'i-rec',   hint: 'Win+Alt+R' },
  { id: 'gamebar',      name: 'Game Bar',    ic: 'i-game',  hint: 'Win+G' },
  { id: 'nvoverlay',    name: 'NVIDIA',      ic: 'i-bolt',  hint: 'Alt+Z',     conditional: true },
  { id: 'nvpanel',      name: 'NV Panel',    ic: 'i-chip',  hint: 'App',       conditional: true },
  { id: 'steamoverlay', name: 'Steam',       ic: 'i-apps',  hint: 'Shift+Tab', conditional: true },
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

const modeName = (m) => m.plan;
const modeCardTitle = () => 'Power mode';


const repeat = { timer: null };
function repeatStop() {}

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
        </div>
        <div class="mode-row"></div>
        <div class="mode-note"></div>
      </div>
    </div>`;


  const shortcutGrid = $('.shortcut-grid', node);
  for (const s of GAME_SHORTCUTS) {
    const b = el('button', 'tile src-tile' + (s.conditional ? ' conditional' : ''), `
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


  const r = {
    node,
    shortcutGrid, modeRow,
    note: $('.mode-note', node),
    perfCard: $('.perf-card', node),
    perfRow: $('.perf-card', node).parentElement,
    modeTtl: $('.mode-ttl', node),
  };

  node.addEventListener('click', (e) => {

    const sc = e.target.closest('[data-shortcut]');
    if (sc) return send('game.shortcut', { id: sc.dataset.shortcut });

    const md = e.target.closest('[data-mode]');
    if (md) return send('sys.thermalMode', { mode: md.dataset.mode });

  });

  return r;
}

function updateGamePage(r) {

  // Removed outright when the host has no mechanism it can drive, rather than shown
  // greyed out: a control that can never work is not information, it is clutter. The row
  // collapses with it, so the card beside it does not sit next to an empty column.
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
        <p>Needs a virtual audio cable on the laptop.<br>VB-Audio Virtual Cable provides one.</p>
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
        <button class="chip ghost b-stopall">${icon('i-stop')}<span>Stop</span></button>
      </div>
      <div class="pad-grid"></div>
      <div class="empty sb-empty">
        ${icon('i-folder')}
        <p class="sb-folder">Drop audio files in the soundboard folder on the laptop.</p>
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
    if (!p) return;
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
  text(rec.route, ROUTE_LABEL[item.route] || item.route);
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
  reconcile(r.pads, items, i => i.id, makePad, updatePad);

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
        <div class="card-title">${icon('i-bolt')} Quick actions</div>
        <span class="chip ghost">hold red tiles to confirm</span>
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
  let timer = null, active = null;
  const clear = () => {
    if (timer) { clearTimeout(timer); timer = null; }
    if (active) { active.classList.remove('holding'); active = null; }
  };

  node.addEventListener('pointerdown', (e) => {
    const b = e.target.closest('[data-quick]');
    if (!b || !b.dataset.confirm) return;
    active = b;
    b.classList.add('holding');
    timer = setTimeout(() => { send('sys.' + b.dataset.quick); clear(); }, 1100);
  });
  ['pointerup', 'pointercancel', 'pointerleave'].forEach(ev => node.addEventListener(ev, clear));

  node.addEventListener('click', (e) => {
    const b = e.target.closest('[data-quick]');
    if (!b || b.dataset.confirm) return;
    send('sys.' + b.dataset.quick);
  });

  // Brightness is a live control rather than a one-shot action, so it gets a slider.
  const bright = makeSlider('brightness', 'i-sun');
  $('.levels', node).appendChild(bright.node);
  bindSliderHost(node,
    (key, v) => { if (key === 'brightness') send('sys.brightness', { value: v }); },
    () => { /* nothing sensible to mute here */ });

  return {
    node, bright,
    card: $('.bright-card', node),
    thermal: $('.thermal', node),
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
}

/* ================================================================= boot === */

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
  mediaStack.append(UI.mediaPage.node, UI.mediaSources.node, UI.audioPage.node);
  $('#mediaFull').appendChild(mediaStack);

  UI.gamePage = makeGamePage();
  $('#gameFull').appendChild(UI.gamePage.node);

  UI.voicePage = makeVoicePage();
  $('#voiceFull').appendChild(UI.voicePage.node);

  UI.perfPage = makePerfCard(true);
  $('#perfFull').appendChild(UI.perfPage.node);

  UI.appsPage = makeAppsCard(0, true);
  $('#appsFull').appendChild(UI.appsPage.node);

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
    case 'assetReady': return refreshAsset(msg.key);
    case 'result':
      if (msg.error) console.warn('command failed:', msg.error);
      return;
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
    text($('#veilTitle'), S.link.phase === 'listening' ? 'Waiting for your laptop' : 'Looking for your laptop');
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
  } else if (page === 'voice') {
    if (needs('cast')) updateVoicePage(UI.voicePage);
    if (needs('soundboard')) updateSoundboard(UI.voicePage);
  } else if (page === 'perf') {
    if (needs('sys', 'ctx')) updatePerfCard(UI.perfPage);
  } else if (page === 'apps') {
    if (needs('apps', 'running')) updateApps(UI.appsPage);
  } else if (page === 'quick') {
    if (needs('sys')) updateQuick(UI.quickPage);
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
  text($('#hostName'), connected ? (S.link.hostName || 'Laptop') : 'Companion Deck');

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
    const tag = '';
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
    game: ['sys', 'ctx'],
    voice: ['cast', 'soundboard'],
    perf: ['sys', 'ctx'],
    // 'running' enumerates every process and every top-level window, so it is asked for
    // only by the one page that shows it - and stops the moment you navigate away.
    apps: ['apps', 'ctx', 'running'],
    // 'sys' carries the current brightness and thermal mode shown on this page.
    quick: ['sys', 'ctx'],
  };
  const sections = map[page] || ['sys'];
  // Nothing on the gaming page moves on its own, so one reading every two seconds is
  // plenty - and that is the page most likely to be open mid-match.
  // The voice page is the exception to the "slower is better" rule: a level meter updated
  // once a second is not a level meter, and the meter is the only evidence the cast is
  // actually carrying audio. It subscribes to nothing else, and the payload is a handful of
  // numbers, so the extra rate buys the one thing that page exists to show.
  const hz = (page === 'perf') ? 2 : (page === 'voice') ? 8 : (page === 'game' ? 0.5 : 1);
  if (native) native.setSubscription(sections.join(','), hz);
}

document.addEventListener('visibilitychange', () => {
  // Same reasoning as the rail: a screen-off tablet must not still be typing into a match.
  if (document.hidden) repeatStop();
  if (!native) return;
  if (document.hidden) native.setSubscription('', 0);
  else pushSubscription();
});

function tickClock() {
  const d = new Date();
  text($('#clock'), d.getHours() + ':' + String(d.getMinutes()).padStart(2, '0'));
}
setInterval(tickClock, 10000);

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
    S.link = { phase: 'connected', transport: 'usb', hostName: 'MY-PC' };
  }
  S.caps = { fps: true, cpuTemp: true, fans: true, brightness: true, audioDeviceSwitch: true,
              gpu: true, gpuDetail: true,
              thermalMode: true, thermalModeKind: 'powerplan', micCast: true,
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
      { id: 'a1', name: 'Airhorn', route: 'both', gain: 80, order: 0, seconds: 2.1 },
      { id: 'a2', name: 'Applause', route: 'both', gain: 100, order: 1, seconds: 4.6 },
      { id: 'a3', name: 'Drum roll', route: 'mic', gain: 100, order: 2, seconds: 3.2 },
      { id: 'a4', name: 'Sad trombone', route: 'both', gain: 90, order: 3, seconds: 2.8 },
      { id: 'a5', name: 'Wow', route: 'mic', gain: 100, order: 4, seconds: 0.9 },
      { id: 'a6', name: 'Rimshot', route: 'both', gain: 100, order: 5, seconds: 1.3 },
      { id: 'a7', name: 'Crowd gasp', route: 'ears', gain: 100, order: 6, seconds: 1.8 },
      { id: 'a8', name: 'clip 2026-10-03 21-14-02', route: 'ears', gain: 100, order: 7, seconds: 30.0 },
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
      { id: 'game', name: 'Neon Drift', kind: 'game', running: true, iconKey: art('g-neon', 'N', '#ff4f8b', '#5a1a7a') },
      { id: 'ow', name: 'Hollow Keep', kind: 'game', iconKey: art('g-keep', 'H', '#2be3d0', '#0b4a52') },
      { id: 'cs', name: 'Starfall Tactics', kind: 'game', iconKey: art('g-star', 'S', '#7c5cff', '#241a5c') },
      { id: 'steam', name: 'Steam', kind: 'app', iconKey: art('a-steam', 'S', '#3b6ea5', '#14243a') },
      { id: 'epic', name: 'Epic Games', kind: 'app', iconKey: art('a-epic', 'E', '#4a5568', '#171b22') },
      { id: 'discord', name: 'Discord', kind: 'app', iconKey: art('a-chat', 'D', '#6c7bff', '#262a6e') },
      { id: 'brave', name: 'Browser', kind: 'app', iconKey: art('a-web', 'B', '#ff7a3d', '#7a2a0b') },
      { id: 'spotify', name: 'Spotify', kind: 'app', iconKey: 'i-spotify' },
    ],
  };
  S.audio = {
    masterVolume: 62, masterMuted: false, micVolume: 80, micMuted: false,
    outputs: [
      { id: 'o1', name: 'Speakers (Realtek Audio)', isDefault: true },
      { id: 'o2', name: 'Headphones (Bluetooth)', isDefault: false },
      { id: 'o3', name: 'CABLE Input (VB-Audio Virtual Cable)', isDefault: false },
    ],
    inputs: [
      { id: 'i1', name: 'Microphone Array', isDefault: true },
      { id: 'i2', name: 'USB Microphone', isDefault: false },
    ],
    apps: [
      { id: 'spotify', name: 'Spotify', volume: 90, muted: false, active: true },
      { id: 'somegame', name: 'Neon Drift', volume: 70, muted: false, active: true },
      { id: 'discord', name: 'Discord', volume: 55, muted: false, active: false },
      { id: 'brave', name: 'Browser', volume: 100, muted: true, active: false },
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
      { id: 'src:spotify-web', name: 'Spotify Web', kind: 'web', iconKey: art('i-web', 'W', '#3d7bff', '#1b3a8a') },
      { id: 'src:ytmusic', name: 'YouTube Music', kind: 'web', iconKey: art('i-ytm', 'Y', '#ff5a4f', '#8a1b1b') },
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
      fan1Rpm: Math.round(1400 + 1600 * Math.abs(Math.sin(t / 11))),
      fan2Rpm: Math.round(1300 + 1700 * Math.abs(Math.sin(t / 13))),
      fanMaxRpm: 3400, thermalMode: 'performance', brightness: 72,
    };
    schedule('sys');

    if (live) {
      S.cast.desktopPeak = Math.round(52 + 30 * Math.abs(Math.sin(t / 2.3)));
      S.cast.micPeak = Math.round(28 + 34 * Math.abs(Math.sin(t / 1.7)));
      S.sb.bufferedSeconds = Math.min(60, 48 + (t % 12));
      schedule('cast'); schedule('soundboard');
    }
  }, 1000);
  schedule('*');
}

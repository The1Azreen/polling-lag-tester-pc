'use strict';

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];

const C = {
  yellow: '#F7C948', teal: '#6DE0F6', magenta: '#FF6FF2', outer: '#3C2E7B', ink: '#120B26',
  good: '#7CFFB2', warn: '#FFB25C', bad: '#FF5C7A',
  grid: 'rgba(247,201,72,.14)', dim: 'rgba(247,201,72,.65)',
};
const FONT = '12px pixelmplus12, monospace';

const state = {
  snap: null,
  view: 'live',
  sessions: [],
  lastSavedId: undefined,
  es: null,
  padEs: null,
  pad: null,
  deviceKey: '',
};

const store = {
  get(k, d) { try { const v = localStorage.getItem('plt.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('plt.' + k, JSON.stringify(v)); } catch { /* storage unavailable */ } },
};

const ok = v => v !== null && v !== undefined && Number.isFinite(v);
const fmt = (v, d = 1) => ok(v) ? Number(v).toFixed(d) : '—';
const fmtInt = v => ok(v) ? Math.round(v).toLocaleString('en-US') : '—';
const fmtMs = us => ok(us) ? (us / 1000).toFixed(us < 1000 ? 3 : 2) : '—';
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtDur = sec => {
  if (!ok(sec)) return '—';
  sec = Math.max(0, Math.round(sec));
  const h = Math.floor(sec / 3600), m = Math.floor(sec / 60) % 60, s = sec % 60;
  return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(s).padStart(2, '0');
};

async function api(path, method = 'POST') {
  const r = await fetch(path, { method });
  const text = await r.text();
  if (!r.ok) throw new Error(text.replace(/^"|"$/g, '') || r.statusText);
  try { return JSON.parse(text); } catch { return text; }
}

let toastUntil = 0, toastText = '';
function toast(msg, ms = 4000) { toastText = msg; toastUntil = Date.now() + ms; renderTicker(); }

/* ---------------- window chrome: minimise / maximise ---------------- */
function setupWindows() {
  const collapsed = store.get('collapsed', {});
  for (const w of $$('section.window[data-win]')) {
    const id = w.dataset.win;
    const tb = document.createElement('span');
    tb.className = 'tb';
    tb.innerHTML = '<button type="button" title="Minimise" aria-label="Minimise">_</button><button type="button" title="Maximise" aria-label="Maximise">□</button>';
    w.querySelector('.title').appendChild(tb);
    const [min, max] = tb.children;
    if (collapsed[id]) w.classList.add('collapsed');
    min.onclick = () => {
      w.classList.toggle('collapsed');
      const c = store.get('collapsed', {}); c[id] = w.classList.contains('collapsed'); store.set('collapsed', c);
      updatePadStream(); scheduleRender();
    };
    max.onclick = () => {
      const was = w.classList.contains('max');
      $$('.window.max').forEach(x => x.classList.remove('max'));
      if (!was) { w.classList.add('max'); w.classList.remove('collapsed'); }
      scheduleRender();
    };
  }
  document.addEventListener('keydown', e => { if (e.key === 'Escape') $$('.window.max').forEach(x => x.classList.remove('max')); });
}

/* ---------------- charts ---------------- */
function setupCanvas(c) {
  const dpr = window.devicePixelRatio || 1;
  const w = c.clientWidth, h = c.clientHeight;
  if (!w || !h) return null;
  if (c.width !== Math.round(w * dpr) || c.height !== Math.round(h * dpr)) {
    c.width = Math.round(w * dpr); c.height = Math.round(h * dpr);
  }
  const ctx = c.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);
  ctx.font = FONT;
  return { ctx, w, h };
}

function emptyMsg(ctx, w, h, msg) {
  ctx.fillStyle = C.dim; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
  ctx.fillText(msg, w / 2, h / 2);
}

function lineChart(c, series, o = {}) {
  const g = setupCanvas(c); if (!g) return;
  const { ctx, w, h } = g;
  const vals = [];
  for (const s of series) for (const v of s.data || []) if (ok(v)) vals.push(v);
  if (!vals.length) return emptyMsg(ctx, w, h, o.empty || 'waiting for data…');
  let lo = Infinity, hi = -Infinity;
  for (const v of vals) { if (v < lo) lo = v; if (v > hi) hi = v; }
  if (ok(o.ref)) { lo = Math.min(lo, o.ref); hi = Math.max(hi, o.ref); }
  if (o.floor0) lo = Math.min(lo, 0);
  if (hi - lo < 1e-6) { hi += Math.max(1, Math.abs(hi) * 0.05); lo -= Math.max(1, Math.abs(lo) * 0.05); }
  const span = hi - lo; hi += span * 0.1; if (!o.floor0) lo -= span * 0.1;
  if (o.floor0) lo = Math.max(lo, 0);
  const pad = { l: 46, r: 8, t: 10, b: 18 };
  const n = Math.max(2, ...series.map(s => (s.data || []).length));
  const X = i => pad.l + i / (n - 1) * (w - pad.l - pad.r);
  const Y = v => pad.t + (1 - (v - lo) / (hi - lo)) * (h - pad.t - pad.b);

  ctx.textAlign = 'right'; ctx.textBaseline = 'middle'; ctx.lineWidth = 1;
  for (let k = 0; k <= 4; k++) {
    const v = lo + (hi - lo) * k / 4, y = Math.round(Y(v)) + 0.5;
    ctx.strokeStyle = C.grid; ctx.beginPath(); ctx.moveTo(pad.l, y); ctx.lineTo(w - pad.r, y); ctx.stroke();
    ctx.fillStyle = C.dim; ctx.fillText(o.fmt ? o.fmt(v) : (Math.abs(hi) >= 100 ? v.toFixed(0) : v.toFixed(1)), pad.l - 5, y);
  }
  ctx.textBaseline = 'alphabetic';
  ctx.textAlign = 'left'; ctx.fillText('-' + fmtDur((n - 1) * (o.step || 1)), pad.l, h - 4);
  ctx.textAlign = 'right'; ctx.fillText(o.xEnd || 'now', w - pad.r, h - 4);

  if (ok(o.ref)) {
    const y = Math.round(Y(o.ref)) + 0.5;
    ctx.setLineDash([4, 4]); ctx.strokeStyle = C.magenta; ctx.beginPath(); ctx.moveTo(pad.l, y); ctx.lineTo(w - pad.r, y); ctx.stroke(); ctx.setLineDash([]);
    ctx.fillStyle = C.magenta; ctx.textAlign = 'left'; ctx.fillText(o.refLabel || '', pad.l + 4, y - 4);
  }
  for (const s of series) {
    const d = s.data || [];
    ctx.strokeStyle = s.color; ctx.lineWidth = s.width || 2; ctx.lineJoin = 'miter';
    ctx.beginPath();
    let pen = false;
    for (let i = 0; i < d.length; i++) {
      const v = d[i];
      if (!ok(v)) { pen = false; continue; }
      const x = X(i), y = Y(v);
      if (pen) ctx.lineTo(x, y); else { ctx.moveTo(x, y); pen = true; }
    }
    ctx.stroke();
  }
}

function histChart(c, hist, o = {}) {
  const g = setupCanvas(c); if (!g) return;
  const { ctx, w, h } = g;
  if (!hist || !hist.total) return emptyMsg(ctx, w, h, o.empty || 'waiting for data…');
  const bins = hist.bins, max = Math.max(1, ...bins);
  const pad = { l: 8, r: 8, t: 10, b: 18 };
  const pw = w - pad.l - pad.r, ph = h - pad.t - pad.b;
  const bw = pw / bins.length;
  for (let i = 0; i < bins.length; i++) {
    if (!bins[i]) continue;
    const bh = Math.max(1, bins[i] / max * ph);
    ctx.fillStyle = C.teal;
    ctx.fillRect(Math.floor(pad.l + i * bw) + 1, Math.floor(pad.t + ph - bh), Math.max(1, Math.floor(bw) - 1), Math.ceil(bh));
  }
  const xOf = us => pad.l + (us - hist.lo) / (hist.hi - hist.lo) * pw;
  if (ok(hist.nominalUs)) {
    ctx.strokeStyle = C.magenta; ctx.setLineDash([4, 4]);
    const step = o.multiples ? hist.nominalUs : Infinity;
    for (let m = hist.nominalUs; m < hist.hi; m += step) {
      const x = Math.round(xOf(m)) + 0.5;
      ctx.beginPath(); ctx.moveTo(x, pad.t); ctx.lineTo(x, pad.t + ph); ctx.stroke();
      if (!Number.isFinite(step)) break;
    }
    ctx.setLineDash([]);
    ctx.fillStyle = C.magenta; ctx.textAlign = 'left'; ctx.textBaseline = 'top';
    ctx.fillText('nominal ' + fmtMs(hist.nominalUs) + ' ms', Math.min(xOf(hist.nominalUs) + 4, w - 120), pad.t);
  }
  ctx.fillStyle = C.dim; ctx.textBaseline = 'alphabetic';
  for (let k = 0; k <= 4; k++) {
    const us = hist.lo + (hist.hi - hist.lo) * k / 4;
    ctx.textAlign = k === 0 ? 'left' : k === 4 ? 'right' : 'center';
    ctx.fillText(fmtMs(us) + ' ms', pad.l + pw * k / 4, h - 4);
  }
}

function stickChart(c, st, trail, circ, drift) {
  const g = setupCanvas(c); if (!g) return;
  const { ctx, w, h } = g;
  const cx = w / 2, cy = h / 2, R = Math.min(w, h) / 2 - 10;
  ctx.lineWidth = 1;
  ctx.strokeStyle = C.grid;
  ctx.beginPath(); ctx.moveTo(cx - R, cy + .5); ctx.lineTo(cx + R, cy + .5); ctx.moveTo(cx + .5, cy - R); ctx.lineTo(cx + .5, cy + R); ctx.stroke();
  ctx.strokeStyle = C.yellow; ctx.lineWidth = 2;
  ctx.beginPath(); ctx.arc(cx, cy, R, 0, Math.PI * 2); ctx.stroke();
  ctx.strokeStyle = 'rgba(109,224,246,.35)'; ctx.lineWidth = 1; ctx.setLineDash([3, 3]);
  ctx.beginPath(); ctx.arc(cx, cy, R * 0.1, 0, Math.PI * 2); ctx.stroke(); ctx.setLineDash([]);
  if (!st || !ok(st.x)) return emptyMsg(ctx, w, h, 'no data');

  if (circ && circ.bins) {
    const bins = circ.bins, n = bins.length;
    ctx.strokeStyle = C.magenta; ctx.lineWidth = 2; ctx.beginPath();
    let pen = false;
    for (let i = 0; i <= n; i++) {
      const r = bins[i % n];
      if (r < 0.5) { pen = false; continue; }
      const a = (i % n + 0.5) / n * Math.PI * 2 - Math.PI;
      const x = cx + Math.cos(a) * r * R, y = cy + Math.sin(a) * r * R;
      if (pen) ctx.lineTo(x, y); else { ctx.moveTo(x, y); pen = true; }
    }
    ctx.stroke();
  }
  if (trail && trail.length > 2) {
    const n = trail.length / 2;
    for (let i = 1; i < n; i++) {
      ctx.strokeStyle = `rgba(109,224,246,${(i / n * 0.8).toFixed(2)})`; ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(cx + trail[2 * i - 2] * R, cy + trail[2 * i - 1] * R);
      ctx.lineTo(cx + trail[2 * i] * R, cy + trail[2 * i + 1] * R);
      ctx.stroke();
    }
  }
  if (drift && ok(drift.offsetX)) {
    const x = cx + drift.offsetX * R, y = cy + drift.offsetY * R;
    ctx.strokeStyle = C.bad; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.arc(cx, cy, Math.max(2, drift.maxDev * R), 0, Math.PI * 2); ctx.stroke();
    ctx.fillStyle = C.bad; ctx.fillRect(Math.round(x) - 2, Math.round(y) - 2, 4, 4);
  }
  const x = cx + st.x * R, y = cy + st.y * R;
  ctx.fillStyle = C.yellow; ctx.fillRect(Math.round(x) - 5, Math.round(y) - 5, 10, 10);
  ctx.strokeStyle = '#000'; ctx.lineWidth = 2; ctx.strokeRect(Math.round(x) - 5, Math.round(y) - 5, 10, 10);
}

/* ---------------- tiles ---------------- */
const tile = (k, v, u = '', cls = '') => `<div class="tile ${cls}"><div class="k">${esc(k)}</div><div class="v">${v}${u ? `<small>${esc(u)}</small>` : ''}</div></div>`;
const cadenceClass = p => !ok(p) ? '' : p >= 90 ? 'good' : p >= 70 ? 'warn' : 'bad';

function pollTiles(sum, mode) {
  if (!sum) return '<div class="dim">No samples yet.</div>';
  if (mode === 'keyboard') {
    return [
      tile('Estimated rate', ok(sum.nominalHz) ? fmtInt(sum.nominalHz) : '?', 'Hz'),
      tile('Confidence', ok(sum.confidence) ? fmt(sum.confidence * 100, 0) : '—', '%'),
      tile('On-grid', fmt(sum.withinPct, 0), '%', cadenceClass(sum.withinPct)),
      tile('Fastest gap', fmtMs(sum.minUs), 'ms'),
      tile('1% gap', fmtMs(sum.p1Us), 'ms'),
      tile('Median gap', fmtMs(sum.medianUs), 'ms'),
      tile('Avg poll wait', fmt(sum.pollWaitMs, 3), 'ms'),
      tile('Samples', fmtInt(sum.n)),
    ].join('');
  }
  return [
    tile('Avg rate', fmtInt(sum.avgHz), 'Hz'),
    tile('Median rate', fmtInt(sum.medianHz), 'Hz'),
    tile('Mean interval', fmtMs(sum.meanUs), 'ms'),
    tile('Median interval', fmtMs(sum.medianUs), 'ms'),
    tile('Jitter σ', fmt(sum.jitterUs, 1), 'µs'),
    tile('1% / 99%', `${fmtMs(sum.p1Us)}<small>/</small>${fmtMs(sum.p99Us)}`, 'ms'),
    tile('Min / Max', `${fmtMs(sum.minUs)}<small>/</small>${fmtMs(sum.maxUs)}`, 'ms'),
    tile('On-cadence', fmt(sum.withinPct, 1), '%', cadenceClass(sum.withinPct)),
    tile('Off-cadence', fmt(sum.offCadencePct, 1), '%', ok(sum.offCadencePct) ? (sum.offCadencePct < 5 ? 'good' : sum.offCadencePct < 20 ? 'warn' : 'bad') : ''),
    tile('Avg poll wait', fmt(sum.pollWaitMs, 3), 'ms'),
    tile('Samples', fmtInt(sum.n)),
  ].join('');
}

const latTiles = (ls, label) => !ls ? '' : [
  tile(label + ' p50', fmt(ls.p50, 1), 'ms'),
  tile('p95', fmt(ls.p95, 1), 'ms'),
  tile('p99', fmt(ls.p99, 1), 'ms'),
  tile('min / max', `${fmt(ls.min, 1)}<small>/</small>${fmt(ls.max, 1)}`, 'ms'),
  tile('samples', fmtInt(ls.n)),
].join('');

function frameTiles(fr) {
  if (!fr) return '<div class="dim">No frames from the target yet.</div>';
  return [
    tile('FPS avg', fmt(fr.fps, 0)),
    tile('1% low', fmt(fr.onePctLow, 0), 'fps'),
    tile('Frametime', fmt(fr.ftAvg, 2), 'ms'),
    tile('Frametime p99', fmt(fr.ftP99, 2), 'ms'),
    tile('Displayed', fmt(fr.displayedFps, 0), 'fps'),
  ].join('');
}

/* ---------------- rendering ---------------- */
const HOWTO = {
  mouse: '<b>Mouse:</b> move in fast, continuous circles for a few seconds. Mice only report while moving, so slow movement lowers <i>avg</i>; <i>median</i> and <i>nominal</i> stay accurate. Works in-game too, just play.',
  keyboard: '<b>Keyboard:</b> keyboards only report when a key changes. Roll your fingers quickly across many keys (or mash). The estimator looks for the timing grid key events snap to; ~30+ fast presses gets a solid reading.',
  controller: '<b>Controller:</b> keep a stick moving. PlayStation pads stream constantly; Xbox pads only send on change. Open the Controller Lab for live sticks, the drift test and circularity.',
};

let renderPending = false;
function scheduleRender() {
  if (renderPending) return;
  renderPending = true;
  requestAnimationFrame(() => { renderPending = false; if (state.snap) render(state.snap); });
}

function render(s) {
  const mode = s.mode;
  for (const b of $$('[data-mode]')) b.classList.toggle('on', b.dataset.mode === mode);
  for (const t of $$('.tab')) t.classList.toggle('on', t.dataset.view === state.view);
  $('#howto').innerHTML = HOWTO[mode];
  $('#hotkey-hint').textContent = s.hotkey ? `Hotkey: ${s.hotkey} starts/stops a session from inside a game.` : 'Global hotkey unavailable (Ctrl+Shift+F9 is taken by another app).';
  $('#w-lab').hidden = mode !== 'controller';

  const rec = s.recording.active;
  const recBtn = $('#btn-rec');
  recBtn.textContent = rec ? `■ Stop & save (${fmtDur(s.recording.elapsed)})` : '● Start session';
  recBtn.classList.toggle('rec', rec);
  const start = $('#start-btn');
  start.textContent = rec ? `■ REC ${fmtDur(s.recording.elapsed)}` : 'START';
  start.classList.toggle('rec', rec);

  renderDevices(s);
  renderPoll(s);
  renderKeys(s);
  renderLatency(s);
  renderOverhead(s);
  renderTicker();

  if (s.lastSavedId !== state.lastSavedId) {
    if (state.lastSavedId !== undefined && s.lastSavedId) toast(`Session ${s.lastSavedId} saved`);
    state.lastSavedId = s.lastSavedId;
    loadSessions();
  }
}

function renderDevices(s) {
  const sel = $('#device-select');
  const key = s.devices.map(d => d.id + d.name).join('|') + '|' + s.selected;
  if (key !== state.deviceKey && document.activeElement !== sel) {
    state.deviceKey = key;
    sel.innerHTML = '<option value="auto">Auto (most active)</option>' +
      s.devices.map(d => `<option value="${esc(d.id)}">${esc(d.name)}${d.vidPid ? ' · ' + esc(d.vidPid) : ''}</option>`).join('');
    sel.value = s.selected;
  }
  const tb = $('#devices-table tbody');
  if (!s.devices.length) tb.innerHTML = `<tr><td class="dim">No ${s.mode} input seen yet.</td></tr>`;
  else {
    const curId = s.device?.id;
    tb.innerHTML = s.devices.map(d => `<tr data-id="${esc(d.id)}" class="${d.id === curId ? 'sel' : ''}">
      <td>${d.active ? '●' : '<span class="dim">○</span>'} ${esc(d.name)}${d.connected ? '' : ' <span class="dim">(unplugged)</span>'}</td>
      <td class="num">${ok(d.liveHz) ? fmtInt(d.liveHz) + ' Hz' : '—'}</td></tr>`).join('');
  }
  const d = s.device;
  $('#device-info').innerHTML = !d ? `<span class="dim">No ${s.mode} input seen yet. Use the device you picked.</span>` : `
    <b>Name</b><span>${esc(d.name)}</span>
    ${d.manufacturer ? `<b>Maker</b><span>${esc(d.manufacturer)}</span>` : ''}
    <b>VID:PID</b><span>${esc(d.vidPid || '—')}</span>
    <b>Reports</b><span>${fmtInt(d.reports)}</span>
    <b>Idle gaps</b><span>${fmtInt(d.gaps)}</span>
    <b>Status</b><span>${d.connected ? 'connected' : 'unplugged'}</span>
    <b>Path</b><span class="dim small">${esc(d.path || '—')}</span>`;
}

function renderPoll(s) {
  const d = s.device, mode = s.mode;
  const session = state.view === 'session';
  const sum = d ? (session ? d.session : d.live) : null;
  $('#poll-window').textContent = session ? (s.recording.active ? 'since session start' : 'no session running') : (mode === 'keyboard' ? 'last 15 s' : 'last 2 s');

  const big = mode === 'keyboard' ? sum?.nominalHz : sum?.avgHz;
  $('#poll-big').textContent = ok(big) ? Math.round(big) : '----';
  const nom = $('#poll-nominal'), q = $('#poll-quality');
  if (!d) nom.textContent = `waiting for ${mode} input`;
  else if (session && !s.recording.active) nom.textContent = 'press START to record a session';
  else if (!sum) nom.textContent = mode === 'keyboard' ? 'press some keys' : 'move it!';
  else if (mode === 'keyboard') nom.textContent = ok(sum.nominalHz) ? `estimated · ${fmt(sum.confidence * 100, 0)}% confidence` : (sum.hint || 'need more presses');
  else nom.textContent = `${sum.snapped ? 'nominal' : '≈'} ${fmtInt(sum.nominalHz)} Hz · avg`;

  const pct = sum?.withinPct;
  q.hidden = !ok(pct);
  if (ok(pct)) {
    const cls = cadenceClass(pct);
    q.className = 'badge ' + cls;
    q.textContent = mode === 'keyboard' ? `${fmt(pct, 0)}% on grid` : cls === 'good' ? 'STABLE' : cls === 'warn' ? 'SOME JITTER' : 'IRREGULAR, move faster?';
  }
  $('#meter-label').textContent = mode === 'keyboard' ? 'on-grid' : 'on-cadence';
  $('#poll-meter').style.width = (ok(pct) ? Math.max(0, Math.min(100, pct)) : 0) + '%';
  $('#meter-val').textContent = ok(pct) ? fmt(pct, 1) + '%' : '—';
  $('#poll-tiles').innerHTML = pollTiles(sum, mode);

  const ser = d ? (session ? d.sessionSeries : d.liveSeries) : null;
  const hist = d ? (session ? d.sessionHist : d.liveHist) : null;
  const visible = id => !$(id).closest('.window').classList.contains('collapsed');
  if (visible('#chart-rate')) {
    if (mode === 'keyboard') {
      lineChart($('#chart-rate'), [{ data: ser?.a, color: C.yellow }], { step: ser?.step, floor0: true, empty: 'press keys…' });
      $('#rate-legend').innerHTML = '<span><i style="--c:var(--yellow)"></i>key events / s</span>';
    } else {
      lineChart($('#chart-rate'), [{ data: ser?.b, color: C.teal, width: 1 }, { data: ser?.a, color: C.yellow }],
        { step: ser?.step, ref: sum?.nominalHz, refLabel: ok(sum?.nominalHz) ? fmtInt(sum.nominalHz) + ' Hz' : '', empty: session && !s.recording.active ? 'no session running' : 'waiting for input…' });
      $('#rate-legend').innerHTML = '<span><i style="--c:var(--yellow)"></i>median Hz</span><span><i style="--c:var(--teal)"></i>mean Hz</span><span><i style="--c:var(--magenta)"></i>nominal</span>';
    }
  }
  if (visible('#chart-hist')) {
    histChart($('#chart-hist'), hist, { multiples: mode === 'keyboard' });
    $('#hist-hint').textContent = mode === 'keyboard'
      ? 'Gaps between key reports. Peaks should sit on the dashed lines (multiples of the poll period).'
      : 'Gaps between reports. A tall narrow spike on the nominal line = stable polling; a wide or split shape = jitter.';
  }
}

function renderKeys(s) {
  const k = s.device?.keys;
  $('#keys-tiles').innerHTML = !k ? '<div class="dim">—</div>' : [
    tile(s.mode === 'mouse' ? 'Clicks' : 'Presses', fmtInt(k.presses)),
    tile('Tap avg', fmt(k.tapAvgMs, 1), 'ms'),
    tile('Tap min', fmt(k.tapMinMs, 1), 'ms'),
    tile('Chatter', fmtInt(k.chatter), '', k.chatter > 0 ? 'bad' : 'good'),
  ].join('');
  const log = $('#chatter-log');
  log.innerHTML = !k || !k.chatterLog.length
    ? '<li class="dim">none, nice and clean</li>'
    : k.chatterLog.map(c => `<li><span>${esc(c.key)}</span><span>${fmt(c.gapMs, 2)} ms</span><span class="dim">@ ${fmtDur(c.atSec)}</span></li>`).join('');
}

function renderLatency(s) {
  const L = s.latency, mode = s.mode;
  const session = state.view === 'session';
  const st = $('#lat-status');
  let html;
  if (!L.admin) html = `<span class="dot">●</span> Input lag needs administrator rights (PresentMon uses ETW). <button class="button" id="btn-admin" type="button">Restart as admin</button>`;
  else if (!L.available) html = `<span class="dot">●</span> PresentMon not found. <button class="button" id="btn-dl" type="button">Download PresentMon</button>`;
  else if (L.running) html = `<span class="dot on">●</span> ${L.status === 'running' ? 'Tracking' : 'Starting…'} ${L.target ? '<b>' + esc(L.target) + '</b>' : '(auto-detect)'} <span class="dim small">PresentMon ${fmt(L.cpuPct, 2)}% CPU</span>`;
  else html = `<span class="dot">●</span> Idle. Press Start, then play.`;
  if (L.error && !L.running && L.admin && L.available) html += `<div class="small" style="color:var(--bad);width:100%">${esc(L.error)}</div>`;
  if (L.download) html += `<div class="small dim" style="width:100%">${esc(L.download)}</div>`;
  if (st.dataset.html !== html) { st.innerHTML = html; st.dataset.html = html; }

  const btn = $('#btn-lat');
  btn.textContent = L.running ? 'Stop' : 'Start';
  btn.disabled = !L.admin || !L.available;
  const input = $('#lat-target');
  input.disabled = L.running;

  const data = session ? L.session : L.live;
  let big, label;
  if (mode === 'controller') { big = L.chain.totalMs; label = 'estimated total (controller)'; }
  else { big = data?.inputToPhoton?.avg ?? (mode === 'mouse' ? data?.clickToPhoton?.avg : null); label = 'input → photon avg'; }
  $('#lag-big').textContent = ok(big) ? fmt(big, 1) : '--.-';
  $('#lag-label').textContent = label;
  const tg = $('#lag-target');
  tg.hidden = !L.target;
  tg.textContent = L.target ? '🎮 ' + L.target : '';

  // latency chain
  const segs = L.chain.segments || [];
  const total = segs.reduce((a, x) => a + (ok(x.ms) ? x.ms : 0), 0);
  const colors = [C.yellow, C.teal, C.magenta];
  $('#chain').innerHTML = total > 0 ? segs.map((x, i) => `<div style="width:${(ok(x.ms) ? x.ms / total * 100 : 0).toFixed(2)}%;background:${colors[i]}" title="${esc(x.label)}"></div>`).join('') : '';
  $('#chain-legend').innerHTML = segs.map((x, i) => `<span title="${esc(x.src)}"><i style="--c:${colors[i]}"></i>${esc(x.label)} ${fmt(x.ms, 2)} ms</span>`).join('') +
    (ok(L.chain.totalMs) ? `<span><b>≈ ${fmt(L.chain.totalMs, 1)} ms total</b>${L.chain.measured ? '' : ' (estimate)'}</span>` : '');

  let tiles = '';
  if (mode === 'controller') {
    tiles = latTiles(data?.frameLatency, 'frame→photon') || '<div class="dim">PresentMon can\'t see controller input. The estimate above uses polling + frame timing.</div>';
  } else {
    tiles = latTiles(data?.inputToPhoton, 'input→photon');
    if (data?.clickToPhoton) tiles += tile('click→photon avg', fmt(data.clickToPhoton.avg, 1), 'ms');
    if (data?.frameLatency) tiles += tile('frame→photon avg', fmt(data.frameLatency.avg, 1), 'ms');
    if (!tiles) tiles = `<div class="dim">${L.running ? 'Play: PresentMon reports a latency sample for every frame that shows new keyboard/mouse input.' : 'Not tracking.'}</div>`;
  }
  $('#lag-tiles').innerHTML = tiles;
  $('#frame-tiles').innerHTML = frameTiles(data?.frames);

  const ser = session ? L.sessionSeries : L.series;
  const visible = id => !$(id).closest('.window').classList.contains('collapsed');
  if (visible('#chart-lag')) lineChart($('#chart-lag'), [{ data: ser?.frameLatency, color: C.teal, width: 1 }, { data: ser?.input, color: C.yellow }], { step: ser?.step, floor0: true, empty: L.running ? 'waiting for frames…' : 'not tracking' });
  if (visible('#chart-fps')) lineChart($('#chart-fps'), [{ data: ser?.fps, color: C.magenta }], { step: ser?.step, floor0: true, empty: 'fps' });

  $('#apps-list').innerHTML = L.apps.length
    ? L.apps.map(a => `<li class="click ${a.target ? 'target' : ''}" data-app="${esc(a.name)}"><span>${a.target ? '▶ ' : ''}${esc(a.name)}</span><span>${fmt(a.fps, 0)} fps</span></li>`).join('')
    : `<li class="dim">${L.running ? 'nothing presenting yet' : 'start tracking to see apps'}</li>`;
}

function renderOverhead(s) {
  const o = s.overhead;
  $('#overhead').innerHTML = [
    tile('CPU (total)', fmt(o.cpuPct, 2), '%', o.cpuPct < 1 ? 'good' : o.cpuPct < 3 ? 'warn' : 'bad'),
    tile('of 1 core', fmt(o.cpuCorePct, 1), '%'),
    tile('Memory', fmt(o.memMb, 0), 'MB'),
    tile('Reports/s', fmtInt(o.eventsPerSec)),
    tile('Dropped', fmtInt(o.dropped), '', o.dropped > 0 ? 'bad' : 'good'),
    tile('PresentMon', fmt(o.presentMonCpuPct, 2), '%'),
  ].join('');
}

function renderTicker() {
  const s = state.snap;
  let txt;
  if (Date.now() < toastUntil) txt = '✦ ' + toastText;
  else if (!s) txt = 'connecting…';
  else {
    const parts = [];
    if (s.recording.active) parts.push('● REC ' + fmtDur(s.recording.elapsed));
    const d = s.device, sum = d?.live;
    if (d) {
      const hz = s.mode === 'keyboard' ? sum?.nominalHz : sum?.avgHz;
      parts.push(`${d.name} @ ${ok(hz) ? fmtInt(hz) + ' Hz' : '…'}`);
    } else parts.push(`waiting for ${s.mode} input`);
    const lag = s.mode === 'controller' ? s.latency.chain.totalMs : s.latency.live?.inputToPhoton?.avg;
    if (ok(lag)) parts.push(`lag ${fmt(lag, 1)} ms`);
    if (s.latency.live?.frames) parts.push(`${fmt(s.latency.live.frames.fps, 0)} fps`);
    parts.push(`tester CPU ${fmt(s.overhead.cpuPct, 2)}%`);
    txt = parts.join('  ✦  ');
  }
  const t = $('#ticker');
  if (t.textContent !== txt) t.textContent = txt;
}

/* ---------------- controller lab ---------------- */
function renderPad() {
  const p = state.pad;
  if (!p || $('#w-lab').hidden) return;
  const status = $('#lab-status');
  if (!p.ok) {
    status.textContent = p.waiting ? 'Waiting for a controller report. Move a stick.' : (p.error || 'This controller could not be decoded.');
    return;
  }
  status.textContent = `${p.name} · ${fmtInt(p.parsed)} reports decoded`;
  const drift = p.drift?.result;
  stickChart($('#stick-l'), p.ls, p.trailL, p.circL, drift?.left);
  stickChart($('#stick-r'), p.rs, p.trailR, p.circR, drift?.right);
  const sv = (st, circ) => !st ? 'n/a' : `x ${fmt(st.x, 3)} y ${fmt(st.y, 3)} · r ${fmt(Math.hypot(st.x, st.y), 3)}` +
    (circ && circ.coverage > 0.9 ? ` · circ err ${fmt(circ.errorPct, 1)}%` : circ && circ.coverage > 0 ? ` · sweep ${fmt(circ.coverage * 100, 0)}%` : '');
  $('#stick-l-val').textContent = sv(p.ls, p.circL);
  $('#stick-r-val').textContent = sv(p.rs, p.circR);
  const trig = (id, v) => {
    $(`#${id}-bar`).style.width = (ok(v) ? v * 100 : 0) + '%';
    $(`#${id}-val`).textContent = ok(v) ? fmt(v * 100, 0) + '%' : 'n/a';
  };
  trig('lt', p.lt); trig('rt', p.rt);

  const n = Math.max(p.buttonCount || 0, ...(p.buttons.length ? p.buttons : [0]));
  const pressed = new Set(p.buttons);
  let bhtml = '';
  for (let i = 1; i <= n; i++) bhtml += `<span class="${pressed.has(i) ? 'on' : ''}">${i}</span>`;
  const grid = $('#btn-grid');
  if (grid.dataset.h !== bhtml) { grid.innerHTML = bhtml || '<span class="dim">no buttons</span>'; grid.dataset.h = bhtml; }
  const dirs = ['↑', '↗', '→', '↘', '↓', '↙', '←', '↖'];
  $('#hat-val').textContent = p.hat === null || p.hat === undefined ? '' : `· D-pad ${p.hat >= 0 && p.hat < 8 ? dirs[p.hat] : '·'}`;

  const db = $('#btn-drift');
  db.disabled = p.drift.active;
  db.textContent = p.drift.active ? `Hands off… ${fmt(p.drift.remaining, 1)} s` : 'Run drift test (5 s)';
  const dr = $('#drift-result');
  if (drift) {
    const vc = v => /Excellent|Good/.test(v) ? 'verdict-good' : /Fair/.test(v) ? 'verdict-warn' : 'verdict-bad';
    const row = (name, r) => !r ? '' : `<tr><td>${name}</td><td>${fmt(r.offset * 100, 2)}%</td><td>${fmt(r.noise * 100, 2)}%</td><td>${fmt(r.maxDev * 100, 2)}%</td><td>${fmt(r.suggestedDeadzonePct, 0)}%</td><td class="${vc(r.verdict)}">${esc(r.verdict)}</td></tr>`;
    const html = `<table class="drift-table"><thead><tr><th>Stick</th><th>Offset</th><th>Noise</th><th>Max dev</th><th>Deadzone</th><th>Verdict</th></tr></thead><tbody>
      ${row('Left', drift.left)}${row('Right', drift.right)}</tbody></table>
      <p class="hint">Triggers at rest: LT ${fmt(drift.ltRestMax * 100, 1)}% · RT ${fmt(drift.rtRestMax * 100, 1)}%.${drift.idleOnly ? ' This pad sent no reports while idle (normal for Xbox pads), so the last resting value was used.' : ''} Values are % of full stick travel.</p>`;
    if (dr.dataset.h !== html) { dr.innerHTML = html; dr.dataset.h = html; }
  } else if (dr.dataset.h) { dr.innerHTML = ''; dr.dataset.h = ''; }

  const axes = $('#axes-list');
  const ah = p.axes.map(a => `<b>${esc(a.name)}</b><span>${fmt(a.v, 4)}</span>`).join('');
  if (axes.dataset.h !== ah && axes.closest('details').open) { axes.innerHTML = ah; axes.dataset.h = ah; }
  $('#lab-mapping').textContent = p.mapping + (p.combinedTriggers ? '. Xbox HID combines both triggers into one axis, so pressing both cancels out (Windows limitation).' : '');
}

function updatePadStream() {
  const want = state.snap?.mode === 'controller' && !document.hidden && !$('#w-lab').classList.contains('collapsed');
  if (want && !state.padEs) {
    state.padEs = new EventSource('/api/gamepad/stream?hz=30');
    state.padEs.onmessage = ev => { state.pad = JSON.parse(ev.data); requestAnimationFrame(renderPad); };
  } else if (!want && state.padEs) {
    state.padEs.close(); state.padEs = null;
  }
}

/* ---------------- sessions ---------------- */
async function loadSessions() {
  try { state.sessions = await api('/api/sessions', 'GET'); } catch { return; }
  const ul = $('#sessions-list');
  if (!state.sessions.length) { ul.innerHTML = '<li class="dim">No sessions yet. Press START or Ctrl+Shift+F9.</li>'; return; }
  ul.innerHTML = state.sessions.map(x => {
    const h = x.headline || {};
    const when = x.startedAt ? new Date(x.startedAt).toLocaleString() : x.id;
    const hz = x.mode === 'keyboard' ? h.nominalHz : h.avgHz;
    return `<li><span>${esc(x.mode)} · ${esc(h.device || '—')}</span>
      <span class="meta">${esc(when)} · ${fmtDur(x.durationSec)} · ${ok(hz) ? fmtInt(hz) + ' Hz' : '—'}${ok(h.lagMs) ? ' · ' + fmt(h.lagMs, 1) + ' ms' : ''}${ok(h.fps) ? ' · ' + fmt(h.fps, 0) + ' fps' : ''}</span>
      <span class="acts"><button class="button" data-view-session="${esc(x.id)}" type="button">View</button><a class="button" href="/api/sessions/${esc(x.id)}?download=true" title="Download JSON">⤓</a><button class="button" data-del-session="${esc(x.id)}" type="button" title="Delete">×</button></span></li>`;
  }).join('');
}

async function openSession(id) {
  let doc;
  try { doc = await api('/api/sessions/' + encodeURIComponent(id), 'GET'); } catch (e) { toast(e.message); return; }
  const dlg = $('#dlg-session');
  $('#dlg-session-title').textContent = `Session ${doc.id} · ${doc.mode}`;
  const dev = doc.devices?.[0];
  const lat = doc.latency?.stats;
  $('#dlg-session-body').innerHTML = `
    <div class="kv"><b>Started</b><span>${esc(new Date(doc.startedAt).toLocaleString())}</span><b>Duration</b><span>${fmtDur(doc.durationSec)}</span>
    <b>Device</b><span>${esc(dev?.name || '—')} ${dev?.vidPid ? '· ' + esc(dev.vidPid) : ''}</span><b>Game</b><span>${esc(doc.latency?.target || '—')}</span></div>
    <div class="label">Polling</div><div class="grid tiles">${pollTiles(dev?.summary, doc.mode)}</div>
    <canvas class="chart" id="dlg-rate"></canvas>
    <canvas class="chart" id="dlg-hist"></canvas>
    <div class="label">Input lag</div><div class="grid tiles">${latTiles(lat?.inputToPhoton, 'input→photon') || latTiles(lat?.frameLatency, 'frame→photon') || '<div class="dim">No latency data in this session.</div>'}</div>
    <div class="grid tiles small" style="margin-top:6px">${frameTiles(lat?.frames)}</div>
    <canvas class="chart" id="dlg-lag"></canvas>
    <div class="label">Keys / clicks</div><div class="grid tiles small">${dev ? [tile('Presses', fmtInt(dev.keys.presses)), tile('Tap avg', fmt(dev.keys.tapAvgMs, 1), 'ms'), tile('Chatter', fmtInt(dev.keys.chatter), '', dev.keys.chatter ? 'bad' : 'good')].join('') : ''}</div>
    ${doc.devices.length > 1 ? `<div class="label">Other devices</div><ul class="log">${doc.devices.slice(1).map(x => `<li><span>${esc(x.name)}</span><span>${ok(x.summary?.avgHz) ? fmtInt(x.summary.avgHz) + ' Hz' : '—'}</span></li>`).join('')}</ul>` : ''}`;
  dlg.showModal();
  const ser = dev?.series;
  if (doc.mode === 'keyboard') lineChart($('#dlg-rate'), [{ data: ser?.a, color: C.yellow }], { step: ser?.step, floor0: true, xEnd: 'end' });
  else lineChart($('#dlg-rate'), [{ data: ser?.b, color: C.teal, width: 1 }, { data: ser?.a, color: C.yellow }], { step: ser?.step, ref: dev?.summary?.nominalHz, refLabel: 'nominal', xEnd: 'end' });
  histChart($('#dlg-hist'), dev?.hist, { multiples: doc.mode === 'keyboard' });
  const ls = doc.latency?.series;
  lineChart($('#dlg-lag'), [{ data: ls?.frameLatency, color: C.teal, width: 1 }, { data: ls?.input, color: C.yellow }], { step: ls?.step, floor0: true, xEnd: 'end', empty: 'no latency data' });
}

/* ---------------- wiring ---------------- */
function connect() {
  if (state.es) state.es.close();
  const es = new EventSource('/api/stream?hz=2');
  es.onmessage = ev => {
    state.snap = JSON.parse(ev.data);
    $('#conn').classList.add('on');
    scheduleRender();
    updatePadStream();
  };
  es.onerror = () => $('#conn').classList.remove('on');
  state.es = es;
}

function wire() {
  const act = async (fn, okMsg) => { try { await fn(); if (okMsg) toast(okMsg); } catch (e) { toast(e.message, 6000); } };

  for (const b of $$('[data-mode]')) b.addEventListener('click', () => act(() => api('/api/mode?m=' + b.dataset.mode), 'Switched to ' + b.dataset.mode));
  for (const t of $$('.tab')) t.addEventListener('click', () => { state.view = t.dataset.view; store.set('view', state.view); scheduleRender(); });
  $('#device-select').addEventListener('change', e => act(() => api('/api/device?id=' + encodeURIComponent(e.target.value))));
  $('#devices-table').addEventListener('click', e => {
    const tr = e.target.closest('tr[data-id]');
    if (tr) act(() => api('/api/device?id=' + encodeURIComponent(tr.dataset.id)));
  });
  const toggleRec = () => act(async () => {
    if (state.snap?.recording.active) await api('/api/session/stop');
    else { await api('/api/session/start'); toast('Recording: go play. Ctrl+Shift+F9 stops it from in-game.'); }
  });
  $('#btn-rec').addEventListener('click', toggleRec);
  $('#start-btn').addEventListener('click', toggleRec);
  $('#btn-reset').addEventListener('click', () => act(() => api('/api/live/reset'), 'Live stats cleared'));

  $('#btn-lat').addEventListener('click', () => act(async () => {
    if (state.snap?.latency.running) await api('/api/latency/stop');
    else await api('/api/latency/start?target=' + encodeURIComponent($('#lat-target').value.trim()));
  }));
  $('#lat-status').addEventListener('click', e => {
    if (e.target.id === 'btn-admin') act(() => api('/api/restart-admin'), 'Accept the UAC prompt. This page reconnects by itself.');
    if (e.target.id === 'btn-dl') { e.target.disabled = true; act(() => api('/api/latency/download'), 'PresentMon downloaded'); }
  });
  $('#apps-list').addEventListener('click', e => {
    const li = e.target.closest('li[data-app]');
    if (!li) return;
    $('#lat-target').value = li.dataset.app;
    if (state.snap?.latency.running) act(async () => { await api('/api/latency/stop'); await api('/api/latency/start?target=' + encodeURIComponent(li.dataset.app)); }, 'Targeting ' + li.dataset.app);
  });

  $('#btn-drift').addEventListener('click', () => act(async () => {
    const r = await api('/api/gamepad/drift?seconds=5');
    if (r !== true) throw new Error('Move a stick first so the controller is detected.');
  }, 'Drift test running: hands off the sticks'));
  $('#btn-circ').addEventListener('click', () => act(() => api('/api/gamepad/circularity/reset'), 'Circularity reset: rotate the sticks around the edge'));

  $('#sessions-list').addEventListener('click', e => {
    const v = e.target.closest('[data-view-session]');
    if (v) openSession(v.dataset.viewSession);
    const d = e.target.closest('[data-del-session]');
    if (d && confirm('Delete session ' + d.dataset.delSession + '?'))
      act(async () => { await api('/api/sessions/' + d.dataset.delSession, 'DELETE'); await loadSessions(); }, 'Deleted');
  });
  for (const dlg of $$('dialog')) {
    dlg.addEventListener('click', e => { if (e.target === dlg || e.target.closest('[data-close]')) dlg.close(); });
  }
  $('#icon-help').addEventListener('click', () => $('#dlg-help').showModal());

  const sky = store.get('sky', false);
  document.body.classList.toggle('sky-anim', sky);
  $('#icon-sky').classList.toggle('on', sky);
  $('#icon-sky').addEventListener('click', () => {
    const on = !document.body.classList.contains('sky-anim');
    document.body.classList.toggle('sky-anim', on);
    $('#icon-sky').classList.toggle('on', on);
    store.set('sky', on);
    toast(on ? 'Night sky scrolling on (costs a little GPU)' : 'Night sky paused');
  });

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { state.es?.close(); state.es = null; }
    else connect();
    updatePadStream();
  });
  window.addEventListener('resize', () => { scheduleRender(); requestAnimationFrame(renderPad); });

  const clock = () => {
    const d = new Date();
    $('#clock').textContent = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  };
  clock(); setInterval(clock, 10000);
}

state.view = store.get('view', 'live');
setupWindows();
wire();
connect();
loadSessions();

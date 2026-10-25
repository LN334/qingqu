/* 实时监测：点位表（5s 刷新）· 单元/类型/搜索筛选 · 24h 曲线叠加（最多 4 个）· CSV 导出 */
import * as ui from '../ui.js';

export const meta = { title: '实时监测' };

const TYPE_LABEL = { measure: '测量量', state: '状态量', counter: '累计量', setpoint: '设定量' };
const TYPE_OPTIONS = [
  { value: 'all', label: '全部类型' },
  { value: 'measure', label: '测量量' },
  { value: 'state', label: '状态量' },
  { value: 'counter', label: '累计量' },
  { value: 'setpoint', label: '设定量' },
];
const BW_NAMES = ['空闲', '气冲', '气水联合', '漂洗'];
const MAX_SERIES = 4;

function isNum(v) { return v !== null && v !== undefined && Number.isFinite(Number(v)); }

/* ui.sourceText 会优先读 source（网关侧统一是 gateway），这里按 provenance 还原数据来源口径 */
function sourceLabel(reading) {
  if (!reading) return '—';
  const src = reading.provenance || reading.source;
  if (src === 'gateway') return '模拟网关';
  if (reading.provenance) return ui.sourceText({ ...reading, source: reading.provenance });
  return ui.sourceText(reading);
}

function qualityOf(p) {
  const q = p.quality || p.q;
  return q || 'good';
}

function timeOf(p) { return p.t || p.at || null; }

function isStale(p) {
  if (p.stale) return true;
  const t = timeOf(p);
  if (!t) return false;
  const ts = new Date(t).getTime();
  return Number.isFinite(ts) && Date.now() - ts > 90000;
}

function displayValue(p) {
  if (p.value === null || p.value === undefined) return '待接入';
  const v = Number(p.value);
  if (TYPE_LABEL[p.type] === undefined) return String(p.value);
  if (p.type === 'state') {
    const code = p.code || '';
    if (/\.BW\.STATE$/.test(code)) return BW_NAMES[v] || String(v);
    if (/\.FAULT$/.test(code)) return v ? '故障' : '正常';
    if (/\.MODE$/.test(code)) return v ? '自动' : '手动';
    if (/\.RUN$/.test(code)) return v ? '运行' : '停止';
    return v ? '运行' : '停止';
  }
  if (!Number.isFinite(v)) return String(p.value);
  if (p.type === 'counter') return ui.fmt.int(v);
  return ui.fmt.auto(v);
}

function niceTicks(min, max, count = 4) {
  if (!Number.isFinite(min) || !Number.isFinite(max)) return [0, 1];
  if (min === max) { min -= 1; max += 1; }
  const span = max - min;
  const step0 = span / Math.max(1, count - 1);
  const mag = 10 ** Math.floor(Math.log10(step0 || 1));
  const norm = step0 / mag;
  const step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
  const lo = Math.floor(min / step) * step;
  const hi = Math.ceil(max / step) * step;
  const ticks = [];
  for (let v = lo; v <= hi + step / 2; v += step) ticks.push(Number(v.toFixed(6)));
  return ticks;
}

/* 单点位曲线叠加阈值参考线：与 ui.chartLine 使用同一 viewBox 与坐标映射 */
function overlayRules(host, points, rules, height) {
  const drawable = (rules || []).filter((r) => isNum(r.low) || isNum(r.high));
  if (!drawable.length || !points.length) return;
  const W = Math.max(320, host.clientWidth || 640);
  const H = height;
  const pad = { l: 44, r: 12, t: 12, b: 22 };
  const ih = H - pad.t - pad.b;
  const values = points.map((p) => Number(p.v)).filter(Number.isFinite);
  let lo = Math.min(...values);
  let hi = Math.max(...values);
  if (!Number.isFinite(lo) || !Number.isFinite(hi)) return;
  if (lo === hi) { lo -= 1; hi += 1; }
  lo -= (hi - lo) * 0.12;
  hi += (hi - lo) * 0.12;
  const ticks = niceTicks(lo, hi, 4);
  lo = Math.min(lo, ticks[0]);
  hi = Math.max(hi, ticks[ticks.length - 1]);
  if (hi === lo) return;

  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'ref-layer');
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  svg.setAttribute('preserveAspectRatio', 'none');
  svg.setAttribute('aria-hidden', 'true');
  svg.style.cssText = `position:absolute;top:0;left:0;width:100%;height:${H}px;pointer-events:none;overflow:visible`;
  const yOf = (v) => pad.t + ih - ((v - lo) / (hi - lo)) * ih;
  drawable.forEach((rule) => {
    const marks = [];
    if (isNum(rule.low)) marks.push({ v: Number(rule.low), label: `下限 ${ui.fmt.auto(rule.low)}` });
    if (isNum(rule.high)) marks.push({ v: Number(rule.high), label: `上限 ${ui.fmt.auto(rule.high)}` });
    marks.forEach((mark) => {
      const y = yOf(mark.v);
      if (y < pad.t || y > pad.t + ih) return;
      const major = rule.level === 'major' || rule.level === 'critical';
      const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
      line.setAttribute('x1', pad.l);
      line.setAttribute('x2', W - pad.r);
      line.setAttribute('y1', y);
      line.setAttribute('y2', y);
      line.setAttribute('stroke', major ? '#b32d22' : '#c0762a');
      line.setAttribute('stroke-dasharray', '4 3');
      line.setAttribute('class', 'ref-line');
      svg.appendChild(line);
      const text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
      text.setAttribute('x', W - pad.r - 4);
      text.setAttribute('y', y - 4);
      text.setAttribute('text-anchor', 'end');
      text.setAttribute('fill', major ? '#b32d22' : '#c0762a');
      text.setAttribute('class', 'ref-label');
      text.textContent = `${mark.label}${rule.unit ? ` ${rule.unit}` : ''}`;
      svg.appendChild(text);
    });
  });
  host.appendChild(svg);
}

export async function render(root, ctx) {
  const { api, siteId } = ctx;
  root.classList.add('p-monitor');
  ctx.setTitle(meta.title);

  const units = (ctx.meta && ctx.meta.units) || [];
  const unitMap = new Map(units.map((u) => [u.code, u]));
  const unitOrder = new Map(units.map((u, i) => [u.code, isNum(u.order) ? Number(u.order) : i]));
  const canExport = Boolean(ctx.user && Array.isArray(ctx.user.perms) && ctx.user.perms.includes('report.export'));
  const initialUnit = ctx.query && ctx.query.unit && unitMap.has(ctx.query.unit) ? ctx.query.unit : 'all';

  const state = {
    points: [],
    byCode: new Map(),
    unit: initialUnit,
    type: 'all',
    q: '',
    selected: [],
    seq: 0,
    rules: [],
    lastUpdateAt: null,
    pollFailed: false,
    polling: false,
  };
  const liveRefs = new Map();
  const statRefs = new Map();

  const actions = [
    ui.el('button', { class: 'btn ghost', type: 'button', onclick: () => ctx.refresh() },
      ui.icon('refresh'), '刷新数据'),
  ];

  ui.mount(root,
    ui.pageHead('实时监测', '点位表每 5 秒刷新；点击点位行叠加 24 小时曲线（最多 4 个点位），并导出当前筛选结果。', actions),
    ui.el('div', { class: 'mon-body' }, ui.skeleton(3, 'block')),
  );

  let points;
  try {
    points = await api.get('/api/points', { siteId });
  } catch (err) {
    root.classList.add('p-monitor');
    ui.mount(root,
      ui.pageHead('实时监测', '点位实时值、质量与来源。', actions),
      ui.card({ body: ui.errorState(err, () => ctx.refresh()) }));
    return;
  }

  if (!points || !points.length) {
    ui.mount(root,
      ui.pageHead('实时监测', '点位实时值、质量与来源。', actions),
      ui.card({ body: ui.empty('暂无点位数据', '服务端未返回本站点点位，请检查网关与点位配置。', 'monitor') }));
    return;
  }

  state.points = points.slice().sort((a, b) => {
    const ua = unitOrder.has(a.unitCode) ? unitOrder.get(a.unitCode) : 99;
    const ub = unitOrder.has(b.unitCode) ? unitOrder.get(b.unitCode) : 99;
    if (ua !== ub) return ua - ub;
    return String(a.code).localeCompare(String(b.code));
  });
  state.points.forEach((p) => state.byCode.set(p.code, p));
  try {
    const latest = await api.get('/api/telemetry/latest', { siteId });
    applyLatest((latest && latest.items) || []);
    state.lastUpdateAt = new Date();
  } catch (err) {
    console.warn('[monitor] 首次实时数据加载失败', err);
  }
  try {
    state.rules = (await api.get('/api/alarm-rules', { siteId })) || [];
  } catch (err) {
    state.rules = [];
  }

  /* ---------------- 页面骨架 ---------------- */
  const page = ui.el('div', { class: 'mon-body' });
  ui.mount(root,
    ui.pageHead('实时监测', '点位表每 5 秒刷新；点击点位行叠加 24 小时曲线（最多 4 个点位），并导出当前筛选结果。', actions),
    page,
  );

  const statGrid = ui.el('div', { class: 'mon-stats' });
  page.appendChild(statGrid);
  const toolbar = buildToolbar();
  page.appendChild(toolbar);
  const tableHost = ui.el('div', { class: 'mon-table-wrap' });
  page.appendChild(ui.card({ body: tableHost, flush: true, class: 'mon-table-card' }));
  const curveHost = ui.el('div', { class: 'mon-curve-wrap' });
  page.appendChild(curveHost);
  const foot = ui.el('div', { class: 'mon-foot' },
    ui.el('span', { class: 'muted small' }, '数据来源：内置模拟网关 · 在线判定：90 秒内有数据 · 图纸未布设仪表的单元标注「待接入」'),
    ui.el('span', { class: 'mono small', 'data-role': 'updated' }, '等待刷新…'),
  );
  page.appendChild(foot);

  buildStats();
  renderTable();
  renderCurve();
  paintUpdated();
  refreshSelectedChips();

  ctx.every(5000, poll);
  ctx.subscribe((evt) => {
    if (!evt || evt.type !== 'telemetry') return;
    const at = state.lastUpdateAt ? state.lastUpdateAt.getTime() : 0;
    if (Date.now() - at > 2000) poll();
  });

  /* ---------------- 顶部统计 ---------------- */
  function computeStats() {
    const total = state.points.length;
    const online = state.points.filter((p) => p.value !== null && p.value !== undefined && !isStale(p)).length;
    const stale = state.points.filter((p) => p.value !== null && p.value !== undefined && isStale(p)).length;
    const pending = state.points.filter((p) => p.value === null || p.value === undefined).length;
    const withPoints = new Set(state.points.map((p) => p.unitCode));
    const noInstrument = units.filter((u) => !withPoints.has(u.code));
    return { total, online, stale, pending, noInstrument };
  }

  function buildStats() {
    const specs = [
      ['points', '点位数', '个'],
      ['online', '在线', '个'],
      ['stale', '超时', '个'],
      ['noinst', '无仪表数', '个'],
    ];
    specs.forEach(([key, label, unit]) => {
      const valueNode = ui.el('span', { class: 'stat-value' }, '—');
      const hintNode = ui.el('span', { class: 'stat-hint' }, '');
      statGrid.appendChild(ui.el('div', { class: `stat-card stat-${key}` },
        ui.el('span', { class: 'stat-label' }, label),
        valueNode,
        ui.el('span', { class: 'stat-unit' }, unit),
        hintNode,
      ));
      statRefs.set(key, { valueNode, hintNode });
    });
    updateStats();
  }

  function updateStats() {
    const stats = computeStats();
    const set = (key, value, hint) => {
      const ref = statRefs.get(key);
      if (!ref) return;
      ref.valueNode.textContent = String(value);
      ref.hintNode.textContent = hint;
    };
    set('points', stats.total, `已配置点位数（含待接入 ${stats.pending} 个）`);
    set('online', stats.online, '90 秒内有有效数据');
    set('stale', stats.stale, '超过 90 秒未更新');
    set('noinst', stats.noInstrument.length, stats.noInstrument.length
      ? `${stats.noInstrument.map((u) => u.short || u.name).join('、')} 未布设在线仪表`
      : '全部单元均有在线点位');
  }

  /* ---------------- 筛选工具栏 ---------------- */
  function buildToolbar() {
    const bar = ui.el('div', { class: 'mon-toolbar' });
    const unitOptions = [{ value: 'all', label: '全部单元' }].concat(
      units.map((u) => ({ value: u.code, label: `${u.short || u.name}${u.name !== (u.short || u.name) ? `（${u.name}）` : ''}`, selected: u.code === state.unit })),
    );
    const unitSelect = ui.select(unitOptions, {
      'aria-label': '按单元筛选',
      onchange: (e) => { state.unit = e.target.value; renderTable(); refreshSelectedChips(); },
    });
    const typeSelect = ui.select(TYPE_OPTIONS.map((o) => ({ ...o, selected: o.value === state.type })), {
      'aria-label': '按点位类型筛选',
      onchange: (e) => { state.type = e.target.value; renderTable(); refreshSelectedChips(); },
    });
    const search = ui.input({
      type: 'search',
      placeholder: '搜索点位 / 编码 / 位置',
      'aria-label': '搜索点位',
      value: state.q,
      oninput: ui.debounce((e) => { state.q = e.target.value.trim(); renderTable(); refreshSelectedChips(); }, 200),
    });
    const reset = ui.el('button', {
      class: 'btn ghost sm', type: 'button',
      onclick: () => {
        state.unit = 'all';
        state.type = 'all';
        state.q = '';
        unitSelect.value = 'all';
        typeSelect.value = 'all';
        search.value = '';
        renderTable();
        refreshSelectedChips();
      },
    }, '重置筛选');
    const exportBtn = ui.el('button', {
      class: 'btn primary',
      type: 'button',
      disabled: !canExport,
      title: canExport ? '导出当前筛选结果（UTF-8 BOM CSV）' : '当前账号无 report.export 权限，无法导出',
      onclick: exportCsv,
    }, ui.icon('report'), '导出 CSV');
    const countNode = ui.el('span', { class: 'mon-count muted small' }, '');
    bar.append(
      ui.el('label', { class: 'toolbar-field' }, ui.el('span', { class: 'toolbar-label' }, '单元'), unitSelect),
      ui.el('label', { class: 'toolbar-field' }, ui.el('span', { class: 'toolbar-label' }, '类型'), typeSelect),
      ui.el('label', { class: 'toolbar-field search-field' }, ui.el('span', { class: 'toolbar-label' }, '搜索'), search),
      reset,
      ui.el('span', { class: 'spacer' }),
      countNode,
      exportBtn,
    );
    return bar;
  }

  function filteredPoints() {
    const q = state.q.toLowerCase();
    return state.points.filter((p) => {
      if (state.unit !== 'all' && p.unitCode !== state.unit) return false;
      if (state.type !== 'all' && p.type !== state.type) return false;
      if (!q) return true;
      const unitName = (unitMap.get(p.unitCode) || {}).name || '';
      return `${p.name || ''} ${p.code || ''} ${unitName} ${p.unit || ''}`.toLowerCase().includes(q);
    });
  }

  /* ---------------- 点位表 ---------------- */
  function renderTable() {
    const rows = filteredPoints();
    const countNode = toolbar.querySelector('.mon-count');
    if (countNode) countNode.textContent = `筛选结果 ${rows.length} / ${state.points.length} 个点位`;
    liveRefs.clear();

    if (!rows.length) {
      const unit = state.unit !== 'all' ? unitMap.get(state.unit) : null;
      const emptyNode = state.q
        ? ui.empty('没有匹配的点位', '调整单元、类型筛选或清空搜索关键词。', 'search')
        : unit
          ? ui.empty('该单元无在线点位', `${unit.name}：图纸未布设在线仪表，相关指标标注为「待接入」。`, 'monitor')
          : ui.empty('没有匹配的点位', '调整单元、类型筛选或清空搜索关键词。', 'search');
      ui.mount(tableHost, emptyNode);
      return;
    }

    const table = ui.el('table', { class: 'data mon-table' });
    const thead = ui.el('thead');
    const trh = ui.el('tr');
    ['点位', '编码', '位置', '实时值', '单位', '质量', '数据时间', '来源'].forEach((label, i) => {
      trh.appendChild(ui.el('th', { class: i === 1 ? 'mono' : undefined }, label));
    });
    thead.appendChild(trh);
    const tbody = ui.el('tbody');

    rows.forEach((p) => {
      const selected = state.selected.includes(p.code);
      const tr = ui.el('tr', {
        class: `clickable${selected ? ' selected' : ''}`,
        tabindex: '0',
        title: '点击查看该点位 24 小时曲线（最多叠加 4 个）',
        'aria-label': `${p.name}，${displayValue(p)}${p.unit || ''}，点击查看曲线`,
      });
      const stale = isStale(p);
      const valueNode = ui.el('span', { class: `mono mon-value${stale ? ' is-stale' : ''}` }, displayValue(p));
      const qualityNode = ui.el('span', { class: 'mon-quality' });
      paintQuality(qualityNode, p);
      const timeNode = ui.el('span', { class: `mono small mon-time${stale ? ' is-stale' : ''}` }, ui.fmt.timeSec(timeOf(p)));
      tr.append(
        ui.el('td', {}, ui.el('span', { class: 'cell-main' }, p.name || p.code)),
        ui.el('td', { class: 'mono small' }, p.code || '—'),
        ui.el('td', {}, (unitMap.get(p.unitCode) || {}).short || (unitMap.get(p.unitCode) || {}).name || p.unitCode || '—'),
        ui.el('td', { class: 'num' }, valueNode),
        ui.el('td', { class: 'small' }, p.unit || '—'),
        ui.el('td', {}, qualityNode),
        ui.el('td', {}, timeNode),
        ui.el('td', { class: 'small muted' }, sourceLabel(p)),
      );
      const toggle = () => toggleSelect(p);
      tr.addEventListener('click', toggle);
      tr.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); }
      });
      tbody.appendChild(tr);
      liveRefs.set(p.code, { point: p, tr, valueNode, qualityNode, timeNode });
    });
    table.append(thead, tbody);
    ui.mount(tableHost, ui.el('div', { class: 'table-wrap' }, table));
  }

  /* 质量列与统计口径一致：超过 90 秒无新数据即「超时」（warn），接口 quality 为 bad 时优先异常 */
  function paintQuality(node, p) {
    const q = qualityOf(p);
    const stale = isStale(p);
    if (q !== 'bad' && stale) {
      ui.mount(node, ui.badge('超时', 'warn', `数据超过 90 秒未更新，最后 ${ui.fmt.timeSec(timeOf(p))}`));
      return;
    }
    ui.mount(node, ui.qualityBadge(q));
    if (q !== 'bad') {
      const tag = ui.staleTag(p);
      if (tag) node.appendChild(tag);
    }
  }

  function updateLiveCells() {
    liveRefs.forEach((ref) => {
      const p = state.byCode.get(ref.point.code) || ref.point;
      ref.point = p;
      ref.valueNode.textContent = displayValue(p);
      ref.valueNode.classList.toggle('is-stale', isStale(p));
      paintQuality(ref.qualityNode, p);
      ref.timeNode.textContent = ui.fmt.timeSec(timeOf(p));
      ref.timeNode.classList.toggle('is-stale', isStale(p));
    });
  }

  /* ---------------- 点位选择与曲线 ---------------- */
  function toggleSelect(p) {
    const idx = state.selected.indexOf(p.code);
    if (idx >= 0) {
      state.selected.splice(idx, 1);
    } else {
      if (state.selected.length >= MAX_SERIES) {
        ctx.toast(`最多同时叠加 ${MAX_SERIES} 个点位曲线，请先移除一个。`, 'info');
        return;
      }
      state.selected.push(p.code);
    }
    renderTable();
    refreshSelectedChips();
    renderCurve();
  }

  function refreshSelectedChips() {
    const box = curveHost.querySelector('.curve-selected');
    if (!box) return;
    const chips = state.selected.map((code, i) => {
      const p = state.byCode.get(code);
      return ui.el('span', { class: 'curve-chip', style: { borderColor: ui.palette(i) } },
        ui.dot('good'),
        ui.el('span', { class: 'curve-chip-name' }, p ? p.name : code),
        ui.el('button', {
          class: 'icon-btn tiny', type: 'button', 'aria-label': `移除 ${p ? p.name : code}`,
          onclick: () => { state.selected = state.selected.filter((c) => c !== code); renderTable(); refreshSelectedChips(); renderCurve(); },
        }, ui.icon('close')),
      );
    });
    ui.mount(box, chips.length ? chips : ui.el('span', { class: 'muted small' }, '尚未选择点位，点击上方表格行添加曲线（最多 4 个）。'));
    if (state.selected.length) {
      box.appendChild(ui.el('button', {
        class: 'btn ghost sm', type: 'button',
        onclick: () => { state.selected = []; renderTable(); refreshSelectedChips(); renderCurve(); },
      }, '清空选择'));
    }
  }

  function renderCurve() {
    const selected = state.selected.map((code) => state.byCode.get(code)).filter(Boolean);
    const selectedBox = ui.el('div', { class: 'curve-selected' });
    const chartHost = ui.el('div', { class: 'curve-chart chart-host' });
    const statsBox = ui.el('div', { class: 'curve-stats' });
    const noteBox = ui.el('div', { class: 'curve-note muted small' });
    const headActions = selected.length
      ? [
        ui.el('button', { class: 'btn ghost sm', type: 'button', onclick: () => loadCurve() }, ui.icon('refresh'), '刷新曲线'),
        ui.el('button', { class: 'btn ghost sm', type: 'button', onclick: () => { state.selected = []; renderTable(); renderCurve(); } }, '清空'),
      ]
      : [];

    const card = ui.card({
      title: '24 小时曲线',
      sub: '历史数据来自内置模拟网关（30 分钟聚合）；同图叠加仅用于趋势对比，单位见点位标签',
      actions: headActions,
      body: ui.el('div', {},
        selectedBox,
        chartHost,
        statsBox,
        noteBox,
      ),
      class: 'curve-card',
    });
    ui.mount(curveHost, card);
    refreshSelectedChips();
    if (!selected.length) {
      ui.mount(chartHost, ui.empty('尚未选择点位', '点击上方表格行，最多叠加 4 个点位；曲线含最小值 / 平均值 / 最大值与阈值线。', 'analysis'));
      statsBox.remove();
      return;
    }
    loadCurve();
  }

  async function loadCurve() {
    const codes = state.selected.slice();
    const mySeq = ++state.seq;
    const chartHost = curveHost.querySelector('.curve-chart');
    const statsBox = curveHost.querySelector('.curve-stats');
    const noteBox = curveHost.querySelector('.curve-note');
    if (!chartHost) return;
    ui.mount(chartHost, ui.el('div', { class: 'chart-loading' }, ui.skeleton(1, 'block')));
    let history;
    try {
      history = await api.get('/api/telemetry/history', { siteId, codes: codes.join(','), hours: 24 });
    } catch (err) {
      if (mySeq !== state.seq) return;
      ui.mount(chartHost, ui.errorState(err, () => loadCurve()));
      return;
    }
    if (mySeq !== state.seq) return;

    const seriesMap = (history && history.series) || {};
    const series = [];
    const statRows = [];
    const missing = [];
    codes.forEach((code, i) => {
      const p = state.byCode.get(code);
      const s = seriesMap[code];
      const rawPoints = (s && s.points) || [];
      const points = rawPoints.filter((pt) => pt.v !== null && Number.isFinite(Number(pt.v)));
      if (!points.length) {
        missing.push(p ? p.name : code);
        return;
      }
      const values = points.map((pt) => Number(pt.v));
      const min = Math.min(...values);
      const max = Math.max(...values);
      const avg = values.reduce((a, b) => a + b, 0) / values.length;
      series.push({
        name: `${p ? p.name : code}${p && p.unit ? `（${p.unit}）` : ''}`,
        color: ui.palette(i),
        points,
      });
      statRows.push({ p, code, min, max, avg, count: points.length, color: ui.palette(i) });
    });

    ui.mount(chartHost);
    if (!series.length) {
      chartHost.appendChild(ui.empty('暂无历史数据', '所选点位近 24 小时没有有效样本。', 'analysis'));
    } else {
      const inner = ui.el('div', { class: 'chart-inner' });
      chartHost.appendChild(inner);
      ui.chartLine(inner, {
        series,
        height: 280,
        xTickCount: 6,
        showLegend: true,
        digits: 2,
      });
      if (codes.length === 1) {
        const rules = (state.rules || []).filter((r) => r.pointCode === codes[0] && r.enabled !== false);
        overlayRules(inner, series[0].points, rules, 280);
      }
    }

    const statCards = statRows.map((row) => {
      const digits = row.p && row.p.type === 'measure' ? 2 : 1;
      return ui.el('div', { class: 'curve-stat' },
        ui.el('div', { class: 'curve-stat-head' },
          ui.el('span', { class: 'curve-dot', style: { background: row.color } }),
          ui.el('span', { class: 'curve-stat-name' }, row.p ? row.p.name : row.code),
          ui.el('span', { class: 'mono small muted' }, row.p && row.p.unit ? row.p.unit : ''),
        ),
        ui.el('div', { class: 'curve-stat-values mono' },
          ui.el('span', {}, `最小 ${ui.fmt.num(row.min, digits)}`),
          ui.el('span', {}, `平均 ${ui.fmt.num(row.avg, digits)}`),
          ui.el('span', {}, `最大 ${ui.fmt.num(row.max, digits)}`),
          ui.el('span', { class: 'muted' }, `${row.count} 点`),
        ),
        ui.el('div', { class: 'curve-stat-src small muted' },
          `来源：${row.p ? sourceLabel(row.p) : '内置模拟网关'}`,
        ),
      );
    });
    ui.mount(statsBox, statCards.length ? statCards : ui.el('div', { class: 'muted small' }, '无有效统计样本。'));

    const notes = [];
    if (missing.length) notes.push(`近 24 小时无数据：${missing.join('、')}`);
    const ruleLines = [];
    codes.forEach((code) => {
      const hit = (state.rules || []).filter((r) => r.pointCode === code && r.enabled !== false);
      hit.forEach((r) => ruleLines.push(`${r.id}「${r.threshold || r.name}」`));
    });
    if (ruleLines.length) {
      notes.push(codes.length === 1
        ? `阈值参考线：${ruleLines.join('；')}`
        : `同一点位告警规则：${ruleLines.join('；')}（多点位叠加时不绘制阈值线）`);
    }
    if (codes.length > 1) {
      const unitSet = new Set(state.selected.map((code) => (state.byCode.get(code) || {}).unit).filter(Boolean));
      if (unitSet.size > 1) notes.push('所选点位单位不同，同一纵轴仅用于趋势对比，读数请看下方统计卡。');
    }
    ui.mount(noteBox, notes.length
      ? notes.map((n) => ui.el('div', {}, n))
      : ui.el('div', {}, '数据来源：内置模拟网关遥测历史（30 分钟聚合，近 24 小时）。'));
  }

  /* ---------------- CSV 导出 ---------------- */
  function exportCsv() {
    if (!canExport) {
      ctx.toast('当前账号无 report.export 权限，无法导出。', 'error');
      return;
    }
    const rows = filteredPoints();
    if (!rows.length) {
      ctx.toast('当前筛选结果为空，没有可导出的点位。', 'info');
      return;
    }
    const header = ['点位', '编码', '位置', '实时值', '单位', '质量', '数据时间', '来源', '数据类型'];
    const lines = [header].concat(rows.map((p) => [
      p.name || '',
      p.code || '',
      (unitMap.get(p.unitCode) || {}).name || p.unitCode || '',
      displayValue(p),
      p.unit || '',
      isStale(p) ? '数据超时' : qualityLabel(qualityOf(p)),
      ui.fmt.dateTime(timeOf(p)),
      sourceLabel(p),
      TYPE_LABEL[p.type] || p.type || '',
    ]));
    const csv = '\uFEFF' + lines.map((line) => line.map(csvCell).join(',')).join('\r\n');
    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    const stamp = new Date();
    const pad = (n) => String(n).padStart(2, '0');
    a.href = url;
    a.download = `实时监测_${(ctx.site && ctx.site.name) || siteId}_${stamp.getFullYear()}${pad(stamp.getMonth() + 1)}${pad(stamp.getDate())}_${pad(stamp.getHours())}${pad(stamp.getMinutes())}.csv`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1500);
    ctx.toast(`已导出 ${rows.length} 个点位`, 'success');
  }

  function qualityLabel(q) {
    if (q === 'good') return '正常';
    if (q === 'uncertain') return '可疑';
    if (q === 'bad') return '异常';
    return q || '未知';
  }

  function csvCell(value) {
    const s = value === null || value === undefined ? '' : String(value);
    return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  }

  /* ---------------- 轮询 ---------------- */
  function applyLatest(items) {
    items.forEach((r) => {
      if (!r || !r.code) return;
      if (r.value === undefined && r.v !== undefined) r.value = r.v;
      const point = state.byCode.get(r.code);
      if (point) Object.assign(point, r);
      else state.byCode.set(r.code, r);
    });
    state.lastUpdateAt = new Date();
  }

  async function poll() {
    if (state.polling) return;
    state.polling = true;
    try {
      const latest = await api.get('/api/telemetry/latest', { siteId });
      applyLatest((latest && latest.items) || []);
      state.pollFailed = false;
      updateStats();
      updateLiveCells();
      paintUpdated();
    } catch (err) {
      state.pollFailed = true;
      paintUpdated();
      console.warn('[monitor] 实时数据刷新失败', err);
    } finally {
      state.polling = false;
    }
  }

  function paintUpdated() {
    const node = foot.querySelector('[data-role="updated"]');
    if (!node) return;
    if (state.pollFailed) {
      node.textContent = `刷新失败，显示上次数据${state.lastUpdateAt ? `（${ui.fmt.timeSec(state.lastUpdateAt)}）` : ''}`;
      node.classList.add('text-warn');
    } else {
      node.textContent = `最后更新 ${ui.fmt.timeSec(state.lastUpdateAt || new Date())} · 5 秒自动刷新`;
      node.classList.remove('text-warn');
    }
  }
}

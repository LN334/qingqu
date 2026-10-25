/* 工艺监控：11 单元动态工艺图（内联 SVG）· 反冲洗阶段 · 联锁判定 · 单元抽屉与卡片 */
import * as ui from '../ui.js';

export const meta = { title: '工艺监控' };

const BW_PHASES = [
  { state: 1, name: '气冲', sec: 30 },
  { state: 2, name: '气水联合', sec: 90 },
  { state: 3, name: '漂洗', sec: 45 },
];
const BW_NAMES = ['空闲', '气冲', '气水联合', '漂洗'];

const DEVICE_TYPE = {
  METER: '仪表', PUMP: '泵', BLOWER: '风机', VALVE: '阀门',
  DOSER: '加药泵', MIXER: '搅拌器', PLC: '控制柜', GATEWAY: '网关',
};

const SHORT_READING = {
  'RAW.LVL': '液位', 'RAW.PUMP.RUN': '提升泵', 'FLOW.IN': '流量',
  'BAF.PH': 'pH', 'BAF.DO': 'DO', 'BAF.DP': '压差',
  'PAC.LEVEL': '药箱', 'PAC.DOSE': '投加', 'PAC.PUMP.RUN': '加药泵',
  'PAM.LEVEL': '药箱', 'PAM.DOSE': '投加', 'PAM.PUMP.RUN': '加药泵',
  'SULFUR.PH': 'pH', 'SULFUR.DO': 'DO', 'SULFUR.P': '压力',
  'CLEAR.LVL': '液位', 'WASTE.LVL': '液位', 'POWER.KW': '功率', 'ENERGY.KWH': '累计电量',
};

const UNIT_STATUS_TEXT = {
  normal: '正常', alarm: '告警', attention: '关注', offline: '离线',
  stale: '数据超时', 'no-instrument': '无仪表',
};

/* 11 单元在画布上的位置：蛇形三行 */
const CANVAS = { w: 1400, h: 560, boxW: 290, boxH: 118, xs: [40, 370, 700, 1030], ys: [40, 210, 380] };
const POS = {
  raw: [0, 0], grit: [1, 0], primary: [2, 0], dist1: [3, 0],
  baf: [3, 1], pac: [2, 1], pam: [1, 1], dist2: [0, 1],
  sulfur: [0, 2], clear: [1, 2], waste: [2, 2],
};
const SEQUENCE = ['raw', 'grit', 'primary', 'dist1', 'baf', 'pac', 'pam', 'dist2', 'sulfur', 'clear', 'waste'];

function svgNode(tag, attrs = {}) {
  const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === undefined || v === null) continue;
    node.setAttribute(k, v);
  }
  return node;
}

function isNum(v) { return v !== null && v !== undefined && Number.isFinite(Number(v)); }

/* ui.sourceText 优先读 source（网关侧统一为 gateway），这里按 provenance 还原方案口径 */
function sourceLabel(reading) {
  if (!reading) return '—';
  const src = reading.provenance || reading.source;
  if (src === 'gateway') return '模拟网关';
  if (reading.provenance) return ui.sourceText({ ...reading, source: reading.provenance });
  return ui.sourceText(reading);
}

function readingText(reading, digits = 1) {
  if (!reading) return '—';
  if (reading.value === null || reading.value === undefined) return '待接入';
  const raw = Number(reading.value);
  if (reading.type === 'state') {
    const code = reading.code || '';
    if (/\.BW\.STATE$/.test(code)) return BW_NAMES[raw] || '—';
    if (/\.FAULT$/.test(code)) return raw ? '故障' : '正常';
    if (/\.MODE$/.test(code)) return raw ? '自动' : '手动';
    if (/\.RUN$/.test(code)) return raw ? '运行' : '停止';
    return raw ? '运行' : '停止';
  }
  const text = reading.type === 'counter' ? ui.fmt.int(raw) : ui.fmt.num(raw, digits);
  return reading.unit ? `${text} ${reading.unit}` : text;
}

function readingLine(reading) {
  if (!reading) return '—';
  return `${SHORT_READING[reading.code] || reading.name} ${readingText(reading)}`;
}

function shortReading(reading) {
  if (!reading) return '—';
  return readingText(reading);
}

function unitStatusBadge(status) {
  return ui.unitStatus(status);
}

function deviceTone(status) {
  if (status === '运行' || status === '在线') return 'good';
  if (status === '待机' || status === '关闭') return 'muted';
  if (status === '保养中') return 'warn';
  return 'bad';
}

function deviceBadge(status) {
  return ui.badge(status || '未知', deviceTone(status));
}

function flowArrow(x1, y1, x2, y2, dashed = false) {
  const group = svgNode('g', { class: 'flow-arrow' });
  group.appendChild(svgNode('line', {
    x1, y1, x2, y2,
    class: dashed ? 'arrow-line dashed' : 'arrow-line',
  }));
  const ang = Math.atan2(y2 - y1, x2 - x1);
  const size = 8;
  const p1 = [x2, y2];
  const p2 = [x2 - size * Math.cos(ang - 0.45), y2 - size * Math.sin(ang - 0.45)];
  const p3 = [x2 - size * Math.cos(ang + 0.45), y2 - size * Math.sin(ang + 0.45)];
  group.appendChild(svgNode('polygon', {
    points: [p1, p2, p3].map((p) => p.map((n) => n.toFixed(1)).join(',')).join(' '),
    class: 'arrow-head',
  }));
  return group;
}

function center(pos) {
  return {
    x: CANVAS.xs[pos[0]] + CANVAS.boxW / 2,
    y: CANVAS.ys[pos[1]] + CANVAS.boxH / 2,
    left: CANVAS.xs[pos[0]],
    right: CANVAS.xs[pos[0]] + CANVAS.boxW,
    top: CANVAS.ys[pos[1]],
    bottom: CANVAS.ys[pos[1]] + CANVAS.boxH,
  };
}

export async function render(root, ctx) {
  const { api, siteId } = ctx;
  root.classList.add('p-process');
  ctx.setTitle(meta.title);

  const actions = [
    ui.el('button', { class: 'btn ghost', type: 'button', onclick: () => ctx.refresh() },
      ui.icon('refresh'), '刷新'),
    ui.el('a', { class: 'btn primary', href: '#/monitor' }, ui.icon('monitor'), '实时监测'),
  ];

  ui.mount(root,
    ui.pageHead('工艺监控', '动态工艺图按方案第 3 章图纸顺序绘制；实时值来自内置模拟网关，每 5 秒刷新。', actions),
    ui.el('div', { class: 'proc-body' }, ui.skeleton(3, 'block')),
  );

  let process;
  try {
    process = await api.get('/api/sites/' + encodeURIComponent(siteId) + '/process');
  } catch (err) {
    ui.mount(root,
      ui.pageHead('工艺监控', '按方案图纸顺序绘制的 11 单元工艺链。', actions),
      ui.card({ body: ui.errorState(err, () => ctx.refresh()) }));
    return;
  }

  const latestMap = new Map();
  const readingMap = new Map();
  const liveTargets = [];
  const statusRefs = new Map();
  const bwRefs = new Map();
  const interlockRefs = new Map();
  const bwObs = { baf: { state: null, since: 0 }, sulfur: { state: null, since: 0 } };
  let updating = false;
  let lastUpdateAt = null;
  let updateFailed = false;

  function seedReadings(proc) {
    (proc.units || []).forEach((u) => u.readings.forEach((r) => readingMap.set(r.code, r)));
  }
  seedReadings(process);

  const page = ui.el('div', { class: 'proc-body' });
  ui.mount(root,
    ui.pageHead('工艺监控', '动态工艺图按方案第 3 章图纸顺序绘制；实时值来自内置模拟网关，每 5 秒刷新。', actions),
    page,
  );

  page.appendChild(summaryStrip());
  page.appendChild(diagramCard());
  page.appendChild(buildBackwashCard());
  page.appendChild(unitCardGrid());
  page.appendChild(interlockCard());

  const foot = ui.el('div', { class: 'proc-foot' },
    ui.el('span', { class: 'muted small' }, '数据来源：内置模拟网关（MQTT/Modbus 抽象）· 人工化验不参与本页实时值 · 缺失仪表单元按方案标注'),
    ui.el('span', { class: 'mono small', 'data-role': 'updated' }, '等待首次刷新…'),
  );
  page.appendChild(foot);

  const updatedNode = foot.querySelector('[data-role="updated"]');
  paintUpdated();
  refreshLatest();
  if (ctx.query && ctx.query.unit) {
    const unit = (process.units || []).find((u) => u.code === ctx.query.unit);
    if (unit) openUnit(unit);
  }

  ctx.every(5000, refreshLatest);
  ctx.every(30000, refreshProcess);
  ctx.subscribe((evt) => {
    if (evt && evt.type === 'telemetry' && Date.now() - (lastUpdateAt ? lastUpdateAt.getTime() : 0) > 2500) refreshLatest();
  });

  /* ---------------- 顶部摘要 ---------------- */
  function sequenceUnits() {
    return SEQUENCE.map((code) => {
      const unit = (process.units || []).find((u) => u.code === code);
      if (unit) return unit;
      const seq = (process.sequence || []).find((s) => s.code === code) || { code, name: code, short: code };
      return { ...seq, status: 'offline', readings: [], devices: { total: 0, installed: 0, online: 0 }, alarms: 0 };
    });
  }

  function summaryStrip() {
    const counts = { normal: 0, attention: 0, alarm: 0, offline: 0, stale: 0, 'no-instrument': 0 };
    sequenceUnits().forEach((u) => { counts[u.status] = (counts[u.status] || 0) + 1; });
    const wrap = ui.el('section', { class: 'proc-summary' });
    const stats = [
      ['单元', `${sequenceUnits().length} 个`],
      ['正常', String(counts.normal)],
      ['关注', String(counts.attention || 0)],
      ['告警', String(counts.alarm || 0)],
      ['无在线仪表', String(counts['no-instrument'] || 0)],
    ];
    stats.forEach(([label, value]) => {
      wrap.appendChild(ui.el('div', { class: 'summary-item' },
        ui.el('span', { class: 'summary-k' }, label),
        ui.el('span', { class: 'summary-v' }, value),
      ));
    });
    wrap.appendChild(ui.el('div', { class: 'summary-item' },
      ui.el('span', { class: 'summary-k' }, '联锁状态'),
      ui.el('span', { class: 'summary-v', 'data-role': 'interlock-count' }, '判定中…'),
    ));
    return wrap;
  }

  /* ---------------- 工艺图 ---------------- */
  function diagramCard() {
    const wrap = ui.el('div', { class: 'proc-canvas' });
    const svg = svgNode('svg', {
      class: 'proc-svg',
      viewBox: `0 0 ${CANVAS.w} ${CANVAS.h}`,
      preserveAspectRatio: 'xMidYMid meet',
      role: 'group',
      'aria-label': '11 单元工艺流程动态图',
    });
    svg.appendChild(svgNode('rect', { x: 0, y: 0, width: CANVAS.w, height: CANVAS.h, class: 'proc-bg' }));

    /* 进水 */
    const inlet = svgNode('g', { class: 'io-label' });
    inlet.appendChild(flowArrow(4, CANVAS.ys[0] + 59, CANVAS.xs[0] - 4, CANVAS.ys[0] + 59));
    const inletText = svgNode('text', { x: 6, y: CANVAS.ys[0] + 50, class: 'io-text' });
    inletText.textContent = '进水';
    inlet.appendChild(inletText);
    svg.appendChild(inlet);

    /* 行内与蛇形连接箭头 */
    const row1 = ['raw', 'grit', 'primary', 'dist1'];
    for (let i = 0; i < row1.length - 1; i += 1) {
      const a = center(POS[row1[i]]);
      const b = center(POS[row1[i + 1]]);
      svg.appendChild(flowArrow(a.right, a.y, b.left, b.y));
    }
    const dist1 = center(POS.dist1);
    const baf = center(POS.baf);
    svg.appendChild(flowArrow(dist1.x, dist1.bottom, baf.x, baf.top));

    const row2 = ['baf', 'pac', 'pam', 'dist2'];
    for (let i = 0; i < row2.length - 1; i += 1) {
      const a = center(POS[row2[i]]);
      const b = center(POS[row2[i + 1]]);
      svg.appendChild(flowArrow(a.left, a.y, b.right, b.y));
    }
    const dist2 = center(POS.dist2);
    const sulfur = center(POS.sulfur);
    svg.appendChild(flowArrow(dist2.x, dist2.bottom, sulfur.x, sulfur.top));

    const row3 = ['sulfur', 'clear', 'waste'];
    for (let i = 0; i < row3.length - 1; i += 1) {
      const a = center(POS[row3[i]]);
      const b = center(POS[row3[i + 1]]);
      svg.appendChild(flowArrow(a.right, a.y, b.left, b.y, i === 1));
    }

    /* 出水 */
    const clear = center(POS.clear);
    svg.appendChild(flowArrow(clear.x, clear.bottom, clear.x, CANVAS.h - 20));
    const outText = svgNode('text', { x: clear.x + 12, y: CANVAS.h - 26, class: 'io-text' });
    outText.textContent = '出水';
    svg.appendChild(outText);

    /* BAF → 废水箱 反冲洗废水虚线 */
    const bafC = center(POS.baf);
    const wasteC = center(POS.waste);
    const dash = svgNode('g', { class: 'recycle-line' });
    dash.appendChild(svgNode('path', {
      d: `M${bafC.x},${bafC.bottom} V${bafC.bottom + 18} H${wasteC.x} V${wasteC.top}`,
      class: 'arrow-line dashed',
      fill: 'none',
    }));
    dash.appendChild(svgNode('polygon', {
      points: `${wasteC.x},${wasteC.top} ${wasteC.x - 4},${wasteC.top - 8} ${wasteC.x + 4},${wasteC.top - 8}`,
      class: 'arrow-head',
    }));
    const dashLabel = svgNode('text', { x: (bafC.x + wasteC.x) / 2, y: bafC.bottom + 12, class: 'dash-label' });
    dashLabel.textContent = '反冲洗废水';
    dash.appendChild(dashLabel);
    svg.appendChild(dash);

    sequenceUnits().forEach((unit, idx) => {
      const pos = POS[unit.code];
      if (pos) svg.appendChild(unitNode(unit, idx + 1, pos));
    });

    wrap.appendChild(svg);
    const legend = ui.el('div', { class: 'proc-legend' },
      ui.el('span', { class: 'legend-item' }, ui.dot('good'), '正常'),
      ui.el('span', { class: 'legend-item' }, ui.dot('warn'), '关注'),
      ui.el('span', { class: 'legend-item' }, ui.dot('bad'), '告警'),
      ui.el('span', { class: 'legend-item' }, ui.dot('muted'), '无在线仪表 / 离线'),
      ui.el('span', { class: 'legend-item muted' }, '虚线：反冲洗废水或非主流程'),
      ui.el('span', { class: 'legend-item muted' }, '点击单元查看设备、点位、告警与联锁'),
    );
    return ui.card({
      title: '动态工艺图',
      sub: process.meta && process.meta.note ? process.meta.note : '',
      actions: ui.el('button', { class: 'btn ghost sm', type: 'button', onclick: () => ctx.refresh() }, ui.icon('refresh'), '重绘'),
      body: ui.el('div', {}, wrap, legend),
      flush: true,
      class: 'diagram-card',
    });
  }

  function unitNode(unit, order, pos) {
    const x = CANVAS.xs[pos[0]];
    const y = CANVAS.ys[pos[1]];
    const w = CANVAS.boxW;
    const h = CANVAS.boxH;
    const group = svgNode('g', {
      class: `unit status-${unit.status}`,
      'data-code': unit.code,
      tabindex: '0',
      role: 'button',
      'aria-label': `${unit.name}，${UNIT_STATUS_TEXT[unit.status] || unit.status}，点击查看单元详情`,
    });
    group.appendChild(svgNode('rect', { x, y, width: w, height: h, rx: 10, class: 'u-box' }));
    group.appendChild(svgNode('rect', { x, y: y, width: 6, height: h, rx: 3, class: 'u-accent' }));

    const orderText = svgNode('text', { x: x + 16, y: y + 18, class: 'u-order' });
    orderText.textContent = String(order).padStart(2, '0');
    group.appendChild(orderText);

    const nameText = svgNode('text', { x: x + 16, y: y + 39, class: 'u-name' });
    nameText.textContent = unit.short || unit.name;
    group.appendChild(nameText);

    const statusText = svgNode('text', { x: x + w - 14, y: y + 18, 'text-anchor': 'end', class: `u-status ${unit.status}` });
    statusText.textContent = `${UNIT_STATUS_TEXT[unit.status] || unit.status}${unit.alarms ? ` ${unit.alarms}` : ''}`;
    group.appendChild(statusText);

    group.appendChild(svgNode('line', { x1: x + 14, x2: x + w - 14, y1: y + 48, y2: y + 48, class: 'u-divider' }));

    const readings = unit.readings || [];
    if (!readings.length) {
      const noInst = svgNode('text', { x: x + 16, y: y + 70, class: 'u-read muted' });
      noInst.textContent = '图纸未布设在线仪表';
      group.appendChild(noInst);
      const pending = svgNode('text', { x: x + 16, y: y + 90, class: 'u-read muted' });
      pending.textContent = '该单元指标：待接入';
      group.appendChild(pending);
    } else {
      readings.slice(0, 2).forEach((reading, i) => {
        const node = svgNode('text', { x: x + 16, y: y + 70 + i * 20, class: 'u-read', 'data-live': reading.code });
        node.textContent = readingLine(reading);
        group.appendChild(node);
        liveTargets.push({ code: reading.code, node, formatter: readingLine });
      });
    }

    const devices = (process.devices || []).filter((d) => d.unitCode === unit.code);
    const markers = devices
      .filter((d) => ['PUMP', 'BLOWER', 'DOSER'].includes(d.type))
      .slice(0, 2)
      .map((d) => `${DEVICE_TYPE[d.type] || d.type}${d.status || ''}`)
      .join(' · ');
    let metaText = `设备 ${unit.devices ? `${unit.devices.online}/${unit.devices.total}` : '—'}`;
    if (markers) metaText += ` · ${markers}`;
    if (unit.code === 'baf' || unit.code === 'sulfur') {
      const code = unit.code === 'baf' ? 'BAF.BW.STATE' : 'SULFUR.BW.STATE';
      const state = Number((readingMap.get(code) || {}).value || 0);
      metaText += ` · BW ${BW_NAMES[state] || '空闲'}`;
    }
    const meta = svgNode('text', { x: x + 16, y: y + h - 12, class: 'u-meta' });
    meta.textContent = metaText;
    group.appendChild(meta);

    const title = svgNode('title');
    title.textContent = `${unit.name}｜${UNIT_STATUS_TEXT[unit.status] || unit.status}｜点击查看设备、点位、告警与联锁`;
    group.appendChild(title);

    const svgRoot = group.ownerSVGElement || group.closest('svg');
    const open = () => {
      if (svgRoot) svgRoot.querySelectorAll('g.unit.selected').forEach((g) => g.classList.remove('selected'));
      group.classList.add('selected');
      openUnit(unit);
    };
    group.addEventListener('click', open);
    group.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); }
    });

    statusRefs.set(unit.code, { group, statusText, meta, unit });
    return group;
  }

  /* ---------------- 反冲洗卡 ---------------- */
  function buildBackwashCard() {
    const body = ui.el('div', { class: 'bw-grid' });
    ['baf', 'sulfur'].forEach((key) => {
      const name = key === 'baf' ? 'BAF 曝气生物滤池' : '硫自养反硝化池';
      const code = key === 'baf' ? 'BAF.BW.STATE' : 'SULFUR.BW.STATE';
      const badgeNode = ui.el('span', { class: 'bw-state' });
      const chips = ui.el('div', { class: 'bw-phases' });
      const chipNodes = BW_PHASES.map((phase) => {
        const chip = ui.el('span', { class: 'bw-phase' },
          ui.el('span', { class: 'bw-phase-name' }, phase.name),
          ui.el('span', { class: 'bw-phase-sec' }, `${phase.sec}s`),
        );
        chips.appendChild(chip);
        return chip;
      });
      const bar = ui.el('div', { class: 'bw-bar' }, ui.el('i', { style: { width: '0%' } }));
      const note = ui.el('div', { class: 'bw-note muted small' });
      const valueNode = ui.el('span', { class: 'bw-value mono' }, '—');
      const row = ui.el('div', { class: 'bw-row' },
        ui.el('div', { class: 'bw-head' },
          ui.el('span', { class: 'bw-name' }, name),
          badgeNode,
          valueNode,
        ),
        chips,
        bar,
        note,
      );
      body.appendChild(row);
      bwRefs.set(key, { code, badgeNode, chipNodes, bar: bar.firstChild, note, valueNode });
    });
    return ui.card({
      title: '反冲洗状态',
      sub: '阶段时长：气冲 30s → 气水联合 90s → 漂洗 45s；开始/中止请在远程控制页操作',
      actions: ctx.user && ctx.user.perms && ctx.user.perms.includes('control')
        ? ui.el('a', { class: 'btn ghost sm', href: '#/control' }, '远程控制')
        : ui.badge('无控制权限', 'muted', '仅展示反冲洗阶段'),
      body,
      class: 'bw-card',
    });
  }

  function paintBackwash() {
    ['baf', 'sulfur'].forEach((key) => {
      const ref = bwRefs.get(key);
      const rec = bwObs[key];
      const state = Number((latestMap.get(ref.code) || readingMap.get(ref.code) || {}).value || 0);
      if (state !== rec.state) {
        rec.state = state;
        rec.since = state ? Date.now() : 0;
      }
      const phase = BW_PHASES.find((p) => p.state === state);
      ui.mount(ref.badgeNode, ui.badge(state ? '进行中' : '空闲', state ? 'info' : 'muted'));
      ref.valueNode.textContent = BW_NAMES[state] || '—';
      ref.chipNodes.forEach((chip, i) => {
        chip.classList.toggle('active', state === BW_PHASES[i].state);
        chip.classList.toggle('done', state > BW_PHASES[i].state);
      });
      if (phase) {
        const elapsed = rec.since ? Math.max(0, (Date.now() - rec.since) / 1000) : 0;
        const pct = Math.min(100, (elapsed / phase.sec) * 100);
        ref.bar.style.width = `${pct.toFixed(1)}%`;
        ref.note.textContent = `进行中：${phase.name} · 已进行 ${Math.round(elapsed)}s / 预计 ${phase.sec}s（按本页观察计时，打开页面前的进度未计入）`;
      } else {
        ref.bar.style.width = '0%';
        ref.note.textContent = '空闲：当前没有进行中的反冲洗；阶段时长按方案设置，进度按本页观察估算。';
      }
    });
  }

  /* ---------------- 单元卡片网格 ---------------- */
  function unitCardGrid() {
    const grid = ui.el('div', { class: 'unit-grid' });
    sequenceUnits().forEach((unit, idx) => {
      const card = ui.el('button', {
        class: `unit-card status-${unit.status}`,
        type: 'button',
        onclick: () => openUnit(unit),
        'aria-label': `${unit.name}，${UNIT_STATUS_TEXT[unit.status] || unit.status}，查看详情`,
      });
      const readsBox = ui.el('div', { class: 'unit-card-reads' });
      if (!unit.readings || !unit.readings.length) {
        readsBox.appendChild(ui.el('div', { class: 'unit-card-read muted' }, '图纸未布设在线仪表 · 待接入'));
      } else {
        unit.readings.slice(0, 4).forEach((reading) => {
          const valueNode = ui.el('span', { class: 'unit-card-value mono' });
          valueNode.textContent = shortReading(reading);
          readsBox.appendChild(ui.el('div', { class: 'unit-card-read' },
            ui.el('span', { class: 'unit-card-label' }, SHORT_READING[reading.code] || reading.name),
            valueNode,
          ));
          liveTargets.push({ code: reading.code, node: valueNode, formatter: shortReading });
        });
      }
      card.appendChild(ui.el('div', { class: 'unit-card-head' },
        ui.el('span', { class: 'unit-card-order' }, String(idx + 1).padStart(2, '0')),
        ui.el('span', { class: 'unit-card-name' }, unit.short || unit.name),
        ui.el('span', { class: 'unit-card-status' }, unitStatusBadge(unit.status)),
      ));
      card.appendChild(readsBox);
      card.appendChild(ui.el('div', { class: 'unit-card-foot' },
        ui.el('span', {}, `设备 ${unit.devices ? `${unit.devices.online}/${unit.devices.total}` : '—'} 在线`),
        ui.el('span', { class: unit.alarms ? 'text-bad' : 'muted' }, `告警 ${unit.alarms || 0}`),
      ));
      grid.appendChild(card);
      const ref = statusRefs.get(unit.code);
      if (ref) {
        ref.card = card;
        ref.cardStatusSlot = card.querySelector('.unit-card-status');
        ref.cardFootAlarm = card.querySelector('.unit-card-foot span:last-child');
      }
    });
    return ui.card({
      title: '单元卡片',
      sub: '按工艺顺序列出 11 个单元；状态与关键读数与上方工艺图联动刷新',
      body: grid,
      flush: true,
      class: 'unit-cards',
    });
  }

  /* ---------------- 联锁说明 ---------------- */
  function interlockSpecs() {
    return (process.meta && process.meta.interlock) || [];
  }

  function evaluateInterlock(spec) {
    const reading = latestMap.get(spec.from) || readingMap.get(spec.from);
    const value = reading && isNum(reading.value) ? Number(reading.value) : null;
    if (value === null) {
      return { tone: 'muted', badge: ui.badge('数据缺失', 'muted'), text: '当前无可信实时值，无法判定；请先检查数据链路。', valueText: '—' };
    }
    const vText = `${ui.fmt.auto(value)}${reading.unit ? ` ${reading.unit}` : ''}`;
    if (spec.from === 'RAW.LVL') {
      const ok = value >= 5;
      return {
        tone: ok ? 'good' : 'bad',
        badge: ui.badge(ok ? '满足' : '联锁生效', ok ? 'good' : 'bad'),
        text: ok ? `液位 ${vText} ≥ 5%，允许启动提升泵。` : `液位 ${vText} < 5%，禁止启动提升泵。`,
        valueText: vText,
      };
    }
    if (spec.from === 'BAF.DO') {
      const ok = value >= 2.5;
      return {
        tone: ok ? 'good' : 'warn',
        badge: ui.badge(ok ? '满足' : '联锁生效', ok ? 'good' : 'warn'),
        text: ok ? `DO ${vText} ≥ 2.5 mg/L，允许停风机。` : `DO ${vText} < 2.5 mg/L，禁止停风机。`,
        valueText: vText,
      };
    }
    if (spec.from === 'WASTE.LVL') {
      const ok = value < 92;
      return {
        tone: ok ? 'good' : 'bad',
        badge: ui.badge(ok ? '满足' : '联锁生效', ok ? 'good' : 'bad'),
        text: ok ? `废水箱液位 ${vText} < 92%，允许启动反冲洗。` : `废水箱液位 ${vText} ≥ 92%，禁止启动反冲洗。`,
        valueText: vText,
      };
    }
    if (spec.from === 'SULFUR.P') {
      const ok = value <= 0.6;
      return {
        tone: ok ? 'good' : 'warn',
        badge: ui.badge(ok ? '满足' : '提示', ok ? 'good' : 'warn'),
        text: ok ? `反冲洗管路压力 ${vText} ≤ 0.6 MPa，压力正常。` : `反冲洗管路压力 ${vText} > 0.6 MPa，请检查阀门与管路。`,
        valueText: vText,
      };
    }
    return {
      tone: 'muted',
      badge: ui.badge('仅展示', 'muted'),
      text: `当前值 ${vText}；该联锁由控制页执行，本页仅展示判定。`,
      valueText: vText,
    };
  }

  function interlockCard() {
    const list = ui.el('div', { class: 'interlock-list' });
    const specs = interlockSpecs();
    if (!specs.length) {
      list.appendChild(ui.empty('暂无联锁规则', '服务端未返回联锁说明。', 'warning'));
    }
    specs.forEach((spec) => {
      const verdict = ui.el('div', { class: 'interlock-verdict' });
      const valueNode = ui.el('span', { class: 'interlock-value mono' }, '—');
      const row = ui.el('div', { class: 'interlock-row' },
        ui.el('div', { class: 'interlock-main' },
          ui.el('div', { class: 'interlock-title' }, spec.text),
          ui.el('div', { class: 'interlock-sub muted small' }, `触发点位：${spec.from}`),
        ),
        valueNode,
        verdict,
      );
      list.appendChild(row);
      interlockRefs.set(spec.from, { row, verdict, valueNode, spec });
    });
    const body = ui.el('div', {},
      list,
      ui.el('p', { class: 'muted small interlock-note' },
        '联锁为工艺保护逻辑：本页只根据当前实时值展示“是否满足条件”，不执行启停操作；需要操作时请前往远程控制页（需 control 权限）。'),
    );
    return ui.card({
      title: '联锁说明',
      sub: '按方案设定的三条工艺联锁 + 反冲洗压力提示；判定随实时值刷新',
      actions: ui.badge('只读判定', 'muted'),
      body,
      class: 'interlock-card',
    });
  }

  function paintInterlocks() {
    let active = 0;
    interlockRefs.forEach((ref) => {
      const result = evaluateInterlock(ref.spec);
      ui.mount(ref.verdict, result.badge);
      ref.valueNode.textContent = result.valueText;
      ref.row.className = `interlock-row tone-${result.tone}`;
      ref.row.title = result.text;
      ref.row.setAttribute('aria-label', result.text);
      if (result.tone === 'bad' || result.tone === 'warn') active += 1;
    });
    const counter = page.querySelector('.proc-summary [data-role="interlock-count"]');
    if (counter) counter.textContent = active ? `${active} 条需关注` : '全部满足';
  }

  /* ---------------- 单元抽屉 ---------------- */
  function openUnit(unit) {
    const devices = (process.devices || []).filter((d) => d.unitCode === unit.code);
    const alarms = (process.alarms || []).filter((a) => a.unitCode === unit.code);
    const readings = (unit.readings || []).map((r) => latestMap.get(r.code) || r);
    const specs = interlockSpecs().filter((spec) => (unit.readings || []).some((r) => r.code === spec.from));
    const seqIdx = (process.sequence || []).findIndex((s) => s.code === unit.code);

    const section = (title, content) => ui.el('section', { class: 'drawer-section' },
      ui.el('h4', { class: 'drawer-h' }, title),
      content,
    );

    const body = ui.el('div', { class: 'unit-drawer' },
      ui.kv([
        ['单元状态', unitStatusBadge(unit.status)],
        ['工艺顺序', seqIdx >= 0 ? `第 ${seqIdx + 1} 单元` : '—'],
        ['设备在线', unit.devices ? `${unit.devices.online}/${unit.devices.total}` : '—'],
        ['活动告警', String(alarms.length)],
        ['图纸说明', unit.note || '—'],
      ]),
      section('设备', devices.length
        ? ui.table({
          columns: [
            { key: 'name', label: '设备' },
            { key: 'code', label: '编码', cls: 'mono' },
            { key: 'type', label: '类型', render: (d) => DEVICE_TYPE[d.type] || d.type },
            { key: 'status', label: '状态', render: (d) => deviceBadge(d.status) },
            { key: 'online', label: '在线', render: (d) => ui.badge(d.online ? '在线' : '离线', d.online ? 'good' : 'bad') },
          ],
          rows: devices,
          emptyText: '该单元无设备',
        })
        : ui.empty('该单元未安装设备', '按方案图纸，本单元不设在线设备。', 'device')),
      section('实时点位', readings.length
        ? ui.table({
          columns: [
            { key: 'name', label: '点位' },
            { key: 'code', label: '编码', cls: 'mono' },
            { key: 'value', label: '实时值', render: (r) => readingText(r) },
            { key: 'quality', label: '质量', render: (r) => ui.qualityBadge(r.quality) },
            { key: 't', label: '数据时间', cls: 'mono', render: (r) => ui.fmt.timeSec(r.t) },
            { key: 'source', label: '来源', render: (r) => ui.el('span', { class: 'small' }, sourceLabel(r)) },
          ],
          rows: readings,
          emptyText: '暂无点位',
          emptySub: '该单元没有在线点位，指标标注为待接入。',
        })
        : ui.empty('暂无在线点位', '图纸未布设在线仪表，指标标注为待接入。', 'monitor')),
      section('告警', alarms.length
        ? ui.table({
          columns: [
            { key: 'levelName', label: '级别', render: (a) => ui.levelBadge(a.level) },
            { key: 'title', label: '告警' },
            { key: 'value', label: '触发值', render: (a) => `${ui.fmt.num(a.value, 1)}${a.unit ? ` ${a.unit}` : ''}` },
            { key: 'durationMin', label: '持续', render: (a) => (isNum(a.durationMin) ? `${a.durationMin} 分钟` : '—') },
            { key: 'status', label: '状态', render: (a) => ui.alarmStatus(a.status) },
          ],
          rows: alarms,
          emptyText: '该单元暂无活动告警',
        })
        : ui.el('div', { class: 'muted small' }, '该单元当前无活动告警。')),
      section('联锁', specs.length
        ? ui.el('div', { class: 'interlock-mini' }, specs.map((spec) => ui.el('div', { class: 'interlock-mini-row' },
          ui.el('span', { class: 'mono small' }, spec.from),
          ui.el('span', { class: 'small' }, spec.text),
        )))
        : ui.el('div', { class: 'muted small' }, '该单元未配置专属联锁说明。')),
      section('快捷入口',
        ui.el('a', { class: 'btn ghost sm', href: `#/monitor?unit=${encodeURIComponent(unit.code)}` }, ui.icon('monitor'), '在实时监测中查看该单元点位')),
    );

    ui.drawer({
      title: unit.name,
      sub: `单元 ${unit.code} · ${UNIT_STATUS_TEXT[unit.status] || unit.status} · 数据来自内置模拟网关`,
      body,
    });
  }

  /* ---------------- 数据刷新 ---------------- */
  async function refreshLatest() {
    if (updating) return;
    updating = true;
    try {
      const latest = await api.get('/api/telemetry/latest', { siteId });
      (latest.items || []).forEach((r) => latestMap.set(r.code, r));
      lastUpdateAt = new Date();
      updateFailed = false;
      paintLive();
      paintBackwash();
      paintInterlocks();
      paintUpdated();
    } catch (err) {
      updateFailed = true;
      paintUpdated();
      console.warn('[process] 实时数据刷新失败', err);
    } finally {
      updating = false;
    }
  }

  function paintLive() {
    liveTargets.forEach((target) => {
      const reading = latestMap.get(target.code);
      if (!reading) return;
      target.node.textContent = target.formatter(reading);
      if (target.node.classList) target.node.classList.toggle('is-stale', Boolean(reading.stale));
    });
    statusRefs.forEach((ref) => {
      const unit = (process.units || []).find((u) => u.code === ref.unit.code);
      if (!unit) return;
      ref.unit.devices = unit.devices;
      const devices = (process.devices || []).filter((d) => d.unitCode === unit.code);
      const markers = devices
        .filter((d) => ['PUMP', 'BLOWER', 'DOSER'].includes(d.type))
        .slice(0, 2)
        .map((d) => `${DEVICE_TYPE[d.type] || d.type}${d.status || ''}`)
        .join(' · ');
      let text = `设备 ${unit.devices ? `${unit.devices.online}/${unit.devices.total}` : '—'}`;
      if (markers) text += ` · ${markers}`;
      if (unit.code === 'baf' || unit.code === 'sulfur') {
        const code = unit.code === 'baf' ? 'BAF.BW.STATE' : 'SULFUR.BW.STATE';
        const state = Number((latestMap.get(code) || readingMap.get(code) || {}).value || 0);
        text += ` · BW ${BW_NAMES[state] || '空闲'}`;
      }
      if (ref.meta) ref.meta.textContent = text;
    });
  }

  async function refreshProcess() {
    try {
      const fresh = await api.get('/api/sites/' + encodeURIComponent(siteId) + '/process');
      process = fresh;
      seedReadings(process);
      sequenceUnits().forEach((unit) => {
        const ref = statusRefs.get(unit.code);
        if (!ref) return;
        ref.unit = unit;
        ref.group.setAttribute('class', `unit status-${unit.status}`);
        ref.statusText.setAttribute('class', `u-status ${unit.status}`);
        ref.statusText.textContent = `${UNIT_STATUS_TEXT[unit.status] || unit.status}${unit.alarms ? ` ${unit.alarms}` : ''}`;
        if (ref.card) {
          ref.card.className = `unit-card status-${unit.status}`;
          if (ref.cardStatusSlot) ui.mount(ref.cardStatusSlot, unitStatusBadge(unit.status));
          if (ref.cardFootAlarm) {
            ref.cardFootAlarm.textContent = `告警 ${unit.alarms || 0}`;
            ref.cardFootAlarm.className = unit.alarms ? 'text-bad' : 'muted';
          }
        }
      });
      paintLive();
      paintInterlocks();
    } catch (err) {
      console.warn('[process] 工艺状态刷新失败', err);
    }
  }

  function paintUpdated() {
    if (!updatedNode) return;
    if (!lastUpdateAt && !updateFailed) return;
    if (updateFailed) {
      updatedNode.textContent = `刷新失败，显示上次数据${lastUpdateAt ? `（${ui.fmt.timeSec(lastUpdateAt)}）` : ''}`;
      updatedNode.classList.add('text-warn');
    } else {
      updatedNode.textContent = `最后更新 ${ui.fmt.timeSec(lastUpdateAt || new Date())} · 数据来源：内置模拟网关`;
      updatedNode.classList.remove('text-warn');
    }
  }
}

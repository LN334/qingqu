/* 综合驾驶舱：站点总览 · 8 项 KPI · 11 单元流程缩略图 · 24h 趋势 · 告警/工单/诊断/健康/通知 */
import * as ui from '../ui.js';

export const meta = { title: '综合驾驶舱' };

const TREND_METRICS = [
  { code: 'RAW.LVL', label: '原料罐液位', unit: '%', digits: 1 },
  { code: 'BAF.DO', label: 'BAF 溶解氧', unit: 'mg/L', digits: 2 },
  { code: 'SULFUR.PH', label: '硫自养池 pH', unit: 'pH', digits: 2 },
  { code: 'FLOW.IN', label: '进水流量', unit: 'm³/h', digits: 3 },
];

const UNIT_ORDER = ['raw', 'grit', 'primary', 'dist1', 'baf', 'pac', 'pam', 'dist2', 'sulfur', 'clear', 'waste'];

/* 每个单元在缩略条上优先展示的点位 */
const READ_PLAN = {
  raw: ['RAW.LVL', 'RAW.PUMP.RUN'],
  grit: [],
  primary: [],
  dist1: ['FLOW.IN'],
  baf: ['BAF.DO', 'BAF.PH'],
  pac: ['PAC.LEVEL', 'PAC.DOSE'],
  pam: ['PAM.LEVEL', 'PAM.DOSE'],
  dist2: [],
  sulfur: ['SULFUR.PH', 'SULFUR.DO'],
  clear: ['CLEAR.LVL'],
  waste: ['WASTE.LVL'],
  plant: ['POWER.KW', 'ENERGY.KWH'],
};

const BW_PHASES = ['空闲', '气冲', '气水联合', '漂洗'];

/* 缩略条上的短标签，避免 96px 卡片里文字被截断 */
const SHORT_LABEL = {
  'RAW.LVL': '液位', 'RAW.PUMP.RUN': '提升泵', 'FLOW.IN': '流量',
  'BAF.PH': 'pH', 'BAF.DO': 'DO', 'PAC.LEVEL': '药箱', 'PAC.DOSE': '投加',
  'PAM.LEVEL': '药箱', 'PAM.DOSE': '投加', 'SULFUR.PH': 'pH', 'SULFUR.DO': 'DO',
  'CLEAR.LVL': '液位', 'WASTE.LVL': '液位', 'POWER.KW': '功率', 'ENERGY.KWH': '累计电量',
};

function svgNode(tag, attrs = {}) {
  const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === undefined || v === null) continue;
    node.setAttribute(k, v);
  }
  return node;
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

/* 状态点颜色 */
function dotKind(status) {
  if (status === 'normal') return 'good';
  if (status === 'alarm') return 'bad';
  if (status === 'attention' || status === 'stale') return 'warn';
  return 'muted';
}

function isNum(v) { return v !== null && v !== undefined && Number.isFinite(Number(v)); }

/* 把 reading 转成一行可读文本；state 点位按业务含义翻译 */
function readingText(reading, digits = 1) {
  if (!reading) return '—';
  if (reading.value === null || reading.value === undefined) return '待接入';
  const raw = Number(reading.value);
  if (reading.type === 'state') {
    const code = reading.code || '';
    if (/\.BW\.STATE$/.test(code)) return BW_PHASES[raw] || '—';
    if (/\.FAULT$/.test(code)) return raw ? '故障' : '正常';
    if (/\.MODE$/.test(code)) return raw ? '自动' : '手动';
    if (/\.RUN$/.test(code)) return raw ? '运行' : '停止';
    return raw ? '运行' : '停止';
  }
  const digitsUsed = reading.type === 'counter' ? 0 : (Math.abs(raw) < 1 ? 2 : digits);
  const text = reading.type === 'counter' ? ui.fmt.int(raw) : ui.fmt.num(raw, digitsUsed);
  return reading.unit ? `${text} ${reading.unit}` : text;
}

function readingWithName(reading, digits = 1) {
  const name = SHORT_LABEL[reading.code] || reading.name.replace(/^(BAF|PAC|PAM|硫自养池)\s*/, '');
  return `${name} ${readingText(reading, digits)}`;
}

function pickReadings(unit) {
  const plan = READ_PLAN[unit.code] || [];
  const byCode = new Map(unit.readings.map((r) => [r.code, r]));
  return plan.map((code) => byCode.get(code)).filter(Boolean);
}

function badgeLevel(level, levelName) {
  const kind = level === 'critical' ? 'bad' : level === 'major' ? 'warn' : level === 'minor' ? 'info' : 'muted';
  return ui.badge(levelName || level, kind);
}

function diagnosisBadge(level) {
  if (level === 'high') return ui.badge('风险', 'bad');
  if (level === 'medium') return ui.badge('关注', 'warn');
  return ui.badge('提示', 'info');
}

function priorityBadge(priority, name) {
  const kind = priority === 'urgent' ? 'bad' : priority === 'high' ? 'warn' : 'muted';
  return ui.badge(name || priority, kind);
}

/* 通知里的站内链接映射到前端 hash 路由 */
function notificationHref(link) {
  if (!link || typeof link !== 'string') return null;
  if (link.startsWith('/lab')) return '#/quality';
  if (link.startsWith('/admin/backup')) return '#/admin?tab=backup';
  if (link.startsWith('/')) return `#${link}`;
  return null;
}

/* 在 chartLine 生成的图之上叠加告警阈值参考线（同一 viewBox，绝对定位对齐） */
function overlayThresholds(host, points, rules, height) {
  const drawRules = rules.filter((r) => isNum(r.low) || isNum(r.high));
  if (!drawRules.length || !points.length) return;
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

  const svg = svgNode('svg', {
    class: 'ref-layer',
    viewBox: `0 0 ${W} ${H}`,
    preserveAspectRatio: 'none',
    'aria-hidden': 'true',
  });
  svg.style.cssText = `position:absolute;top:0;left:0;width:100%;height:${H}px;pointer-events:none;overflow:visible`;
  const yOf = (v) => pad.t + ih - ((v - lo) / (hi - lo)) * ih;

  for (const rule of drawRules) {
    const marks = [];
    if (isNum(rule.low)) marks.push({ v: Number(rule.low), label: `下限 ${ui.fmt.auto(rule.low)}` });
    if (isNum(rule.high)) marks.push({ v: Number(rule.high), label: `上限 ${ui.fmt.auto(rule.high)}` });
    for (const mark of marks) {
      const y = yOf(mark.v);
      if (y < pad.t || y > pad.t + ih) continue;
      svg.appendChild(svgNode('line', {
        x1: pad.l, x2: W - pad.r, y1: y, y2: y,
        class: 'ref-line',
        stroke: rule.level === 'major' || rule.level === 'critical' ? '#b32d22' : '#c0762a',
      }));
      const label = svgNode('text', {
        x: W - pad.r - 4, y: y - 4, 'text-anchor': 'end', class: 'ref-label',
        fill: rule.level === 'major' || rule.level === 'critical' ? '#b32d22' : '#c0762a',
      });
      label.textContent = `${mark.label}${rule.unit ? ` ${rule.unit}` : ''}`;
      svg.appendChild(label);
    }
  }
  host.appendChild(svg);
}

export async function render(root, ctx) {
  const { api, siteId } = ctx;
  root.classList.add('p-dashboard');
  ctx.setTitle(meta.title);

  const actions = [
    ui.el('button', { class: 'btn ghost', type: 'button', onclick: () => ctx.refresh() },
      ui.icon('refresh'), '刷新数据'),
    ui.el('a', { class: 'btn primary', href: '#/monitor' }, ui.icon('monitor'), '实时监测'),
  ];

  ui.mount(root,
    ui.pageHead('综合驾驶舱', '站点运行总览与关键指标；所有数值均来自平台接口，人工化验数据单独标注。', actions),
    ui.el('div', { class: 'dash-body' }, ui.skeleton(2, 'block')),
  );

  let data;
  try {
    const [overview, rules, process, notifications, diagnosis] = await Promise.all([
      api.get('/api/overview', { siteId }),
      api.get('/api/alarm-rules', { siteId }).catch(() => []),
      api.get('/api/sites/' + encodeURIComponent(siteId) + '/process').catch(() => null),
      api.getFull('/api/notifications', { siteId }).catch(() => null),
      api.get('/api/diagnosis', { siteId }).catch(() => []),
    ]);
    data = { overview, rules: rules || [], process, notifications, diagnosis: diagnosis || [] };
  } catch (err) {
    ui.mount(root, ui.pageHead('综合驾驶舱', '站点运行总览', actions),
      ui.card({ body: ui.errorState(err, () => ctx.refresh()) }));
    return;
  }

  const { overview, rules, process, notifications, diagnosis } = data;
  const trendCache = new Map();
  Object.entries(overview.trends || {}).forEach(([code, t]) => trendCache.set(code, t));

  const page = ui.el('div', { class: 'dash-body' });
  ui.mount(root,
    ui.pageHead('综合驾驶舱', '站点运行总览与关键指标；所有数值均来自平台接口，人工化验数据单独标注。', actions),
    page,
  );

  page.appendChild(siteStrip(overview));
  page.appendChild(kpiGrid(overview, process));
  page.appendChild(processCard(overview));

  const trendCard = buildTrendCard();
  const diagCard = diagnosisCard(diagnosis);
  page.appendChild(ui.el('div', { class: 'dash-main' }, trendCard.node, diagCard));

  page.appendChild(ui.el('div', { class: 'dash-lists' },
    alarmCard(overview),
    workorderCard(overview),
    healthCard(overview),
  ));
  page.appendChild(noticeCard(overview, notifications));

  /* 首次绘制趋势图（需要节点已挂载，chartLine 依赖容器宽度） */
  trendCard.draw();

  /* ---------------- 站点总览条 ---------------- */
  function siteStrip(ov) {
    const site = ov.site || {};
    const health = ov.dataHealth || {};
    const statusText = site.status === 'online' ? '运行中' : '离线';
    const strip = ui.el('section', { class: 'site-strip' },
      ui.el('div', { class: 'site-main' },
        ui.el('div', { class: 'site-title' },
          ui.el('span', { class: 'site-name' }, site.name || '未选择站点'),
          ui.badge(statusText, site.status === 'online' ? 'good' : 'muted'),
        ),
        ui.el('div', { class: 'site-sub' }, [site.region, site.area, site.process].filter(Boolean).join(' · ')),
      ),
      ui.el('div', { class: 'site-fact' },
        ui.el('span', { class: 'fact-k' }, '工艺能力'),
        ui.el('span', { class: 'fact-v' }, site.capacity || '—'),
        ui.el('span', { class: 'fact-n' }, site.deviceRef || ''),
      ),
      ui.el('div', { class: 'site-fact' },
        ui.el('span', { class: 'fact-k' }, '数据来源'),
        ui.el('span', { class: 'fact-v' }, '模拟网关'),
        ui.el('span', { class: 'fact-n' }, '外部接口'),
      ),
      ui.el('div', { class: 'site-fact' },
        ui.el('span', { class: 'fact-k' }, '最后更新'),
        ui.el('span', { class: 'fact-v mono' }, ui.fmt.timeSec(health.latestAt)),
        ui.el('span', { class: 'fact-n' }, `网关延迟 ${isNum(health.delaySec) ? health.delaySec + ' 秒' : '—'} · 汇总生成 ${ui.fmt.timeSec(ov.generatedAt)}`),
      ),
    );
    const wrap = ui.el('div', {});
    wrap.appendChild(strip);
    return wrap;
  }

  /* ---------------- KPI 区 ---------------- */
  function kpiGrid(ov, proc) {
    const getKpi = (key) => (ov.kpis || []).find((k) => k.key === key) || {};
    const volume = getKpi('volume');
    const energy = getKpi('energy');
    const alarms = getKpi('alarms');
    const workorders = getKpi('workorders');
    const online = getKpi('online');
    const counts = ov.alarmCounts || {};
    const woCounts = ov.workorderCounts || {};
    const latest = (ov.waterQuality && ov.waterQuality.latest) || {};
    const removal = (ov.waterQuality && ov.waterQuality.removal) || {};
    const inlet = latest.inlet && latest.inlet.metrics ? latest.inlet.metrics : {};
    const outlet = latest.outlet && latest.outlet.metrics ? latest.outlet.metrics : {};
    const res = ov.resources || {};
    const devices = (proc && proc.devices) || [];
    const running = devices.filter((d) => d.status === '运行').length;
    const devicesTotal = devices.length;
    const onlineDevices = devices.filter((d) => d.online).length;
    const vol = Number(volume.value);
    const energyIntensity = vol > 0 ? Number(energy.value) / vol : null;
    const chemicalTotal = Number(res.pacTodayL || 0) + Number(res.pamTodayL || 0);
    const chemicalIntensity = vol > 0 ? chemicalTotal / vol : null;
    const codValue = isNum(inlet.COD) && isNum(outlet.COD)
      ? `${ui.fmt.num(inlet.COD, 1)} → ${ui.fmt.num(outlet.COD, 1)}`
      : '待化验';
    const alarmsTotal = isNum(counts.active) ? counts.active : alarms.value;
    const highWos = (ov.workorders || []).filter((w) => w.priority === 'high' || w.priority === 'urgent').length;
    const alarmTone = (Number(counts.critical) || 0) > 0 || (Number(counts.major) || 0) > 0
      ? 'bad'
      : (Number(counts.minor) || 0) > 0 ? 'warn' : 'good';
    const workorderTone = highWos > 0 ? 'warn' : 'good';

    const items = [
      ui.kpi({
        label: '今日处理水量',
        value: ui.fmt.num(vol, 2),
        unit: 'm³',
        iconName: 'monitor',
      }),
      ui.kpi({
        label: 'COD（人工化验）',
        value: codValue,
        unit: 'mg/L',
        iconName: 'quality',
        delta: `进水 → 出水 · 去除率 ${isNum(removal.COD) ? ui.fmt.pct(removal.COD) : '—'} · 出水采样 ${ui.fmt.timeSec(latest.outlet && latest.outlet.sampledAt)}`,
      }),
      ui.kpi({
        label: '运行设备',
        value: `${running}/${devicesTotal || '—'}`,
        unit: '台',
        iconName: 'device',
        delta: `在线 ${onlineDevices} 台`,
      }),
      ui.kpi({
        label: '点位在线率',
        value: isNum(online.value) ? ui.fmt.num(online.value, 1) : '—',
        unit: '%',
        iconName: 'monitor',
        delta: online.hint || '90 秒内有数据视为在线',
      }),
      ui.kpi({
        label: '活动告警',
        value: isNum(alarmsTotal) ? alarmsTotal : '—',
        unit: '条',
        iconName: 'alarm',
        tone: alarmTone,
        delta: `紧急 ${counts.critical || 0} · 重要 ${counts.major || 0} · 次要 ${counts.minor || 0}`,
      }),
      ui.kpi({
        label: '待处理工单',
        value: isNum(woCounts.open) ? woCounts.open : workorders.value,
        unit: '单',
        iconName: 'workorder',
        tone: workorderTone,
        delta: `高优先级 ${highWos} 单 · 含处理中与待接单`,
      }),
      ui.kpi({
        label: '吨水电耗',
        value: chemicalOrNumber(energyIntensity, 3),
        unit: 'kWh/m³',
        iconName: 'analysis',
        delta: `今日用电 ${ui.fmt.num(energy.value, 2)} kWh ÷ 水量 ${ui.fmt.num(vol, 2)} m³`,
      }),
      ui.kpi({
        label: '吨水药耗',
        value: chemicalOrNumber(chemicalIntensity, 2),
        unit: 'L/m³',
        iconName: 'params',
        delta: `PAC ${ui.fmt.num(res.pacTodayL, 1)} L + PAM ${ui.fmt.num(res.pamTodayL, 1)} L ÷ 水量`,
      }),
    ];
    return ui.el('div', { class: 'kpi-grid' }, items);
  }

  /* ---------------- 11 单元流程缩略条 ---------------- */
  function processCard(ov) {
    const units = (ov.process || [])
      .filter((u) => UNIT_ORDER.includes(u.code))
      .sort((a, b) => UNIT_ORDER.indexOf(a.code) - UNIT_ORDER.indexOf(b.code));
    const strip = ui.el('div', { class: 'flow-strip' });
    units.forEach((unit, idx) => {
      const reads = pickReadings(unit);
      const node = ui.el('a', {
        class: `flow-node status-${unit.status}`,
        href: '#/process',
        title: `${unit.name}：点击进入工艺监控`,
        'aria-label': `${unit.name}，状态${unitStatusText(unit.status)}，点击查看工艺监控`,
      },
        ui.el('div', { class: 'flow-top' },
          ui.el('span', { class: 'flow-name' }, unit.short || unit.name),
          ui.dot(dotKind(unit.status), unit.status !== 'no-instrument'),
        ),
        ui.el('div', { class: 'flow-status' }, ui.unitStatus(unit.status)),
        ui.el('div', { class: 'flow-reads' },
          reads.length
            ? reads.slice(0, 2).map((r) => ui.el('span', { class: 'flow-read' }, readingWithName(r, 1)))
            : ui.el('span', { class: 'flow-read muted' }, '无在线仪表'),
        ),
        ui.el('div', { class: 'flow-meta' },
          ui.el('span', {}, `设备 ${unit.devices.online}/${unit.devices.total}`),
          unit.alarms ? ui.el('span', { class: 'flow-alarm' }, `${unit.alarms} 告警`) : null,
        ),
      );
      if (idx < units.length - 1) node.appendChild(ui.el('span', { class: 'flow-arrow', 'aria-hidden': 'true' }, '›'));
      strip.appendChild(node);
    });
    return ui.card({
      title: '工艺流程（11 单元）',
      actions: ui.el('a', { class: 'btn ghost sm', href: '#/process' }, ui.icon('process'), '进入工艺监控'),
      body: strip,
      flush: true,
      class: 'flow-card',
    });
  }

  function unitStatusText(status) {
    return ({ normal: '正常', alarm: '告警', attention: '关注', offline: '离线', stale: '数据超时', 'no-instrument': '无在线仪表' })[status] || status;
  }

  /* ---------------- 24h 趋势卡 ---------------- */
  function buildTrendCard() {
    let current = TREND_METRICS[0].code;
    let seq = 0;
    const actions = ui.el('div', { class: 'trend-actions' });
    const host = ui.el('div', { class: 'chart-host' });
    const foot = ui.el('div', { class: 'trend-foot' });
    const node = ui.card({
      title: '24 小时趋势',
      actions,
      body: host,
      foot,
      class: 'trend-card',
    });

    function paintTabs() {
      ui.mount(actions, ui.segmented(
        TREND_METRICS.map((m) => ({ value: m.code, label: m.label })),
        current,
        (code) => { if (code !== current) { current = code; draw(); } },
      ));
    }

    async function getSeries(code) {
      try {
        const history = await api.get('/api/telemetry/history', { siteId, codes: code, hours: 24 });
        const s = history && history.series ? history.series[code] : null;
        if (s) return s;
      } catch (err) {
        console.warn('[dashboard] 趋势历史加载失败', code, err);
      }
      return trendCache.get(code) || { points: [] };
    }

    function ruleCaption(code) {
      const hit = (rules || []).filter((r) => r.pointCode === code && r.enabled !== false);
      if (!hit.length) return '';
      return '阈值：' + hit.map((r) => r.threshold || r.name).join('；');
    }

    function statsText(metric, points) {
      const values = points.map((p) => Number(p.v)).filter(Number.isFinite);
      if (!values.length) return '近 24 小时无有效样本';
      const min = Math.min(...values);
      const max = Math.max(...values);
      const avg = values.reduce((a, b) => a + b, 0) / values.length;
      const unitText = metric.unit ? ` ${metric.unit}` : '';
      return `近 24h：最小 ${ui.fmt.num(min, metric.digits)} · 平均 ${ui.fmt.num(avg, metric.digits)} · 最大 ${ui.fmt.num(max, metric.digits)}${unitText} · ${points.length} 个采样点`;
    }

    async function draw() {
      const metric = TREND_METRICS.find((m) => m.code === current);
      paintTabs();
      ui.mount(host, ui.el('div', { class: 'chart-loading' }, ui.skeleton(1, 'block')));
      const mySeq = ++seq;
      const series = await getSeries(current);
      if (mySeq !== seq) return;
      const points = (series.points || []).filter((p) => isNum(p.v));
      ui.mount(host);
      host.classList.remove('chart-loading');
      if (!points.length) {
        host.appendChild(ui.empty('暂无历史数据', '该指标近 24 小时没有有效样本，可能尚未接入或数据中断。', 'analysis'));
        ui.mount(foot, ruleCaption(current));
        return;
      }
      const inner = ui.el('div', { class: 'chart-inner' });
      host.appendChild(inner);
      ui.chartLine(inner, {
        series: [{ name: metric.label, color: '#0b7a70', points }],
        height: 240,
        unit: metric.unit,
        digits: metric.digits,
        xTickCount: 5,
      });
      const metricRules = (rules || []).filter((r) => r.pointCode === current && r.enabled !== false);
      overlayThresholds(inner, points, metricRules, 240);
      ui.mount(foot,
        ui.el('div', { class: 'trend-caption' }, statsText(metric, points)),
        ui.el('div', { class: 'trend-caption muted' }, ruleCaption(current)),
      );
    }

    paintTabs();
    return { node, draw };
  }

  /* ---------------- 智能诊断摘要（top3） ---------------- */
  function diagnosisCard(items) {
    const listItems = (items || overview.diagnosis || []).slice(0, 3);
    const list = ui.el('div', { class: 'mini-list' });
    if (!listItems.length) {
      list.appendChild(ui.empty('暂无诊断结果', '实时数据均在规则区间内。', 'diagnosis'));
    } else {
      listItems.forEach((d) => {
        list.appendChild(ui.el('a', { class: 'mini-row diag-row', href: '#/diagnosis' },
          ui.el('div', { class: 'mini-top' },
            diagnosisBadge(d.level),
            ui.el('span', { class: 'mini-title' }, d.title),
          ),
          ui.el('div', { class: 'mini-note' }, d.phenomenon || ''),
          d.basis && d.basis.rule ? ui.el('div', { class: 'mini-meta' }, d.basis.rule) : null,
        ));
      });
    }
    return ui.card({
      title: '智能诊断摘要',
      actions: ui.el('a', { class: 'btn ghost sm', href: '#/diagnosis' }, '查看全部'),
      body: list,
      flush: true,
      class: 'mini-card',
    });
  }

  /* ---------------- 活动告警 top5 ---------------- */
  function alarmCard(ov) {
    const items = (ov.alarms || []).slice(0, 5);
    const list = ui.el('div', { class: 'mini-list' });
    if (!items.length) list.appendChild(ui.empty('暂无活动告警', '当前所有点位均在规则区间内。', 'alarm'));
    items.forEach((a) => {
      list.appendChild(ui.el('a', { class: 'mini-row', href: '#/alarms' },
        ui.el('div', { class: 'mini-top' },
          badgeLevel(a.level, a.levelName),
          ui.el('span', { class: 'mini-title' }, a.title),
        ),
        ui.el('div', { class: 'mini-meta mono' },
          `${ui.fmt.num(a.value, 1)}${a.unit ? ' ' + a.unit : ''} · 持续 ${ui.fmt.ago(a.firstAt)} · `,
          ui.alarmStatus(a.status),
        ),
      ));
    });
    return ui.card({
      title: '活动告警',
      sub: '按级别与最近发生排序，点击进入告警中心处置',
      actions: ui.el('a', { class: 'btn ghost sm', href: '#/alarms' }, '告警中心'),
      body: list,
      flush: true,
      class: 'mini-card',
    });
  }

  /* ---------------- 待办工单 top5 ---------------- */
  function workorderCard(ov) {
    const items = (ov.workorders || []).slice(0, 5);
    const list = ui.el('div', { class: 'mini-list' });
    if (!items.length) list.appendChild(ui.empty('暂无待办工单', '所有工单均已完成或关闭。', 'workorder'));
    items.forEach((w) => {
      list.appendChild(ui.el('a', { class: 'mini-row', href: '#/workorders' },
        ui.el('div', { class: 'mini-top' },
          ui.woStatus(w.status),
          priorityBadge(w.priority, priorityText(w.priority)),
          ui.el('span', { class: 'mini-title' }, w.title),
        ),
        ui.el('div', { class: 'mini-meta' }, `类型：${typeText(w.type)} · 截止 ${ui.fmt.dateTime(w.dueAt)}${w.assigneeName ? ' · ' + w.assigneeName : ''}`),
      ));
    });
    return ui.card({
      title: '待办工单',
      sub: '未关闭工单',
      actions: ui.el('a', { class: 'btn ghost sm', href: '#/workorders' }, '工单中心'),
      body: list,
      flush: true,
      class: 'mini-card',
    });
  }

  function priorityText(p) { return ({ urgent: '紧急', high: '高', normal: '中', low: '低' })[p] || p; }
  function typeText(t) { return ({ repair: '维修', maintenance: '保养', inspection: '巡检异常', other: '其他' })[t] || t || '—'; }

  /* ---------------- 数据健康 ---------------- */
  function healthCard(ov) {
    const health = ov.dataHealth || {};
    const units = ov.process || [];
    const missing = units.filter((u) => u.status === 'no-instrument').map((u) => u.short || u.name);
    const rows = [
      ['点位在线率', `${ui.fmt.num(health.pointsOnline, 0)}/${ui.fmt.num(health.pointsTotal, 0)} 个（${isNum(health.pointsTotal) && Number(health.pointsTotal) > 0 ? ui.fmt.pct((Number(health.pointsOnline) / Number(health.pointsTotal)) * 100) : '—'}）`],
      ['数据延迟', isNum(health.delaySec) ? `${health.delaySec} 秒` : '—'],
      ['最近数据时间', `${ui.fmt.timeSec(health.latestAt)}（${ui.fmt.ago(health.latestAt)}）`],
      ['网关状态', health.gateway === 'connected' ? '已连接' : '连接异常'],
      ['缺失在线仪表', missing.length ? `${missing.join('、')}：图纸未布点` : '无'],
    ];
    const list = ui.el('div', { class: 'health-list' });
    rows.forEach(([k, v]) => {
      list.appendChild(ui.el('div', { class: 'health-row' },
        ui.el('span', { class: 'health-k' }, k),
        ui.el('span', { class: 'health-v' }, v),
      ));
    });
    return ui.card({
      title: '数据健康',
      body: list,
      class: 'mini-card',
    });
  }

  /* ---------------- 平台公告 / 通知 ---------------- */
  function noticeCard(ov, notif) {
    const items = (notif && notif.data) || (Array.isArray(notif) ? notif : []);
    const unread = notif && notif.meta ? notif.meta.unread : 0;
    const list = ui.el('div', { class: 'notice-list' });
    if (!items.length) list.appendChild(ui.el('div', { class: 'muted small' }, '暂无通知。'));
    items.slice(0, 3).forEach((n) => {
      const href = notificationHref(n.link);
      const body = [
        ui.el('div', { class: 'notice-top' },
          ui.el('span', { class: 'notice-title' }, n.title || '系统通知'),
          ui.el('span', { class: 'notice-time' }, ui.fmt.time(n.at)),
        ),
        ui.el('div', { class: 'notice-body' }, n.body || ''),
      ];
      const node = href
        ? ui.el('a', { class: `notice-item${n.read ? '' : ' unread'}`, href }, body)
        : ui.el('div', { class: `notice-item${n.read ? '' : ' unread'}` }, body);
      list.appendChild(node);
    });
    return ui.card({
      title: '平台公告与通知',
      actions: unread ? ui.badge(`${unread} 条未读`, 'warn') : ui.badge('全部已读', 'muted'),
      body: ui.el('div', {}, list),
      class: 'notice-card',
    });
  }
}

function chemicalOrNumber(v, digits) {
  if (v === null || v === undefined || !Number.isFinite(Number(v))) return '—';
  return ui.fmt.num(Number(v), digits);
}

/* 现场触摸屏（kiosk）：1920×1080 值守大屏。
   数据全部来自真实接口；命令与告警确认走真实后端；仅两项安全快捷动作。 */
import * as ui from '../ui.js';

const el = ui.el;
const { fmt } = ui;

export const meta = { title: '现场触摸屏' };

const TONE = { good: 'good', warn: 'warn', bad: 'bad', muted: 'muted', info: 'info' };

const LEVEL_TONE = { critical: 'bad', major: 'bad', minor: 'warn', info: 'muted', warning: 'warn' };
const LEVEL_LABEL = { critical: '紧急', major: '重要', minor: '次要', info: '提示', warning: '警告' };

const UNIT_TONE = {
  normal: 'good', alarm: 'bad', attention: 'warn', offline: 'muted', stale: 'warn', 'no-instrument': 'muted',
};

const SITE_STATUS = {
  online: ['运行中', 'good'],
  offline: ['离线', 'bad'],
  maintenance: ['维护中', 'warn'],
  demo: ['演示模式', 'info'],
};

const CMD_STATUS = {
  pending_confirm: ['待确认', 'warn'],
  pending_dispatch: ['待下发', 'info'],
  sent: ['已下发', 'info'],
  executing: ['执行中', 'info'],
  success: ['成功', 'good'],
  failed: ['失败', 'bad'],
  timeout: ['超时', 'bad'],
  cancelled: ['已取消', 'muted'],
};

const BW_PHASE = ['待机', '气冲', '气水联合', '漂洗'];

/* 每个单元优先展示的关键点位（与方案工艺顺序一致） */
const UNIT_READING = {
  raw: ['RAW.LVL', 'FLOW.IN'],
  grit: [],
  primary: [],
  dist1: ['FLOW.IN'],
  baf: ['BAF.DO', 'BAF.PH'],
  pac: ['PAC.LEVEL'],
  pam: ['PAM.LEVEL'],
  dist2: [],
  sulfur: ['SULFUR.PH', 'SULFUR.DO'],
  clear: ['CLEAR.LVL'],
  waste: ['WASTE.LVL'],
};

function isNum(v) { return v !== null && v !== undefined && Number.isFinite(Number(v)); }

function qualityOf(r) {
  const q = r && (r.quality || r.q);
  return q || 'good';
}

function timeOf(r) { return r ? (r.t || r.at || null) : null; }

function isStale(r) {
  if (!r) return false;
  if (r.stale) return true;
  const t = timeOf(r);
  if (!t) return false;
  const ts = new Date(t).getTime();
  return Number.isFinite(ts) && Date.now() - ts > 90000;
}

function sourceLabel(r) {
  if (!r) return '—';
  const src = r.provenance || r.source;
  if (src === 'drawing') return '图纸点位 · 模拟网关';
  if (src === 'optional') return '选配点位 · 模拟网关';
  if (src === 'lab') return '人工化验';
  if (src === 'manual') return '人工录入';
  if (src === 'external') return '外部接口';
  return '模拟网关';
}

function readingText(r, digits = 2) {
  if (!r || r.value === null || r.value === undefined) return '待接入';
  const v = Number(r.value);
  if (!Number.isFinite(v)) return String(r.value);
  const d = Number.isFinite(Number(digits)) ? Number(digits) : 2;
  return v.toFixed(d);
}

function readingUnit(r) { return (r && r.unit) || ''; }

function findUnit(ov, code) {
  return ((ov && ov.process) || []).find((u) => u.code === code) || null;
}

function findReading(ov, code, pointCode) {
  const u = findUnit(ov, code);
  if (!u || !Array.isArray(u.readings)) return null;
  if (pointCode) {
    const hit = u.readings.find((r) => r.code === pointCode);
    if (hit) return hit;
  }
  return u.readings.find((r) => r.value !== null && r.value !== undefined) || u.readings[0] || null;
}

function findKpi(ov, key) {
  const list = (ov && ov.kpis) || [];
  return list.find((k) => k.key === key) || null;
}

function kpiValue(ov, key) {
  const k = findKpi(ov, key);
  return k ? k.value : null;
}

function statusOfReading(r, unit) {
  if (unit && unit.status === 'alarm') return { text: '告警', tone: 'bad' };
  if (unit && unit.status === 'attention') return { text: '关注', tone: 'warn' };
  if (!r || r.value === null || r.value === undefined) return { text: '待接入', tone: 'muted' };
  if (isStale(r)) return { text: '数据超时', tone: 'warn' };
  const q = qualityOf(r);
  if (q === 'bad') return { text: '数据异常', tone: 'bad' };
  if (q === 'uncertain') return { text: '数据可疑', tone: 'warn' };
  return { text: '正常', tone: 'good' };
}

function siteStatus(ov) {
  const raw = (ov && ov.site && ov.site.status) || 'online';
  const hit = SITE_STATUS[raw];
  return hit ? { text: hit[0], tone: hit[1] } : { text: raw, tone: 'info' };
}

function gatewayState(ov) {
  const h = (ov && ov.dataHealth) || {};
  if (!h.gateway) return { text: '网关状态未知', tone: 'muted', ok: false };
  const ok = h.gateway === 'connected';
  const delay = isNum(h.delaySec) ? ` · 延迟 ${Math.round(Number(h.delaySec))}s` : '';
  return { text: `${ok ? '网关在线' : '网关离线'}${delay}`, tone: ok ? 'good' : 'bad', ok };
}

function clockParts(d = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  const week = ['日', '一', '二', '三', '四', '五', '六'][d.getDay()];
  return {
    time: `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`,
    date: `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} 周${week}`,
  };
}

function levelName(a) {
  return a.levelName || LEVEL_LABEL[a.level] || a.level || '告警';
}

function levelTone(a) {
  return LEVEL_TONE[a.level] || 'warn';
}

function alarmTime(a) {
  return fmt.timeSec(a.lastAt || a.firstAt);
}

function delay(ms) { return new Promise((r) => setTimeout(r, ms)); }

/* ------------------------------------------------------------------ */

export async function render(root, ctx) {
  const siteId = ctx.siteId || (ctx.site && ctx.site.id) || 'site-1';
  const perms = (ctx.user && Array.isArray(ctx.user.perms) && ctx.user.perms) || [];
  const canHandle = perms.includes('alarm.handle');
  const canControl = perms.includes('control');

  const state = {
    overview: null,
    process: null,
    alarms: [],
    alarmMeta: null,
    actions: [],
    error: null,
    loading: false,
    at: 0,
    command: null,
    commandError: '',
    commandBusy: false,
    alive: true,
  };

  /* ---------- 骨架（先出页面，再填数据） ---------- */
  const siteNameNode = el('div', { class: 'k-site-name' }, '现场触摸屏');
  const siteMetaNode = el('div', { class: 'k-site-meta' }, ctx.settings && ctx.settings.siteName ? ctx.settings.siteName : '');
  const clockTimeNode = el('div', { class: 'k-clock-time mono' }, '--:--:--');
  const clockDateNode = el('div', { class: 'k-clock-date' }, '');
  const siteStatusNode = el('span', { class: 'k-chip' }, ui.dot('muted'), el('span', {}, '站点状态…'));
  const gatewayNode = el('span', { class: 'k-chip' }, ui.dot('muted'), el('span', {}, '网关状态…'));
  const updatedNode = el('span', { class: 'k-updated' }, '最后更新 —');

  const exitBtn = el('button', {
    class: 'btn ghost k-exit', type: 'button', 'aria-label': '退出触摸屏，返回综合驾驶舱',
  }, ui.icon('back'), el('span', {}, '退出触摸屏'));
  exitBtn.addEventListener('click', () => ctx.go('#/dashboard'));

  const topbar = el('header', { class: 'k-top' },
    el('div', { class: 'k-brand' }, siteNameNode, siteMetaNode),
    el('div', { class: 'k-clock', 'aria-live': 'off' }, clockTimeNode, clockDateNode),
    el('div', { class: 'k-top-right' }, siteStatusNode, gatewayNode, updatedNode, exitBtn),
  );

  const cardsHost = el('section', { class: 'k-cards', 'aria-label': '关键状态' },
    [0, 1, 2, 3, 4, 5].map(() => el('div', { class: 'k-card k-card-skeleton' }, ui.skeleton(2, 'block'))),
  );

  const chainNote = el('span', { class: 'k-sec-note' }, '数据来源：内置模拟网关（SIM-GW）');
  const chainHost = el('div', { class: 'k-chain', role: 'list' }, ui.skeleton(2, 'block'));
  const chainCard = el('section', { class: 'k-panel k-chain-panel' },
    el('div', { class: 'k-sec-head' },
      el('h2', {}, '工艺流程（11 单元）'),
      el('span', { class: 'k-sec-hint' }, '点击单元查看读数、设备与告警'),
      chainNote,
    ),
    chainHost,
  );

  const alarmCountNode = el('span', { class: 'k-sec-badge' }, '—');
  const alarmHost = el('div', { class: 'k-alarms' }, ui.skeleton(3, 'line'));
  const alarmPanel = el('section', { class: 'k-panel k-alarm-panel', 'aria-label': '最近未关闭告警' },
    el('div', { class: 'k-sec-head' },
      el('h2', {}, '最近未关闭告警'),
      el('span', { class: 'k-sec-hint' }, '显示最近 3 条；确认需 alarm.handle 权限'),
      alarmCountNode,
    ),
    canHandle ? null : el('div', { class: 'k-inline-note warn' }, '当前账号没有告警处置权限（alarm.handle），告警仅可查看。'),
    alarmHost,
  );

  const bwBtn = el('button', { class: 'k-btn k-btn-primary', type: 'button' }, '启动 BAF 反冲洗');
  const refreshBtn = el('button', { class: 'k-btn', type: 'button' }, '刷新数据');
  const bwStateNode = el('span', { class: 'k-bw-state' }, '反冲洗状态：—');
  const commandNode = el('div', { class: 'k-command' }, el('span', { class: 'muted' }, '最近命令：—'));
  const actionPanel = el('section', { class: 'k-panel k-action-panel', 'aria-label': '快捷操作' },
    el('div', { class: 'k-sec-head' },
      el('h2', {}, '快捷操作'),
      el('span', { class: 'k-sec-hint' }, '仅两项安全动作'),
    ),
    bwStateNode,
    el('div', { class: 'k-action-btns' }, bwBtn, refreshBtn),
    commandNode,
    el('p', { class: 'k-foot-note' }, '全部命令仅作用于本机模拟网关（SIM-GW），不会下发至真实 PLC 或现场设备；操作理由自动记为「现场触摸屏快捷操作」。'),
    canControl ? null : el('div', { class: 'k-inline-note warn' }, '当前账号没有控制权限（control），快捷操作已禁用。'),
  );

  const sourcesNode = el('span', {}, '数据来源：内置模拟网关（SIM-GW）');
  const footer = el('footer', { class: 'k-foot' },
    sourcesNode,
    el('span', { class: 'k-foot-sep' }, '·'),
    el('span', {}, '自动刷新：5 秒；支持 ctx.subscribe(telemetry) 即时推送'),
    el('span', { class: 'k-foot-sep' }, '·'),
    el('span', {}, '目标分辨率 1920×1080 / 兼容 1366×768'),
  );

  const main = el('main', { class: 'k-main' }, cardsHost, chainCard,
    el('div', { class: 'k-bottom' }, alarmPanel, actionPanel));
  const page = el('div', { class: 'page p-kiosk', role: 'main', 'aria-label': '现场触摸屏值守界面' }, topbar, main, footer);
  ui.mount(root, page);

  /* ---------- 时钟 ---------- */
  function tickClock() {
    const c = clockParts();
    clockTimeNode.textContent = c.time;
    clockDateNode.textContent = c.date;
  }
  tickClock();
  ctx.every(1000, tickClock);

  /* ---------- 数据加载 ---------- */
  async function loadData({ first = false } = {}) {
    if (state.loading) return;
    state.loading = true;
    if (first) {
      /* 骨架已在 DOM 中，无需重绘 */
    }
    const safe = (p, fallback = null) => p.catch(() => fallback);
    try {
      const [ov, proc, alarmRes, actRes] = await Promise.all([
        ctx.api.get('/api/overview', { siteId }),
        safe(ctx.api.get(`/api/sites/${encodeURIComponent(siteId)}/process`)),
        safe(ctx.api.getFull('/api/alarms', { siteId, status: 'open', pageSize: 5 })),
        safe(ctx.api.get('/api/actions', { siteId })),
      ]);
      state.overview = ov || null;
      state.process = proc || null;
      const alarmRows = (alarmRes && (alarmRes.data || alarmRes)) || [];
      state.alarmMeta = (alarmRes && alarmRes.meta) || null;
      state.alarms = alarmRows
        .slice()
        .sort((a, b) => new Date(b.lastAt || b.firstAt || 0) - new Date(a.lastAt || a.firstAt || 0))
        .slice(0, 3);
      state.actions = (actRes && actRes.actions) || [];
      state.error = null;
      state.at = Date.now();
      renderAll();
    } catch (err) {
      state.error = err;
      if (!state.overview) renderError(err);
      else renderRefreshFailure(err);
    } finally {
      state.loading = false;
    }
  }

  function renderError(err) {
    ui.mount(main, ui.card({
      title: '触摸屏数据加载失败',
      body: ui.errorState(err, () => loadData({ first: true })),
    }));
  }

  function renderRefreshFailure(err) {
    updatedNode.textContent = `刷新失败 · 保留 ${fmt.timeSec(state.at)} 数据`;
    updatedNode.classList.add('is-bad');
    if (err && err.message) sourcesNode.textContent = `数据来源：内置模拟网关（SIM-GW）· 刷新失败：${err.message}`;
  }

  /* ---------- 渲染入口 ---------- */
  function renderAll() {
    const ov = state.overview || {};
    renderHeader(ov);
    renderCards(ov);
    renderChain(ov);
    renderAlarms();
    renderActions(ov);
    renderFooter(ov);
  }

  function renderHeader(ov) {
    const site = ov.site || {};
    siteNameNode.textContent = site.name || (ctx.site && ctx.site.name) || '本站点';
    siteMetaNode.textContent = [site.region, site.area, site.process, site.capacity].filter(Boolean).join(' · ') || `站点 ${siteId}`;
    const st = siteStatus(ov);
    ui.mount(siteStatusNode, ui.dot(st.tone, true), el('span', {}, st.text));
    const gw = gatewayState(ov);
    ui.mount(gatewayNode, ui.dot(gw.tone, true), el('span', {}, gw.text));
    updatedNode.classList.remove('is-bad');
    const at = ov.generatedAt || state.at;
    updatedNode.textContent = `最后更新 ${fmt.timeSec(at)}`;
  }

  function cardsFor(ov) {
    const rawUnit = findUnit(ov, 'raw');
    const bafUnit = findUnit(ov, 'baf');
    const sulfurUnit = findUnit(ov, 'sulfur');
    const raw = findReading(ov, 'raw', 'RAW.LVL');
    const bafDo = findReading(ov, 'baf', 'BAF.DO');
    const sulfurPh = findReading(ov, 'sulfur', 'SULFUR.PH');
    const alarms = kpiValue(ov, 'alarms');
    const workorders = kpiValue(ov, 'workorders');
    const online = kpiValue(ov, 'online');
    const counts = ov.alarmCounts || {};
    const health = ov.dataHealth || {};
    const raised = Number(alarms || 0);
    const woRaised = Number(workorders || 0);
    const onlineNum = isNum(online) ? Number(online) : null;
    const lvlCounts = [
      ['紧急', Number(counts.critical || 0)],
      ['重要', Number(counts.major || 0)],
      ['次要', Number(counts.minor || 0)],
    ];

    return [
      {
        key: 'raw',
        label: '原料罐液位',
        value: raw ? readingText(raw, 1) : '待接入',
        unit: raw ? readingUnit(raw) : '',
        source: raw ? sourceLabel(raw) : '待接入',
        time: raw ? timeOf(raw) : null,
        status: statusOfReading(raw, rawUnit),
        sub: raw ? `提升泵 ${rawUnit && rawUnit.devices ? `${rawUnit.devices.online}/${rawUnit.devices.total} 在线` : '—'}` : '图纸未布设在线仪表',
      },
      {
        key: 'baf',
        label: 'BAF 溶解氧',
        value: bafDo ? readingText(bafDo, 2) : '待接入',
        unit: bafDo ? readingUnit(bafDo) : '',
        source: bafDo ? sourceLabel(bafDo) : '待接入',
        time: bafDo ? timeOf(bafDo) : null,
        status: statusOfReading(bafDo, bafUnit),
        sub: bafDo ? `pH ${readingText(findReading(ov, 'baf', 'BAF.PH'), 2)}` : '无在线仪表',
      },
      {
        key: 'sulfur',
        label: '硫自养池 pH',
        value: sulfurPh ? readingText(sulfurPh, 2) : '待接入',
        unit: sulfurPh ? readingUnit(sulfurPh) : '',
        source: sulfurPh ? sourceLabel(sulfurPh) : '待接入',
        time: sulfurPh ? timeOf(sulfurPh) : null,
        status: statusOfReading(sulfurPh, sulfurUnit),
        sub: sulfurPh ? `DO ${readingText(findReading(ov, 'sulfur', 'SULFUR.DO'), 2)} mg/L` : '无在线仪表',
      },
      {
        key: 'alarms',
        label: '活动告警',
        value: alarms === null || alarms === undefined ? '—' : fmt.int(raised),
        unit: '条',
        source: '告警中心',
        time: ov.generatedAt,
        status: raised > 0
          ? { text: '需处置', tone: Number(counts.critical || 0) + Number(counts.major || 0) > 0 ? 'bad' : 'warn' }
          : { text: '正常', tone: 'good' },
        sub: lvlCounts.map(([l, n]) => `${l} ${n}`).join(' · '),
      },
      {
        key: 'workorders',
        label: '待办工单',
        value: workorders === null || workorders === undefined ? '—' : fmt.int(woRaised),
        unit: '单',
        source: '工单中心',
        time: ov.generatedAt,
        status: woRaised > 0 ? { text: '待处理', tone: 'warn' } : { text: '正常', tone: 'good' },
        sub: `高优先级 ${Number((ov.workorderCounts && ov.workorderCounts.high) || 0)} 单`,
      },
      {
        key: 'online',
        label: '点位在线率',
        value: onlineNum === null ? '—' : `${onlineNum.toFixed(1)}`,
        unit: '%',
        source: '遥测网关',
        time: ov.generatedAt,
        status: onlineNum === null
          ? { text: '未知', tone: 'muted' }
          : onlineNum >= 99.9
            ? { text: '正常', tone: 'good' }
            : onlineNum >= 90 ? { text: '关注', tone: 'warn' } : { text: '异常', tone: 'bad' },
        sub: `${Number(health.pointsOnline || 0)}/${Number(health.pointsTotal || 0)} 点位在线${Number(health.delaySec || 0) > 90 ? ` · 延迟 ${Math.round(Number(health.delaySec))}s` : ''}`,
      },
    ];
  }

  function renderCards(ov) {
    const models = cardsFor(ov);
    const nodes = models.map((m) => {
      const statusCls = TONE[m.status.tone] || 'muted';
      return el('article', { class: `k-card tone-${statusCls}`, 'data-key': m.key },
        el('div', { class: 'k-card-head' },
          el('span', { class: 'k-card-label' }, m.label),
          el('span', { class: 'k-card-src' }, m.source),
        ),
        el('div', { class: 'k-card-body' },
          el('span', { class: 'k-num mono' }, m.value),
          m.unit ? el('span', { class: 'k-suffix' }, m.unit) : null,
        ),
        el('div', { class: 'k-card-sub' }, m.sub || '—'),
        el('div', { class: 'k-card-foot' },
          ui.badge(m.status.text, statusCls),
          el('span', { class: 'k-card-time' }, m.time ? `更新 ${fmt.timeSec(m.time)}` : '更新 —'),
        ),
      );
    });
    ui.mount(cardsHost, nodes);
  }

  function preferredReading(ov, code) {
    const unit = findUnit(ov, code);
    if (!unit || !Array.isArray(unit.readings) || !unit.readings.length) return null;
    const list = UNIT_READING[code] || [];
    for (const c of list) {
      const hit = unit.readings.find((r) => r.code === c);
      if (hit) return hit;
    }
    return unit.readings.find((r) => r.value !== null && r.value !== undefined) || unit.readings[0];
  }

  function renderChain(ov) {
    const units = ((ov.process) || [])
      .filter((u) => u.code !== 'plant')
      .sort((a, b) => Number(a.order || 0) - Number(b.order || 0));
    if (!units.length) {
      ui.mount(chainHost, ui.empty('暂无单元数据', '服务端未返回工艺单元，请稍后刷新', 'process'));
      return;
    }
    chainHost.setAttribute('aria-label', `${units.length} 个工艺单元`);
    ui.mount(chainHost, units.map((u) => {
      const r = preferredReading(ov, u.code);
      const tone = UNIT_TONE[u.status] || 'muted';
      const statusBadge = ui.unitStatus(u.status);
      const node = el('button', {
        class: `k-unit tone-${tone}`,
        type: 'button',
        role: 'listitem',
        title: `${u.name}：点击查看读数、设备与告警`,
        'aria-label': `${u.short || u.name}，${statusBadge.textContent || ''}，${r ? `${readingText(r, 1)} ${readingUnit(r)}` : '待接入'}`,
      },
        el('span', { class: 'k-unit-top' }, canNoStatusIcon(tone), el('span', { class: 'k-unit-name' }, u.short || u.name)),
        el('span', { class: 'k-unit-tag' }, statusBadge),
        r && r.value !== null && r.value !== undefined
          ? el('span', { class: 'k-unit-value mono' }, readingText(r, 1), el('span', { class: 'k-unit-unit' }, readingUnit(r)))
          : el('span', { class: 'k-unit-value muted' }, '待接入'),
      );
      node.addEventListener('click', () => openUnitDrawer(u));
      return node;
    }));
  }

  function canNoStatusIcon(tone) { return ui.dot(tone, false); }

  /* ---------- 单元抽屉 ---------- */
  function openUnitDrawer(unit) {
    const ov = state.overview || {};
    const proc = state.process || {};
    const procUnit = ((proc.units) || []).find((u) => u.code === unit.code) || unit;
    const readings = Array.isArray(procUnit.readings) ? procUnit.readings : (Array.isArray(unit.readings) ? unit.readings : []);
    const devices = ((proc.devices) || []).filter((d) => d.unitCode === unit.code);
    const unitAlarms = ((proc.alarms) || []).filter((a) => a.unitCode === unit.code && a.status !== 'closed');

    const body = el('div', { class: 'k-drawer' },
      el('div', { class: 'k-drawer-head' },
        ui.unitStatus(procUnit.status),
        el('span', { class: 'muted small' }, procUnit.note || ''),
      ),
      el('div', { class: 'k-drawer-sec' },
        el('h3', {}, `在线点位（${readings.length}）`),
        readings.length ? ui.table({
          columns: [
            { key: 'name', label: '点位', render: (r) => el('span', {}, r.name || r.code) },
            { key: 'code', label: '编码', cls: 'mono small', render: (r) => r.code || '—' },
            { key: 'value', label: '实时值', cls: 'num', render: (r) => `${readingText(r, 2)} ${readingUnit(r)}` },
            { key: 'q', label: '质量', render: (r) => ui.qualityBadge(isStale(r) ? 'uncertain' : qualityOf(r)) },
            { key: 't', label: '数据时间', render: (r) => fmt.timeSec(timeOf(r)) },
            { key: 'src', label: '来源', cls: 'small muted', render: (r) => sourceLabel(r) },
          ],
          rows: readings,
          emptyText: '该单元未布设在线仪表',
          flush: true,
        }) : ui.empty('该单元无在线点位', '按方案图纸，本单元未布设在线仪表，指标标注为「待接入」。', 'monitor'),
      ),
      el('div', { class: 'k-drawer-sec' },
        el('h3', {}, `设备（${devices.length} · 在线 ${devices.filter((d) => d.online !== false).length}）`),
        devices.length
          ? el('ul', { class: 'k-device-list' }, devices.map((d) => el('li', {},
            ui.dot(d.online === false ? 'muted' : 'good', d.online !== false),
            el('span', { class: 'k-device-name' }, d.name || d.code),
            el('span', { class: 'k-device-code mono' }, d.code || ''),
            el('span', { class: 'k-device-status' }, d.status || (d.online === false ? '离线' : '在线')),
          )))
          : ui.empty('该单元未安装设备', '按方案图纸，本单元不设在线设备。', 'device'),
      ),
      el('div', { class: 'k-drawer-sec' },
        el('h3', {}, `未关闭告警（${unitAlarms.length}）`),
        unitAlarms.length
          ? el('ul', { class: 'k-drawer-alarms' }, unitAlarms.map((a) => el('li', {},
            ui.badge(levelName(a), levelTone(a)),
            el('span', { class: 'k-drawer-alarm-title' }, a.title || '—'),
            el('span', { class: 'k-drawer-alarm-time' }, fmt.timeSec(a.lastAt || a.firstAt)),
          )))
          : ui.empty('该单元无未关闭告警', '当前该单元所有点位均在规则区间内。', 'alarm'),
      ),
      el('div', { class: 'k-drawer-foot' },
        `${procUnit.name || unit.name} · 数据来源：内置模拟网关（SIM-GW）· 最后更新：${fmt.timeSec(timeOf((readings[0] || {})) || state.at)}`,
      ),
    );
    ctx.drawer({ title: unit.name || unit.short || unit.code, sub: '单元实时读数、设备与告警', body });
  }

  /* ---------- 告警区 ---------- */
  function renderAlarms() {
    ui.mount(alarmCountNode, ui.badge(`${state.alarms.length} 条`, state.alarms.length ? 'bad' : 'good'));
    if (!state.alarms.length) {
      alarmHost.classList.remove('is-loading');
      ui.mount(alarmHost, ui.empty('当前无未关闭告警', '所有点位均在规则区间内。', 'alarm'));
      return;
    }
    alarmHost.classList.remove('is-loading');
    ui.mount(alarmHost, state.alarms.map((a) => {
      const acked = a.status === 'acknowledged';
      const actionNode = el('div', { class: 'k-alarm-act' });
      if (acked) {
        actionNode.appendChild(ui.badge('已确认', 'info'));
      } else {
        const btn = el('button', { class: 'k-btn k-btn-alarm', type: 'button' }, '确认');
        btn.disabled = !canHandle;
        if (!canHandle) btn.title = '当前账号无 alarm.handle 权限';
        btn.addEventListener('click', () => acknowledge(a, btn));
        actionNode.appendChild(btn);
      }
      return el('div', { class: `k-alarm level-${levelTone(a)}`, role: 'listitem' },
        el('div', { class: 'k-alarm-main' },
          el('div', { class: 'k-alarm-head' },
            ui.badge(levelName(a), levelTone(a)),
            el('span', { class: 'k-alarm-title' }, a.title || '—'),
          ),
          el('div', { class: 'k-alarm-meta' },
            [a.unitShort || a.unitName, a.pointName, isNum(a.value) ? `${a.value} ${a.pointUnit || ''}` : null, alarmTime(a)]
              .filter(Boolean).join(' · '),
          ),
          el('div', { class: 'k-alarm-detail' }, a.detail || ''),
        ),
        actionNode,
      );
    }));
  }

  async function acknowledge(alarm, btn) {
    if (!canHandle) return;
    btn.disabled = true;
    const old = btn.textContent;
    btn.textContent = '确认中…';
    try {
      await ctx.api.post(`/api/alarms/${encodeURIComponent(alarm.id)}/ack`, { note: '现场触摸屏确认' });
      ui.toast('告警已确认', 'success');
      await loadData({ first: false });
    } catch (err) {
      ui.toast((err && err.message) || '确认失败', 'error');
      btn.disabled = false;
      btn.textContent = old;
    }
  }

  /* ---------- 快捷操作 ---------- */
  function bwReading(ov) {
    return findReading(ov || state.overview, 'baf', 'BAF.BW.STATE');
  }

  function bwState(ov) {
    const r = bwReading(ov);
    const raw = r && r.value !== null && r.value !== undefined ? Number(r.value) : null;
    return { reading: r, raw: Number.isFinite(raw) ? raw : null, running: Number.isFinite(raw) && raw > 0 };
  }

  function renderActions(ov) {
    const bw = bwState(ov);
    const phase = bw.raw !== null && bw.raw >= 0 ? (BW_PHASE[Math.round(bw.raw)] || `阶段 ${bw.raw}`) : '—';
    bwStateNode.textContent = `反冲洗状态：${phase}${bw.reading ? ` · ${sourceLabel(bw.reading)} · ${fmt.timeSec(timeOf(bw.reading))}` : ''}`;
    bwBtn.textContent = bw.running ? '停止 BAF 反冲洗' : '启动 BAF 反冲洗';
    bwBtn.disabled = !canControl || state.commandBusy;
    bwBtn.title = canControl ? '执行前需要二次确认' : '当前账号无 control 权限';
    renderCommandState();
  }

  function renderCommandState() {
    const cmd = state.command;
    if (state.commandError) {
      ui.mount(commandNode, el('div', { class: 'k-command-line' },
        ui.badge('失败', 'bad'), el('span', {}, state.commandError)));
      return;
    }
    if (!cmd) {
      ui.mount(commandNode, el('span', { class: 'muted' }, '最近命令：—'));
      return;
    }
    const st = CMD_STATUS[cmd.status] || [cmd.status || '未知', 'muted'];
    ui.mount(commandNode, el('div', { class: 'k-command-line' },
      ui.badge(st[0], st[1]),
      el('span', { class: 'mono tiny' }, cmd.id ? `#${cmd.id}` : ''),
      el('span', {}, cmd.label || cmd.action || ''),
      el('span', { class: 'muted tiny' }, cmd.failReason ? `原因：${cmd.failReason}` : (cmd.at || cmd.createdAt ? fmt.timeSec(cmd.at || cmd.createdAt) : '')),
    ));
  }

  function currentBwAction() {
    const bw = bwState(state.overview);
    return bw.running ? 'baf.backwash.stop' : 'baf.backwash.start';
  }

  async function runBackwash() {
    if (!canControl || state.commandBusy) return;
    const actionKey = currentBwAction();
    const def = state.actions.find((a) => a.key === actionKey) || null;
    const label = (def && def.label) || (actionKey.endsWith('.stop') ? '停止 BAF 反冲洗' : '启动 BAF 反冲洗');
    const constraints = (def && Array.isArray(def.constraints) && def.constraints.length) ? def.constraints : [];
    const ok = await ctx.confirm({
      title: actionKey.endsWith('.stop') ? '确认停止反冲洗' : '确认启动反冲洗',
      message: `${label}？操作将下发到本机模拟网关（SIM-GW）。`,
      detail: [
        constraints.length ? `联锁与影响：\n- ${constraints.join('\n- ')}` : '',
        '理由（自动填写）：现场触摸屏快捷操作',
        '执行后需现场确认滤池状态；高风险动作提交后还需二次确认。',
      ].filter(Boolean).join('\n\n'),
      confirmText: '提交命令',
      danger: actionKey.endsWith('.start'),
    });
    if (!ok) return;

    state.commandBusy = true;
    state.commandError = '';
    bwBtn.disabled = true;
    const oldLabel = bwBtn.textContent;
    bwBtn.textContent = '提交中…';
    try {
      const res = await ctx.api.post('/api/commands', {
        siteId,
        action: actionKey,
        args: {},
        reason: '现场触摸屏快捷操作',
      });
      const command = (res && res.command) || null;
      if (res && res.blocked) {
        const why = (command && command.failReason) || res.message || '联锁条件不满足';
        state.command = command;
        state.commandError = `命令被拒绝：${why}`;
        ui.toast('联锁条件不满足，命令未下发', 'error');
        renderCommandState();
        return;
      }
      if (res && res.needsConfirm && command) {
        const ok2 = await ctx.confirm({
          title: '高风险动作二次确认',
          message: `确认向模拟网关下发「${label}」？`,
          detail: `命令编号：${command.id}\n影响：${(def && def.effect) || '—'}\n理由：现场触摸屏快捷操作\n\n高风险动作需再次确认后才进入下发队列。`,
          confirmText: '确认下发',
          danger: true,
        });
        if (!ok2) {
          state.command = command;
          renderCommandState();
          ui.toast('已暂缓下发，命令处于「待确认」，可在远程控制页处理', 'info');
          return;
        }
        const confirmed = await ctx.api.post(`/api/commands/${encodeURIComponent(command.id)}/confirm`);
        state.command = (confirmed && confirmed.command) || confirmed || command;
        renderCommandState();
        ui.toast('命令已确认，进入下发队列', 'success');
        pollCommand(command.id);
      } else {
        state.command = command;
        renderCommandState();
        ui.toast('命令已下发到模拟网关', 'success');
        if (command && command.id) pollCommand(command.id);
      }
    } catch (err) {
      state.commandError = (err && err.message) || '命令提交失败，请稍后重试';
      ui.toast(state.commandError, 'error');
      renderCommandState();
    } finally {
      state.commandBusy = false;
      bwBtn.textContent = oldLabel;
      bwBtn.disabled = !canControl;
    }
  }

  async function pollCommand(id) {
    if (!id) return;
    const terminal = ['success', 'failed', 'timeout', 'cancelled'];
    for (let i = 0; i < 5; i += 1) {
      await delay(900);
      if (!state.alive) return;
      try {
        const cmd = await ctx.api.get(`/api/commands/${encodeURIComponent(id)}`);
        state.command = cmd || state.command;
        renderCommandState();
        if (cmd && terminal.includes(cmd.status)) return;
      } catch (err) {
        return;
      }
    }
  }

  bwBtn.addEventListener('click', runBackwash);
  refreshBtn.addEventListener('click', async () => {
    refreshBtn.disabled = true;
    const old = refreshBtn.textContent;
    refreshBtn.textContent = '刷新中…';
    await loadData({ first: false });
    refreshBtn.textContent = old;
    refreshBtn.disabled = false;
    ui.toast('数据已刷新', 'success');
  });

  /* ---------- 页脚 ---------- */
  function renderFooter(ov) {
    const gw = gatewayState(ov);
    const h = ov.dataHealth || {};
    const parts = [
      '数据来源：内置模拟网关（SIM-GW）',
      gw.ok ? `网关在线${isNum(h.delaySec) ? `（延迟 ${Math.round(Number(h.delaySec))}s）` : ''}` : '网关离线',
      `点位 ${Number(h.pointsOnline || 0)}/${Number(h.pointsTotal || 0)} 在线`,
      `最后更新 ${fmt.timeSec(ov.generatedAt || state.at)}`,
      '无数据/无仪表指标标注为「待接入」，不参与估算',
    ];
    ui.mount(sourcesNode, el('span', {}, parts.join(' · ')));
  }

  /* ---------- 启动 ---------- */
  ctx.every(5000, () => { if (!state.loading) loadData({}); });
  ctx.subscribe((evt) => {
    if (!evt || evt.type !== 'telemetry') return;
    if (Date.now() - state.at > 2000) loadData({});
  });

  await loadData({ first: true });
  return () => { state.alive = false; };
}

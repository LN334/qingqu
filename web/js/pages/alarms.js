import * as ui from '../ui.js';

export const meta = { title: '告警中心' };

const { el, fmt } = ui;

/* 级别元信息：颜色仅作辅助，文字与徽标双通道表达状态 */
const LEVELS = [
  { value: 'critical', label: '紧急', color: '#b32d22' },
  { value: 'major', label: '重要', color: '#c0762a' },
  { value: 'minor', label: '次要', color: '#1f6fb2' },
  { value: 'info', label: '提示', color: '#7d93a2' },
];

const STATUS_TABS = [
  { value: 'open', label: '待处理' },
  { value: 'all', label: '全部' },
  { value: 'active', label: '未处理' },
  { value: 'acknowledged', label: '已确认' },
  { value: 'resolved', label: '已恢复' },
  { value: 'closed', label: '已关闭' },
];

const RANGES = [
  { value: '24h', label: '近 24 小时' },
  { value: '7d', label: '近 7 天' },
  { value: '30d', label: '近 30 天' },
  { value: 'all', label: '全部时间' },
];

const ACTION_NAME = {
  create: '告警触发',
  ack: '已确认',
  escalate: '级别升级',
  resolve: '已恢复',
  close: '已关闭',
  'alarm.create': '告警触发',
  'alarm.escalate': '级别升级',
  'alarm.ack': '已确认',
  'alarm.resolve': '已恢复',
  'alarm.close': '已关闭',
};

function hasPerm(ctx, perm) {
  const perms = (ctx.user && ctx.user.perms) || [];
  return perms.includes(perm);
}

function levelMeta(value) {
  return LEVELS.find((l) => l.value === value) || { value, label: value || '未知', color: '#7d93a2' };
}

function durationText(minutes) {
  const m = Math.max(0, Math.round(Number(minutes) || 0));
  if (m < 60) return `${m} 分钟`;
  const h = Math.floor(m / 60);
  const rest = m % 60;
  if (h < 24) return rest ? `${h} 小时 ${rest} 分` : `${h} 小时`;
  const d = Math.floor(h / 24);
  const rh = h % 24;
  return rh ? `${d} 天 ${rh} 小时` : `${d} 天`;
}

function conditionText(rule) {
  if (!rule) return '—';
  const unit = rule.unit || '';
  if (rule.op === 'high') return `高于 ${fmt.auto(rule.high)} ${unit}`.trim();
  if (rule.op === 'low') return `低于 ${fmt.auto(rule.low)} ${unit}`.trim();
  if (rule.op === 'outside') return `超出 ${fmt.auto(rule.low)} – ${fmt.auto(rule.high)} ${unit}`.trim();
  if (rule.op === 'state') return `开关量异常（${rule.threshold || '状态'}）`;
  return rule.threshold || '自定义条件';
}

function rangeFrom(value) {
  const days = value === '24h' ? 1 : value === '7d' ? 7 : value === '30d' ? 30 : 0;
  return days ? new Date(Date.now() - days * 86400000).toISOString() : null;
}

function todayKey() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

export async function render(root, ctx) {
  const canHandle = hasPerm(ctx, 'alarm.handle');
  const canRule = hasPerm(ctx, 'param.edit');
  const canOrder = hasPerm(ctx, 'workorder.handle');
  const units = (ctx.meta && ctx.meta.units) || [];
  const q = ctx.query || {};

  const state = {
    tab: q.tab === 'rules' ? 'rules' : 'list',
    status: STATUS_TABS.some((t) => t.value === q.status) ? q.status : 'open',
    level: LEVELS.some((l) => l.value === q.level) ? q.level : '',
    unitCode: units.some((u) => u.code === q.unit) ? q.unit : '',
    keyword: typeof q.q === 'string' ? q.q.slice(0, 60) : '',
    range: RANGES.some((r) => r.value === q.range) ? q.range : '7d',
    page: 1,
    pageSize: 20,
    items: [],
    listMeta: null,
    listLoading: true,
    listError: null,
    stats: null,
    statsError: null,
    openItems: [],
    openLoading: true,
    openError: null,
    rules: null,
    rulesLoading: true,
    rulesError: null,
    lastUpdated: 0,
    busy: false,
  };

  /* ---------- 容器 ---------- */
  const statsHost = el('div', { class: 'stat-strip' });
  const trendBox = el('div', { class: 'chart-box' }, ui.skeleton(1, 'block'));
  const trendFoot = el('p', { class: 'tiny muted' }, '');
  const donutBox = el('div', { class: 'donut-box' }, ui.skeleton(1, 'block'));
  const updatedLabel = el('span', { class: 'tiny muted mono' }, '');
  const tabsHost = el('div', { class: 'tabs-row' });
  const filterHost = el('div', { class: 'filters' });
  const statusSegHost = el('div', { class: 'seg-host' });
  const tableHost = el('div', { class: 'table-host' }, ui.skeleton(6, 'line'));
  const pagerHost = el('div', { class: 'pager' });
  const rulesHint = el('div', { class: 'rules-hint' });
  const rulesHost = el('div', { class: 'table-host' }, ui.skeleton(6, 'line'));
  const listSection = el('div', { class: 'list-section' }, filterHost, tableHost, pagerHost);
  const rulesSection = el('div', { class: 'rules-section', hidden: true }, rulesHint, rulesHost);
  const bodyHost = el('div', { class: 'stack tight' }, listSection, rulesSection);
  const refreshBtn = el('button', { class: 'btn ghost', type: 'button' }, ui.icon('refresh'), '刷新数据');

  const page = el('div', { class: 'page p-alarms' },
    ui.pageHead(
      '告警中心',
      `规则引擎与网关事件驱动的告警研判闭环：确认 → 恢复 → 关闭，可转工单跟进。数据范围：${ctx.site ? ctx.site.name : ctx.siteId}。`,
      el('div', { class: 'row' }, updatedLabel, refreshBtn),
    ),
    statsHost,
    el('div', { class: 'overview-grid' },
      ui.card({
        title: '近 7 日告警趋势',
        sub: '按日统计（含已恢复与已关闭），柱色表示当日最高等级',
        body: el('div', { class: 'stack tight' }, trendBox, trendFoot),
      }),
      ui.card({
        title: '活动告警构成',
        sub: '未处理 + 已确认，按级别分布',
        body: donutBox,
      }),
    ),
    ui.card({
      title: '告警处置',
      sub: '处置动作写入审计日志；列表与统计每 30 秒自动刷新，收到实时事件时立即更新',
      flush: true,
      body: el('div', { class: 'tabs-body' }, el('div', { class: 'tabs-pad' }, tabsHost), bodyHost),
    }),
  );

  ui.mount(root, page);

  /* ---------- 筛选栏 ---------- */
  const levelSelect = ui.select(
    [{ value: '', label: '全部级别' }].concat(LEVELS.map((l) => ({ value: l.value, label: l.label }))),
    { 'aria-label': '按级别筛选' },
  );
  levelSelect.value = state.level;
  levelSelect.addEventListener('change', () => {
    state.level = levelSelect.value;
    state.page = 1;
    syncQuery();
    loadList();
  });

  const unitSelect = ui.select(
    [{ value: '', label: '全部单元' }].concat(units.map((u) => ({ value: u.code, label: u.name }))),
    { 'aria-label': '按工艺单元筛选' },
  );
  unitSelect.value = state.unitCode;
  unitSelect.addEventListener('change', () => {
    state.unitCode = unitSelect.value;
    state.page = 1;
    syncQuery();
    loadList();
  });

  const rangeSelect = ui.select(RANGES, { 'aria-label': '按时间范围筛选' });
  rangeSelect.value = state.range;
  rangeSelect.addEventListener('change', () => {
    state.range = rangeSelect.value;
    state.page = 1;
    syncQuery();
    loadList();
  });

  const searchInput = ui.input({
    type: 'search',
    placeholder: '搜索告警编号 / 标题 / 点位编码',
    value: state.keyword,
    maxlength: 60,
    'aria-label': '关键字搜索',
  });
  const onSearch = ui.debounce(() => {
    state.keyword = searchInput.value.trim().slice(0, 60);
    state.page = 1;
    syncQuery();
    loadList();
  }, 320);
  searchInput.addEventListener('input', onSearch);

  const resetBtn = el('button', {
    class: 'btn ghost sm',
    type: 'button',
    onclick: () => {
      state.status = 'open';
      state.level = '';
      state.unitCode = '';
      state.keyword = '';
      state.range = '7d';
      state.page = 1;
      levelSelect.value = '';
      unitSelect.value = '';
      rangeSelect.value = '7d';
      searchInput.value = '';
      renderStatusSeg();
      syncQuery();
      loadList();
    },
  }, '重置筛选');

  filterHost.append(
    statusSegHost,
    el('div', { class: 'row wrap filter-row' }, levelSelect, unitSelect, rangeSelect, searchInput, resetBtn),
  );

  function renderStatusSeg() {
    ui.clear(statusSegHost);
    statusSegHost.append(ui.segmented(STATUS_TABS, state.status, (value) => {
      state.status = value;
      state.page = 1;
      renderStatusSeg();
      syncQuery();
      loadList();
    }));
  }
  renderStatusSeg();

  /* ---------- 页签 ---------- */
  const TABS = [
    { value: 'list', label: '告警列表' },
    { value: 'rules', label: '告警规则' },
  ];

  function renderTabs() {
    ui.clear(tabsHost);
    tabsHost.append(ui.tabs(TABS, state.tab, (value) => setTab(value)));
  }

  function setTab(value) {
    if (value === state.tab) return;
    state.tab = value;
    listSection.hidden = value !== 'list';
    rulesSection.hidden = value !== 'rules';
    renderTabs();
    syncQuery();
    if (value === 'rules' && !state.rules) loadRules();
    if (value === 'list') loadList();
  }
  renderTabs();

  /* ---------- URL 查询串同步（replaceState，不触发路由重渲染） ---------- */
  function syncQuery() {
    const params = new URLSearchParams();
    if (state.tab !== 'list') params.set('tab', state.tab);
    if (state.status !== 'open') params.set('status', state.status);
    if (state.level) params.set('level', state.level);
    if (state.unitCode) params.set('unit', state.unitCode);
    if (state.keyword) params.set('q', state.keyword);
    if (state.range !== '7d') params.set('range', state.range);
    const qs = params.toString();
    const next = `#/alarms${qs ? `?${qs}` : ''}`;
    if (location.hash !== next) {
      try { history.replaceState(null, '', next); } catch { /* 忽略不可写环境 */ }
    }
  }

  /* ---------- 数据加载 ---------- */
  let listSeq = 0;

  async function loadList() {
    const seq = ++listSeq;
    state.listLoading = true;
    renderTable();
    try {
      const res = await ctx.api.getFull('/api/alarms', {
        siteId: ctx.siteId,
        status: state.status,
        level: state.level || undefined,
        unitCode: state.unitCode || undefined,
        q: state.keyword || undefined,
        from: rangeFrom(state.range) || undefined,
        page: state.page,
        pageSize: state.pageSize,
      });
      if (seq !== listSeq) return;
      state.items = res.data || [];
      state.listMeta = res.meta || {};
      state.listError = null;
      state.lastUpdated = Date.now();
    } catch (err) {
      if (seq !== listSeq) return;
      state.listError = err;
      state.items = [];
      state.listMeta = null;
    } finally {
      if (seq === listSeq) {
        state.listLoading = false;
        renderTable();
        renderUpdated();
      }
    }
  }

  async function loadStats() {
    try {
      state.stats = await ctx.api.get('/api/alarms/stats', { siteId: ctx.siteId, days: 7 });
      state.statsError = null;
    } catch (err) {
      state.statsError = err;
    }
    renderStats();
  }

  async function loadOpen() {
    state.openLoading = true;
    try {
      state.openItems = (await ctx.api.get('/api/alarms', { siteId: ctx.siteId, status: 'open', pageSize: 200 })) || [];
      state.openError = null;
    } catch (err) {
      state.openError = err;
      state.openItems = [];
    } finally {
      state.openLoading = false;
      renderStats();
    }
  }

  async function loadRules() {
    state.rulesLoading = true;
    renderRules();
    try {
      state.rules = (await ctx.api.get('/api/alarm-rules', { siteId: ctx.siteId })) || [];
      state.rulesError = null;
    } catch (err) {
      state.rulesError = err;
      state.rules = [];
    } finally {
      state.rulesLoading = false;
      renderRules();
    }
  }

  /* ---------- 统计条 ---------- */
  function openLevelCounts(list) {
    const rows = Array.isArray(list) ? list : state.openItems;
    const counts = { critical: 0, major: 0, minor: 0, info: 0 };
    for (const a of rows) {
      if (Object.prototype.hasOwnProperty.call(counts, a.level)) counts[a.level] += 1;
    }
    return counts;
  }

  function renderStats() {
    ui.clear(statsHost);
    if (state.statsError && !state.stats) {
      statsHost.append(ui.errorState(state.statsError, () => loadStats()));
      return;
    }
    const s = state.stats;
    if (!s) {
      statsHost.append(ui.skeleton(5, 'kpi'));
      return;
    }
    /* 口径：活动告警 = 未处理（active）+ 已确认（acknowledged），不含已恢复/已关闭 */
    const openList = (state.openItems || []).filter((a) => a.status === 'active' || a.status === 'acknowledged');
    const counts = openLevelCounts(openList);
    const activeTotal = openList.length;
    const today = (s.byDay || []).find((d) => d.day === todayKey());
    const ackRate = typeof s.ackRate === 'number' ? s.ackRate : null;
    statsHost.append(
      ui.kpi({
        label: '活动告警',
        value: state.openLoading && !state.openItems.length ? '—' : activeTotal,
        unit: '条',
        tone: counts.critical ? 'bad' : counts.major ? 'warn' : '',
        hint: `未处理 + 已确认 · 紧急 ${counts.critical} / 重要 ${counts.major}`,
        iconName: 'alarm',
      }),
      ui.kpi({
        label: '今日新增',
        value: today ? today.total : 0,
        unit: '条',
        hint: `近 7 日合计 ${s.total} 条`,
        iconName: 'bell',
      }),
      ui.kpi({
        label: '平均确认时长 MTTA',
        value: s.mttaMin == null ? '—' : s.mttaMin,
        unit: s.mttaMin == null ? '' : '分钟',
        hint: '窗口内已确认告警',
        iconName: 'inspection',
      }),
      ui.kpi({
        label: '平均恢复时长 MTTR',
        value: s.mttrMin == null ? '—' : s.mttrMin,
        unit: s.mttrMin == null ? '' : '分钟',
        hint: '窗口内已恢复告警',
        iconName: 'workorder',
      }),
      ui.kpi({
        label: '确认率',
        value: ackRate == null ? '—' : ackRate,
        unit: ackRate == null ? '' : '%',
        tone: ackRate == null ? '' : ackRate >= 80 ? 'good' : 'warn',
        hint: '已确认 / 窗口内告警',
        iconName: 'quality',
      }),
    );
    renderTrend();
    renderDonut(counts);
  }

  function renderTrend() {
    ui.clear(trendBox);
    ui.clear(trendFoot);
    const s = state.stats;
    const days = (s && s.byDay) || [];
    if (!days.length) {
      trendBox.append(ui.empty('暂无趋势数据', '窗口内没有产生告警记录'));
      return;
    }
    ui.chartBars(trendBox, {
      items: days.map((d) => ({
        label: String(d.day || '').slice(5),
        value: d.total,
        color: d.critical ? LEVELS[0].color : d.major ? LEVELS[1].color : d.minor ? LEVELS[2].color : LEVELS[3].color,
        dim: !d.total,
      })),
      height: 158,
      unit: ' 条',
      valueFmt: (v) => String(v),
    });
    const sum = (key) => days.reduce((acc, d) => acc + (d[key] || 0), 0);
    trendFoot.append(`近 7 日共 ${sum('total')} 条：紧急 ${sum('critical')} · 重要 ${sum('major')} · 次要 ${sum('minor')} · 提示 ${sum('info')}`);
  }

  function renderDonut(counts) {
    ui.clear(donutBox);
    if (state.openError) {
      donutBox.append(ui.errorState(state.openError, () => loadOpen()));
      return;
    }
    const total = Object.values(counts).reduce((a, b) => a + b, 0);
    ui.chartDonut(donutBox, {
      items: LEVELS.map((l) => ({ label: l.label, value: counts[l.value] || 0, color: l.color })),
      size: 148,
      thickness: 16,
      centerLabel: '活动告警',
      centerValue: total,
      unit: ' 条',
    });
    donutBox.append(el('p', { class: 'tiny muted donut-note' },
      total ? `紧急与重要共 ${counts.critical + counts.major} 条，需优先研判` : '当前无活动告警'));
  }

  /* ---------- 告警表 ---------- */
  function renderUpdated() {
    updatedLabel.textContent = state.lastUpdated ? `最近更新 ${fmt.timeSec(state.lastUpdated)}` : '';
  }

  function renderTable() {
    ui.clear(tableHost);
    if (state.listLoading) {
      tableHost.append(ui.skeleton(6, 'line'));
      ui.clear(pagerHost);
      return;
    }
    if (state.listError) {
      tableHost.append(ui.errorState(state.listError, () => loadList()));
      ui.clear(pagerHost);
      return;
    }
    ui.clear(pagerHost);
    tableHost.append(ui.table({
      columns: [
        { key: 'level', label: '级别', width: '80px', render: (row) => ui.levelBadge(row.level) },
        {
          key: 'title',
          label: '告警内容',
          render: (row) => el('div', { class: 'cell-main' },
            el('span', { class: 'cell-title' }, row.title || '—'),
            el('span', { class: 'cell-sub' }, `${row.id} · ${row.detail || '无补充说明'}`),
          ),
        },
        {
          key: 'pointCode',
          label: '点位 / 单元',
          render: (row) => el('div', { class: 'cell-main' },
            el('span', {}, row.pointName || row.pointCode || '—'),
            el('span', { class: 'cell-sub mono' }, `${row.pointCode || '—'} · ${row.unitShort || row.unitName || '—'}`),
          ),
        },
        {
          key: 'value',
          label: '当前值 / 阈值',
          cls: 'num',
          render: (row) => el('div', { class: 'cell-main' },
            el('span', { class: 'mono' }, `${fmt.auto(row.value)} ${row.pointUnit || ''}`),
            el('span', { class: 'cell-sub' }, `阈值 ${row.threshold || '—'}`),
          ),
        },
        { key: 'durationMin', label: '持续', width: '92px', render: (row) => durationText(row.durationMin) },
        { key: 'status', label: '状态', width: '92px', render: (row) => ui.alarmStatus(row.status) },
        { key: 'firstAt', label: '首次触发', width: '128px', render: (row) => el('span', { class: 'mono' }, fmt.dateTime(row.firstAt)) },
        {
          key: 'op',
          label: '',
          width: '68px',
          render: (row) => el('button', { class: 'btn ghost sm', type: 'button', onclick: () => openAlarm(row.id) }, '详情'),
        },
      ],
      rows: state.items,
      emptyText: '当前筛选条件下没有告警',
      emptySub: '可切换状态或时间范围，或点击「重置筛选」查看全部告警',
      onRow: (row) => openAlarm(row.id),
    }));
    renderPager();
  }

  function renderPager() {
    ui.clear(pagerHost);
    const meta = state.listMeta || {};
    const total = meta.total || 0;
    if (!total) return;
    const pages = Math.max(1, meta.pages || 1);
    const pageNo = Math.min(state.page, pages);
    const prev = el('button', {
      class: 'btn ghost sm', type: 'button', disabled: pageNo <= 1,
      onclick: () => { if (pageNo > 1) { state.page = pageNo - 1; loadList(); } },
    }, '上一页');
    const next = el('button', {
      class: 'btn ghost sm', type: 'button', disabled: pageNo >= pages,
      onclick: () => { if (pageNo < pages) { state.page = pageNo + 1; loadList(); } },
    }, '下一页');
    pagerHost.append(
      el('span', { class: 'tiny muted' }, `共 ${total} 条 · 第 ${pageNo} / ${pages} 页 · 每页 ${meta.pageSize || state.pageSize} 条`),
      el('span', { class: 'spacer' }),
      prev,
      next,
    );
  }

  /* ---------- 规则表 ---------- */
  function renderRules() {
    ui.clear(rulesHint);
    rulesHint.append(ui.notice(
      canRule
        ? '规则阈值由工艺负责人维护；停用规则会同步关闭该规则在办的告警，操作写入审计日志。'
        : '当前账号没有参数编辑权限（param.edit），规则仅可查看，启停开关已禁用。',
      canRule ? '' : 'warn',
    ));
    ui.clear(rulesHost);
    if (state.rulesLoading) {
      rulesHost.append(ui.skeleton(6, 'line'));
      return;
    }
    if (state.rulesError) {
      rulesHost.append(ui.errorState(state.rulesError, () => loadRules()));
      return;
    }
    rulesHost.append(ui.table({
      columns: [
        {
          key: 'name',
          label: '规则',
          render: (row) => el('div', { class: 'cell-main' },
            el('span', { class: 'cell-title' }, row.name || '—'),
            el('span', { class: 'cell-sub mono' }, row.key || row.id),
          ),
        },
        {
          key: 'pointCode',
          label: '点位',
          render: (row) => el('div', { class: 'cell-main' },
            el('span', {}, row.pointName || row.pointCode || '—'),
            el('span', { class: 'cell-sub mono' }, row.pointCode || '—'),
          ),
        },
        {
          key: 'condition',
          label: '触发条件',
          render: (row) => el('div', { class: 'cell-main' },
            el('span', {}, conditionText(row)),
            el('span', { class: 'cell-sub' }, row.latest ? `最新 ${fmt.auto(row.latest.value)} ${row.unit || ''} · ${fmt.time(row.latest.t)}` : '暂无最新读数'),
          ),
        },
        { key: 'level', label: '级别', width: '74px', render: (row) => ui.levelBadge(row.level) },
        { key: 'durationSec', label: '持续', width: '76px', render: (row) => `${Math.round((row.durationSec || 0) / 60)} 分` },
        { key: 'hysteresis', label: '迟滞', width: '68px', render: (row) => (row.hysteresis ? fmt.auto(row.hysteresis) : '—') },
        { key: 'escalateSec', label: '升级', width: '86px', render: (row) => (row.escalateSec ? durationText(row.escalateSec / 60) : '不升级') },
        {
          key: 'openCount',
          label: '在办',
          width: '66px',
          render: (row) => (row.openCount ? ui.badge(`${row.openCount} 条`, 'warn') : el('span', { class: 'muted' }, '0')),
        },
        {
          key: 'enabled',
          label: '启用',
          width: '96px',
          render: (row) => {
            const on = !!row.enabled;
            const btn = el('button', {
              class: `switch${on ? ' on' : ''}${canRule ? '' : ' readonly'}`,
              type: 'button',
              role: 'switch',
              'aria-checked': on ? 'true' : 'false',
              'aria-label': `${row.name} ${on ? '已启用' : '已停用'}`,
              disabled: !canRule,
              title: canRule ? (on ? '点击停用该规则' : '点击启用该规则') : '无参数编辑权限',
            }, el('i', {}));
            btn.addEventListener('click', () => toggleRule(row));
            return el('span', { class: 'switch-wrap' }, btn, el('span', { class: 'tiny muted' }, on ? '启用' : '停用'));
          },
        },
      ],
      rows: state.rules || [],
      emptyText: '暂无告警规则',
      emptySub: '规则由工艺负责人维护，当前站点尚未配置',
    }));
  }

  async function toggleRule(rule) {
    if (!canRule) {
      ui.toast('当前账号没有参数编辑权限，无法修改规则', 'error');
      return;
    }
    if (state.busy) return;
    if (rule.enabled) {
      const okToDisable = await ctx.confirm({
        title: '停用告警规则',
        message: `停用「${rule.name}」后不再产生新告警。`,
        detail: rule.openCount
          ? `该规则当前有 ${rule.openCount} 条在办告警，停用后会被自动关闭（关闭原因：规则已停用）。`
          : '该规则当前没有在办告警。停用操作写入审计日志。',
        confirmText: '停用规则',
        danger: true,
      });
      if (!okToDisable) return;
    }
    state.busy = true;
    try {
      await ctx.api.patch(`/api/alarm-rules/${encodeURIComponent(rule.id)}`, { enabled: !rule.enabled });
      ui.toast(`规则已${rule.enabled ? '停用' : '启用'}`, 'success');
      await Promise.all([loadRules(), loadList(), loadStats(), loadOpen()]);
    } catch (err) {
      ui.toast(err.message || '规则更新失败', 'error');
    } finally {
      state.busy = false;
    }
  }

  /* ---------- 详情抽屉 ---------- */
  async function openAlarm(id) {
    const body = el('div', { class: 'stack drawer-stack' }, ui.loading('正在读取告警详情…'));
    const dr = ui.drawer({ title: '告警详情', sub: String(id), body });
    let alarm;
    try {
      alarm = await ctx.api.get(`/api/alarms/${encodeURIComponent(id)}`);
    } catch (err) {
      ui.clear(body);
      body.append(ui.errorState(err, () => { dr.close(); openAlarm(id); }));
      return;
    }
    if (!alarm) {
      ui.clear(body);
      body.append(ui.empty('未找到该告警', '可能已被删除或站点权限不足'));
      return;
    }
    ui.clear(body);

    const timelineItems = (alarm.timeline || []).map((t) => ({
      time: fmt.dateTime(t.at),
      title: ACTION_NAME[t.action] || t.action,
      note: [t.by, t.note].filter(Boolean).join(' · '),
      tone: t.action === 'create' ? 'bad' : t.action === 'resolve' ? 'good' : t.action === 'ack' ? 'warn' : '',
    }));

    const rule = alarm.rule || {};
    const point = alarm.point || {};
    const device = alarm.device || {};
    const reading = alarm.reading || {};

    const chartHost = el('div', { class: 'chart-host' }, ui.skeleton(1, 'block'));
    const chartNote = el('p', { class: 'tiny muted' }, '');
    const chartCard = ui.card({
      title: '关联点位 24 小时曲线',
      sub: alarm.pointName ? `${alarm.pointName}（${alarm.pointCode}）` : '点位数据',
      body: el('div', { class: 'stack tight' }, chartHost, chartNote),
    });

    body.append(
      el('div', { class: 'detail-top' },
        el('div', { class: 'row wrap' },
          ui.levelBadge(alarm.level),
          ui.alarmStatus(alarm.status),
          ui.badge(`已触发 ${alarm.count || 1} 次`, 'muted'),
          ui.badge(durationText(alarm.durationMin), 'info', '持续时长'),
        ),
        el('h3', { class: 'detail-title' }, alarm.title || '—'),
        el('p', { class: 'detail-desc' }, alarm.detail || '无补充说明'),
      ),

      ui.card({
        title: '基本信息',
        body: ui.kv([
          ['告警编号', el('span', { class: 'mono' }, alarm.id)],
          ['站点', alarm.siteName || ctx.siteId],
          ['工艺单元', alarm.unitName || alarm.unitShort || '—'],
          ['点位', `${alarm.pointName || '—'}（${alarm.pointCode || '—'}）`],
          ['设备', device.name ? `${device.name}（${device.code || '—'}）` : '—'],
          ['当前读数', `${fmt.auto(reading.value == null ? alarm.value : reading.value)} ${point.unit || alarm.pointUnit || ''}`],
          ['触发阈值', alarm.threshold || rule.threshold || '—'],
          ['首次触发', fmt.dateTime(alarm.firstAt)],
          ['最近触发', fmt.dateTime(alarm.lastAt)],
          ['确认', alarm.ackAt ? `${alarm.ackByName || alarm.ackBy || '—'} · ${fmt.dateTime(alarm.ackAt)}` : '尚未确认'],
          ['恢复', alarm.resolvedAt ? `${alarm.resolvedByName || alarm.resolvedBy || '—'} · ${fmt.dateTime(alarm.resolvedAt)}` : '尚未恢复'],
          ['通知渠道', (alarm.notifyChannels || []).map((c) => (c === 'inapp' ? '站内消息' : c)).join('、') || '—'],
        ]),
      }),

      ui.card({
        title: '触发依据',
        sub: rule.name ? `规则「${rule.name}」（${rule.id}）` : '自定义 / 网关事件',
        body: el('div', { class: 'stack tight' },
          ui.kvRows([
            ['判定条件', conditionText(rule)],
            ['持续阈值', rule.durationSec ? `连续越限 ${rule.durationSec} 秒后触发` : '即时触发'],
            ['迟滞带', rule.hysteresis ? `±${fmt.auto(rule.hysteresis)} ${rule.unit || ''}` : '未设置'],
            ['升级时限', rule.escalateSec ? `未处理 ${durationText(rule.escalateSec / 60)} 后升级` : '不自动升级'],
            ['数据质量', reading.quality ? ui.qualityBadge(reading.quality) : '—'],
          ]),
          el('p', { class: 'tiny muted' }, '判定逻辑：越限持续达到阈值时长才生成告警，避免瞬时波动误报；恢复需离开迟滞带。'),
        ),
      }),

      chartCard,

      ui.card({
        title: '处置记录',
        sub: '时间线按发生先后排列，来源为审计记录',
        body: timelineItems.length
          ? ui.timeline(timelineItems)
          : ui.empty('暂无处置记录', '该告警尚未有确认或恢复动作'),
      }),

      ui.card({
        title: '关联工单',
        sub: (alarm.workorders || []).length ? `共 ${(alarm.workorders || []).length} 张` : '未转工单',
        body: (alarm.workorders || []).length
          ? el('div', { class: 'stack tight' }, (alarm.workorders || []).map((wo) => el('div', { class: 'row wrap wo-row' },
            ui.woStatus(wo.status),
            el('span', { class: 'wo-title' }, wo.title || wo.id),
            el('span', { class: 'spacer' }),
            el('button', { class: 'btn ghost sm', type: 'button', onclick: () => { dr.close(); ctx.go(`#/workorders?id=${wo.id}`); } }, '查看工单'),
          )))
          : ui.empty('尚未关联工单', '如需现场核查，可在下方「转工单」创建'),
      }),

      el('div', { class: 'drawer-actions' },
        !canHandle
          ? ui.notice('当前账号没有告警处置权限（alarm.handle），仅可查看。请联系值班人员或区域管理员处理。', 'warn')
          : el('div', { class: 'row wrap' },
            alarm.status === 'active' ? el('button', { class: 'btn', type: 'button', onclick: () => ackFlow(alarm) }, '确认告警') : null,
            alarm.status === 'active' || alarm.status === 'acknowledged'
              ? el('button', { class: 'btn primary', type: 'button', onclick: () => resolveFlow(alarm) }, '恢复告警')
              : null,
            alarm.status === 'active' || alarm.status === 'acknowledged' || alarm.status === 'resolved'
              ? el('button', { class: 'btn ghost', type: 'button', onclick: () => closeFlow(alarm) }, '关闭告警')
              : null,
            canOrder ? el('button', { class: 'btn ghost', type: 'button', onclick: () => transferFlow(alarm, dr) }, '转工单') : null,
          ),
        el('p', { class: 'tiny muted' }, '确认：已有人跟进；恢复：异常已消失且核实（须填依据，计入 MTTR）；关闭：不再跟进（须填结论）。'),
      ),
    );

    loadPointChart(alarm, chartHost, chartNote).catch(() => {});
  }

  async function loadPointChart(alarm, chartHost, noteNode) {
    if (!alarm.pointId) {
      ui.clear(chartHost);
      chartHost.append(ui.empty('无可绘制点位', '该告警未关联点位或点位已移除'));
      return;
    }
    let detail;
    try {
      detail = await ctx.api.get(`/api/points/${encodeURIComponent(alarm.pointId)}`);
    } catch (err) {
      ui.clear(chartHost);
      chartHost.append(ui.errorState(err, () => { ui.clear(chartHost); loadPointChart(alarm, chartHost, noteNode); }));
      return;
    }
    const history = (detail && detail.history) || [];
    const unit = (detail && detail.unit) || (alarm.point && alarm.point.unit) || alarm.pointUnit || '';
    const name = (detail && detail.name) || alarm.pointName || alarm.pointCode || '点位';
    ui.clear(chartHost);
    if (!history.length) {
      chartHost.append(ui.empty('暂无历史数据', '该点位最近 24 小时没有有效采样'));
      return;
    }
    ui.chartLine(chartHost, {
      series: [{ name, points: history.map((p) => ({ t: p.t, v: p.v })), color: levelMeta(alarm.level).color }],
      height: 186,
      unit,
      showLegend: false,
    });
    const stats = (detail && detail.stats) || {};
    const parts = [];
    if (stats.min != null) parts.push(`最低 ${fmt.auto(stats.min)}`);
    if (stats.avg != null) parts.push(`均值 ${fmt.auto(stats.avg)}`);
    if (stats.max != null) parts.push(`最高 ${fmt.auto(stats.max)}`);
    if (stats.count != null) parts.push(`${stats.count} 个采样点（约 5 分钟一采）`);
    noteNode.textContent = parts.length ? parts.join(' · ') : '样本不足';
  }

  /* ---------- 处置动作 ---------- */
  function actionModal({ title, sub, body, confirmText, danger, run }) {
    const err = el('div', { class: 'error-text', hidden: true });
    const cancel = el('button', { class: 'btn', type: 'button' }, '取消');
    const ok = el('button', { class: `btn ${danger ? 'danger' : 'primary'}`, type: 'button' }, confirmText);
    const m = ui.modal({
      title,
      sub,
      body: el('div', { class: 'stack' }, body, err),
      footer: el('div', { class: 'row', style: { width: '100%' } }, el('span', { class: 'spacer' }), cancel, ok),
    });
    cancel.addEventListener('click', () => m.close());
    ok.addEventListener('click', async () => {
      err.hidden = true;
      ok.disabled = true;
      const label = ok.textContent;
      ok.textContent = '提交中…';
      try {
        await run();
        m.close();
      } catch (e) {
        err.hidden = false;
        err.textContent = e && e.message ? e.message : '操作失败，请稍后重试';
      } finally {
        ok.disabled = false;
        ok.textContent = label;
      }
    });
    return m;
  }

  async function afterAction(message, alarmId) {
    ui.toast(message, 'success');
    await Promise.all([loadList(), loadStats(), loadOpen()]);
    if (alarmId) openAlarm(alarmId);
  }

  function ackFlow(alarm) {
    const note = ui.textarea({ rows: 3, maxlength: 300, placeholder: '可填写研判说明，例如「已联系值班人员，正在核对在线表」' });
    actionModal({
      title: '确认告警',
      sub: `${alarm.id} · ${alarm.title}`,
      body: ui.field('研判说明（选填）', note, '确认仅表示已有人跟进，不改变告警状态；确认人、时间写入处置记录。'),
      confirmText: '确认告警',
      run: async () => {
        await ctx.api.post(`/api/alarms/${encodeURIComponent(alarm.id)}/ack`, { note: note.value.trim() });
        await afterAction('告警已确认', alarm.id);
      },
    });
  }

  function resolveFlow(alarm) {
    const note = ui.textarea({ rows: 4, maxlength: 300, placeholder: '必填：说明恢复依据，例如「便携 pH 计复核 6.92，在线表已回到 6.9，碱度投加调整后恢复」' });
    actionModal({
      title: '恢复告警',
      sub: `${alarm.id} · ${alarm.title}`,
      body: el('div', { class: 'stack tight' },
        ui.notice('恢复表示异常已消失且经过核实，会写入处置记录并作为 MTTR 统计依据。', ''),
        ui.field('恢复依据 / 处置说明', note, '必填，最多 300 字。请写清核实方式与结果。'),
      ),
      confirmText: '确认恢复',
      run: async () => {
        const text = note.value.trim();
        if (!text) throw new Error('请填写恢复依据或处置说明');
        await ctx.api.post(`/api/alarms/${encodeURIComponent(alarm.id)}/resolve`, { note: text });
        await afterAction('告警已恢复', alarm.id);
      },
    });
  }

  function closeFlow(alarm) {
    const note = ui.textarea({ rows: 4, maxlength: 300, placeholder: '必填：填写关闭结论，例如「在线表校验后修正，属误报」或「现场处理后指标恢复正常」' });
    actionModal({
      title: '关闭告警',
      sub: `${alarm.id} · ${alarm.title}`,
      body: el('div', { class: 'stack tight' },
        ui.notice('关闭后该告警不再出现在待处理列表，仍可在「全部」中查询。', 'warn'),
        ui.field('关闭结论', note, '必填，最多 300 字。'),
      ),
      confirmText: '关闭告警',
      danger: true,
      run: async () => {
        const text = note.value.trim();
        if (!text) throw new Error('请填写关闭结论');
        await ctx.api.post(`/api/alarms/${encodeURIComponent(alarm.id)}/close`, { note: text });
        await afterAction('告警已关闭', alarm.id);
      },
    });
  }

  function transferFlow(alarm, dr) {
    if (!canOrder) {
      ui.toast('当前账号没有工单处理权限（workorder.handle）', 'error');
      return;
    }
    const existing = (alarm.workorders || [])[0];
    if (existing) {
      ui.toast(`该告警已关联工单 ${existing.id}`, 'info');
      if (dr) dr.close();
      ctx.go(`#/workorders?id=${existing.id}`);
      return;
    }
    const title = ui.input({ value: `${alarm.title} · 现场核查`, maxlength: 120 });
    const type = ui.select([{ value: 'repair', label: '维修' }, { value: 'inspection', label: '巡检' }]);
    type.value = 'repair';
    const priority = ui.select([
      { value: 'normal', label: '普通' },
      { value: 'high', label: '高' },
      { value: 'urgent', label: '紧急' },
    ]);
    priority.value = alarm.level === 'critical' ? 'urgent' : alarm.level === 'major' ? 'high' : 'normal';
    const desc = ui.textarea({
      rows: 4,
      maxlength: 1000,
      value: [alarm.detail, `告警编号：${alarm.id}`, `首次触发：${fmt.dateTime(alarm.firstAt)}`].filter(Boolean).join('\n'),
    });
    actionModal({
      title: '转工单',
      sub: `关联告警 ${alarm.id}`,
      body: el('div', { class: 'stack tight' },
        ui.field('工单标题', title, '默认引用告警标题，可补充处理动作'),
        el('div', { class: 'form-grid' }, ui.field('工单类型', type), ui.field('优先级', priority)),
        ui.field('问题描述', desc, '默认带入告警说明与触发时间'),
        el('p', { class: 'tiny muted' }, '创建后跳转到工单详情，由工单处理人接单跟进。'),
      ),
      confirmText: '创建工单',
      run: async () => {
        const t = title.value.trim();
        if (!t) throw new Error('请填写工单标题');
        const created = await ctx.api.post('/api/workorders', {
          siteId: ctx.siteId,
          alarmId: alarm.id,
          title: t,
          type: type.value,
          priority: priority.value,
          unitCode: alarm.unitCode || undefined,
          deviceId: alarm.device ? alarm.device.id : undefined,
          description: desc.value.trim(),
        });
        ui.toast('工单已创建', 'success');
        if (dr) dr.close();
        await Promise.all([loadList(), loadStats(), loadOpen()]);
        if (created && created.id) ctx.go(`#/workorders?id=${created.id}`);
      },
    });
  }

  /* ---------- 刷新入口 ---------- */
  async function refreshAll(manual) {
    await Promise.all([
      loadList(),
      loadStats(),
      loadOpen(),
      state.tab === 'rules' || !state.rules ? loadRules() : Promise.resolve(),
    ]);
    if (manual) ui.toast('已刷新告警数据', 'success');
  }

  refreshBtn.addEventListener('click', () => refreshAll(true));

  /* ---------- 初始加载与实时 ---------- */
  renderStats();
  renderTable();
  await refreshAll(false);
  renderUpdated();

  const scheduleLive = ui.debounce(() => {
    loadList().catch(() => {});
    loadStats().catch(() => {});
    loadOpen().catch(() => {});
  }, 700);

  ctx.subscribe((evt) => {
    if (evt && evt.type === 'alarm') scheduleLive();
  });

  ctx.every(30000, () => {
    loadList().catch(() => {});
    loadStats().catch(() => {});
    loadOpen().catch(() => {});
  });
}

import * as ui from '../ui.js';

export const meta = { title: '水质与化验' };

/* 指标与限值口径：GB 18918-2002《城镇污水处理厂污染物排放标准》一级 A 标准。
   平台没有 COD / 氨氮 / 总氮 / 总磷 / 悬浮物的在线仪表，全部来自人工取样化验。 */
const METRICS = ['COD', 'NH3N', 'TN', 'TP', 'SS'];
const METRIC_NAMES = { COD: 'COD', NH3N: '氨氮', TN: '总氮', TP: '总磷', SS: '悬浮物' };
const LIMITS = { COD: 50, NH3N: 5, TN: 15, TP: 0.5, SS: 10 };
const DIGITS = { COD: 1, NH3N: 2, TN: 1, TP: 2, SS: 1 };
const POINT_FALLBACK = {
  inlet: '进水（原料罐）',
  baf_out: 'BAF 出水',
  sulfur_out: '硫自养池出水',
  outlet: '总出水（清水池）',
};
const STATUS = {
  pending_review: { name: '待复核', kind: 'warn' },
  reviewed: { name: '已复核', kind: 'good' },
  rejected: { name: '已驳回', kind: 'bad' },
  draft: { name: '草稿', kind: 'muted' },
};
const METHOD_LABEL = '检测方法依据 HJ 828 / HJ 535 / HJ 636 / GB 11893 / GB 11901（记录中逐项列出）';

function hasPerm(ctx, perm) {
  return (ctx.user && Array.isArray(ctx.user.perms) ? ctx.user.perms : []).includes(perm);
}

function pointName(ctx, key) {
  const list = (ctx.meta && Array.isArray(ctx.meta.samplePoints)) ? ctx.meta.samplePoints : [];
  const hit = list.find(p => p.key === key);
  return (hit && hit.name) || POINT_FALLBACK[key] || key;
}

function pointOptions(ctx) {
  return ['inlet', 'baf_out', 'sulfur_out', 'outlet'].map(key => ({ key, name: pointName(ctx, key) }));
}

function statusOf(status) {
  return STATUS[status] || { name: status || '未知', kind: 'muted' };
}

function isExceed(row, metric) {
  const list = Array.isArray(row.exceed) ? row.exceed : [];
  return list.indexOf(METRIC_NAMES[metric]) !== -1;
}

function metricValue(row, metric) {
  const m = (row && row.metrics) || {};
  const v = m[metric];
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function toLocalInput(d) {
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}

function isoOrEmpty(value, endOfDay) {
  if (!value) return undefined;
  const d = new Date(`${value}T${endOfDay ? '23:59:59' : '00:00:00'}`);
  return Number.isNaN(d.getTime()) ? undefined : d.toISOString();
}

function monthStart() {
  const now = new Date();
  return new Date(now.getFullYear(), now.getMonth(), 1).getTime();
}

async function loadData(ctx) {
  const siteId = ctx.siteId;
  const [stats, lab, inlet] = await Promise.all([
    ctx.api.get('/api/lab/stats', { siteId, days: 30 }),
    ctx.api.getFull('/api/lab', { siteId, pageSize: 200 }),
    ctx.api.getFull('/api/lab', { siteId, samplePoint: 'inlet', pageSize: 200 }),
  ]);
  return { stats: stats || {}, lab, inlet };
}

export async function render(root, ctx) {
  const page = ui.el('div', { class: 'p-quality stack' });
  ui.mount(root, page);

  const canEntry = hasPerm(ctx, 'inspection.execute');
  const canReview = hasPerm(ctx, 'lab.review');
  const reload = () => { ctx.refresh(); };

  const actions = [
    ui.el('button', {
      class: 'btn ghost sm', type: 'button',
      onclick: () => { ctx.toast('正在刷新化验数据…', 'info', 1500); reload(); },
    }, ui.icon('refresh'), '刷新'),
  ];
  if (canEntry) {
    actions.push(ui.el('button', {
      class: 'btn primary sm', type: 'button',
      onclick: () => openEntry(ctx, reload),
    }, '录入化验'));
  }

  const head = ui.pageHead(
    '水质与化验',
    '出水水质以人工取样化验为准；在线仪表只覆盖 pH、DO、液位、流量、压力、功率与累计量，不产生 COD / 氨氮 / 总氮 / 总磷 / SS 实时值。',
    actions,
  );
  ui.mount(page, head, ui.loading('加载化验数据…'));

  let data;
  try {
    data = await loadData(ctx);
  } catch (err) {
    ui.mount(page, head, ui.card({ body: ui.errorState(err, reload) }));
    return;
  }

  const content = ui.el('div', { class: 'stack' });
  ui.mount(page, head, content);
  paint(ctx, content, data, { canEntry, canReview, reload });
}

function paint(ctx, host, data, opts) {
  const stats = data.stats || {};
  const lab = data.lab || { data: [], meta: {} };
  const labRows = Array.isArray(lab.data) ? lab.data : [];
  const latest = stats.latestByPoint || {};
  const outlet = latest.outlet || null;
  const metricNames = stats.metricNames || METRIC_NAMES;
  const limits = Object.assign({}, LIMITS, stats.limits || {});

  const pendingCount = Number.isFinite(stats.pending) ? stats.pending : (lab.meta && lab.meta.pending) || 0;
  const monthCount = labRows.filter(r => {
    const t = new Date(r.sampledAt).getTime();
    return Number.isFinite(t) && t >= monthStart();
  }).length;

  /* ---------------- KPI：最近一次出水化验（人工） ---------------- */
  const kpiCards = METRICS.map(key => {
    const value = outlet ? metricValue(outlet, key) : null;
    const exceed = outlet ? isExceed(outlet, key) : false;
    const st = outlet ? statusOf(outlet.status) : null;
    const tone = !outlet ? 'blue' : exceed ? 'bad' : st.kind === 'warn' ? 'warn' : 'good';
    const delta = outlet
      ? ui.el('span', { class: exceed ? 'down' : 'up' },
        `${ui.fmt.dateTime(outlet.sampledAt)} 取样 · ${st.name} · ${exceed ? '超一级A限值' : '达标'}`)
      : ui.el('span', { class: 'muted' }, '暂无出水化验记录');
    return ui.kpi({
      label: `${metricNames[key] || METRIC_NAMES[key]}（出水 · 人工化验）`,
      value: value === null ? '—' : ui.fmt.num(value, DIGITS[key]),
      unit: 'mg/L',
      tone,
      hint: `一级A限值 ${limits[key]} mg/L；数据来自人工取样化验，非在线实时值`,
      delta,
    });
  });

  kpiCards.push(ui.kpi({
    label: '本月化验批次',
    value: ui.fmt.int(monthCount),
    unit: '批',
    tone: 'blue',
    hint: '本自然月 1 日至今日的化验记录数（含各取样点）',
    delta: ui.el('span', { class: 'muted' }, `近 30 天共 ${lab.meta && lab.meta.total != null ? lab.meta.total : labRows.length} 条`),
  }));
  kpiCards.push(ui.kpi({
    label: '待复核',
    value: ui.fmt.int(pendingCount),
    unit: '条',
    tone: pendingCount > 0 ? 'warn' : 'good',
    hint: 'lab.review 权限可复核；复核通过后计入出水统计',
    delta: ui.el('span', { class: pendingCount > 0 ? 'down' : 'up' }, pendingCount > 0 ? '需工艺/区域复核人确认' : '全部已复核'),
  }));

  const kpiGrid = ui.el('div', { class: 'grid cols-4' }, kpiCards);

  const sourceNotice = ui.notice(
    '数据来源说明：COD、氨氮、总氮、总磷、悬浮物（SS）为人工取样化验数据（人工/化验通道）；'
    + '在线仪表通道仅有 pH、DO、液位、流量、压力、功率与累计量。页面不将化验值渲染为「在线实时值」。'
    + '限值参考 GB 18918-2002《城镇污水处理厂污染物排放标准》一级 A 标准。',
    'warn',
  );

  /* ---------------- 趋势卡：指标切换 + 限值参考线 ---------------- */
  const trendHost = ui.el('div', { class: 'chart q-trend' });
  const tabHost = ui.el('div', { class: 'tabs' });
  const state = { metric: 'COD' };

  function drawTabs() {
    ui.mount(tabHost, ...METRICS.map(key => ui.el('button', {
      class: `tab${state.metric === key ? ' active' : ''}`,
      type: 'button',
      onclick: () => { state.metric = key; drawTabs(); drawTrend(); },
    }, METRIC_NAMES[key])));
  }

  function drawTrend() {
    const key = state.metric;
    const inletRows = Array.isArray(data.inlet && data.inlet.data) ? data.inlet.data : [];
    const inletPoints = inletRows
      .map(r => ({ t: new Date(r.sampledAt).getTime(), v: metricValue(r, key) }))
      .filter(p => Number.isFinite(p.t) && p.v !== null)
      .sort((a, b) => a.t - b.t);
    const outletPoints = ((stats.series && stats.series[key]) || [])
      .map(p => ({ t: Number(p.t), v: Number(p.v) }))
      .filter(p => Number.isFinite(p.t) && Number.isFinite(p.v));

    if (!inletPoints.length && !outletPoints.length) {
      ui.mount(trendHost, ui.empty(`「${METRIC_NAMES[key]}」暂无化验数据`, '人工取样化验录入并复核后显示曲线；不使用在线仪表估算'));
      return;
    }

    const all = inletPoints.concat(outletPoints);
    const tMin = Math.min(...all.map(p => p.t));
    const tMax = Math.max(...all.map(p => p.t));
    const limit = limits[key];
    const series = [
      { name: '进水（人工化验）', color: '#1f6fb2', points: inletPoints },
      { name: '总出水（人工化验）', color: '#0b7a70', points: outletPoints },
      { name: `一级A限值 ${limit}`, color: '#b32d22', points: [{ t: tMin, v: limit }, { t: tMax, v: limit }] },
    ];
    const svg = ui.chartLine(trendHost, {
      series,
      height: 240,
      unit: 'mg/L',
      digits: DIGITS[key],
      area: false,
      showLegend: true,
    });
    if (svg) {
      const lines = Array.from(svg.querySelectorAll('.series-line'));
      const limitLine = lines.find(p => p.getAttribute('stroke') === '#b32d22');
      if (limitLine) {
        limitLine.setAttribute('stroke-dasharray', '6 4');
        limitLine.setAttribute('stroke-width', '1.4');
      }
      const circles = svg.querySelectorAll('circle');
      if (circles.length) circles[circles.length - 1].remove();
    }
  }

  drawTabs();

  const trendSub = `近 30 天 · 进水 ${pointName(ctx, 'inlet')} / 出水 ${pointName(ctx, 'outlet')} · 虚线为一级A限值参考`;
  const trendCard = ui.card({
    title: '水质趋势（人工化验）',
    sub: trendSub,
    actions: tabHost,
    body: trendHost,
    foot: ui.el('span', { class: 'tiny muted' }, `参考标准：GB 18918-2002 一级 A（COD ${limits.COD}、氨氮 ${limits.NH3N}、总氮 ${limits.TN}、总磷 ${limits.TP}、SS ${limits.SS} mg/L）`),
  });

  /* ---------------- 去除率 + 达标率 ---------------- */
  const removalHost = ui.el('div', { class: 'chart q-removal' });
  const removal = stats.removalRates || {};
  const compliance = stats.compliance || {};
  const removalCard = ui.card({
    title: '去除率（进水 → 总出水）',
    sub: '按最近一次进/出水化验计算 · 人工化验通道',
    body: removalHost,
    foot: ui.el('div', { class: 'row wrap tiny muted' }, ...METRICS.map(key => {
      const c = compliance[key] || {};
      return ui.el('span', {}, `${METRIC_NAMES[key]}达标率 ${c.rate == null ? '—' : `${ui.fmt.num(c.rate, 1)}%`}（${c.samples || 0} 样本）`);
    })),
  });
  if (METRICS.some(key => typeof removal[key] === 'number')) {
    ui.chartBars(removalHost, {
      items: METRICS.map((key, i) => ({
        label: METRIC_NAMES[key],
        value: typeof removal[key] === 'number' ? removal[key] : 0,
        color: ui.palette(i),
      })),
      height: 200,
      unit: '%',
      valueFmt: v => `${Number(v).toFixed(1)}%`,
    });
  } else {
    ui.mount(removalHost, ui.empty('暂无去除率数据', '需要同一时间窗口内进水与总出水的复核化验记录'));
  }

  /* ---------------- 化验记录表 ---------------- */
  const tableState = { samplePoint: '', status: '', from: '', to: '' };
  const tableArea = ui.el('div', { class: 'q-table-area' });
  const countLabel = ui.el('span', { class: 'tiny muted' }, '');

  const filterBar = ui.el('div', { class: 'toolbar q-filters' },
    ui.field('取样点', ui.select(
      [{ value: '', label: '全部取样点' }].concat(pointOptions(ctx).map(p => ({ value: p.key, label: p.name }))),
      { onchange: e => { tableState.samplePoint = e.target.value; reloadTable(); } },
    )),
    ui.field('状态', ui.select(
      [{ value: '', label: '全部状态' }].concat(Object.keys(STATUS).map(k => ({ value: k, label: STATUS[k].name }))),
      { onchange: e => { tableState.status = e.target.value; reloadTable(); } },
    )),
    ui.field('起始日期', ui.input({
      type: 'date', value: tableState.from,
      onchange: e => { tableState.from = e.target.value; reloadTable(); },
    })),
    ui.field('截止日期', ui.input({
      type: 'date', value: tableState.to,
      onchange: e => { tableState.to = e.target.value; reloadTable(); },
    })),
    ui.el('button', { class: 'btn ghost sm', type: 'button', onclick: () => ctx.refresh() }, '重置'),
  );

  function renderRows(rows, meta) {
    ui.mount(tableArea, ui.table({
      columns: tableColumns(ctx, opts.canReview),
      rows,
      emptyText: '暂无符合条件的化验记录',
      emptySub: '可调整筛选条件，或由具备 inspection.execute 权限的人员录入化验',
      onRow: row => openDetail(ctx, row, opts, row.status === 'pending_review'),
      rowClass: row => (row.status === 'pending_review' ? 'row-attention' : ''),
    }));
    if (meta) {
      countLabel.textContent = `共 ${meta.total || 0} 条 · 待复核 ${meta.pending || 0} 条 · 数据来源：人工取样化验`;
    }
  }

  async function reloadTable() {
    ui.mount(tableArea, ui.loading('查询化验记录…'));
    try {
      const res = await ctx.api.getFull('/api/lab', {
        siteId: ctx.siteId,
        pageSize: 200,
        samplePoint: tableState.samplePoint || undefined,
        status: tableState.status || undefined,
        from: isoOrEmpty(tableState.from, false),
        to: isoOrEmpty(tableState.to, true),
      });
      renderRows(Array.isArray(res.data) ? res.data : [], res.meta);
    } catch (err) {
      ui.mount(tableArea, ui.errorState(err, reloadTable));
    }
  }

  const tableCard = ui.card({
    title: '化验记录',
    sub: `${METHOD_LABEL}；QC 标记为平行样/加标回收质控结果`,
    actions: countLabel,
    body: [filterBar, tableArea],
  });

  renderRows(labRows, lab.meta);
  if (opts.canReview && pendingCount > 0) {
    countLabel.appendChild(ui.el('span', { class: 'badge warn' }, '存在待复核记录'));
  }

  ui.mount(host, kpiGrid, sourceNotice, trendCard, removalCard, tableCard);
  drawTrend();
}

function tableColumns(ctx, canReview) {
  return [
    {
      key: 'sampledAt', label: '取样时间', width: '132px',
      render: row => ui.el('div', {},
        ui.el('div', { class: 'mono' }, ui.fmt.dateTime(row.sampledAt)),
        ui.el('span', { class: 'cell-sub' }, row.id || ''),
      ),
    },
    {
      key: 'samplePoint', label: '取样点', width: '128px',
      render: row => ui.el('div', {},
        ui.el('span', {}, row.samplePointText || row.samplePointName || pointName(ctx, row.samplePoint)),
        ui.el('span', { class: 'cell-sub' }, row.samplePoint || ''),
      ),
    },
    {
      key: 'metrics', label: '指标值（mg/L）',
      render: row => ui.el('div', { class: 'q-metrics' }, ...METRICS.map(key => {
        const v = metricValue(row, key);
        const exceed = isExceed(row, key);
        return ui.el('span', {
          class: `q-metric${exceed ? ' over' : ''}`,
          title: `${METRIC_NAMES[key]} ${v == null ? '—' : ui.fmt.num(v, DIGITS[key])} mg/L${exceed ? '，超一级A限值' : ''}`,
        },
        ui.el('b', {}, METRIC_NAMES[key]),
        v == null ? '—' : ui.fmt.num(v, DIGITS[key]),
        );
      })),
    },
    {
      key: 'method', label: '检测方法', width: '160px',
      render: row => ui.el('span', { class: 'q-method', title: row.method || '' }, row.method || '—'),
    },
    {
      key: 'operatorName', label: '录入人', width: '86px',
      render: row => ui.el('div', {},
        ui.el('span', {}, row.operatorName || row.operatorId || '—'),
        ui.el('span', { class: 'cell-sub' }, row.recordedAt ? ui.fmt.time(row.recordedAt) : ''),
      ),
    },
    {
      key: 'status', label: '状态', width: '92px',
      render: row => {
        const st = statusOf(row.status);
        return ui.el('span', { class: `badge ${st.kind}` }, st.name);
      },
    },
    {
      key: 'qc', label: 'QC 标记', width: '132px',
      render: row => {
        const qc = row.qc || {};
        const parts = [];
        if (qc.duplicate) parts.push(`平行样 ${qc.duplicate}`);
        if (qc.spike) parts.push(`加标 ${qc.spike}`);
        return ui.el('div', { class: 'tiny muted q-qc' }, parts.length ? parts.join(' · ') : '未记录质控');
      },
    },
    {
      key: 'reviewerName', label: '复核', width: '96px',
      render: row => ui.el('div', {},
        ui.el('span', {}, row.reviewerName || '—'),
        ui.el('span', { class: 'cell-sub' }, row.reviewedAt ? ui.fmt.time(row.reviewedAt) : ''),
      ),
    },
    {
      key: 'ops', label: '操作', width: '120px',
      render: row => ui.el('div', { class: 'row' },
        ui.el('button', { class: 'btn sm', type: 'button', onclick: () => openDetail(ctx, row, { canReview }, false) }, '查看'),
        row.status === 'pending_review' && canReview
          ? ui.el('button', {
            class: 'btn sm primary', type: 'button',
            onclick: () => openDetail(ctx, row, { canReview }, true),
          }, '复核')
          : null,
      ),
    },
  ];
}

function openEntry(ctx, reload) {
  const pointSel = ui.select(pointOptions(ctx).map(p => ({ value: p.key, label: p.name })), {});
  const timeInput = ui.input({ type: 'datetime-local', value: toLocalInput(new Date()) });
  const metricInputs = {};
  METRICS.forEach(key => {
    metricInputs[key] = ui.input({ type: 'number', min: '0', step: '0.01', placeholder: 'mg/L' });
  });
  const remarkInput = ui.textarea({ maxlength: '300', placeholder: '可选：异常工况、取样说明' });

  const body = ui.el('div', { class: 'stack' },
    ui.el('div', { class: 'form-grid' },
      ui.field('取样点', pointSel, 'PAC 出水暂无独立取样点；PAC 加药效果以 BAF 出水 / 硫自养池出水指标评估'),
      ui.field('取样时间', timeInput, '人工取样时间，默认当前时间'),
    ),
    ui.el('div', { class: 'form-grid three' },
      ...METRICS.map(key => ui.field(
        `${METRIC_NAMES[key]}（${key}）`,
        metricInputs[key],
        `mg/L · 一级A限值 ${LIMITS[key]}`,
      )),
    ),
    ui.field('备注', remarkInput),
    ui.notice('提交后状态为「待复核」，系统将通知工艺/区域复核人；复核通过后该记录才计入出水统计。', ''),
  );

  const footer = ui.el('div', { class: 'row' },
    ui.el('span', { class: 'tiny muted' }, '数据通道：人工取样化验'),
    ui.el('span', { class: 'spacer' }),
    ui.el('button', { class: 'btn ghost', type: 'button', onclick: () => modal.close() }, '取消'),
    (() => {
      const btn = ui.el('button', { class: 'btn primary', type: 'button' }, '提交并通知复核');
      btn.addEventListener('click', async () => {
        const metrics = {};
        let filled = 0;
        METRICS.forEach(key => {
          const raw = String(metricInputs[key].value || '').trim();
          if (!raw) return;
          const v = Number(raw);
          if (!Number.isFinite(v) || v < 0) return;
          metrics[key] = Math.round(v * 1000) / 1000;
          filled += 1;
        });
        if (!filled) { ctx.toast('请至少填写一项指标值', 'error'); return; }
        const sampledAt = new Date(timeInput.value);
        if (!timeInput.value || Number.isNaN(sampledAt.getTime())) { ctx.toast('请选择有效的取样时间', 'error'); return; }
        if (sampledAt.getTime() > Date.now() + 60000) { ctx.toast('取样时间不能晚于当前时间', 'error'); return; }

        btn.disabled = true;
        btn.textContent = '提交中…';
        try {
          await ctx.api.post('/api/lab', {
            siteId: ctx.siteId,
            samplePoint: pointSel.value,
            sampledAt: sampledAt.toISOString(),
            metrics,
            remark: String(remarkInput.value || '').trim(),
          });
          modal.close();
          ctx.toast('化验记录已提交，已通知工艺复核', 'success');
          reload();
        } catch (err) {
          btn.disabled = false;
          btn.textContent = '提交并通知复核';
          ctx.toast((err && err.message) || '提交失败，请稍后重试', 'error');
        }
      });
      return btn;
    })(),
  );

  const modal = ctx.modal({
    title: '录入化验记录',
    sub: '人工取样 · COD / 氨氮 / 总氮 / 总磷 / SS',
    body,
    footer,
    wide: true,
  });
}

function openDetail(ctx, row, opts, focusReview) {
  const st = statusOf(row.status);
  const metricRows = METRICS.map(key => {
    const v = metricValue(row, key);
    const exceed = isExceed(row, key);
    return {
      name: METRIC_NAMES[key],
      value: v == null ? '—' : ui.fmt.num(v, DIGITS[key]),
      limit: String(LIMITS[key]),
      judge: ui.el('span', { class: `badge ${exceed ? 'bad' : 'good'}` }, exceed ? '超一级A限值' : '达标'),
      unit: 'mg/L',
    };
  });

  const info = ui.kvRows([
    ['编号', row.id],
    ['状态', ui.el('span', { class: `badge ${st.kind}` }, st.name)],
    ['取样点', row.samplePointText || pointName(ctx, row.samplePoint)],
    ['取样时间', ui.fmt.dateTime(row.sampledAt)],
    ['录入人', row.operatorName || row.operatorId || '—'],
    ['录入时间', row.recordedAt ? ui.fmt.dateTime(row.recordedAt) : '—'],
    ['数据通道', ui.el('span', { class: 'badge accent' }, '人工化验')],
    ['复核人', row.reviewerName || '—'],
    ['复核时间', row.reviewedAt ? ui.fmt.dateTime(row.reviewedAt) : '—'],
    ['复核意见', row.reviewNote || '—'],
    ['备注', row.remark || '—'],
    ['QC 质控', [row.qc && row.qc.duplicate ? `平行样 ${row.qc.duplicate}` : '', row.qc && row.qc.spike ? `加标回收 ${row.qc.spike}` : ''].filter(Boolean).join(' · ') || '未记录'],
  ]);

  const body = ui.el('div', { class: 'stack q-detail' },
    info,
    ui.el('h4', { class: 'q-section-title' }, '指标实测值与限值'),
    ui.table({
      columns: [
        { key: 'name', label: '指标' },
        { key: 'value', label: '实测值', cls: 'num' },
        { key: 'limit', label: '一级A限值', cls: 'num' },
        { key: 'judge', label: '判定' },
        { key: 'unit', label: '单位' },
      ],
      rows: metricRows,
      flush: true,
    }),
    ui.el('p', { class: 'tiny muted' }, `检测方法：${row.method || '—'}`),
    ui.el('p', { class: 'tiny muted' }, '说明：本记录为人工取样化验数据（人工/化验通道），不是在线仪表实时值；在线仪表仅覆盖 pH、DO、液位、流量等。'),
  );

  let reviewBox = null;
  let noteInput = null;

  if (opts.canReview && row.status === 'pending_review') {
    noteInput = ui.textarea({ maxlength: '200', placeholder: '复核意见；驳回时必填（如：平行样偏差过大，请重测）' });
    const approveBtn = ui.el('button', { class: 'btn primary', type: 'button' }, '复核通过');
    const rejectBtn = ui.el('button', { class: 'btn danger', type: 'button' }, '驳回并退回重测');

    const submit = async (approved) => {
      const note = String(noteInput.value || '').trim();
      if (!approved && !note) { ctx.toast('驳回需填写退回原因', 'error'); return; }
      approveBtn.disabled = true;
      rejectBtn.disabled = true;
      try {
        await ctx.api.post(`/api/lab/${encodeURIComponent(row.id)}/review`, { approved, note });
        ctx.toast(approved ? '复核通过，已通知录入人' : '已驳回并通知录入人重测', 'success');
        drawer.close();
        ctx.refresh();
      } catch (err) {
        approveBtn.disabled = false;
        rejectBtn.disabled = false;
        ctx.toast((err && err.message) || '复核失败，请稍后重试', 'error');
      }
    };
    approveBtn.addEventListener('click', () => submit(true));
    rejectBtn.addEventListener('click', () => submit(false));

    reviewBox = ui.el('div', { class: 'q-review-box stack' },
      ui.el('h4', { class: 'q-section-title' }, '工艺复核'),
      ui.field('复核意见', noteInput, '驳回时必填；通过时可补充质控结论'),
      ui.el('div', { class: 'row wrap' }, approveBtn, rejectBtn),
      ui.el('p', { class: 'tiny muted' }, '需要 lab.review 权限；复核结果会写入审计日志并通知录入人。'),
    );
  } else if (row.status === 'pending_review' && !opts.canReview) {
    reviewBox = ui.notice('该记录待复核，但当前账号没有 lab.review 权限，仅可查看。', 'warn');
  }

  if (reviewBox) body.appendChild(reviewBox);

  const drawer = ctx.drawer({
    title: `化验记录 ${row.id}`,
    sub: `${row.samplePointText || pointName(ctx, row.samplePoint)} · 取样 ${ui.fmt.dateTime(row.sampledAt)}`,
    body,
  });

  if (focusReview && noteInput) {
    setTimeout(() => { try { noteInput.focus(); } catch (e) { /* 忽略焦点异常 */ } }, 60);
  }
}

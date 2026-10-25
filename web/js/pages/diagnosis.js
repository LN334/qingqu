import * as ui from '../ui.js';

export const meta = { title: '智能诊断' };

/* 诊断级别由规则引擎输出 high / medium / low / none，与告警等级枚举不同，这里做一次语义映射。 */
const LEVEL_META = {
  high: { name: '紧急', kind: 'bad', tone: 'bad', rank: 4 },
  medium: { name: '重要', kind: 'warn', tone: 'warn', rank: 3 },
  low: { name: '提示', kind: 'info', tone: 'blue', rank: 2 },
  none: { name: '正常', kind: 'good', tone: 'good', rank: 1 },
};

function levelMeta(level) {
  return LEVEL_META[level] || { name: level || '未知', kind: 'muted', tone: 'blue', rank: 0 };
}

function readingValue(r) {
  if (!r) return null;
  const v = (r.value !== undefined && r.value !== null) ? r.value : r.v;
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function valueText(r) {
  const v = readingValue(r);
  if (v === null) return '数据中断';
  return `${ui.fmt.auto(v)}${r.unit ? r.unit : ''}`;
}

function qualityText(q) {
  if (q === 'stale') return '数据超时';
  if (q === 'uncertain') return '数据可疑';
  if (q === 'good') return '正常';
  return q || '—';
}

async function loadData(ctx) {
  const siteId = ctx.siteId;
  const [diag, latest] = await Promise.all([
    ctx.api.getFull('/api/diagnosis', { siteId }),
    ctx.api.getFull('/api/telemetry/latest', { siteId }),
  ]);
  return { diag, latest };
}

/* /api/telemetry/latest 的 data 形状为 {siteId, at, items}；兼容数组与对象两种形态。 */
function latestItems(payload) {
  const data = payload && payload.data;
  if (Array.isArray(data)) return data;
  if (data && Array.isArray(data.items)) return data.items;
  return [];
}

function healthSummary(items) {
  const list = Array.isArray(items) ? items : [];
  const total = list.length;
  const stale = list.filter(r => r.stale).length;
  const noValue = list.filter(r => readingValue(r) === null).length;
  const times = list.map(r => (r.t ? new Date(r.t).getTime() : 0)).filter(t => Number.isFinite(t) && t > 0);
  const latestAt = times.length ? Math.max(...times) : null;
  return { total, stale, noValue, latestAt, online: total - stale };
}

export async function render(root, ctx) {
  const page = ui.el('div', { class: 'p-diagnosis stack' });
  ui.mount(root, page);

  const head = ui.pageHead(
    '智能诊断',
    '由实时遥测与阈值规则即时推导，每条诊断给出触发依据、关联点位与依据数据时间；诊断项随数据变化动态出现或消失。',
    [ui.el('button', {
      class: 'btn ghost sm', type: 'button',
      onclick: () => ctx.refresh(),
    }, ui.icon('refresh'), '刷新诊断')],
  );
  ui.mount(page, head, ui.loading('正在汇集实时数据并执行诊断规则…'));

  let data;
  try {
    data = await loadData(ctx);
  } catch (err) {
    ui.mount(page, head, ui.card({ body: ui.errorState(err, () => ctx.refresh()) }));
    return;
  }

  const content = ui.el('div', { class: 'stack' });
  ui.mount(page, head, content);
  paint(ctx, content, data);
}

function paint(ctx, host, data) {
  const diag = data.diag || { data: [], meta: {} };
  const items = Array.isArray(diag.data) ? diag.data.slice() : [];
  const health = healthSummary(latestItems(data.latest));
  const note = (diag.meta && diag.meta.note) || '诊断结果由当前实时数据与规则阈值即时推导，每条均给出触发依据与数据时间。';

  const counts = { high: 0, medium: 0, low: 0, none: 0 };
  items.forEach(d => { counts[d.level] = (counts[d.level] || 0) + 1; });
  const active = items.filter(d => d.level !== 'none');
  const dataTime = items
    .map(d => d.generatedAt)
    .filter(Boolean)
    .sort()
    .pop() || (health.latestAt ? ui.fmt.dateTime(health.latestAt) : '—');

  /* ---------------- 顶部统计 ---------------- */
  const kpiGrid = ui.el('div', { class: 'grid cols-4' },
    ui.kpi({
      label: '当前诊断条数',
      value: active.length,
      unit: '条',
      tone: active.length ? 'warn' : 'good',
      hint: '不含「正常」类占位项；诊断由规则引擎即时推导',
      delta: ui.el('span', { class: active.length ? 'down' : 'up' }, active.length ? '存在需要处置的异常' : '未发现需要处置的异常'),
    }),
    ['high', 'medium', 'low'].map(key => {
      const meta = levelMeta(key);
      return ui.kpi({
        label: `${meta.name}诊断`,
        value: counts[key] || 0,
        unit: '条',
        tone: meta.tone,
        hint: `规则引擎输出级别「${key}」`,
        delta: ui.el('span', { class: 'muted' }, (counts[key] || 0) > 0 ? '见下方诊断卡片' : '无'),
      });
    }),
  );

  const timeBar = ui.el('div', { class: 'row wrap dg-timebar' },
    ui.el('span', { class: 'badge accent' }, '依据数据时间'),
    ui.el('span', { class: 'small' }, dataTime),
    ui.el('span', { class: 'tiny muted' }, '·'),
    ui.el('span', { class: 'tiny muted' }, note),
  );

  /* ---------------- 数据健康摘要 ---------------- */
  const healthRows = [
    ['在线点位数', health.total ? `${health.online} / ${health.total}` : '—'],
    ['数据超时点位', health.total ? `${health.stale} 个` : '—'],
    ['无最新值点位', health.total ? `${health.noValue} 个` : '—'],
    ['最新数据时间', health.latestAt ? ui.fmt.dateTime(health.latestAt) : '—'],
    ['诊断生成时间', dataTime],
  ];
  const healthCard = ui.card({
    title: '数据健康摘要',
    sub: '诊断所依赖的在线点位状态（在线仪表通道）',
    body: ui.el('div', { class: 'stack' },
      health.total
        ? null
        : ui.notice('当前无在线点位数据：平台尚未接入在线仪表，或当前站点未配置在线点位，诊断与关联曲线暂不可用。', 'warn'),
      ui.kvRows(healthRows),
    ),
    foot: ui.el('span', { class: 'tiny muted' }, '在线通道仅覆盖 pH、DO、液位、流量、压力、功率与累计量；COD / 氨氮 / 总氮 / 总磷 / SS 为人工化验，不参与实时诊断。'),
  });

  /* ---------------- 诊断卡片 ---------------- */
  const list = ui.el('div', { class: 'stack dg-list' });
  if (!active.length) {
    ui.mount(list, ui.card({
      body: ui.empty(
        '当前无异常诊断',
        health.total
          ? `规则引擎未触发任何异常（检查时间 ${dataTime}）。数据健康：${health.online}/${health.total} 点位在线，${health.stale} 个点位数据超时。`
          : '当前无在线点位数据，规则引擎无法基于实时数据给出诊断。',
        'check',
      ),
      foot: ui.el('span', { class: 'tiny muted' }, note),
    }));
  } else {
    active
      .slice()
      .sort((a, b) => levelMeta(b.level).rank - levelMeta(a.level).rank)
      .forEach(d => list.appendChild(diagCard(ctx, d)));
  }

  ui.mount(host, kpiGrid, timeBar, ui.el('div', { class: 'grid cols-2' }, healthCard), list);
}

function diagCard(ctx, d) {
  const meta = levelMeta(d.level);
  const basis = d.basis || {};
  const points = Array.isArray(basis.points) ? basis.points : [];
  const checks = Array.isArray(d.checks) ? d.checks : [];
  const causes = Array.isArray(d.causes) ? d.causes : [];

  const pointChips = points.map(p => ui.el('span', {
    class: `chip dg-point${p.quality === 'stale' || readingValue(p) === null ? ' stale' : ''}`,
    title: `${p.name || p.code} · ${qualityText(p.quality)} · 时间 ${p.time || '—'}`,
  },
  ui.el('b', {}, p.name || p.code),
  ` ${valueText(p)}`,
  p.time ? ui.el('span', { class: 'dg-point-time' }, p.time) : null,
  ));

  const body = ui.el('div', { class: 'stack dg-body' },
    ui.el('div', { class: 'dg-section' },
      ui.el('h4', {}, '现象'),
      ui.el('p', { class: 'dg-phenomenon' }, d.phenomenon || '—'),
    ),
    ui.el('div', { class: 'dg-section' },
      ui.el('h4', {}, '可能原因'),
      causes.length
        ? ui.el('ul', { class: 'dg-causes' }, ...causes.map(c => ui.el('li', {}, c)))
        : ui.el('p', { class: 'small muted' }, '规则库未给出原因清单，请结合现场工况判断。'),
    ),
    ui.el('div', { class: 'dg-section' },
      ui.el('h4', {}, '检查项'),
      ui.table({
        columns: [
          { key: 'item', label: '检查项', width: '30%' },
          { key: 'how', label: '操作方法' },
          { key: 'expect', label: '期望值', width: '26%' },
        ],
        rows: checks,
        emptyText: '暂无检查项',
        flush: true,
      }),
    ),
    ui.el('div', { class: 'dg-section' },
      ui.el('h4', {}, '边界说明'),
      ui.el('p', { class: 'small muted' }, d.boundary || '—'),
    ),
    ui.el('div', { class: 'dg-section' },
      ui.el('h4', {}, '触发依据'),
      pointChips.length ? ui.el('div', { class: 'row wrap dg-points' }, ...pointChips) : null,
      ui.el('dl', { class: 'dg-basis' },
        ui.el('dt', {}, '趋势'),
        ui.el('dd', {}, basis.trend || '—'),
        ui.el('dt', {}, '规则'),
        ui.el('dd', {}, basis.rule || '—'),
      ),
      ui.el('p', { class: 'tiny muted dg-datatime' }, `依据数据时间：${d.generatedAt || '—'}（各关联点位时间见上方标记）`),
    ),
    ui.el('div', { class: 'dg-suggestion' },
      ui.icon('check'),
      ui.el('div', {},
        ui.el('b', {}, '建议动作'),
        ui.el('p', {}, d.suggestion || '—'),
      ),
    ),
  );

  const foot = ui.el('div', { class: 'row wrap' },
    ui.el('button', {
      class: 'btn sm', type: 'button',
      onclick: () => openCurve(ctx, d),
    }, '查看关联点位 24h 曲线'),
    ui.el('button', {
      class: 'btn ghost sm', type: 'button',
      onclick: () => ctx.go('#/alarms'),
    }, ui.icon('alarm'), '告警中心'),
    ui.el('button', {
      class: 'btn ghost sm', type: 'button',
      onclick: () => ctx.go('#/control'),
    }, ui.icon('control'), '远程控制'),
    ui.el('span', { class: 'spacer' }),
    ui.el('span', { class: 'tiny muted' }, `诊断编号 ${d.id || '—'} · 依据数据时间 ${d.generatedAt || '—'}`),
  );

  return ui.card({
    class: 'dg-card',
    title: d.title || '未命名诊断',
    sub: `现象与处置建议由规则引擎生成 · 级别 ${meta.name} · 依据数据时间 ${d.generatedAt || '—'}`,
    actions: [ui.el('span', { class: `badge ${meta.kind}` }, meta.name)],
    body,
    foot,
  });
}

async function openCurve(ctx, d) {
  const basis = d.basis || {};
  const points = Array.isArray(basis.points) ? basis.points : [];
  const codes = points.map(p => p.code).filter(Boolean);
  const meta = levelMeta(d.level);

  const chartHost = ui.el('div', { class: 'stack dg-curves' });
  const body = ui.el('div', { class: 'stack dg-drawer' },
    ui.kvRows([
      ['诊断项', d.title],
      ['级别', ui.el('span', { class: `badge ${meta.kind}` }, meta.name)],
      ['依据数据时间', d.generatedAt || '—'],
      ['趋势判定', basis.trend || '—'],
      ['触发规则', basis.rule || '—'],
    ]),
    ui.el('p', { class: 'small muted' }, '曲线来自 /api/telemetry/history：近 24 小时、30 分钟聚合桶，仅展示该诊断引用的关联点位（在线仪表通道）。'),
    chartHost,
  );

  ctx.drawer({ title: '关联点位 24h 曲线', sub: `${d.title || ''} · 依据数据时间 ${d.generatedAt || '—'}`, body });

  if (!codes.length) {
    ui.mount(chartHost, ui.empty('该诊断没有关联点位', '规则依据为数据时效或组合条件，未引用具体点位'));
    return;
  }

  ui.mount(chartHost, ui.loading('加载 24 小时曲线…'));
  const to = new Date();
  const from = new Date(to.getTime() - 24 * 3600 * 1000);
  try {
    const res = await ctx.api.get('/api/telemetry/history', {
      siteId: ctx.siteId,
      codes: codes.join(','),
      bucketSec: 1800,
      from: from.toISOString(),
      to: to.toISOString(),
    });
    const series = (res && res.series) || {};
    const keys = Object.keys(series);
    if (!keys.length) {
      ui.mount(chartHost, ui.empty('无可用曲线', '关联点位在当前时间窗内没有历史数据'));
      return;
    }
    const blocks = keys.map((code, i) => {
      const s = series[code] || {};
      const chart = ui.el('div', { class: 'chart dg-mini-chart' });
      return {
        code,
        series: s,
        chart,
        node: ui.el('div', { class: 'dg-curve-block' },
          ui.el('div', { class: 'row' },
            ui.el('b', {}, s.name || code),
            ui.el('span', { class: 'tiny muted' }, s.unit || ''),
            ui.el('span', { class: 'spacer' }),
            ui.el('span', { class: 'tiny muted' }, `${(s.points || []).length} 个数据点`),
          ),
          chart,
        ),
        index: i,
      };
    });
    ui.mount(chartHost, ...blocks.map(b => b.node));
    blocks.forEach(b => {
      const pts = (b.series.points || [])
        .map(p => ({ t: Number(p.t), v: Number(p.v) }))
        .filter(p => Number.isFinite(p.t) && Number.isFinite(p.v));
      if (!pts.length) {
        ui.mount(b.chart, ui.empty('窗口内无数据', '该点位近 24 小时没有落库数据'));
        return;
      }
      ui.chartLine(b.chart, {
        series: [{ name: b.series.name || b.code, color: ui.palette(b.index), points: pts }],
        height: 150,
        unit: b.series.unit || '',
        area: false,
        showLegend: false,
        xTickCount: 3,
      });
    });
  } catch (err) {
    ui.mount(chartHost, ui.errorState(err, () => openCurve(ctx, d)));
  }
}

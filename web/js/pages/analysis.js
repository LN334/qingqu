import * as ui from '../ui.js';

export const meta = { title: '数据分析' };

const PRICE_FALLBACK = { electricity: 0.65, pac: 2.2, pam: 12 };

function pad2(n) { return String(n).padStart(2, '0'); }

function dayKey(ts) {
  const d = new Date(Number(ts));
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
}

function dayTime(key) {
  const d = new Date(`${key}T00:00:00`);
  return Number.isFinite(d.getTime()) ? d.getTime() : 0;
}

function dayLabel(key) {
  return key ? key.slice(5) : '—';
}

function dayList(fromISO, toISO) {
  const start = new Date(fromISO);
  const end = new Date(toISO);
  if (!Number.isFinite(start.getTime()) || !Number.isFinite(end.getTime())) return [];
  start.setHours(0, 0, 0, 0);
  end.setHours(0, 0, 0, 0);
  const out = [];
  const cur = new Date(start);
  let guard = 0;
  while (cur <= end && guard < 400) {
    out.push(`${cur.getFullYear()}-${pad2(cur.getMonth() + 1)}-${pad2(cur.getDate())}`);
    cur.setDate(cur.getDate() + 1);
    guard += 1;
  }
  return out;
}

function counterDaily(points) {
  const out = new Map();
  const list = (points || [])
    .map(p => ({ t: Number(p.t), v: Number(p.v) }))
    .filter(p => Number.isFinite(p.t) && Number.isFinite(p.v))
    .sort((a, b) => a.t - b.t);
  for (let i = 1; i < list.length; i += 1) {
    const delta = list[i].v - list[i - 1].v;
    if (!Number.isFinite(delta) || delta < 0) continue;
    const k = dayKey(list[i].t);
    out.set(k, (out.get(k) || 0) + delta);
  }
  return out;
}

function readingValue(r) {
  if (!r) return null;
  const v = (r.value !== undefined && r.value !== null) ? r.value : r.v;
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function coverageText(agg) {
  const list = Array.isArray(agg.days) ? agg.days : [];
  if (!list.length) return '窗口内暂无逐日数据（历史保留 30 天）。';
  return `窗口覆盖 ${list.length} 天（${dayLabel(list[0])} ~ ${dayLabel(list[list.length - 1])}，历史保留 30 天）。`;
}

function readingText(r) {
  const v = readingValue(r);
  const code = r.code || r.name || '点位';
  if (v === null) return `${code} 待接入`;
  if (r.type === 'state') return `${code} ${v ? '运行' : '停止'}`;
  return `${code} ${ui.fmt.auto(v)}${r.unit || ''}`;
}

function numOrDash(v, digits = 1) {
  const n = Number(v);
  return Number.isFinite(n) ? ui.fmt.num(n, digits) : '—';
}

async function fetchData(ctx, days) {
  const siteId = ctx.siteId;
  const to = new Date();
  const from = new Date(to.getTime() - days * 24 * 3600 * 1000);
  const bucketSec = days <= 7 ? 3600 : 6 * 3600;
  const [summary, units, eff, hist] = await Promise.all([
    ctx.api.getFull('/api/analysis/summary', { siteId, days }),
    ctx.api.getFull('/api/analysis/units', { siteId, days }),
    ctx.api.getFull('/api/analysis/efficiency', { siteId, days }),
    ctx.api.get('/api/telemetry/history', {
      siteId,
      codes: 'FLOW.IN,ENERGY.KWH,PAC.TOTAL,PAM.TOTAL',
      bucketSec,
      from: from.toISOString(),
      to: to.toISOString(),
    }).catch(() => null),
  ]);
  return {
    summary: summary.data || {},
    units: units.data || {},
    eff: eff.data || {},
    hist,
    bucketSec,
  };
}

function aggregate(data) {
  const summary = data.summary || {};
  const series = (data.hist && data.hist.series) || {};
  const hoursPerBucket = (data.hist && data.hist.bucketSec ? data.hist.bucketSec : 3600) / 3600;
  const days = dayList(summary.from, summary.to);

  const water = new Map();
  const flowPoints = ((series['FLOW.IN'] || {}).points) || [];
  flowPoints.forEach(p => {
    const v = Number(p.v);
    if (!Number.isFinite(v)) return;
    const k = dayKey(p.t);
    water.set(k, (water.get(k) || 0) + v * hoursPerBucket);
  });

  let energy = counterDaily((series['ENERGY.KWH'] || {}).points);
  if (!energy.size && Array.isArray(summary.daily)) {
    energy = new Map(summary.daily.map(d => [d.day, Number(d.energy) || 0]));
  }
  const pac = counterDaily((series['PAC.TOTAL'] || {}).points);
  const pam = counterDaily((series['PAM.TOTAL'] || {}).points);

  const waterPts = days.map(k => ({ t: dayTime(k), v: water.get(k) || 0 })).filter(p => p.t);
  const energyPts = days.map(k => ({ t: dayTime(k), v: energy.get(k) || 0 })).filter(p => p.t);
  const pacPts = days.map(k => ({ t: dayTime(k), v: pac.get(k) || 0 })).filter(p => p.t);
  const pamPts = days.map(k => ({ t: dayTime(k), v: pam.get(k) || 0 })).filter(p => p.t);

  const alarmMap = summary.alarmDaily || {};
  const alarmItems = days.map(k => ({ label: dayLabel(k), value: Number(alarmMap[k]) || 0, day: k })).filter(it => it.day);

  const cost = summary.cost || {};
  return {
    summary,
    days,
    waterPts,
    energyPts,
    pacPts,
    pamPts,
    alarmItems,
    waterAvailable: !!(summary.water && summary.water.available) && flowPoints.length > 0,
    removal: (summary.quality && summary.quality.removal) || {},
    labAvg: (summary.quality && summary.quality.labAvg) || {},
    cost,
    price: cost.priceAssumption || PRICE_FALLBACK,
  };
}

export async function render(root, ctx) {
  const page = ui.el('div', { class: 'p-analysis stack' });
  ui.mount(root, page);
  let days = 7;

  const head = () => ui.pageHead(
    '数据分析',
    '按所选时间窗汇总处理水量、能耗、药耗、成本、水质去除率与运行负荷；指标全部来自在线遥测与人工化验，缺失项明确标注「待接入」。',
    [
      ui.segmented([
        { value: 7, label: '近 7 天' },
        { value: 30, label: '近 30 天' },
      ], days, (v) => { days = v; reload(); }),
      ui.el('button', { class: 'btn ghost sm', type: 'button', onclick: () => reload() }, ui.icon('refresh'), '刷新'),
    ],
  );

  async function reload() {
    ui.mount(page,
      head(),
      ui.loading('正在汇总水量、能耗、药耗与设备运行数据…'),
    );
    try {
      const data = await fetchData(ctx, days);
      const built = build(ctx, data, days, () => reload());
      ui.mount(page, head(), ...built.nodes);
      built.draw();
    } catch (err) {
      ui.mount(page,
        head(),
        ui.card({ body: ui.errorState(err, () => reload()) }),
      );
    }
  }

  await reload();
}

function build(ctx, data, days) {
  const agg = aggregate(data);
  const cover = coverageText(agg);
  const { summary, cost, price } = agg;
  const water = summary.water || {};
  const energy = summary.energy || {};
  const chemicals = summary.chemicals || {};
  const pac = chemicals.pac || {};
  const pam = chemicals.pam || {};
  const alarms = summary.alarms || {};
  const workorders = summary.workorders || {};
  const availability = summary.availability || {};
  const eff = data.eff || {};
  const units = data.units || {};
  const waterAvailable = agg.waterAvailable;
  const waterTotal = Number(water.total) || 0;
  const perWater = (v) => (waterAvailable && waterTotal > 0 ? Number(v) / waterTotal : null);

  /* ---------------- 顶部 KPI ---------------- */
  const removal = agg.removal;
  const kpis = ui.el('div', { class: 'grid cols-4' },
    ui.kpi({
      label: '处理水量',
      value: waterAvailable ? numOrDash(water.total, 1) : '待接入',
      unit: waterAvailable ? 'm³' : '',
      tone: 'blue',
      hint: waterAvailable ? '进水瞬时流量按小时桶积分求和（在线仪表）' : '未检测到有效的进水流量数据，不估算',
      delta: ui.el('span', { class: 'muted' }, waterAvailable ? `${water.points || 0} 个流量采样桶` : '待接入'),
    }),
    ui.kpi({
      label: '吨水电耗',
      value: waterAvailable ? numOrDash(energy.perM3, 2) : '待接入',
      unit: waterAvailable ? 'kWh/m³' : '',
      tone: 'blue',
      hint: '装置累计用电量差值 ÷ 处理水量',
      delta: ui.el('span', { class: 'muted' }, `总电耗 ${numOrDash(energy.total, 1)} kWh · 电费 ${ui.fmt.money(cost.electricity || 0)}`),
    }),
    ui.kpi({
      label: '吨水药耗',
      value: waterAvailable ? numOrDash(perWater(pac.total), 2) : '待接入',
      unit: waterAvailable ? 'L/m³（PAC）' : '',
      tone: 'blue',
      hint: 'PAC / PAM 累计投加量差值 ÷ 处理水量',
      delta: ui.el('span', { class: 'muted' }, `PAM ${waterAvailable ? numOrDash(perWater(pam.total), 2) : '—'} L/m³ · PAC 总量 ${numOrDash(pac.total, 1)} L`),
    }),
    ui.kpi({
      label: '吨水成本',
      value: numOrDash(cost.perM3, 2),
      unit: '元/m³',
      tone: 'blue',
      hint: `电价 ${price.electricity} 元/kWh · PAC ${price.pac} 元/L · PAM ${price.pam} 元/L（演示假设）`,
      delta: ui.el('span', { class: 'muted' }, `合计 ${ui.fmt.money(cost.total || 0)} · 药剂占 ${ui.fmt.pct(((cost.chemicals || 0) / (cost.total || 1)) * 100, 0)}`),
    }),
    ui.kpi({
      label: 'COD 去除率',
      value: numOrDash(removal.COD, 1),
      unit: '%',
      tone: Number(removal.COD) >= 75 ? 'good' : 'warn',
      hint: '基于人工化验：进水与总出水已复核数据（非在线实时值）',
      delta: ui.el('span', { class: 'muted' }, `出水均值 ${numOrDash(agg.labAvg.COD && agg.labAvg.COD.avg, 1)} mg/L · 限值 ${(agg.labAvg.COD && agg.labAvg.COD.limit) || 50}`),
    }),
    ui.kpi({
      label: '氨氮去除率',
      value: numOrDash(removal.NH3N, 1),
      unit: '%',
      tone: Number(removal.NH3N) >= 90 ? 'good' : 'warn',
      hint: '基于人工化验：进水与总出水已复核数据（非在线实时值）',
      delta: ui.el('span', { class: 'muted' }, `出水均值 ${numOrDash(agg.labAvg.NH3N && agg.labAvg.NH3N.avg, 2)} mg/L · 限值 ${(agg.labAvg.NH3N && agg.labAvg.NH3N.limit) || 5}`),
    }),
    ui.kpi({
      label: '告警次数',
      value: alarms.total || 0,
      unit: '次',
      tone: (alarms.total || 0) > 0 ? 'warn' : 'good',
      hint: '统计窗口内产生的告警条数（含各等级）',
      delta: ui.el('span', { class: 'muted' }, `未恢复 ${alarms.unresolved || 0} 条`),
    }),
    ui.kpi({
      label: '工单数',
      value: workorders.created || 0,
      unit: '单',
      tone: (workorders.open || 0) > 0 ? 'blue' : 'good',
      hint: '统计窗口内创建的工单',
      delta: ui.el('span', { class: 'muted' }, `未闭环 ${workorders.open || 0} 单 · 已闭环 ${workorders.closed || 0} 单`),
    }),
  );

  /* ---------------- 趋势图 ---------------- */
  const waterChart = ui.el('div', { class: 'chart a-chart' });
  const energyChart = ui.el('div', { class: 'chart a-chart' });
  const chemChart = ui.el('div', { class: 'chart a-chart' });
  const costChart = ui.el('div', { class: 'chart a-chart a-donut' });
  const alarmChart = ui.el('div', { class: 'chart a-chart' });
  const healthChart = ui.el('div', { class: 'chart a-chart a-donut' });

  const healthLevels = { good: 0, attention: 0, risk: 0 };
  (eff.devices || []).forEach(d => {
    if (healthLevels[d.healthLevel] !== undefined) healthLevels[d.healthLevel] += 1;
  });

  const charts = ui.el('div', { class: 'grid cols-3' },
    ui.card({
      title: '逐日处理水量',
      sub: waterAvailable ? '进水流量积分（在线仪表通道）' : '待接入',
      body: waterAvailable ? waterChart : ui.empty('处理水量待接入', '进水流量计未接入或无有效数据，不做估算', 'monitor'),
      foot: ui.el('span', { class: 'tiny muted' }, `流量按小时桶积分；30 天窗口使用 6 小时聚合桶。${cover}`),
    }),
    ui.card({
      title: '逐日能耗',
      sub: '装置累计用电量差值（在线电表）',
      body: energyChart,
      foot: ui.el('span', { class: 'tiny muted' }, `电费合计 ${ui.fmt.money(cost.electricity || 0)}，按 ${price.electricity} 元/kWh 演示电价折算。${cover}`),
    }),
    ui.card({
      title: '逐日药剂投加',
      sub: 'PAC / PAM 累计投加量差值',
      body: chemChart,
      foot: ui.el('span', { class: 'tiny muted' }, `PAC 合计 ${numOrDash(pac.total, 1)} L，PAM 合计 ${numOrDash(pam.total, 1)} L（累计量差值口径）。${cover}`),
    }),
  );

  const costItems = [
    { label: '电费', value: Math.max(0, Number(cost.electricity) || 0), color: ui.palette(1) },
    { label: 'PAC 药剂', value: Math.max(0, Number(pac.cost) || 0), color: ui.palette(2) },
    { label: 'PAM 药剂', value: Math.max(0, Number(pam.cost) || 0), color: ui.palette(3) },
  ].filter(it => it.value > 0);

  const charts2 = ui.el('div', { class: 'grid cols-3' },
    ui.card({
      title: '成本构成',
      sub: '电费与药剂费（演示单价）',
      body: costItems.length ? costChart : ui.empty('暂无成本数据', '窗口内没有能耗或药剂消耗'),
      foot: ui.el('span', { class: 'tiny muted' }, `吨水成本 ${numOrDash(cost.perM3, 2)} 元/m³。`),
    }),
    ui.card({
      title: '告警按日',
      sub: `窗口内共 ${alarms.total || 0} 次告警`,
      body: alarmChart,
      foot: ui.el('span', { class: 'tiny muted' }, `未恢复 ${alarms.unresolved || 0} 条；告警数据来自告警中心。`),
    }),
    ui.card({
      title: '设备健康分布',
      sub: `在线率 ${numOrDash(availability.dataRate, 1)}% · 平均健康度 ${numOrDash(availability.deviceHealth, 1)}%`,
      body: healthChart,
      foot: ui.el('span', { class: 'tiny muted' }, '运行时长与保养信息来自设备台账（PLC 累计量）。'),
    }),
  );

  /* ---------------- 单元效率表 ---------------- */
  const devByUnit = new Map();
  (eff.devices || []).forEach(d => {
    const k = d.unitCode || 'other';
    if (!devByUnit.has(k)) devByUnit.set(k, []);
    devByUnit.get(k).push(d);
  });

  const unitRows = (units.items || [])
    .slice()
    .sort((a, b) => (a.order || 0) - (b.order || 0))
    .map(u => {
      const devs = devByUnit.get(u.code) || [];
      const runtime = devs.reduce((s, d) => s + (Number(d.runtimeHours) || 0), 0);
      return { unit: u, devs, runtime };
    });

  const unitTable = ui.table({
    columns: [
      {
        key: 'unit', label: '单元', width: '20%',
        render: (row) => ui.el('div', {},
          ui.el('div', { class: 'cell-main' }, ui.el('b', {}, row.unit.name || row.unit.code)),
          ui.el('div', { class: 'cell-sub' }, row.unit.code || '—'),
        ),
      },
      {
        key: 'readings', label: '在线读数',
        render: (row) => {
          const list = row.unit.readings || [];
          if (!list.length) return ui.el('span', { class: 'badge muted' }, '该单元无在线仪表');
          const shown = list.slice(0, 3).map(readingText).join(' · ');
          return ui.el('div', { class: 'a-readings' },
            ui.el('div', {}, shown),
            list.length > 3 ? ui.el('div', { class: 'cell-sub' }, `另有 ${list.length - 3} 个点位`) : null,
          );
        },
      },
      {
        key: 'alarms', label: '告警', width: '12%', cls: 'num',
        render: (row) => {
          const a = row.unit.alarms || {};
          return ui.el('div', {},
            ui.el('div', {}, `${a.total || 0} 条`),
            ui.el('div', { class: 'cell-sub' }, `未处理 ${a.open || 0}`),
          );
        },
      },
      {
        key: 'devices', label: '设备', width: '10%', cls: 'num',
        render: (row) => {
          const d = row.unit.devices || {};
          return ui.el('div', {}, `${d.online || 0}/${d.total || 0}`);
        },
      },
      {
        key: 'health', label: '设备健康度', width: '14%',
        render: (row) => {
          const h = row.unit.health;
          if (h === null || h === undefined) return ui.el('span', { class: 'badge muted' }, '待接入');
          const kind = h >= 90 ? 'good' : (h >= 75 ? 'warn' : 'bad');
          return ui.el('div', { class: 'row' },
            ui.el('span', { class: `badge ${kind}` }, `${ui.fmt.num(h, 0)}%`),
            ui.progress(h, { max: 100, tone: kind === 'good' ? 'good' : (kind === 'warn' ? 'warn' : 'bad'), showLabel: false }),
          );
        },
      },
      {
        key: 'runtime', label: '运行时长', width: '12%', cls: 'num',
        render: (row) => (row.devs.length ? `${ui.fmt.int(row.runtime)} h` : ui.el('span', { class: 'muted' }, '—')),
      },
    ],
    rows: unitRows,
    emptyText: '暂无单元数据',
    emptySub: '当前站点没有可用的单元与设备记录',
  });

  const unitCard = ui.card({
    title: '单元效率',
    sub: `${(units.items || []).length} 个处理单元 · 告警与设备健康来自当前站点台账`,
    body: ui.el('div', { class: 'a-unit-wrap' }, unitTable),
    foot: ui.el('div', { class: 'row wrap' },
      ui.el('span', { class: 'tiny muted' }, `设备 ${(eff.devices || []).length} 台 · 运行率 ${numOrDash(eff.uptime, 1)}% · 待保养 ${eff.maintDue || 0} 台`),
      ui.el('span', { class: 'spacer' }),
      ui.el('span', { class: 'tiny muted' }, (eff.utilization && eff.utilization.note) || '设备运行时长来自 PLC 累计量，启停次数来自运行状态跳变计数。'),
    ),
  });

  /* ---------------- 口径说明 ---------------- */
  const notes = Array.isArray(summary.notes) ? summary.notes.slice() : [];
  const footNote = ui.el('ul', { class: 'a-notes' },
    ...notes.map(n => ui.el('li', {}, n)),
    ui.el('li', {}, '电价、药剂单价为演示假设值，仅用于趋势对比，不作为结算依据。'),
    ui.el('li', {}, '在线仪表通道仅覆盖流量、液位、pH、DO、压力、功率与累计量；COD、氨氮、总氮、总磷、SS 为人工化验数据，存在取样周期，不代表实时水质。'),
    ui.el('li', {}, '历史遥测保留 7 天，30 天窗口的逐日趋势仅覆盖存在落库数据的日期；缺失日期不插值、不估算。'),
  );
  const noteCard = ui.card({
    title: '口径说明',
    sub: '关于水量、能耗、药耗与成本的取数方式',
    body: ui.el('div', { class: 'stack' },
      waterAvailable ? null : ui.notice('进水管路未安装流量计（或数据未接入），处理水量与吨水类指标标记为「待接入」，不进行估算。', 'warn'),
      footNote,
    ),
    foot: ui.el('span', { class: 'tiny muted' }, `统计窗口 ${ui.fmt.dateTime(summary.from)} 至 ${ui.fmt.dateTime(summary.to)}，共 ${agg.days.length} 个自然日。`),
  });

  return {
    nodes: [kpis, charts, charts2, unitCard, noteCard],
    draw() {
      if (waterAvailable) {
        if (!agg.waterPts.length) {
          ui.mount(waterChart, ui.empty('无逐日流量数据', '当前窗口内没有进水流量落库记录'));
        } else {
          ui.chartLine(waterChart, {
            series: [{ name: '处理水量', color: ui.palette(0), points: agg.waterPts }],
            height: 190, unit: 'm³', digits: 2, area: true, showLegend: false,
            xTickCount: days <= 7 ? 4 : 5,
          });
        }
      }
      if (agg.energyPts.length) {
        ui.chartLine(energyChart, {
          series: [{ name: '用电量', color: ui.palette(1), points: agg.energyPts }],
          height: 190, unit: 'kWh', digits: 2, area: true, showLegend: false,
          xTickCount: days <= 7 ? 4 : 5,
        });
      } else {
        ui.mount(energyChart, ui.empty('无逐日能耗数据', '累计电量点位无可用差值'));
      }
      if (agg.pacPts.length || agg.pamPts.length) {
        ui.chartLine(chemChart, {
          series: [
            { name: 'PAC 投加量', color: ui.palette(2), points: agg.pacPts },
            { name: 'PAM 投加量', color: ui.palette(3), points: agg.pamPts },
          ],
          height: 190, unit: 'L', digits: 2, area: false, showLegend: true,
          xTickCount: days <= 7 ? 4 : 5,
        });
      } else {
        ui.mount(chemChart, ui.empty('无逐日药耗数据', 'PAC / PAM 累计量点位无可用差值'));
      }
      if (costItems.length) {
        ui.chartDonut(costChart, {
          items: costItems,
          size: 170,
          thickness: 18,
          centerLabel: '合计成本',
          centerValue: ui.fmt.money(cost.total || 0),
          unit: '',
        });
      }
      if (agg.alarmItems.length) {
        /* 纯柱状：不使用面积填充，避免 0 轴以上整块红底被误读为异常带。 */
        ui.chartBars(alarmChart, {
          items: agg.alarmItems.map(it => ({ label: it.label, value: it.value, color: it.value > 0 ? '#b32d22' : '#b9c9d3' })),
          height: 190, unit: '次',
          valueFmt: (v) => ui.fmt.int(v),
        });
      } else {
        ui.mount(alarmChart, ui.empty('窗口内无告警记录', '告警数据来自告警中心'));
      }
      ui.chartDonut(healthChart, {
        items: [
          { label: '健康', value: healthLevels.good, color: '#157f4b' },
          { label: '关注', value: healthLevels.attention, color: '#a86200' },
          { label: '风险', value: healthLevels.risk, color: '#b32d22' },
        ],
        size: 170,
        thickness: 18,
        centerLabel: '设备总数',
        centerValue: (eff.devices || []).length,
        unit: '台',
      });
    },
  };
}

'use strict';
/**
 * 综合分析 / 报表 / 智能诊断 / 通知 / 审计 / 用户与角色 / 系统设置 / 备份。
 * 口径说明：水量按进水流量对时间做梯形积分；能耗、药耗按累计量做差值；
 * 电费与药剂单价属于演示假设，界面必须标注「估算」，不冒充计量结算数据。
 */
const fs = require('fs');
const path = require('path');
const { ok, ApiError, requireAuth, requirePerm, getSite, scopedSites, paginate, audit } = require('./common');
const A = require('../analytics');
const { round, iso, dayKey, startOfDay, uid, hashPassword } = require('../util');
const { nextCode } = require('../codes');
const { sendText } = require('../http');
const { HISTORY_DAYS } = require('../telemetry');

const H = 3600000, D = 86400000;
const PRICE = { electricity: 0.65, pac: 2.2, pam: 12.0 }; // 元/kWh、元/kg
const LEVEL_NAME = { info: '提示', minor: '次要', major: '重要', critical: '紧急' };
const LIMITS = { COD: 50, NH3N: 5, TN: 15, TP: 0.5, SS: 10 };
const METRIC_NAMES = { COD: 'COD', NH3N: '氨氮', TN: '总氮', TP: '总磷', SS: '悬浮物' };
const REPORT_TYPES = { daily: '运行日报', weekly: '运行周报', monthly: '运行月报', custom: '自定义报表' };
const RM = /[^\u4e00-\u9fa5A-Za-z0-9_：；，。、（）()\-\s]/g;

function safeName(s) { return String(s || '').replace(/[\\/:*?"<>|]/g, '_').slice(0, 80); }

function daysBack(days) {
  const to = Date.now();
  return { from: to - days * D, to };
}

function pointUnit(ctx, code, fallback) {
  return (ctx.db.points.find(p => p.code === code) || {}).unit || fallback;
}

/** 站点某时间窗的运行指标（供分析与报表共用） */
function siteMetrics(ctx, siteId, from, to) {
  const flow = A.series(ctx, siteId, ['FLOW.IN'], from, to, 3600);
  const energy = A.series(ctx, siteId, ['ENERGY.KWH'], from, to, 3600);
  const pac = A.series(ctx, siteId, ['PAC.TOTAL'], from, to, 3600);
  const pam = A.series(ctx, siteId, ['PAM.TOTAL'], from, to, 3600);
  const flowPts = (flow['FLOW.IN'] || []).filter(p => p.v != null);
  const waterM3 = round(A.integrate(flowPts));
  const energyKwh = round(A.counterDelta(energy['ENERGY.KWH'] || []), 2);
  const pacKg = round(A.counterDelta(pac['PAC.TOTAL'] || []), 3);
  const pamKg = round(A.counterDelta(pam['PAM.TOTAL'] || []), 4);
  const energyCost = round(energyKwh * PRICE.electricity, 2);
  const pacCost = round(pacKg * PRICE.pac, 2);
  const pamCost = round(pamKg * PRICE.pam, 2);
  const totalCost = round(energyCost + pacCost + pamCost, 2);
  const perM3 = waterM3 > 0 ? round(totalCost / waterM3, 2) : null;

  const labs = ctx.db.labRecords.filter(r => r.siteId === siteId && r.status === 'reviewed'
    && new Date(r.sampledAt) >= from && new Date(r.sampledAt) <= to);
  const labAvg = {};
  for (const m of Object.keys(LIMITS)) {
    const vals = labs.filter(r => r.metrics[m] != null).map(r => Number(r.metrics[m]));
    labAvg[m] = vals.length ? { avg: round(vals.reduce((s, v) => s + v, 0) / vals.length, 2), n: vals.length, limit: LIMITS[m] } : { avg: null, n: 0, limit: LIMITS[m] };
  }
  const inlet = labs.filter(r => r.samplePoint === 'inlet').sort((a, b) => new Date(b.sampledAt) - new Date(a.sampledAt))[0] || null;
  const outlet = labs.filter(r => r.samplePoint === 'outlet').sort((a, b) => new Date(b.sampledAt) - new Date(a.sampledAt))[0] || null;
  const removal = {};
  for (const m of Object.keys(LIMITS)) {
    const a = inlet ? inlet.metrics[m] : null, b = outlet ? outlet.metrics[m] : null;
    removal[m] = a > 0 && b != null ? round((a - b) / a * 100, 1) : null;
  }

  const alarms = ctx.db.alarms.filter(a => a.siteId === siteId && new Date(a.firstAt) >= from);
  const byLevel = {};
  for (const l of Object.keys(LEVEL_NAME)) byLevel[l] = alarms.filter(a => a.level === l).length;
  const wos = ctx.db.workorders.filter(w => w.siteId === siteId);
  const created = wos.filter(w => new Date(w.createdAt) >= from).length;
  const closed = wos.filter(w => w.closedAt && new Date(w.closedAt) >= from).length;
  const closedDur = wos.filter(w => w.closedAt && w.finishedAt).map(w => (new Date(w.finishedAt) - new Date(w.createdAt)) / H);
  const avgCloseHours = closedDur.length ? round(closedDur.reduce((s, v) => s + v, 0) / closedDur.length, 1) : null;

  const points = ctx.db.points.filter(p => p.siteId === siteId);
  const online = points.filter(p => {
    const l = ctx.telemetry.getLatest(p.id);
    return l && Date.now() - l.t < 90000;
  }).length;
  const devices = ctx.db.devices.filter(d => d.siteId === siteId && d.status !== '未安装');
  const health = devices.map(d => A.deviceHealth(d));
  const healthAvg = health.length ? Math.round(health.reduce((s, h) => s + h.score, 0) / health.length) : null;

  return {
    siteId, from: iso(from), to: iso(to),
    water: { total: waterM3, unit: 'm³', available: flowPts.length > 0, points: flowPts.length },
    energy: { total: energyKwh, unit: 'kWh', cost: energyCost, perM3: waterM3 > 0 ? round(energyKwh / waterM3, 2) : null },
    chemicals: {
      pac: { total: pacKg, unit: pointUnit(ctx, 'PAC.TOTAL', 'kg'), cost: pacCost },
      pam: { total: pamKg, unit: pointUnit(ctx, 'PAM.TOTAL', 'kg'), cost: pamCost },
      totalCost: round(pacCost + pamCost, 2),
    },
    cost: { electricity: energyCost, chemicals: round(pacCost + pamCost, 2), total: totalCost, perM3, priceAssumption: PRICE },
    quality: { labAvg, removal, inlet, outlet, reviewedSamples: labs.length, limits: LIMITS },
    alarms: { total: alarms.length, byLevel, unresolved: alarms.filter(a => a.status === 'active' || a.status === 'acknowledged').length },
    workorders: { created, closed, avgCloseHours, open: wos.filter(w => ['pending', 'accepted', 'processing'].includes(w.status)).length },
    availability: { points: points.length, online, dataRate: points.length ? round(online / points.length * 100, 1) : null, deviceHealth: healthAvg, devices: devices.length },
  };
}

function register(router, app) {
  /* ---------------- 综合分析 ---------------- */
  router.get('/api/analysis/summary', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const days = Math.min(90, Math.max(1, parseInt(ctx.query.days, 10) || 7));
    const { from, to } = daysBack(days);
    const m = siteMetrics(ctx, siteId, from, to);

    // 按天水量 / 能耗
    const flowDay = A.series(ctx, siteId, ['FLOW.IN'], from, to, 3600)['FLOW.IN'] || [];
    const energyDay = A.series(ctx, siteId, ['ENERGY.KWH'], from, to, 3600)['ENERGY.KWH'] || [];
    const daily = {};
    for (const p of flowDay) {
      const k = localDay(p.t);
      daily[k] = daily[k] || { day: k, water: 0, energy: 0 };
      // 1 小时桶的平均流量（m³/h）× 1 h = 该小时水量（m³）
      daily[k].water += (p.v || 0);
    }
    for (const p of energyDay) {
      const k = localDay(p.t);
      daily[k] = daily[k] || { day: k, water: 0, energy: 0 };
    }
    // 能耗按累计量逐日差值
    const byDayEnergy = groupBy(energyDay, p => localDay(p.t));
    for (const [k, arr] of Object.entries(byDayEnergy)) {
      daily[k] = daily[k] || { day: k, water: 0, energy: 0 };
      daily[k].energy = round(A.counterDelta(arr), 2);
    }
    const dailyArr = Object.values(daily).sort((a, b) => a.day.localeCompare(b.day))
      .map(d => ({ ...d, water: round(d.water, 3) }));

    const alarmDaily = {};
    for (const a of ctx.db.alarms.filter(x => x.siteId === siteId)) {
      const k = localDay(new Date(a.firstAt).getTime());
      alarmDaily[k] = (alarmDaily[k] || 0) + 1;
    }

    ok(ctx.res, {
      ...m,
      days, daily: dailyArr, alarmDaily,
      notes: [
        '水量由进水流量累计积分得到（演示数据由内置模拟网关生成）。',
        '能耗、药耗按累计量差值计算；单价为演示假设，仅用于趋势对比，不作为结算依据。',
        '总氮、总磷、SS 无在线仪表，出水指标取人工化验复核后的数据。',
      ],
    });
  });

  router.get('/api/analysis/units', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const days = Math.min(90, Math.max(1, parseInt(ctx.query.days, 10) || 7));
    const { from, to } = daysBack(days);
    const units = ctx.db.units.filter(u => u.siteId === siteId).sort((a, b) => (a.order || 0) - (b.order || 0));
    const alarms = ctx.db.alarms.filter(a => a.siteId === siteId && new Date(a.firstAt) >= from);
    const items = units.map(u => {
      const readings = A.latestList(ctx, siteId).filter(r => r.unitCode === u.code);
      const unitAlarms = alarms.filter(a => a.unitCode === u.code);
      const devices = ctx.db.devices.filter(d => d.siteId === siteId && d.unitCode === u.code);
      const health = devices.filter(d => d.status !== '未安装').map(d => A.deviceHealth(d));
      return {
        code: u.code, name: u.name, order: u.order,
        readings: readings.slice(0, 8),
        alarms: { total: unitAlarms.length, open: unitAlarms.filter(a => a.status === 'active' || a.status === 'acknowledged').length },
        devices: { total: devices.length, online: devices.filter(d => d.online).length },
        health: health.length ? Math.round(health.reduce((s, h) => s + h.score, 0) / health.length) : null,
      };
    });
    ok(ctx.res, { siteId, days, items });
  });

  router.get('/api/analysis/efficiency', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const days = Math.min(90, Math.max(1, parseInt(ctx.query.days, 10) || 7));
    const { from, to } = daysBack(days);
    const devices = ctx.db.devices.filter(d => d.siteId === siteId && d.status !== '未安装');
    const list = devices.map(d => {
      const h = A.deviceHealth(d);
      const runtime = A.series(ctx, siteId, [d.key === 'raw-pump' ? 'RAW.PUMP.HOURS' : ''].filter(Boolean), from, to, 3600);
      return {
        id: d.id, code: d.code, name: d.name, unitCode: d.unitCode, type: d.type,
        online: d.online, status: d.status, runtimeHours: d.runtimeHours, starts: d.starts,
        health: h.score, healthLevel: h.level, daysToMaint: h.daysToMaint, needMaint: h.needMaint,
      };
    });
    const uptime = list.length ? round(list.filter(d => d.online).length / list.length * 100, 1) : null;
    const maintDue = list.filter(d => d.needMaint).length;
    ok(ctx.res, {
      siteId, days, uptime, maintDue,
      devices: list.sort((a, b) => a.health - b.health),
      utilization: {
        rawPumpStarts: (ctx.db.points.find(p => p.siteId === siteId && p.code === 'RAW.PUMP.STARTS') || {}).value,
        note: '设备运行时长来自 PLC 累计量，启停次数来自运行状态跳变计数。',
      },
    });
  });

  /* ---------------- 报表 ---------------- */

  function reportRange(type, b) {
    const now = Date.now();
    if (type === 'daily') return { from: startOfDay(now), to: now, label: `${dayKey(now)} 运行日报` };
    if (type === 'weekly') return { from: now - 7 * D, to: now, label: `${dayKey(now - 6 * D)} 至 ${dayKey(now)} 运行周报` };
    if (type === 'monthly') return { from: now - 30 * D, to: now, label: `${dayKey(now - 29 * D)} 至 ${dayKey(now)} 运行月报` };
    const from = b.from ? new Date(b.from).getTime() : now - 7 * D;
    const to = b.to ? new Date(b.to).getTime() : now;
    if (!(from < to)) throw new ApiError(400, '自定义报表的结束时间必须晚于开始时间', 'BAD_RANGE');
    return { from, to, label: `${dayKey(from)} 至 ${dayKey(to)} 运行报表` };
  }

  function reportSections(ctx, rep) {
    const from = new Date(rep.periodFrom).getTime();
    const to = new Date(rep.periodTo).getTime();
    const m = siteMetrics(ctx, rep.siteId, from, to);
    const site = ctx.db.sites.find(s => s.id === rep.siteId) || {};
    const labRows = ctx.db.labRecords
      .filter(r => r.siteId === rep.siteId && new Date(r.sampledAt) >= from && new Date(r.sampledAt) <= to)
      .sort((a, b) => new Date(a.sampledAt) - new Date(b.sampledAt));
    const alRows = ctx.db.alarms.filter(a => a.siteId === rep.siteId && new Date(a.firstAt) >= from)
      .sort((a, b) => new Date(b.firstAt) - new Date(a.firstAt));
    const woRows = ctx.db.workorders.filter(w => w.siteId === rep.siteId && new Date(w.createdAt) >= from);
    return [
      {
        key: 'summary', title: '一、运行概况',
        columns: ['指标', '数值', '单位', '说明'],
        rows: [
          ['站点', site.name || rep.siteId, '', '示范站点'],
          ['统计区间', `${rep.periodFrom.slice(0, 16).replace('T', ' ')} ~ ${rep.periodTo.slice(0, 16).replace('T', ' ')}`, '', ''],
          ['累计处理水量', m.water.total, 'm³', m.water.available ? '进水流量积分' : '待接入在线流量计'],
          ['累计耗电', m.energy.total, 'kWh', `电费估算 ${m.energy.cost} 元（${PRICE.electricity} 元/kWh）`],
          ['吨水电耗', m.energy.perM3 == null ? '—' : m.energy.perM3, 'kWh/m³', ''],
          ['PAC 投加量', m.chemicals.pac.total, m.chemicals.pac.unit, `估算 ${m.chemicals.pac.cost} 元`],
          ['PAM 投加量', m.chemicals.pam.total, m.chemicals.pam.unit, `估算 ${m.chemicals.pam.cost} 元`],
          ['吨水综合成本', m.cost.perM3 == null ? '—' : m.cost.perM3, '元/m³', '电费+药剂，演示单价'],
          ['活跃告警', m.alarms.total, '条', `未闭环 ${m.alarms.unresolved} 条`],
          ['新增工单', m.workorders.created, '单', `闭环 ${m.workorders.closed} 单`],
          ['点位在线率', m.availability.dataRate == null ? '—' : m.availability.dataRate, '%', `${m.availability.online}/${m.availability.points}`],
        ],
      },
      {
        key: 'quality', title: '二、水质与去除率',
        columns: ['指标', '出水均值', '限值（一级A参考）', '去除率', '样本数'],
        rows: Object.keys(LIMITS).map(k => [
          METRIC_NAMES[k], m.quality.labAvg[k].avg == null ? '—' : m.quality.labAvg[k].avg,
          LIMITS[k], m.quality.removal[k] == null ? '—' : `${m.quality.removal[k]}%`, m.quality.labAvg[k].n,
        ]),
      },
      {
        key: 'lab', title: '三、化验记录',
        columns: ['编号', '取样时间', '取样点', 'COD', '氨氮', '总氮', '总磷', 'SS', '状态'],
        rows: labRows.map(r => [r.id, r.sampledAt.slice(0, 16).replace('T', ' '), r.samplePointName || r.samplePoint,
          r.metrics.COD, r.metrics.NH3N, r.metrics.TN, r.metrics.TP, r.metrics.SS,
          r.status === 'reviewed' ? '已复核' : r.status === 'rejected' ? '已退回' : '待复核']),
      },
      {
        key: 'alarms', title: '四、告警明细',
        columns: ['编号', '发生时间', '等级', '单元', '内容', '状态'],
        rows: alRows.map(a => [a.id, a.firstAt.slice(0, 16).replace('T', ' '), LEVEL_NAME[a.level] || a.level, a.unitCode, a.title, a.status]),
      },
      {
        key: 'workorders', title: '五、工单明细',
        columns: ['编号', '创建时间', '标题', '类型', '优先级', '状态', '处理结果'],
        rows: woRows.map(w => [w.id, w.createdAt.slice(0, 16).replace('T', ' '), w.title, w.type, w.priority, w.status, w.result || '']),
      },
    ];
  }

  router.get('/api/reports', async (ctx) => {
    requireAuth(ctx);
    const siteIds = scopedSites(ctx).map(s => s.id);
    let items = ctx.db.reports.filter(r => siteIds.includes(r.siteId));
    if (ctx.query.siteId) { getSite(ctx, ctx.query.siteId); items = items.filter(r => r.siteId === ctx.query.siteId); }
    if (ctx.query.type) items = items.filter(r => r.type === ctx.query.type);
    items = items.sort((a, b) => new Date(b.at) - new Date(a.at));
    const page = paginate(items, ctx.query);
    ok(ctx.res, page.items.map(r => ({
      ...r, typeName: REPORT_TYPES[r.type] || r.type,
      siteName: (ctx.db.sites.find(s => s.id === r.siteId) || {}).shortName,
      creatorName: (ctx.db.users.find(u => u.id === r.createdBy) || {}).name,
    })), { ...page, items: undefined, total: items.length, types: REPORT_TYPES });
  });

  router.get('/api/reports/:id', async (ctx) => {
    requireAuth(ctx);
    const rep = ctx.db.reports.find(r => r.id === ctx.params.id);
    if (!rep) throw new ApiError(404, '报表不存在', 'REPORT_NOT_FOUND');
    getSite(ctx, rep.siteId);
    ok(ctx.res, {
      ...rep, typeName: REPORT_TYPES[rep.type] || rep.type,
      siteName: (ctx.db.sites.find(s => s.id === rep.siteId) || {}).name,
      creatorName: (ctx.db.users.find(u => u.id === rep.createdBy) || {}).name,
      sections: reportSections(ctx, rep),
    });
  });

  router.post('/api/reports', async (ctx) => {
    const user = requirePerm(ctx, 'report.export');
    const b = ctx.body || {};
    const type = REPORT_TYPES[b.type] ? b.type : 'weekly';
    const siteId = b.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const range = reportRange(type, b);
    const rep = {
      id: nextCode(ctx.db, 'reports', 'RP'), siteId, type,
      title: safeName(b.title || range.label),
      periodFrom: iso(range.from), periodTo: iso(range.to),
      createdBy: user.id, createdAt: iso(), status: 'ready', format: b.format || 'csv',
      note: String(b.note || '').slice(0, 200),
    };
    ctx.db.reports.push(rep);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'report.create', targetType: 'report', targetId: rep.id, siteId, detail: `生成${REPORT_TYPES[type]}：${rep.title}` });
    app.publish({ type: 'report', action: 'create', data: rep });
    ok(ctx.res, { ...rep, sections: reportSections(ctx, rep) });
  });

  router.get('/api/reports/:id/export', async (ctx) => {
    const user = requirePerm(ctx, 'report.export');
    const rep = ctx.db.reports.find(r => r.id === ctx.params.id);
    if (!rep) throw new ApiError(404, '报表不存在', 'REPORT_NOT_FOUND');
    getSite(ctx, rep.siteId);
    const sections = reportSections(ctx, rep);
    const lines = [];
    lines.push(`${rep.title}`);
    lines.push(`报表编号,${rep.id}`);
    lines.push(`站点,${(ctx.db.sites.find(s => s.id === rep.siteId) || {}).name || rep.siteId}`);
    lines.push(`统计区间,${rep.periodFrom} ~ ${rep.periodTo}`);
    lines.push(`生成时间,${iso()}`);
    lines.push(`生成人,${user.name}`);
    lines.push('');
    for (const s of sections) {
      lines.push(s.title);
      lines.push(s.columns.map(csvEsc).join(','));
      for (const r of s.rows) lines.push(r.map(csvEsc).join(','));
      lines.push('');
    }
    lines.push('说明：演示环境数据由内置模拟网关生成；电费与药剂单价为估算假设，不作为结算依据。');
    const csv = '\uFEFF' + lines.join('\r\n');
    sendText(ctx.res, 200, csv, 'text/csv; charset=utf-8', {
      'Content-Disposition': `attachment; filename="report-${rep.id}.csv"`,
    });
    audit(ctx, { actor: user, action: 'report.export', targetType: 'report', targetId: rep.id, siteId: rep.siteId, detail: `导出 CSV：${rep.title}` });
  });

  router.delete('/api/reports/:id', async (ctx) => {
    const user = requirePerm(ctx, 'report.export');
    const idx = ctx.db.reports.findIndex(r => r.id === ctx.params.id);
    if (idx < 0) throw new ApiError(404, '报表不存在', 'REPORT_NOT_FOUND');
    const rep = ctx.db.reports[idx];
    getSite(ctx, rep.siteId);
    ctx.db.reports.splice(idx, 1);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'report.delete', targetType: 'report', targetId: rep.id, siteId: rep.siteId, detail: `删除报表：${rep.title}` });
    ok(ctx.res, { deleted: true });
  });

  /* ---------------- 智能诊断 ---------------- */
  router.get('/api/diagnosis', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const items = app.diagnosis.list(siteId);
    ok(ctx.res, items, { note: '诊断结果由当前实时数据与规则阈值即时推导，每条均给出触发依据与数据时间。' });
  });

  router.get('/api/diagnosis/:id', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const found = app.diagnosis.list(siteId).find(d => d.id === ctx.params.id);
    if (!found) throw new ApiError(404, '诊断项已随数据变化消失，请刷新列表', 'DIAGNOSIS_NOT_FOUND');
    ok(ctx.res, found);
  });

  /* ---------------- 通知 ---------------- */
  router.get('/api/notifications', async (ctx) => {
    requireAuth(ctx);
    let items = ctx.db.notifications.filter(n => n.userId === ctx.user.id);
    if (ctx.query.unread === '1') items = items.filter(n => !n.read);
    if (ctx.query.type) items = items.filter(n => n.type === ctx.query.type);
    items = items.sort((a, b) => new Date(b.at) - new Date(a.at));
    const page = paginate(items, ctx.query);
    ok(ctx.res, page.items, {
      ...page, items: undefined,
      unread: ctx.db.notifications.filter(n => n.userId === ctx.user.id && !n.read).length,
    });
  });

  router.post('/api/notifications/read', async (ctx) => {
    requireAuth(ctx);
    const b = ctx.body || {};
    const ids = Array.isArray(b.ids) ? b.ids : null;
    if (b.all) app.notifier.markRead(ctx.user.id, null);
    else {
      if (!ids || !ids.length) throw new ApiError(400, '请指定要标记的通知', 'IDS_REQUIRED');
      app.notifier.markRead(ctx.user.id, ids);
    }
    ctx.store.saveSoon();
    ok(ctx.res, { unread: ctx.db.notifications.filter(n => n.userId === ctx.user.id && !n.read).length });
  });

  router.delete('/api/notifications/:id', async (ctx) => {
    requireAuth(ctx);
    const idx = ctx.db.notifications.findIndex(n => n.id === ctx.params.id && n.userId === ctx.user.id);
    if (idx < 0) throw new ApiError(404, '通知不存在', 'NOTIFICATION_NOT_FOUND');
    ctx.db.notifications.splice(idx, 1);
    ctx.store.saveSoon();
    ok(ctx.res, { deleted: true });
  });

  /* ---------------- 审计 ---------------- */
  router.get('/api/audit', async (ctx) => {
    requirePerm(ctx, 'admin.audit');
    let items = ctx.db.auditLogs.slice();
    if (ctx.query.siteId) { getSite(ctx, ctx.query.siteId); items = items.filter(a => a.siteId === ctx.query.siteId); }
    if (ctx.query.action) items = items.filter(a => String(a.action).startsWith(String(ctx.query.action)));
    if (ctx.query.actor) items = items.filter(a => a.actor === ctx.query.actor || (a.actorName || '').includes(String(ctx.query.actor)));
    if (ctx.query.result) items = items.filter(a => a.result === ctx.query.result);
    if (ctx.query.from) items = items.filter(a => new Date(a.at) >= new Date(ctx.query.from));
    if (ctx.query.to) items = items.filter(a => new Date(a.at) <= new Date(ctx.query.to));
    if (ctx.query.q) {
      const q = String(ctx.query.q).toLowerCase();
      items = items.filter(a => `${a.action} ${a.targetType} ${a.targetId} ${a.detail} ${a.actorName}`.toLowerCase().includes(q));
    }
    items = items.sort((a, b) => new Date(b.at) - new Date(a.at));
    const page = paginate(items, ctx.query);
    ok(ctx.res, page.items.map(a => ({
      ...a,
      actorName: a.actorName || (ctx.db.users.find(u => u.id === a.actor) || {}).name || a.actor,
      actionName: AUDIT_ACTIONS[a.action] || a.action,
    })), { ...page, items: undefined, total: items.length, actions: AUDIT_ACTIONS });
  });

  /* ---------------- 用户与角色 ---------------- */
  router.get('/api/users', async (ctx) => {
    requirePerm(ctx, 'admin.users');
    let items = ctx.db.users.slice();
    if (ctx.query.role) items = items.filter(u => u.role === ctx.query.role);
    if (ctx.query.status) items = items.filter(u => u.status === ctx.query.status);
    if (ctx.query.q) {
      const q = String(ctx.query.q).toLowerCase();
      items = items.filter(u => `${u.username} ${u.name} ${u.title}`.toLowerCase().includes(q));
    }
    ok(ctx.res, items.map(u => ({
      ...u, password: undefined,
      roleName: (ctx.db.roles[u.role] || {}).name || u.role,
      siteNames: (u.siteIds || []).map(id => (ctx.db.sites.find(s => s.id === id) || {}).shortName).filter(Boolean),
    })), { total: items.length, roles: Object.entries(ctx.db.roles).map(([k, v]) => ({ id: k, name: v.name, sites: v.sites, perms: v.perms })) });
  });

  router.post('/api/users', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.users');
    const b = ctx.body || {};
    if (!b.username || !/^[a-z0-9_]{3,20}$/.test(b.username)) throw new ApiError(400, '账号需为 3-20 位小写字母、数字或下划线', 'BAD_USERNAME');
    if (ctx.db.users.some(u => u.username === b.username)) throw new ApiError(409, '账号已存在', 'USERNAME_TAKEN');
    if (!b.name) throw new ApiError(400, '请填写姓名', 'NAME_REQUIRED');
    if (!ctx.db.roles[b.role]) throw new ApiError(400, '角色不合法', 'BAD_ROLE');
    const pwd = String(b.password || 'water123');
    if (pwd.length < 6) throw new ApiError(400, '密码至少 6 位', 'BAD_PASSWORD');
    const user = {
      id: uid('u-'), username: b.username, name: String(b.name).slice(0, 30),
      role: b.role, title: String(b.title || '').slice(0, 40),
      phone: String(b.phone || '').slice(0, 20),
      siteIds: Array.isArray(b.siteIds) && b.siteIds.length ? b.siteIds : [scopedSites(ctx)[0].id],
      status: 'active', password: hashPassword(pwd), createdAt: iso(), lastLoginAt: null,
    };
    ctx.db.users.push(user);
    ctx.store.saveSoon();
    audit(ctx, { actor, action: 'user.create', targetType: 'user', targetId: user.id, detail: `创建账号 ${user.username}（${user.name}）角色 ${user.role}` });
    ok(ctx.res, { ...user, password: undefined });
  });

  router.patch('/api/users/:id', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.users');
    const u = ctx.db.users.find(x => x.id === ctx.params.id);
    if (!u) throw new ApiError(404, '用户不存在', 'USER_NOT_FOUND');
    const b = ctx.body || {};
    const before = { name: u.name, role: u.role, status: u.status, siteIds: u.siteIds };
    if (b.role && !ctx.db.roles[b.role]) throw new ApiError(400, '角色不合法', 'BAD_ROLE');
    if (b.role && u.role === 'super' && b.role !== 'super') {
      const supers = ctx.db.users.filter(x => x.role === 'super' && x.status === 'active');
      if (supers.length <= 1) throw new ApiError(409, '至少保留一个启用的超级管理员', 'LAST_SUPER');
    }
    if (u.id === actor.id && b.status === 'inactive') throw new ApiError(409, '不能停用当前登录账号', 'SELF_DISABLE');
    for (const k of ['name', 'title', 'phone', 'role', 'status']) if (b[k] !== undefined) u[k] = String(b[k]).slice(0, 40);
    if (Array.isArray(b.siteIds)) u.siteIds = b.siteIds;
    if (u.status !== 'active') app.auth.logoutAllFor(u.id);
    ctx.store.saveSoon();
    audit(ctx, { actor, action: 'user.update', targetType: 'user', targetId: u.id, detail: `修改账号 ${u.username}：${JSON.stringify(before)} → ${JSON.stringify({ name: u.name, role: u.role, status: u.status, siteIds: u.siteIds })}` });
    ok(ctx.res, { ...u, password: undefined });
  });

  router.post('/api/users/:id/reset-password', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.users');
    const u = ctx.db.users.find(x => x.id === ctx.params.id);
    if (!u) throw new ApiError(404, '用户不存在', 'USER_NOT_FOUND');
    const pwd = String((ctx.body || {}).password || 'water123');
    if (pwd.length < 6) throw new ApiError(400, '密码至少 6 位', 'BAD_PASSWORD');
    u.password = hashPassword(pwd);
    app.auth.logoutAllFor(u.id);
    ctx.store.saveSoon();
    audit(ctx, { actor, action: 'user.resetPassword', targetType: 'user', targetId: u.id, detail: `重置账号 ${u.username} 的密码` });
    ok(ctx.res, { reset: true, username: u.username });
  });

  router.get('/api/roles', async (ctx) => {
    requireAuth(ctx);
    ok(ctx.res, Object.entries(ctx.db.roles).map(([k, v]) => ({ id: k, ...v })));
  });

  /* ---------------- 系统设置 ---------------- */
  router.get('/api/settings', async (ctx) => {
    requireAuth(ctx);
    const s = ctx.store.data.settings;
    ok(ctx.res, {
      ...s,
      channels: ctx.db.channels.map(c => ({ id: c.id, name: c.name, type: c.type, enabled: c.enabled, status: c.status })),
      storage: { file: 'data/db.json', size: safeSize(ctx.store.file), savedAt: ctx.store.lastSavedAt ? iso(ctx.store.lastSavedAt) : null, historyDays: HISTORY_DAYS },
    });
  });

  router.patch('/api/settings', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.config');
    const b = ctx.body || {};
    const s = ctx.store.data.settings;
    const before = JSON.parse(JSON.stringify(s));
    const allow = ['platformName', 'shortName', 'orgName', 'notice', 'autoRefreshSec', 'commandTimeoutSec', 'dataRetentionDays', 'offlineCacheMinutes', 'quietHours', 'backup'];
    for (const k of allow) {
      if (b[k] === undefined) continue;
      if (k === 'autoRefreshSec') s[k] = Math.min(60, Math.max(2, Number(b[k]) || 5));
      else if (k === 'commandTimeoutSec') s[k] = Math.min(180, Math.max(10, Number(b[k]) || 30));
      else if (k === 'dataRetentionDays') s[k] = Math.min(90, Math.max(1, Number(b[k]) || 7));
      else if (k === 'offlineCacheMinutes') s[k] = Math.min(240, Math.max(5, Number(b[k]) || 30));
      else if (k === 'quietHours' || k === 'backup') s[k] = { ...s[k], ...b[k] };
      else s[k] = String(b[k]).slice(0, 200);
    }
    ctx.store.saveSoon();
    audit(ctx, { actor, action: 'settings.update', targetType: 'settings', targetId: 'settings', detail: `修改系统设置`, before, after: s });
    app.publish({ type: 'settings', action: 'update', data: { autoRefreshSec: s.autoRefreshSec, platformName: s.platformName, notice: s.notice } });
    ok(ctx.res, s);
  });

  /* ---------------- 通知通道 ---------------- */
  router.get('/api/channels', async (ctx) => {
    requirePerm(ctx, 'admin.config');
    ok(ctx.res, ctx.db.channels.map(c => ({ ...c, available: c.type === 'inapp' || (c.enabled && !!c.target) })));
  });

  router.patch('/api/channels/:id', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.config');
    const c = ctx.db.channels.find(x => x.id === ctx.params.id);
    if (!c) throw new ApiError(404, '通知通道不存在', 'CHANNEL_NOT_FOUND');
    const b = ctx.body || {};
    const before = { enabled: c.enabled, target: c.target };
    if (b.enabled !== undefined) c.enabled = !!b.enabled;
    if (b.target !== undefined) c.target = String(b.target).slice(0, 120);
    if (b.note !== undefined) c.note = String(b.note).slice(0, 200);
    c.status = !c.enabled ? '已停用' : (c.target ? '正常' : '待配置');
    ctx.store.saveSoon();
    audit(ctx, { actor, action: 'channel.update', targetType: 'channel', targetId: c.id, detail: `更新通知通道「${c.name}」：${JSON.stringify(before)} → ${JSON.stringify({ enabled: c.enabled, target: c.target })}` });
    ok(ctx.res, c);
  });

  router.post('/api/channels/:id/test', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.config');
    const c = ctx.db.channels.find(x => x.id === ctx.params.id);
    if (!c) throw new ApiError(404, '通知通道不存在', 'CHANNEL_NOT_FOUND');
    if (!c.enabled) throw new ApiError(409, `通道「${c.name}」未启用`, 'CHANNEL_DISABLED');
    if (c.type !== 'inapp' && !c.target) throw new ApiError(409, `通道「${c.name}」未配置接收地址`, 'CHANNEL_NO_TARGET');
    c.testedAt = iso();
    c.status = '正常';
    ctx.store.saveSoon();
    app.notifier.send([actor.id], { type: 'system', title: `通知通道测试：${c.name}`, body: c.type === 'inapp' ? '站内消息通道正常。' : `演示环境仅记录发送动作，未真实调用外部网关（目标：${c.target}）。`, link: '/settings' });
    audit(ctx, { actor, action: 'channel.test', targetType: 'channel', targetId: c.id, detail: `测试通道「${c.name}」` });
    ok(ctx.res, { tested: true, simulated: c.type !== 'inapp', channel: c });
  });

  /* ---------------- 备份与恢复 ---------------- */
  const backupDir = () => {
    const d = path.join(app.dataDir, 'backups');
    fs.mkdirSync(d, { recursive: true });
    return d;
  };

  router.get('/api/backups', async (ctx) => {
    requirePerm(ctx, 'admin.config');
    const d = backupDir();
    const files = fs.readdirSync(d).filter(f => f.endsWith('.json')).map(f => {
      const st = fs.statSync(path.join(d, f));
      const known = ctx.db.backups.find(b => b.file === f);
      return {
        id: known ? known.id : f.replace(/\.json$/, ''),
        file: f, size: st.size, createdAt: known ? known.createdAt : iso(st.mtimeMs),
        createdBy: known ? known.createdBy : null, note: known ? known.note : '文件系统中的备份',
        auto: known ? known.auto : false,
      };
    }).sort((a, b) => new Date(b.at) - new Date(a.at));
    ok(ctx.res, files, { dir: 'data/backups', count: files.length, lastAt: ctx.store.data.settings.backup.lastAt });
  });

  router.post('/api/backups', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.config');
    const b = ctx.body || {};
    fs.mkdirSync(backupDir(), { recursive: true });
    const stamp = iso().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
    const file = `backup-${stamp}.json`;
    const target = path.join(backupDir(), file);
    ctx.store.save(true);
    fs.copyFileSync(ctx.store.file, target);
    const st = fs.statSync(target);
    const rec = {
      id: nextCode(ctx.db, 'backups', 'BK'), file, size: st.size, createdAt: iso(),
      createdBy: actor.id, note: String(b.note || '手工备份').slice(0, 120), auto: false,
    };
    ctx.db.backups.push(rec);
    ctx.store.data.settings.backup.lastAt = rec.createdAt;
    ctx.store.saveSoon();
    audit(ctx, { actor, action: 'backup.create', targetType: 'backup', targetId: rec.id, detail: `创建备份 ${file}（${st.size} 字节）` });
    ok(ctx.res, rec);
  });

  router.get('/api/backups/:id/download', async (ctx) => {
    requirePerm(ctx, 'admin.config');
    const rec = ctx.db.backups.find(b => b.id === ctx.params.id);
    if (!rec) throw new ApiError(404, '备份记录不存在', 'BACKUP_NOT_FOUND');
    const file = path.join(backupDir(), path.basename(rec.file));
    if (!fs.existsSync(file)) throw new ApiError(404, '备份文件已不存在', 'BACKUP_FILE_MISSING');
    const buf = fs.readFileSync(file);
    ctx.res.writeHead(200, {
      'Content-Type': 'application/json; charset=utf-8',
      'Content-Disposition': `attachment; filename="${rec.file}"`,
      'Content-Length': buf.length,
    });
    ctx.res.end(buf);
  });

  router.post('/api/backups/:id/restore', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.config');
    const rec = ctx.db.backups.find(b => b.id === ctx.params.id);
    if (!rec) throw new ApiError(404, '备份记录不存在', 'BACKUP_NOT_FOUND');
    const file = path.join(backupDir(), path.basename(rec.file));
    if (!fs.existsSync(file)) throw new ApiError(404, '备份文件已不存在', 'BACKUP_FILE_MISSING');
    if ((ctx.body || {}).confirm !== true) throw new ApiError(400, '恢复备份将覆盖当前数据，请传 confirm:true 确认', 'CONFIRM_REQUIRED');
    let parsed;
    try { parsed = JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { throw new ApiError(400, '备份文件损坏，无法解析', 'BACKUP_CORRUPT'); }
    if (!parsed || !parsed.meta || !Array.isArray(parsed.users)) throw new ApiError(400, '备份内容不是有效的平台数据', 'BACKUP_INVALID');
    // 先给当前状态留一份回滚点
    const stamp = iso().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
    const rollback = `pre-restore-${stamp}.json`;
    ctx.store.save(true);
    fs.copyFileSync(ctx.store.file, path.join(backupDir(), rollback));
    app.restoreDb(parsed);
    audit(ctx, { actor, action: 'backup.restore', targetType: 'backup', targetId: rec.id, detail: `从备份 ${rec.file} 恢复数据，恢复前状态保存为 ${rollback}` });
    ok(ctx.res, { restored: true, rollbackFile: rollback, note: '数据已从备份恢复；实时遥测内存将在下一次采集时继续写入。' });
  });

  router.delete('/api/backups/:id', async (ctx) => {
    const actor = requirePerm(ctx, 'admin.config');
    const idx = ctx.db.backups.findIndex(b => b.id === ctx.params.id);
    if (idx < 0) throw new ApiError(404, '备份记录不存在', 'BACKUP_NOT_FOUND');
    const rec = ctx.db.backups[idx];
    const file = path.join(backupDir(), path.basename(rec.file));
    if (fs.existsSync(file)) fs.unlinkSync(file);
    ctx.db.backups.splice(idx, 1);
    ctx.store.saveSoon();
    audit(ctx, { actor, action: 'backup.delete', targetType: 'backup', targetId: rec.id, detail: `删除备份 ${rec.file}` });
    ok(ctx.res, { deleted: true });
  });
}

function localDay(ts) {
  const d = new Date(ts + 8 * H);
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
}

function groupBy(arr, fn) {
  const out = {};
  for (const x of arr) { const k = fn(x); (out[k] = out[k] || []).push(x); }
  return out;
}

function csvEsc(v) {
  const s = v === null || v === undefined ? '' : String(v);
  return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

function safeSize(p) {
  try { return fs.statSync(p).size; } catch (e) { return null; }
}

const AUDIT_ACTIONS = {
  'user.login': '登录', 'user.login.fail': '登录失败', 'user.logout': '退出',
  'command.create': '下发控制命令', 'command.confirm': '确认高风险命令', 'command.cancel': '取消命令',
  'alarm.ack': '确认告警', 'alarm.resolve': '处理告警', 'alarm.close': '关闭告警',
  'alarmRule.update': '修改告警规则', 'alarmRule.create': '新增告警规则',
  'workorder.create': '创建工单', 'workorder.accept': '接单', 'workorder.start': '开始处理',
  'workorder.finish': '完成工单', 'workorder.verify': '验收工单', 'workorder.assign': '指派工单', 'workorder.close': '关闭工单',
  'inspection.create': '生成巡检任务', 'inspection.submit': '提交巡检',
  'lab.create': '录入化验', 'lab.review': '复核化验',
  'param.update': '修改工艺参数', 'param.rollback': '回滚工艺参数', 'param.request': '提交参数申请',
  'param.approve': '审批参数申请', 'param.reject': '驳回参数申请',
  'report.create': '生成报表', 'report.export': '导出报表', 'report.delete': '删除报表',
  'user.create': '创建用户', 'user.update': '修改用户', 'user.resetPassword': '重置密码',
  'settings.update': '修改系统设置', 'channel.update': '修改通知通道', 'channel.test': '测试通知通道',
  'backup.create': '创建备份', 'backup.restore': '恢复备份', 'backup.delete': '删除备份',
  'device.maintenance': '登记设备保养',
};

module.exports = { register, siteMetrics, REPORT_TYPES, LIMITS, METRIC_NAMES, PRICE };

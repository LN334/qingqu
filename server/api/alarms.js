'use strict';
const { ok, ApiError, requireAuth, requirePerm, getSite, scopedSites, paginate, audit } = require('./common');
const { LEVEL_NAME, thresholdText, LEVELS } = require('../rules');
const { iso, startOfDay, dayKey, round } = require('../util');

function levelRank(l) { return { critical: 4, major: 3, minor: 2, info: 1 }[l] || 0; }

function alarmView(ctx, a) {
  const point = ctx.db.points.find(p => p.id === a.pointId) || null;
  const site = ctx.db.sites.find(s => s.id === a.siteId) || null;
  const unit = ctx.db.units.find(u => u.siteId === a.siteId && u.code === a.unitCode) || null;
  return {
    ...a,
    levelName: LEVEL_NAME[a.level] || a.level,
    unitName: unit ? unit.name : a.unitCode,
    unitShort: unit ? unit.short : a.unitCode,
    siteName: site ? site.shortName || site.name : a.siteId,
    pointName: point ? point.name : a.pointCode,
    pointUnit: point ? point.unit : '',
    ackByName: a.ackBy ? ((ctx.db.users.find(u => u.id === a.ackBy) || {}).name || a.ackBy) : null,
    resolvedByName: a.resolvedBy ? ((ctx.db.users.find(u => u.id === a.resolvedBy) || {}).name || a.resolvedBy) : (a.resolvedBy === 'system' ? '系统自动' : null),
    durationMin: a.firstAt ? Math.max(0, Math.round(((a.resolvedAt ? new Date(a.resolvedAt) : new Date()) - new Date(a.firstAt)) / 60000)) : null,
  };
}

function register(router, app) {
  router.get('/api/alarms', async (ctx) => {
    requireAuth(ctx);
    const siteIds = scopedSites(ctx).map(s => s.id);
    let items = ctx.db.alarms.filter(a => siteIds.includes(a.siteId));
    if (ctx.query.siteId) { getSite(ctx, ctx.query.siteId); items = items.filter(a => a.siteId === ctx.query.siteId); }
    if (ctx.query.status === 'open') items = items.filter(a => a.status === 'active' || a.status === 'acknowledged');
    else if (ctx.query.status) items = items.filter(a => a.status === ctx.query.status);
    if (ctx.query.level) items = items.filter(a => a.level === ctx.query.level);
    if (ctx.query.unitCode) items = items.filter(a => a.unitCode === ctx.query.unitCode);
    if (ctx.query.from) items = items.filter(a => new Date(a.firstAt) >= new Date(ctx.query.from));
    if (ctx.query.to) items = items.filter(a => new Date(a.firstAt) <= new Date(ctx.query.to));
    if (ctx.query.q) {
      const q = String(ctx.query.q).toLowerCase();
      items = items.filter(a => `${a.id} ${a.title} ${a.detail} ${a.pointCode}`.toLowerCase().includes(q));
    }
    const counts = {
      all: items.length,
      active: items.filter(a => a.status === 'active').length,
      acknowledged: items.filter(a => a.status === 'acknowledged').length,
      resolved: items.filter(a => a.status === 'resolved').length,
      closed: items.filter(a => a.status === 'closed').length,
      critical: items.filter(a => a.level === 'critical' && (a.status === 'active' || a.status === 'acknowledged')).length,
      major: items.filter(a => a.level === 'major' && (a.status === 'active' || a.status === 'acknowledged')).length,
    };
    items = items.sort((a, b) => new Date(b.firstAt) - new Date(a.firstAt));
    const page = paginate(items, ctx.query);
    ok(ctx.res, page.items.map(a => alarmView(ctx, a)), { ...page, items: undefined, counts, levels: LEVELS.map(k => ({ key: k, name: LEVEL_NAME[k] })) });
  });

  router.get('/api/alarms/stats', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const days = Math.min(90, Math.max(1, parseInt(ctx.query.days, 10) || 7));
    const from = startOfDay(Date.now() - (days - 1) * 86400000);
    const list = ctx.db.alarms.filter(a => a.siteId === siteId && new Date(a.firstAt) >= from);
    const byDay = [];
    for (let i = days - 1; i >= 0; i--) {
      const t = startOfDay(Date.now() - i * 86400000);
      const key = dayKey(t);
      const inDay = list.filter(a => dayKey(new Date(a.firstAt).getTime()) === key);
      byDay.push({ day: key, total: inDay.length, critical: inDay.filter(a => a.level === 'critical').length, major: inDay.filter(a => a.level === 'major').length, minor: inDay.filter(a => a.level === 'minor').length, info: inDay.filter(a => a.level === 'info').length });
    }
    const acked = list.filter(a => a.ackAt);
    const resolved = list.filter(a => a.resolvedAt);
    const avg = arr => arr.length ? Math.round(arr.reduce((s, x) => s + x, 0) / arr.length) : null;
    const byUnit = {};
    for (const a of list) byUnit[a.unitCode] = (byUnit[a.unitCode] || 0) + 1;
    const byRule = {};
    for (const a of list) if (a.ruleId) byRule[a.ruleId] = (byRule[a.ruleId] || 0) + 1;
    const topRules = Object.entries(byRule).sort((a, b) => b[1] - a[1]).slice(0, 5).map(([id, n]) => {
      const r = ctx.db.alarmRules.find(x => x.id === id);
      return { ruleId: id, name: r ? r.name : id, count: n };
    });
    ok(ctx.res, {
      siteId, days, from: iso(from), total: list.length,
      byDay, byLevel: LEVELS.reduce((o, k) => ({ ...o, [k]: list.filter(a => a.level === k).length }), {}),
      byUnit, topRules,
      mttaMin: avg(acked.map(a => (new Date(a.ackAt) - new Date(a.firstAt)) / 60000)),
      mttrMin: avg(resolved.map(a => (new Date(a.resolvedAt) - new Date(a.firstAt)) / 60000)),
      ackRate: list.length ? round(acked.length / list.length * 100, 1) : 0,
      open: list.filter(a => a.status === 'active' || a.status === 'acknowledged').length,
    });
  });

  router.get('/api/alarms/:id', async (ctx) => {
    requireAuth(ctx);
    const a = ctx.db.alarms.find(x => x.id === ctx.params.id);
    if (!a) throw new ApiError(404, '告警不存在', 'ALARM_NOT_FOUND');
    getSite(ctx, a.siteId);
    const rule = a.ruleId ? ctx.db.alarmRules.find(r => r.id === a.ruleId) : null;
    const point = ctx.db.points.find(p => p.id === a.pointId) || null;
    const device = point ? ctx.db.devices.find(d => d.id === point.deviceId) : null;
    const workorders = ctx.db.workorders.filter(w => w.alarmId === a.id);
    const audits = ctx.db.auditLogs.filter(x => x.targetId === a.id).sort((x, y) => new Date(x.at) - new Date(y.at));
    const timeline = [{ at: a.firstAt, action: 'create', by: '规则引擎', note: `触发条件：${a.detail}` }];
    if (a.ackAt) timeline.push({ at: a.ackAt, action: 'ack', by: (ctx.db.users.find(u => u.id === a.ackBy) || {}).name || a.ackBy, note: a.ackNote || '已确认' });
    if (a.escalatedAt) timeline.push({ at: a.escalatedAt, action: 'escalate', by: '系统', note: `未按时确认，升级为${LEVEL_NAME[a.level]}` });
    if (a.resolvedAt) timeline.push({ at: a.resolvedAt, action: 'resolve', by: a.resolvedBy === 'system' ? '系统自动' : ((ctx.db.users.find(u => u.id === a.resolvedBy) || {}).name || a.resolvedBy), note: a.closeNote || '标记恢复' });
    if (a.closedAt) timeline.push({ at: a.closedAt, action: 'close', by: (ctx.db.users.find(u => u.id === a.closedBy) || {}).name || 'system', note: a.closeNote || '关闭' });
    for (const au of audits) if (!['alarm.create', 'alarm.ack', 'alarm.resolve'].includes(au.action)) timeline.push({ at: au.at, action: au.action, by: au.actorName || '系统', note: au.detail });
    timeline.sort((x, y) => new Date(x.at) - new Date(y.at));

    const reading = point ? app.telemetry.getLatest(point.id) : null;
    ok(ctx.res, {
      ...alarmView(ctx, a),
      rule: rule ? { id: rule.id, key: rule.key, name: rule.name, op: rule.op, low: rule.low, high: rule.high, stateValues: rule.stateValues, durationSec: rule.durationSec, hysteresis: rule.hysteresis, escalateSec: rule.escalateSec, threshold: thresholdText(rule) } : null,
      point: point ? { id: point.id, code: point.code, name: point.name, unit: point.unit, range: point.range, accuracy: point.accuracy, provenance: point.provenance, desc: point.desc } : null,
      device: device ? { id: device.id, code: device.code, name: device.name, model: device.model, status: device.status } : null,
      reading: reading ? { value: reading.value, t: iso(reading.t), quality: reading.quality } : null,
      workorders,
      timeline,
    });
  });

  router.post('/api/alarms/:id/ack', async (ctx) => {
    const user = requirePerm(ctx, 'alarm.handle');
    const a = ctx.db.alarms.find(x => x.id === ctx.params.id);
    if (!a) throw new ApiError(404, '告警不存在', 'ALARM_NOT_FOUND');
    getSite(ctx, a.siteId);
    const r = app.rules.acknowledge(user, a.id, String((ctx.body || {}).note || '').slice(0, 300), ctx.ip);
    if (r.error) throw new ApiError(r.error.status, r.error.message, 'ALARM_ACK_FAILED');
    ok(ctx.res, alarmView(ctx, r.alarm));
  });

  router.post('/api/alarms/:id/resolve', async (ctx) => {
    const user = requirePerm(ctx, 'alarm.handle');
    const a = ctx.db.alarms.find(x => x.id === ctx.params.id);
    if (!a) throw new ApiError(404, '告警不存在', 'ALARM_NOT_FOUND');
    getSite(ctx, a.siteId);
    const note = String((ctx.body || {}).note || '');
    if (!note.trim()) throw new ApiError(400, '请填写恢复依据或处置说明', 'NOTE_REQUIRED');
    const r = app.rules.resolve(user, a.id, note.slice(0, 300), ctx.ip);
    if (r.error) throw new ApiError(r.error.status, r.error.message, 'ALARM_RESOLVE_FAILED');
    ok(ctx.res, alarmView(ctx, r.alarm));
  });

  router.post('/api/alarms/:id/close', async (ctx) => {
    const user = requirePerm(ctx, 'alarm.handle');
    const a = ctx.db.alarms.find(x => x.id === ctx.params.id);
    if (!a) throw new ApiError(404, '告警不存在', 'ALARM_NOT_FOUND');
    getSite(ctx, a.siteId);
    if (!['resolved', 'active', 'acknowledged'].includes(a.status)) throw new ApiError(409, '该告警已关闭', 'ALARM_CLOSED');
    const note = String((ctx.body || {}).note || '');
    if (!note.trim()) throw new ApiError(400, '关闭告警需填写处置说明', 'NOTE_REQUIRED');
    const before = { status: a.status };
    a.status = 'closed';
    a.closedAt = iso();
    a.closedBy = user.id;
    a.closeNote = note.slice(0, 400);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'alarm.close', targetType: 'alarm', targetId: a.id, siteId: a.siteId, detail: `关闭告警：${a.title}；${note}`, before });
    app.publish({ type: 'alarm', action: 'close', data: a });
    ok(ctx.res, alarmView(ctx, a));
  });

  /* ---------------- 告警规则 ---------------- */
  function ruleView(ctx, r) {
    const point = ctx.db.points.find(p => p.code === r.pointCode && (r.siteId === '*' || p.siteId === r.siteId));
    const latest = point ? app.telemetry.getLatest(point.id) : null;
    const open = ctx.db.alarms.filter(a => a.ruleId === r.id && (a.status === 'active' || a.status === 'acknowledged'));
    return {
      ...r, threshold: thresholdText(r), levelName: LEVEL_NAME[r.level] || r.level,
      pointName: point ? point.name : r.pointCode, unit: r.unit || (point ? point.unit : ''),
      latest: latest ? { value: latest.value, t: iso(latest.t) } : null,
      openCount: open.length,
      siteName: r.siteId === '*' ? '全部站点' : ((ctx.db.sites.find(s => s.id === r.siteId) || {}).shortName || r.siteId),
    };
  }

  router.get('/api/alarm-rules', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const items = ctx.db.alarmRules.filter(r => r.siteId === '*' || r.siteId === siteId).map(r => ruleView(ctx, r));
    ok(ctx.res, items, { total: items.length });
  });

  router.patch('/api/alarm-rules/:id', async (ctx) => {
    const user = requirePerm(ctx, 'param.edit');
    const r = ctx.db.alarmRules.find(x => x.id === ctx.params.id);
    if (!r) throw new ApiError(404, '规则不存在', 'RULE_NOT_FOUND');
    if (r.siteId !== '*') getSite(ctx, r.siteId);
    else getSite(ctx, scopedSites(ctx)[0].id);
    const body = ctx.body || {};
    const before = { ...r };
    for (const k of ['name', 'desc', 'enabled', 'level', 'durationSec', 'escalateSec', 'hysteresis', 'low', 'high']) {
      if (body[k] === undefined) continue;
      if (k === 'enabled') r.enabled = !!body.enabled;
      else if (k === 'level') {
        if (!LEVELS.includes(body.level)) throw new ApiError(400, '告警等级不合法', 'BAD_LEVEL');
        r.level = body.level;
      } else if (['durationSec', 'escalateSec', 'hysteresis', 'low', 'high'].includes(k)) {
        const v = Number(body[k]);
        if (!Number.isFinite(v) || v < 0) throw new ApiError(400, `${k} 必须为非负数字`, 'BAD_VALUE');
        r[k] = v;
      } else r[k] = String(body[k]).slice(0, 200);
    }
    r.updatedAt = iso();
    r.updatedBy = user.id;
    if (body.enabled === false) {
      for (const a of ctx.db.alarms) {
        if (a.ruleId === r.id && (a.status === 'active' || a.status === 'acknowledged')) {
          a.status = 'closed';
          a.closedAt = iso();
          a.closedBy = user.id;
          a.closeNote = '告警规则已停用，历史告警自动关闭。';
        }
      }
    }
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'alarm.rule.update', targetType: 'alarmRule', targetId: r.id, siteId: r.siteId === '*' ? null : r.siteId, detail: `修改规则「${r.name}」`, before, after: r });
    ok(ctx.res, ruleView(ctx, r));
  });

  router.post('/api/alarm-rules', async (ctx) => {
    const user = requirePerm(ctx, 'param.edit');
    const body = ctx.body || {};
    if (!body.name || !body.pointCode || !body.op) throw new ApiError(400, '请填写规则名称、点位与判断方式', 'BAD_REQUEST');
    if (!['low', 'high', 'outside', 'state'].includes(body.op)) throw new ApiError(400, '判断方式不合法', 'BAD_OP');
    const siteId = body.siteId && body.siteId !== '*' ? getSite(ctx, body.siteId).id : '*';
    const r = {
      id: 'rule-' + (ctx.db.alarmRules.length + 1),
      key: body.key || 'custom-' + Date.now(),
      siteId, pointCode: body.pointCode, name: String(body.name).slice(0, 100),
      op: body.op, low: body.low != null ? Number(body.low) : null, high: body.high != null ? Number(body.high) : null,
      stateValues: body.stateValues || null, level: LEVELS.includes(body.level) ? body.level : 'minor',
      durationSec: Number(body.durationSec) || 300, hysteresis: Number(body.hysteresis) || 0,
      escalateSec: Number(body.escalateSec) || 1800, unit: body.unit || '', enabled: body.enabled !== false,
      desc: String(body.desc || '').slice(0, 300),
      createdAt: iso(), updatedAt: iso(), updatedBy: user.id,
    };
    if (ctx.db.alarmRules.some(x => x.key === r.key)) r.key += '-' + Date.now();
    ctx.db.alarmRules.push(r);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'alarm.rule.create', targetType: 'alarmRule', targetId: r.id, siteId, detail: `新建规则「${r.name}」`, after: r });
    ok(ctx.res, ruleView(ctx, r));
  });
}

module.exports = { register };

'use strict';
const { ok, ApiError, requireAuth, requirePerm, getSite, scopedSites, paginate, audit } = require('./common');
const { ACTIONS, STATUS_NAME } = require('../commands');
const { iso, round } = require('../util');

function commandView(ctx, c) {
  const device = c.deviceId ? ctx.db.devices.find(d => d.id === c.deviceId) : null;
  const site = ctx.db.sites.find(s => s.id === c.siteId) || {};
  return {
    ...c,
    statusName: STATUS_NAME[c.status] || c.status,
    siteName: site.shortName || site.name || c.siteId,
    device: device ? { id: device.id, code: device.code, name: device.name, model: device.model, status: device.status } : null,
    byName: (ctx.db.users.find(u => u.id === c.createdBy) || {}).name || c.createdByName,
    durationSec: c.finishedAt ? Math.round((new Date(c.finishedAt) - new Date(c.createdAt)) / 1000) : Math.round((Date.now() - new Date(c.createdAt)) / 1000),
  };
}

function applyParam(ctx, user, p, value, reason, extra = {}) {
  const v = round(Number(value), 3);
  if (!Number.isFinite(v)) throw new ApiError(400, '参数值必须是数字', 'BAD_VALUE');
  if (v < p.min || v > p.max) throw new ApiError(400, `取值须在 ${p.min}–${p.max} ${p.unit || ''} 之间`, 'OUT_OF_RANGE');
  const before = { value: p.currentValue, version: p.version };
  for (const pv of ctx.db.paramVersions) if (pv.paramId === p.id && pv.status === 'active') pv.status = 'superseded';
  p.version += 1;
  p.currentValue = v;
  p.updatedAt = iso();
  p.updatedBy = user.id;
  const version = {
    id: `pv-${p.id}-${p.version}`, siteId: p.siteId, paramId: p.id, paramKey: p.key,
    version: p.version, value: v, status: 'active', operatorId: user.id,
    reviewerId: extra.reviewerId || null, createdAt: iso(),
    reason: String(reason || '').slice(0, 300), requestId: extra.requestId || null,
  };
  ctx.db.paramVersions.push(version);
  ctx.store.saveSoon();
  audit(ctx, { actor: user, action: 'param.update', targetType: 'param', targetId: p.id, siteId: p.siteId, detail: `${p.name} 由 ${before.value}${p.unit || ''} 调整为 ${v}${p.unit || ''}；理由：${reason || '未填写'}`, before, after: { value: v, version: p.version } });
  appPublish(ctx, { type: 'param', action: 'update', data: { paramId: p.id, siteId: p.siteId, value: v, version: p.version } });
  return version;
}

function appPublish(ctx, payload) {
  if (ctx.app && ctx.app.publish) ctx.app.publish(payload);
}

function register(router, app) {
  /* ---------------- 控制动作与命令 ---------------- */
  router.get('/api/actions', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const items = Object.entries(ACTIONS).map(([key, a]) => {
      const device = ctx.db.devices.find(d => d.siteId === siteId && d.key === a.deviceKey) || null;
      const points = device ? ctx.db.points.filter(p => p.deviceId === device.id) : [];
      const readings = points.map(p => {
        const l = app.telemetry.getLatest(p.id);
        return { code: p.code, name: p.name, unit: p.unit, value: l ? l.value : null, t: l ? iso(l.t) : null };
      });
      return {
        key, label: a.label, risk: a.risk, effect: a.effect, deviceKey: a.deviceKey,
        device: device ? { id: device.id, code: device.code, name: device.name, status: device.status, online: device.online } : null,
        readings,
        needsConfirm: a.risk === 'high',
        args: key.endsWith('.dose.set') ? [{ key: 'value', label: '设定值', unit: 'L/h', min: 0, max: key.startsWith('pac') ? 0.2 : 0.1, step: 0.005 }]
          : key === 'raw.pump.mode.set' ? [{ key: 'mode', label: '模式', options: [{ value: 0, label: '自动' }, { value: 1, label: '手动' }] }] : [],
        constraints: describeConstraints(key),
      };
    });
    ok(ctx.res, { siteId, status: app.sim.status(siteId), actions: items });
  });

  function describeConstraints(key) {
    switch (key) {
      case 'raw.pump.start': return ['原料罐液位 ≥ 5%（低液位保护）', '提升泵无故障信号'];
      case 'raw.pump.stop': return ['停泵后进水流量归零，注意下游液位'];
      case 'raw.pump.mode.set': return ['手动模式仅限现场操作并记录', '切换需二次确认'];
      case 'baf.blower.stop': return ['BAF 溶解氧 ≥ 2.5 mg/L（DO 联锁）'];
      case 'baf.backwash.start':
      case 'sulfur.backwash.start': return ['当前无进行中的反冲洗', '废水箱液位 < 92%（排水阀联锁）', '先气冲 30 s → 气水联合 90 s → 漂洗 45 s'];
      case 'baf.backwash.stop':
      case 'sulfur.backwash.stop': return ['仅在中止异常反冲洗时使用', '停止后需现场确认滤池状态'];
      case 'baf.blower.start': return ['风机无过载/故障'];
      case 'pac.dose.set':
      case 'pam.dose.set': return ['投加量范围 0–' + (key.startsWith('pac') ? '0.2' : '0.1') + ' L/h', '单次调整不超过 20%（工艺建议）'];
      default: return [];
    }
  }

  router.get('/api/commands', async (ctx) => {
    requireAuth(ctx);
    const siteIds = scopedSites(ctx).map(s => s.id);
    let items = ctx.db.commands.filter(c => siteIds.includes(c.siteId));
    if (ctx.query.siteId) { getSite(ctx, ctx.query.siteId); items = items.filter(c => c.siteId === ctx.query.siteId); }
    if (ctx.query.status) items = items.filter(c => c.status === ctx.query.status);
    if (ctx.query.action) items = items.filter(c => c.action === ctx.query.action);
    if (ctx.query.from) items = items.filter(c => new Date(c.createdAt) >= new Date(ctx.query.from));
    if (ctx.query.q) {
      const q = String(ctx.query.q).toLowerCase();
      items = items.filter(c => `${c.id} ${c.actionLabel} ${c.reason}`.toLowerCase().includes(q));
    }
    items = items.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
    const page = paginate(items, ctx.query);
    const counts = {};
    for (const key of Object.keys(STATUS_NAME)) counts[key] = items.filter(c => c.status === key).length;
    ok(ctx.res, page.items.map(c => commandView(ctx, c)), { ...page, items: undefined, counts, statusNames: STATUS_NAME });
  });

  router.get('/api/commands/:id', async (ctx) => {
    requireAuth(ctx);
    const c = ctx.db.commands.find(x => x.id === ctx.params.id);
    if (!c) throw new ApiError(404, '命令不存在', 'COMMAND_NOT_FOUND');
    getSite(ctx, c.siteId);
    const audits = ctx.db.auditLogs.filter(a => a.targetId === c.id).sort((x, y) => new Date(x.at) - new Date(y.at));
    ok(ctx.res, { ...commandView(ctx, c), audits });
  });

  router.post('/api/commands', async (ctx) => {
    const user = requirePerm(ctx, 'control');
    const { siteId = scopedSites(ctx)[0].id, action, args = {}, reason = '' } = ctx.body || {};
    getSite(ctx, siteId);
    if (!reason || !String(reason).trim()) throw new ApiError(400, '下发控制命令必须填写操作理由（审计要求）', 'REASON_REQUIRED');
    const r = app.commands.create(user, { siteId, action, args, reason: String(reason), ip: ctx.ip });
    if (r.error) throw new ApiError(r.error.status, r.error.message, 'COMMAND_FAILED');
    if (r.blocked) {
      ok(ctx.res, { blocked: true, command: commandView(ctx, r.command), message: r.error }, { accepted: false });
      return;
    }
    ok(ctx.res, { blocked: false, command: commandView(ctx, r.command), needsConfirm: r.command.status === 'pending_confirm' }, { accepted: true });
  });

  router.post('/api/commands/:id/confirm', async (ctx) => {
    const user = requirePerm(ctx, 'control');
    const c = ctx.db.commands.find(x => x.id === ctx.params.id);
    if (!c) throw new ApiError(404, '命令不存在', 'COMMAND_NOT_FOUND');
    getSite(ctx, c.siteId);
    const r = app.commands.confirm(user, c.id, ctx.ip);
    if (r.error) throw new ApiError(r.error.status, r.error.message, 'COMMAND_CONFIRM_FAILED');
    ok(ctx.res, commandView(ctx, r.command));
  });

  router.post('/api/commands/:id/cancel', async (ctx) => {
    const user = requirePerm(ctx, 'control');
    const c = ctx.db.commands.find(x => x.id === ctx.params.id);
    if (!c) throw new ApiError(404, '命令不存在', 'COMMAND_NOT_FOUND');
    getSite(ctx, c.siteId);
    const r = app.commands.cancel(user, c.id, String((ctx.body || {}).reason || '用户取消'), ctx.ip);
    if (r.error) throw new ApiError(r.error.status, r.error.message, 'COMMAND_CANCEL_FAILED');
    ok(ctx.res, commandView(ctx, r.command));
  });

  /* ---------------- 工艺参数 ---------------- */
  function paramView(ctx, p) {
    const pending = (ctx.db.paramRequests || []).find(r => r.paramId === p.id && r.status === 'pending_approval');
    return {
      ...p,
      writeModeName: { cloud: '云端可写（留痕）', approval: '审批后生效', local: '仅现场可改', read: '只读' }[p.writeMode] || p.writeMode,
      pendingRequest: pending ? { id: pending.id, value: pending.value, requestedBy: pending.requestedByName, createdAt: pending.createdAt } : null,
      updatedByName: (ctx.db.users.find(u => u.id === p.updatedBy) || {}).name || p.updatedBy,
      unitName: (ctx.db.units.find(u => u.siteId === p.siteId && u.name === p.group) || {}).short || p.group,
    };
  }

  router.get('/api/params', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    let items = ctx.db.params.filter(p => p.siteId === siteId);
    if (ctx.query.group) items = items.filter(p => p.group === ctx.query.group);
    if (ctx.query.q) {
      const q = String(ctx.query.q).toLowerCase();
      items = items.filter(p => `${p.name} ${p.key}`.toLowerCase().includes(q));
    }
    const groups = [...new Set(items.map(p => p.group))];
    ok(ctx.res, items.map(p => paramView(ctx, p)), { total: items.length, groups });
  });

  router.get('/api/params/:id', async (ctx) => {
    requireAuth(ctx);
    const p = ctx.db.params.find(x => x.id === ctx.params.id);
    if (!p) throw new ApiError(404, '参数不存在', 'PARAM_NOT_FOUND');
    getSite(ctx, p.siteId);
    const versions = ctx.db.paramVersions.filter(v => v.paramId === p.id).sort((a, b) => b.version - a.version).map(v => ({
      ...v,
      operatorName: (ctx.db.users.find(u => u.id === v.operatorId) || {}).name || v.operatorId,
      reviewerName: v.reviewerId ? ((ctx.db.users.find(u => u.id === v.reviewerId) || {}).name || v.reviewerId) : null,
    }));
    const requests = (ctx.db.paramRequests || []).filter(r => r.paramId === p.id).sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
    const audits = ctx.db.auditLogs.filter(a => a.targetId === p.id).sort((a, b) => new Date(b.at) - new Date(a.at)).slice(0, 20);
    ok(ctx.res, { ...paramView(ctx, p), versions, requests, audits });
  });

  router.post('/api/params/:id/change', async (ctx) => {
    const user = requirePerm(ctx, 'param.edit');
    const p = ctx.db.params.find(x => x.id === ctx.params.id);
    if (!p) throw new ApiError(404, '参数不存在', 'PARAM_NOT_FOUND');
    getSite(ctx, p.siteId);
    const { value, reason = '' } = ctx.body || {};
    if (!reason || !String(reason).trim()) throw new ApiError(400, '参数变更必须填写理由', 'REASON_REQUIRED');
    if (p.writeMode === 'local') throw new ApiError(403, '该参数仅允许现场修改，平台不提供远程写入', 'LOCAL_ONLY');
    if (p.writeMode === 'read') throw new ApiError(403, '该参数为只读', 'READ_ONLY');
    const num = Number(value);
    if (!Number.isFinite(num)) throw new ApiError(400, '参数值必须是数字', 'BAD_VALUE');
    if (num < p.min || num > p.max) throw new ApiError(400, `取值须在 ${p.min}–${p.max} ${p.unit || ''} 之间`, 'OUT_OF_RANGE');
    if (p.writeMode === 'approval') {
      if (!ctx.db.paramRequests) ctx.db.paramRequests = [];
      if (ctx.db.paramRequests.some(r => r.paramId === p.id && r.status === 'pending_approval')) {
        throw new ApiError(409, '该参数已有待审批的变更申请，请先处理', 'REQUEST_EXISTS');
      }
      const req = {
        id: 'PA-' + Date.now().toString().slice(-8), siteId: p.siteId, paramId: p.id, paramKey: p.key,
        paramName: p.name, unit: p.unit, currentValue: p.currentValue, value: round(num, 3),
        reason: String(reason).slice(0, 300), status: 'pending_approval',
        requestedBy: user.id, requestedByName: user.name, createdAt: iso(),
        reviewedBy: null, reviewedAt: null, reviewNote: '',
      };
      ctx.db.paramRequests.push(req);
      ctx.store.saveSoon();
      audit(ctx, { actor: user, action: 'param.request', targetType: 'param', targetId: p.id, siteId: p.siteId, detail: `${p.name} 申请由 ${p.currentValue} 调整为 ${req.value}${p.unit || ''}；理由：${reason}` });
      app.notifier.sendRoles(p.siteId, ['process', 'region'], {
        type: 'param', title: `参数变更待审批：${p.name}`,
        body: `${user.name} 申请将 ${p.name} 由 ${p.currentValue}${p.unit || ''} 调整为 ${req.value}${p.unit || ''}。`,
        link: '/control?tab=params',
      });
      ok(ctx.res, { applied: false, request: req, param: paramView(ctx, p) });
      return;
    }
    const version = applyParam(ctx, user, p, num, reason);
    ok(ctx.res, { applied: true, version, param: paramView(ctx, p) });
  });

  router.post('/api/params/:id/rollback', async (ctx) => {
    const user = requirePerm(ctx, 'param.edit');
    const p = ctx.db.params.find(x => x.id === ctx.params.id);
    if (!p) throw new ApiError(404, '参数不存在', 'PARAM_NOT_FOUND');
    getSite(ctx, p.siteId);
    const { version, reason = '' } = ctx.body || {};
    const pv = ctx.db.paramVersions.find(v => v.paramId === p.id && Number(v.version) === Number(version));
    if (!pv) throw new ApiError(404, '目标版本不存在', 'VERSION_NOT_FOUND');
    if (!reason || !String(reason).trim()) throw new ApiError(400, '回滚必须填写理由', 'REASON_REQUIRED');
    const newVersion = applyParam(ctx, user, p, pv.value, `回滚至 v${pv.version}：${reason}`, { reviewerId: user.id });
    audit(ctx, { actor: user, action: 'param.rollback', targetType: 'param', targetId: p.id, siteId: p.siteId, detail: `${p.name} 回滚至 v${pv.version}（${pv.value}${p.unit || ''}），生成 v${newVersion.version}` });
    ok(ctx.res, { applied: true, version: newVersion, param: paramView(ctx, p) });
  });

  router.get('/api/param-requests', async (ctx) => {
    requireAuth(ctx);
    const siteIds = scopedSites(ctx).map(s => s.id);
    let items = (ctx.db.paramRequests || []).filter(r => siteIds.includes(r.siteId));
    if (ctx.query.siteId) { getSite(ctx, ctx.query.siteId); items = items.filter(r => r.siteId === ctx.query.siteId); }
    if (ctx.query.status) items = items.filter(r => r.status === ctx.query.status);
    items = items.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
    const user = ctx.user;
    const canReview = app.auth.can(user, 'param.edit');
    ok(ctx.res, items.map(r => ({
      ...r,
      siteName: (ctx.db.sites.find(s => s.id === r.siteId) || {}).shortName,
      canReview: canReview && r.requestedBy !== user.id || (canReview && user.role === 'super'),
    })), { total: items.length, pending: items.filter(r => r.status === 'pending_approval').length });
  });

  router.post('/api/param-requests/:id/approve', async (ctx) => {
    const user = requirePerm(ctx, 'param.edit');
    const req = (ctx.db.paramRequests || []).find(r => r.id === ctx.params.id);
    if (!req) throw new ApiError(404, '审批单不存在', 'REQUEST_NOT_FOUND');
    getSite(ctx, req.siteId);
    if (req.status !== 'pending_approval') throw new ApiError(409, '该申请已处理', 'REQUEST_DONE');
    if (req.requestedBy === user.id && user.role !== 'super') throw new ApiError(403, '不能审批本人提交的参数变更', 'SELF_APPROVAL');
    const p = ctx.db.params.find(x => x.id === req.paramId);
    if (!p) throw new ApiError(404, '参数不存在', 'PARAM_NOT_FOUND');
    const version = applyParam(ctx, user, p, req.value, `审批通过 ${req.id}：${req.reason}`, { reviewerId: user.id, requestId: req.id });
    req.status = 'approved';
    req.reviewedBy = user.id;
    req.reviewedAt = iso();
    req.reviewNote = String((ctx.body || {}).note || '同意调整');
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'param.approve', targetType: 'param', targetId: p.id, siteId: p.siteId, detail: `审批通过 ${req.id}，${p.name} = ${req.value}${p.unit || ''}` });
    app.notifier.send([req.requestedBy], { type: 'param', title: `参数变更已批准：${p.name}`, body: `${user.name} 已批准将 ${p.name} 调整为 ${req.value}${p.unit || ''}。`, link: '/control?tab=params', siteId: p.siteId });
    ok(ctx.res, { request: req, version, param: paramView(ctx, p) });
  });

  router.post('/api/param-requests/:id/reject', async (ctx) => {
    const user = requirePerm(ctx, 'param.edit');
    const req = (ctx.db.paramRequests || []).find(r => r.id === ctx.params.id);
    if (!req) throw new ApiError(404, '审批单不存在', 'REQUEST_NOT_FOUND');
    getSite(ctx, req.siteId);
    if (req.status !== 'pending_approval') throw new ApiError(409, '该申请已处理', 'REQUEST_DONE');
    req.status = 'rejected';
    req.reviewedBy = user.id;
    req.reviewedAt = iso();
    req.reviewNote = String((ctx.body || {}).note || '不同意本次调整').slice(0, 300);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'param.reject', targetType: 'param', targetId: req.paramId, siteId: req.siteId, detail: `驳回 ${req.id}：${req.reviewNote}` });
    app.notifier.send([req.requestedBy], { type: 'param', title: `参数变更被驳回：${req.paramName}`, body: req.reviewNote, link: '/control?tab=params', siteId: req.siteId });
    ok(ctx.res, { request: req });
  });
}

module.exports = { register };

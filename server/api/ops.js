'use strict';
const { ok, ApiError, requireAuth, requirePerm, getSite, scopedSites, paginate, audit } = require('./common');
const { iso, round, dayKey } = require('../util');
const { nextCode } = require('../codes');

const WO_STATUS_NAME = { pending: '待接单', accepted: '已接单', processing: '处理中', done: '待验收', verified: '已验收', closed: '已关闭' };
const PRIORITY_NAME = { urgent: '紧急', high: '高', normal: '普通', low: '低' };
const TYPE_NAME = { repair: '维修', maintain: '保养', calibrate: '校准', inspect: '巡检异常' };
const OPEN = ['pending', 'accepted', 'processing'];

function woView(ctx, w) {
  const site = ctx.db.sites.find(s => s.id === w.siteId) || {};
  const assignee = ctx.db.users.find(u => u.id === w.assigneeId);
  const creator = ctx.db.users.find(u => u.id === w.creatorId);
  const device = w.deviceId ? ctx.db.devices.find(d => d.id === w.deviceId) : null;
  const unit = ctx.db.units.find(u => u.siteId === w.siteId && u.code === w.unitCode);
  return {
    ...w,
    statusName: WO_STATUS_NAME[w.status] || w.status,
    priorityName: PRIORITY_NAME[w.priority] || w.priority,
    typeName: TYPE_NAME[w.type] || w.type,
    siteName: site.shortName || site.name || w.siteId,
    assigneeName: assignee ? assignee.name : null,
    creatorName: creator ? creator.name : w.creatorId,
    deviceName: device ? device.name : null,
    unitName: unit ? unit.name : w.unitCode,
    overdue: w.dueAt && OPEN.includes(w.status) ? new Date(w.dueAt) < new Date() : false,
    dueInHours: w.dueAt ? round((new Date(w.dueAt) - Date.now()) / 3600000, 1) : null,
  };
}

function addTimeline(w, user, action, note) {
  w.timeline = w.timeline || [];
  w.timeline.push({ at: iso(), by: user.id, byName: user.name, action, note });
}

function register(router, app) {
  /* ---------------- 工单 ---------------- */
  router.get('/api/workorders', async (ctx) => {
    requireAuth(ctx);
    const siteIds = scopedSites(ctx).map(s => s.id);
    let items = ctx.db.workorders.filter(w => siteIds.includes(w.siteId));
    if (ctx.query.siteId) { getSite(ctx, ctx.query.siteId); items = items.filter(w => w.siteId === ctx.query.siteId); }
    if (ctx.query.status === 'open') items = items.filter(w => OPEN.includes(w.status));
    else if (ctx.query.status) items = items.filter(w => w.status === ctx.query.status);
    if (ctx.query.priority) items = items.filter(w => w.priority === ctx.query.priority);
    if (ctx.query.type) items = items.filter(w => w.type === ctx.query.type);
    if (ctx.query.assignee === 'me') items = items.filter(w => w.assigneeId === ctx.user.id);
    else if (ctx.query.assignee) items = items.filter(w => w.assigneeId === ctx.query.assignee);
    if (ctx.query.q) {
      const q = String(ctx.query.q).toLowerCase();
      items = items.filter(w => `${w.id} ${w.title} ${w.description}`.toLowerCase().includes(q));
    }
    items = items.sort((a, b) => {
      const pr = (PRIORITY_NAME[b.priority] ? 0 : 0);
      return new Date(b.createdAt) - new Date(a.createdAt);
    });
    const page = paginate(items, ctx.query);
    const counts = {};
    for (const k of Object.keys(WO_STATUS_NAME)) counts[k] = items.filter(w => w.status === k).length;
    counts.open = items.filter(w => OPEN.includes(w.status)).length;
    counts.overdue = items.filter(w => w.dueAt && OPEN.includes(w.status) && new Date(w.dueAt) < new Date()).length;
    counts.mine = items.filter(w => w.assigneeId === ctx.user.id && OPEN.includes(w.status)).length;
    ok(ctx.res, page.items.map(w => woView(ctx, w)), { ...page, items: undefined, counts });
  });

  router.get('/api/workorders/:id', async (ctx) => {
    requireAuth(ctx);
    const w = ctx.db.workorders.find(x => x.id === ctx.params.id);
    if (!w) throw new ApiError(404, '工单不存在', 'WORKORDER_NOT_FOUND');
    getSite(ctx, w.siteId);
    const alarm = w.alarmId ? ctx.db.alarms.find(a => a.id === w.alarmId) : null;
    const audits = ctx.db.auditLogs.filter(a => a.targetId === w.id).sort((a, b) => new Date(a.at) - new Date(b.at));
    ok(ctx.res, {
      ...woView(ctx, w),
      alarm: alarm ? { id: alarm.id, title: alarm.title, level: alarm.level, status: alarm.status } : null,
      timelineView: (w.timeline || []).map(t => ({ ...t, byName: t.byName || (ctx.db.users.find(u => u.id === t.by) || {}).name || t.by })),
      audits,
      candidates: ctx.db.users.filter(u => u.status === 'active' && (u.siteIds || []).includes(w.siteId) && ['operator', 'maintainer', 'region', 'super', 'process'].includes(u.role))
        .map(u => ({ id: u.id, name: u.name, title: u.title })),
    });
  });

  router.post('/api/workorders', async (ctx) => {
    const user = requirePerm(ctx, 'workorder.handle');
    const b = ctx.body || {};
    const siteId = b.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    if (!b.title || !String(b.title).trim()) throw new ApiError(400, '请填写工单标题', 'TITLE_REQUIRED');
    const now = Date.now();
    const w = {
      id: nextCode(ctx.db, 'workorders', 'WO'), siteId,
      title: String(b.title).slice(0, 120), type: TYPE_NAME[b.type] ? b.type : 'repair',
      priority: PRIORITY_NAME[b.priority] ? b.priority : 'normal',
      status: b.assigneeId ? 'accepted' : 'pending',
      source: b.alarmId ? 'alarm' : 'manual', alarmId: b.alarmId || null,
      unitCode: b.unitCode || 'plant', deviceId: b.deviceId || null,
      assigneeId: b.assigneeId || null, creatorId: user.id,
      createdAt: iso(now), dueAt: b.dueAt ? iso(new Date(b.dueAt)) : iso(now + 24 * 3600000),
      acceptedAt: b.assigneeId ? iso(now) : null, startedAt: null, finishedAt: null, verifiedAt: null,
      description: String(b.description || '').slice(0, 1000), result: '', parts: '', photos: [],
      timeline: [{ at: iso(now), by: user.id, byName: user.name, action: 'create', note: b.alarmId ? `由告警 ${b.alarmId} 转工单` : '手工创建工单' }],
      slaHours: Number(b.slaHours) || 24,
    };
    ctx.db.workorders.push(w);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'workorder.create', targetType: 'workorder', targetId: w.id, siteId, detail: `创建工单「${w.title}」` });
    if (w.assigneeId) app.notifier.send([w.assigneeId], { type: 'workorder', title: `新工单：${w.title}`, body: `${TYPE_NAME[w.type]} · ${PRIORITY_NAME[w.priority]}优先级`, link: '/workorders?id=' + w.id, siteId });
    app.publish({ type: 'workorder', action: 'create', data: w });
    ok(ctx.res, woView(ctx, w));
  });

  function transition(ctx, action, opts) {
    const user = requirePerm(ctx, 'workorder.handle');
    const w = ctx.db.workorders.find(x => x.id === ctx.params.id);
    if (!w) throw new ApiError(404, '工单不存在', 'WORKORDER_NOT_FOUND');
    getSite(ctx, w.siteId);
    if (!opts.from.includes(w.status)) throw new ApiError(409, `当前状态为「${WO_STATUS_NAME[w.status]}」，不能执行${opts.label}`, 'BAD_STATUS');
    if (opts.onlyAssignee && w.assigneeId && w.assigneeId !== user.id && user.role !== 'super' && user.role !== 'region') {
      throw new ApiError(403, '该工单已指派给他人', 'NOT_ASSIGNEE');
    }
    opts.apply(user, w, ctx.body || {});
    if (opts.notify && w.creatorId && w.creatorId !== user.id) app.notifier.send([w.creatorId], { type: 'workorder', title: `工单进展：${w.title}`, body: opts.notify, link: '/workorders?id=' + w.id, siteId: w.siteId });
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: opts.audit, targetType: 'workorder', targetId: w.id, siteId: w.siteId, detail: `${opts.label}：${w.title}` });
    app.publish({ type: 'workorder', action: opts.audit, data: w });
    ok(ctx.res, woView(ctx, w));
  }

  router.post('/api/workorders/:id/accept', async (ctx) => transition(ctx, 'accept', {
    from: ['pending'], label: '接单', audit: 'workorder.accept', onlyAssignee: false,
    apply: (user, w, b) => {
      w.status = 'accepted'; w.assigneeId = user.id; w.acceptedAt = iso();
      addTimeline(w, user, 'accept', String(b.note || '已接单'));
    },
    notify: `${ctx.user.name} 已接单。`,
  }));

  router.post('/api/workorders/:id/start', async (ctx) => transition(ctx, 'start', {
    from: ['accepted', 'pending'], label: '开始处理', audit: 'workorder.start', onlyAssignee: true,
    apply: (user, w, b) => {
      w.status = 'processing'; w.assigneeId = w.assigneeId || user.id; w.startedAt = iso();
      addTimeline(w, user, 'start', String(b.note || '开始处理'));
    },
    notify: `${ctx.user.name} 已开始处理。`,
  }));

  router.post('/api/workorders/:id/finish', async (ctx) => transition(ctx, 'finish', {
    from: ['processing', 'accepted'], label: '完成并送验', audit: 'workorder.finish', onlyAssignee: true,
    apply: (user, w, b) => {
      if (!b.result || !String(b.result).trim()) throw new ApiError(400, '请填写处理结果', 'RESULT_REQUIRED');
      w.status = 'done'; w.finishedAt = iso();
      w.result = String(b.result).slice(0, 1000);
      w.parts = String(b.parts || '').slice(0, 300);
      w.photos = Array.isArray(b.photos) ? b.photos.slice(0, 9) : [];
      addTimeline(w, user, 'finish', `完成处理：${w.result}`);
    },
    notify: `${ctx.user.name} 已提交处理结果，待验收。`,
  }));

  router.post('/api/workorders/:id/verify', async (ctx) => transition(ctx, 'verify', {
    from: ['done'], label: '验收', audit: 'workorder.verify', onlyAssignee: false,
    apply: (user, w, b) => {
      w.status = 'verified'; w.verifiedAt = iso(); w.verifiedBy = user.id;
      w.verifyNote = String(b.note || '验收通过').slice(0, 300);
      addTimeline(w, user, 'verify', w.verifyNote);
    },
    notify: `${ctx.user.name} 已验收通过。`,
  }));

  router.post('/api/workorders/:id/assign', async (ctx) => transition(ctx, 'assign', {
    from: ['pending', 'accepted', 'processing'], label: '指派', audit: 'workorder.assign', onlyAssignee: false,
    apply: (user, w, b) => {
      const target = ctx.db.users.find(u => u.id === b.assigneeId);
      if (!target) throw new ApiError(404, '指派对象不存在', 'USER_NOT_FOUND');
      w.assigneeId = target.id;
      if (w.status === 'pending') { w.status = 'accepted'; w.acceptedAt = iso(); }
      addTimeline(w, user, 'assign', `指派给 ${target.name}`);
      app.notifier.send([target.id], { type: 'workorder', title: `工单已指派：${w.title}`, body: `${PRIORITY_NAME[w.priority]}优先级 · 截止 ${w.dueAt.slice(0, 16).replace('T', ' ')}`, link: '/workorders?id=' + w.id, siteId: w.siteId });
    },
  }));

  router.post('/api/workorders/:id/close', async (ctx) => transition(ctx, 'close', {
    from: ['verified', 'done'], label: '关闭', audit: 'workorder.close', onlyAssignee: false,
    apply: (user, w, b) => {
      w.status = 'closed'; w.closedAt = iso(); w.closedBy = user.id;
      w.closeNote = String(b.note || '工单闭环').slice(0, 300);
      addTimeline(w, user, 'close', w.closeNote);
    },
  }));

  /* ---------------- 巡检 ---------------- */
  router.get('/api/inspection-plans', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    ok(ctx.res, ctx.db.inspectionPlans.filter(p => p.siteId === siteId));
  });

  router.get('/api/inspections', async (ctx) => {
    requireAuth(ctx);
    const siteIds = scopedSites(ctx).map(s => s.id);
    let items = ctx.db.inspections.filter(i => siteIds.includes(i.siteId));
    if (ctx.query.siteId) { getSite(ctx, ctx.query.siteId); items = items.filter(i => i.siteId === ctx.query.siteId); }
    if (ctx.query.status) items = items.filter(i => i.status === ctx.query.status);
    if (ctx.query.assignee === 'me') items = items.filter(i => i.assigneeId === ctx.user.id);
    items = items.sort((a, b) => new Date(a.dueAt) - new Date(b.dueAt));
    ok(ctx.res, items.map(i => ({
      ...i,
      siteName: (ctx.db.sites.find(s => s.id === i.siteId) || {}).shortName,
      assigneeName: (ctx.db.users.find(u => u.id === i.assigneeId) || {}).name,
      overdue: i.status === 'pending' && new Date(i.dueAt) < new Date(),
      doneCount: (i.items || []).filter(x => x.result).length,
      totalCount: (i.items || []).length,
    })), { total: items.length, pending: items.filter(i => i.status === 'pending').length });
  });

  router.get('/api/inspections/:id', async (ctx) => {
    requireAuth(ctx);
    const i = ctx.db.inspections.find(x => x.id === ctx.params.id);
    if (!i) throw new ApiError(404, '巡检任务不存在', 'INSPECTION_NOT_FOUND');
    getSite(ctx, i.siteId);
    ok(ctx.res, {
      ...i,
      siteName: (ctx.db.sites.find(s => s.id === i.siteId) || {}).shortName,
      assigneeName: (ctx.db.users.find(u => u.id === i.assigneeId) || {}).name,
      planName: (ctx.db.inspectionPlans.find(p => p.id === i.planId) || {}).name,
    });
  });

  router.post('/api/inspections', async (ctx) => {
    const user = requirePerm(ctx, 'inspection.execute');
    const b = ctx.body || {};
    const plan = ctx.db.inspectionPlans.find(p => p.id === b.planId);
    if (!plan) throw new ApiError(404, '巡检计划不存在', 'PLAN_NOT_FOUND');
    getSite(ctx, plan.siteId);
    const task = {
      id: nextCode(ctx.db, 'inspections', 'IN'), siteId: plan.siteId, planId: plan.id,
      title: `${plan.name} · ${dayKey()}`, type: plan.cycle,
      dueAt: b.dueAt ? iso(new Date(b.dueAt)) : iso(Date.now() + 8 * 3600000),
      assigneeId: b.assigneeId || user.id, status: 'pending', createdAt: iso(),
      items: plan.items.map(it => ({ ...it, result: '', note: '' })), doneAt: null, doneBy: null, summary: '',
    };
    ctx.db.inspections.push(task);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'inspection.create', targetType: 'inspection', targetId: task.id, siteId: plan.siteId, detail: `从计划「${plan.name}」生成巡检任务` });
    ok(ctx.res, task);
  });

  router.post('/api/inspections/:id/submit', async (ctx) => {
    const user = requirePerm(ctx, 'inspection.execute');
    const task = ctx.db.inspections.find(x => x.id === ctx.params.id);
    if (!task) throw new ApiError(404, '巡检任务不存在', 'INSPECTION_NOT_FOUND');
    getSite(ctx, task.siteId);
    if (task.status === 'done') throw new ApiError(409, '该巡检已完成', 'INSPECTION_DONE');
    if (task.assigneeId && task.assigneeId !== user.id && !['super', 'region'].includes(user.role)) throw new ApiError(403, '该巡检任务已指派给他人', 'NOT_ASSIGNEE');
    const b = ctx.body || {};
    const items = Array.isArray(b.items) ? b.items : [];
    if (!items.length) throw new ApiError(400, '请提交巡检项结果', 'ITEMS_REQUIRED');
    const byKey = new Map(items.map(x => [x.key, x]));
    let abnormal = [];
    for (const it of task.items) {
      const input = byKey.get(it.key);
      if (!input) continue;
      if (!['ok', 'attention', 'abnormal'].includes(input.result)) throw new ApiError(400, `巡检项「${it.label}」结果不合法`, 'BAD_RESULT');
      it.result = input.result;
      it.note = String(input.note || '').slice(0, 300);
      if (it.result === 'abnormal') abnormal.push(it.label);
    }
    const unanswered = task.items.filter(it => !it.result);
    if (unanswered.length) throw new ApiError(400, `还有 ${unanswered.length} 个巡检项未填写`, 'INCOMPLETE', { items: unanswered.map(x => x.key) });
    task.status = 'done';
    task.doneAt = iso();
    task.doneBy = user.id;
    task.summary = String(b.summary || '').slice(0, 500);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'inspection.submit', targetType: 'inspection', targetId: task.id, siteId: task.siteId, detail: `完成巡检 ${task.id}，异常项 ${abnormal.length} 个` });
    let workorder = null;
    if (abnormal.length && b.createWorkorder !== false) {
      const now = Date.now();
      workorder = {
        id: nextCode(ctx.db, 'workorders', 'WO'), siteId: task.siteId,
        title: `巡检异常：${abnormal.slice(0, 2).join('、')}${abnormal.length > 2 ? ' 等' : ''}`,
        type: 'inspect', priority: abnormal.length > 1 ? 'high' : 'normal', status: 'pending',
        source: 'inspection', alarmId: null, unitCode: 'plant', deviceId: null,
        assigneeId: null, creatorId: user.id, createdAt: iso(now), dueAt: iso(now + 24 * 3600000),
        acceptedAt: null, startedAt: null, finishedAt: null, verifiedAt: null,
        description: `巡检任务 ${task.id} 发现异常：${abnormal.join('；')}。巡检备注：${task.summary || '无'}`,
        result: '', parts: '', photos: [], slaHours: 24,
        timeline: [{ at: iso(now), by: user.id, byName: user.name, action: 'create', note: `由巡检任务 ${task.id} 自动创建` }],
      };
      ctx.db.workorders.push(workorder);
      ctx.store.saveSoon();
      app.notifier.sendRoles(task.siteId, ['operator', 'maintainer'], { type: 'workorder', title: `巡检发现异常：${abnormal[0]}`, body: `已自动创建工单 ${workorder.id}`, link: '/workorders?id=' + workorder.id });
      app.publish({ type: 'workorder', action: 'create', data: workorder });
    }
    app.notifier.send([task.assigneeId], { type: 'inspection', title: '巡检已提交', body: `${task.title} 已完成`, link: '/inspections' });
    ok(ctx.res, { task, abnormal, workorder });
  });

  /* ---------------- 水质化验 ---------------- */
  const LIMITS = { COD: 50, NH3N: 5, TN: 15, TP: 0.5, SS: 10 };
  const METRIC_NAMES = { COD: 'COD', NH3N: '氨氮', TN: '总氮', TP: '总磷', SS: '悬浮物' };
  const SAMPLE_NAMES = { inlet: '进水（原料罐）', baf_out: 'BAF 出水', sulfur_out: '硫自养池出水', outlet: '总出水（清水池）' };

  function labView(ctx, r) {
    return {
      ...r,
      samplePointText: r.samplePointName || SAMPLE_NAMES[r.samplePoint] || r.samplePoint,
      siteName: (ctx.db.sites.find(s => s.id === r.siteId) || {}).shortName,
      operatorName: (ctx.db.users.find(u => u.id === r.operatorId) || {}).name || r.operatorId,
      reviewerName: r.reviewerId ? ((ctx.db.users.find(u => u.id === r.reviewerId) || {}).name || r.reviewerId) : null,
      exceed: Object.entries(r.metrics || {}).filter(([k, v]) => LIMITS[k] != null && Number(v) > LIMITS[k]).map(([k]) => METRIC_NAMES[k] || k),
    };
  }

  router.get('/api/lab', async (ctx) => {
    requireAuth(ctx);
    const siteIds = scopedSites(ctx).map(s => s.id);
    let items = ctx.db.labRecords.filter(r => siteIds.includes(r.siteId));
    if (ctx.query.siteId) { getSite(ctx, ctx.query.siteId); items = items.filter(r => r.siteId === ctx.query.siteId); }
    if (ctx.query.samplePoint) items = items.filter(r => r.samplePoint === ctx.query.samplePoint);
    if (ctx.query.status) items = items.filter(r => r.status === ctx.query.status);
    if (ctx.query.from) items = items.filter(r => new Date(r.sampledAt) >= new Date(ctx.query.from));
    if (ctx.query.to) items = items.filter(r => new Date(r.sampledAt) <= new Date(ctx.query.to));
    if (ctx.query.q) {
      const q = String(ctx.query.q).toLowerCase();
      items = items.filter(r => `${r.id} ${r.samplePointText || ''} ${r.samplePointName || ''}`.toLowerCase().includes(q));
    }
    items = items.sort((a, b) => new Date(b.sampledAt) - new Date(a.sampledAt));
    const page = paginate(items, ctx.query);
    ok(ctx.res, page.items.map(r => labView(ctx, r)), { ...page, items: undefined, pending: items.filter(r => r.status === 'pending_review').length, limits: LIMITS, metricNames: METRIC_NAMES, sampleNames: SAMPLE_NAMES });
  });

  router.get('/api/lab/stats', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    const days = Math.min(90, Math.max(1, parseInt(ctx.query.days, 10) || 7));
    const from = Date.now() - days * 86400000;
    const list = ctx.db.labRecords.filter(r => r.siteId === siteId && new Date(r.sampledAt) >= from)
      .sort((a, b) => new Date(a.sampledAt) - new Date(b.sampledAt));
    const latestByPoint = {};
    for (const sp of Object.keys(SAMPLE_NAMES)) {
      const recs = list.filter(r => r.samplePoint === sp);
      const last = recs[recs.length - 1];
      if (last) latestByPoint[sp] = labView(ctx, last);
    }
    const series = {};
    for (const m of Object.keys(LIMITS)) {
      series[m] = list.filter(r => r.samplePoint === 'outlet').map(r => ({ t: new Date(r.sampledAt).getTime(), v: r.metrics[m] }));
    }
    const removalRates = {};
    if (latestByPoint.inlet && latestByPoint.outlet) {
      for (const m of Object.keys(LIMITS)) {
        const a = latestByPoint.inlet.metrics[m], b = latestByPoint.outlet.metrics[m];
        removalRates[m] = a > 0 ? round((a - b) / a * 100, 1) : null;
      }
    }
    const compliance = {};
    for (const m of Object.keys(LIMITS)) {
      const recs = list.filter(r => r.samplePoint === 'outlet' && r.metrics[m] != null);
      const over = recs.filter(r => Number(r.metrics[m]) > LIMITS[m]).length;
      compliance[m] = { limit: LIMITS[m], samples: recs.length, exceed: over, rate: recs.length ? round((recs.length - over) / recs.length * 100, 1) : null };
    }
    ok(ctx.res, { siteId, days, from: iso(from), series, latestByPoint, removalRates, compliance, limits: LIMITS, metricNames: METRIC_NAMES, total: list.length, pending: list.filter(r => r.status === 'pending_review').length });
  });

  router.get('/api/lab/:id', async (ctx) => {
    requireAuth(ctx);
    const r = ctx.db.labRecords.find(x => x.id === ctx.params.id);
    if (!r) throw new ApiError(404, '化验记录不存在', 'LAB_NOT_FOUND');
    getSite(ctx, r.siteId);
    const history = ctx.db.labRecords.filter(x => x.siteId === r.siteId && x.samplePoint === r.samplePoint)
      .sort((a, b) => new Date(b.sampledAt) - new Date(a.sampledAt)).slice(0, 8).map(x => ({ id: x.id, sampledAt: x.sampledAt, metrics: x.metrics, status: x.status }));
    const audits = ctx.db.auditLogs.filter(a => a.targetId === r.id).sort((a, b) => new Date(b.at) - new Date(a.at));
    ok(ctx.res, { ...labView(ctx, r), history, audits });
  });

  router.post('/api/lab', async (ctx) => {
    const user = requirePerm(ctx, 'inspection.execute');
    const b = ctx.body || {};
    const siteId = b.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    if (!SAMPLE_NAMES[b.samplePoint]) throw new ApiError(400, '取样点不合法', 'BAD_SAMPLE_POINT');
    const metrics = {};
    for (const m of Object.keys(LIMITS)) {
      if (b.metrics && b.metrics[m] !== undefined && b.metrics[m] !== '') {
        const v = Number(b.metrics[m]);
        if (!Number.isFinite(v) || v < 0) throw new ApiError(400, `${METRIC_NAMES[m]} 必须为非负数字`, 'BAD_METRIC');
        metrics[m] = round(v, 3);
      }
    }
    if (!Object.keys(metrics).length) throw new ApiError(400, '请至少填写一项检测指标', 'METRICS_REQUIRED');
    const rec = {
      id: nextCode(ctx.db, 'labRecords', 'LAB'), siteId, samplePoint: b.samplePoint,
      samplePointName: SAMPLE_NAMES[b.samplePoint], sampledAt: b.sampledAt ? iso(new Date(b.sampledAt)) : iso(),
      recordedAt: iso(), operatorId: user.id, metrics, source: 'manual',
      method: String(b.method || 'COD：重铬酸钾法（HJ 828）；氨氮：纳氏试剂法（HJ 535）；总氮：碱性过硫酸钾消解（HJ 636）；总磷：钼酸铵分光光度法（GB 11893）；SS：重量法（GB 11901）'),
      status: 'pending_review', reviewerId: null, reviewedAt: null, reviewNote: '',
      qc: b.qc || { duplicate: '', spike: '' }, remark: String(b.remark || '').slice(0, 300),
    };
    ctx.db.labRecords.push(rec);
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'lab.create', targetType: 'lab', targetId: rec.id, siteId, detail: `录入 ${rec.samplePointName} 化验结果：${Object.entries(metrics).map(([k, v]) => `${k}=${v}`).join('，')}` });
    app.notifier.sendRoles(siteId, ['process', 'region'], { type: 'lab', title: `化验记录待复核：${rec.samplePointName}`, body: `${user.name} 录入 ${rec.id}，请工艺负责人复核。`, link: '/lab?id=' + rec.id });
    app.publish({ type: 'lab', action: 'create', data: rec });
    ok(ctx.res, labView(ctx, rec));
  });

  router.post('/api/lab/:id/review', async (ctx) => {
    const user = requirePerm(ctx, 'lab.review');
    const rec = ctx.db.labRecords.find(x => x.id === ctx.params.id);
    if (!rec) throw new ApiError(404, '化验记录不存在', 'LAB_NOT_FOUND');
    getSite(ctx, rec.siteId);
    if (rec.status !== 'pending_review') throw new ApiError(409, `当前状态为「${rec.status}」，不能复核`, 'BAD_STATUS');
    const b = ctx.body || {};
    if (b.approved === false) {
      rec.status = 'rejected';
      rec.reviewNote = String(b.note || '退回重测').slice(0, 300);
    } else {
      rec.status = 'reviewed';
      rec.reviewNote = String(b.note || '数据与质控样一致，复核通过。').slice(0, 300);
    }
    rec.reviewerId = user.id;
    rec.reviewedAt = iso();
    ctx.store.saveSoon();
    audit(ctx, { actor: user, action: 'lab.review', targetType: 'lab', targetId: rec.id, siteId: rec.siteId, detail: `${rec.status === 'reviewed' ? '复核通过' : '退回'} ${rec.id}：${rec.reviewNote}` });
    app.notifier.send([rec.operatorId], { type: 'lab', title: `化验记录${rec.status === 'reviewed' ? '已复核' : '被退回'}`, body: `${rec.id} ${rec.samplePointName}：${rec.reviewNote}`, link: '/lab?id=' + rec.id });
    app.publish({ type: 'lab', action: 'review', data: rec });
    ok(ctx.res, labView(ctx, rec));
  });

  router.get('/api/lab-plans', async (ctx) => {
    requireAuth(ctx);
    const siteId = ctx.query.siteId || scopedSites(ctx)[0].id;
    getSite(ctx, siteId);
    ok(ctx.res, ctx.db.labPlans.filter(p => p.siteId === siteId));
  });
}

module.exports = { register };

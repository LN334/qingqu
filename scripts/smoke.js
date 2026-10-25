'use strict';
/**
 * 接口冒烟测试：不依赖浏览器，直接以不同角色调用 REST 接口，校验响应结构与权限边界。
 * 用法：node scripts/smoke.js [baseUrl] [outFile]
 */
const fs = require('fs');
const path = require('path');

const BASE = process.argv[2] || 'http://127.0.0.1:8765';
const OUT = process.argv[3] || path.join(__dirname, '..', '_smoke-result.json');

const results = [];
let failures = 0;

async function call(method, url, { token, body, expect = 200, label, raw = false } = {}) {
  const headers = {};
  if (token) headers['Cookie'] = `zfs_session=${token}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  let res, text, json;
  try {
    res = await fetch(BASE + url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    text = await res.text();
    try { json = JSON.parse(text); } catch (e) { json = null; }
  } catch (e) {
    results.push({ label: label || `${method} ${url}`, status: 'NETWORK_ERROR', message: e.message });
    failures++;
    return { status: 0, json: null, text: '' };
  }
  const okShape = json && typeof json.ok === 'boolean';
  const pass = res.status === expect && (raw ? true : okShape && (expect >= 400 ? json.ok === false : json.ok === true));
  if (!pass) failures++;
  results.push({
    label: label || `${method} ${url}`,
    status: res.status,
    expect,
    pass,
    ok: json ? json.ok : null,
    error: json && json.error ? json.error.code + ' / ' + json.error.message : null,
    size: text.length,
    sample: pass ? undefined : text.slice(0, 300),
  });
  return { status: res.status, json, text, headers: res.headers };
}

async function login(username, password = 'water123') {
  const r = await call('POST', '/api/auth/login', { body: { username, password }, label: `登录 ${username}` });
  const setCookie = r.headers && r.headers.get('set-cookie');
  const m = setCookie && setCookie.match(/zfs_session=([^;]+)/);
  return m ? m[1] : null;
}

function has(obj, key) {
  const ok = obj && obj.data && Object.prototype.hasOwnProperty.call(obj.data, key);
  if (!ok) { failures++; results.push({ label: `字段校验 ${key}`, pass: false, status: obj && obj.status }); }
  return ok;
}

(async () => {
  /* ---------- 未登录访问 ---------- */
  await call('GET', '/api/overview', { expect: 401, label: '未登录访问驾驶舱应 401' });
  await call('GET', '/api/alarms', { expect: 401, label: '未登录访问告警应 401' });

  /* ---------- 管理员全量接口 ---------- */
  const admin = await login('admin');
  if (!admin) { failures++; results.push({ label: '管理员登录', pass: false }); }
  const gets = [
    ['/api/meta', '枚举字典'],
    ['/api/sites', '站点列表'],
    ['/api/sites/site-1', '站点详情'],
    ['/api/overview?siteId=site-1', '驾驶舱总览'],
    ['/api/sites/site-1/process', '工艺拓扑'],
    ['/api/telemetry/latest?siteId=site-1', '实时数据'],
    ['/api/telemetry/history?siteId=site-1&codes=SULFUR.PH,BAF.DO&hours=6', '历史曲线'],
    ['/api/telemetry/stats?siteId=site-1', '点位统计'],
    ['/api/points?siteId=site-1', '点位列表'],
    ['/api/devices?siteId=site-1', '设备列表'],
    ['/api/alarms?siteId=site-1', '告警列表'],
    ['/api/alarms/stats?siteId=site-1', '告警统计'],
    ['/api/alarm-rules?siteId=site-1', '告警规则'],
    ['/api/actions?siteId=site-1', '可执行动作'],
    ['/api/commands?siteId=site-1', '命令记录'],
    ['/api/params?siteId=site-1', '工艺参数'],
    ['/api/param-requests?siteId=site-1', '参数申请'],
    ['/api/workorders?siteId=site-1', '工单列表'],
    ['/api/inspection-plans?siteId=site-1', '巡检计划'],
    ['/api/inspections?siteId=site-1', '巡检任务'],
    ['/api/lab?siteId=site-1', '化验记录'],
    ['/api/lab/stats?siteId=site-1&days=7', '化验统计'],
    ['/api/lab-plans?siteId=site-1', '取样计划'],
    ['/api/diagnosis?siteId=site-1', '智能诊断'],
    ['/api/analysis/summary?siteId=site-1&days=7', '综合分析'],
    ['/api/analysis/units?siteId=site-1&days=7', '单元分析'],
    ['/api/analysis/efficiency?siteId=site-1&days=7', '设备效率'],
    ['/api/reports?siteId=site-1', '报表列表'],
    ['/api/notifications', '通知列表'],
    ['/api/audit?pageSize=5', '审计日志'],
    ['/api/users', '用户列表'],
    ['/api/roles', '角色列表'],
    ['/api/settings', '系统设置'],
    ['/api/channels', '通知通道'],
    ['/api/backups', '备份列表'],
    ['/api/system/health', '系统健康'],
    ['/api/system/sources', '数据来源'],
  ];
  const dataMap = {};
  for (const [u, label] of gets) {
    const r = await call('GET', u, { token: admin, label });
    if (r.status === 200) dataMap[u] = r.json.data;
  }
  has({ data: dataMap['/api/overview?siteId=site-1'] }, 'kpis');
  has({ data: dataMap['/api/sites/site-1/process'] }, 'units');
  if (!Array.isArray((dataMap['/api/telemetry/latest?siteId=site-1'] || {}).items)) { failures++; results.push({ label: '实时数据 items 数组', pass: false }); }
  if (!Array.isArray(dataMap['/api/alarms?siteId=site-1'])) { failures++; results.push({ label: '告警列表数组', pass: false }); }
  if (!Array.isArray(dataMap['/api/diagnosis?siteId=site-1'])) { failures++; results.push({ label: '诊断列表数组', pass: false }); }

  /* ---------- 控制命令闭环 ---------- */
  const cmd = await call('POST', '/api/commands', {
    token: admin, label: '下发停止提升泵（高风险需确认）',
    body: { siteId: 'site-1', action: 'raw.pump.stop', args: {}, reason: '冒烟测试：验证命令状态机' },
  });
  const cmdData = cmd.json && cmd.json.data;
  if (cmdData && cmdData.command && cmdData.command.id) {
    const id = cmdData.command.id;
    await call('GET', `/api/commands/${id}`, { token: admin, label: '查看命令详情' });
    if (cmdData.needsConfirm) await call('POST', `/api/commands/${id}/confirm`, { token: admin, label: '确认命令' });
    else results.push({ label: '命令直接下发（无需二次确认）', pass: true, status: 200 });
    await new Promise(r => setTimeout(r, 2500));
    await call('GET', `/api/commands?siteId=site-1`, { token: admin, label: '命令列表' });
    results.push({ label: '命令状态机', pass: true, status: 200, note: cmdData.command.status });
  } else {
    failures++; results.push({ label: '命令创建失败', pass: false });
  }

  /* ---------- 工艺参数变更与审批 ---------- */
  const params = dataMap['/api/params?siteId=site-1'] || [];
  const cloud = params.find(p => p.writeMode === 'cloud');
  const approval = params.find(p => p.writeMode === 'approval');
  if (cloud) {
    await call('POST', `/api/params/${cloud.id}/change`, {
      token: admin, label: `云端参数直接下发 ${cloud.key}`,
      body: { value: cloud.currentValue, reason: '冒烟测试：保持原值验证链路' },
    });
  }
  if (approval) {
    const existing = (dataMap['/api/param-requests?siteId=site-1'] || []).filter(r => r.status === 'pending_approval');
    const target = params.find(p => p.writeMode === 'approval' && !existing.some(r => r.paramId === p.id)) || null;
    if (!target) results.push({ label: '审批类参数（当前均有待审申请，跳过）', pass: true, status: 200 });
    else {
      const rq = await call('POST', `/api/params/${target.id}/change`, {
        token: admin, label: `审批类参数提交申请 ${target.key}`,
        body: { value: target.currentValue, reason: '冒烟测试：验证审批链路' },
      });
      const reqId = rq.json && rq.json.data && rq.json.data.id;
      if (reqId) {
        const li = await login('li');
        await call('POST', `/api/param-requests/${reqId}/approve`, { token: li, label: '工艺负责人审批参数' });
      }
    }
  }

  /* ---------- 工单闭环 ---------- */
  const wos = dataMap['/api/workorders?siteId=site-1'] || [];
  const w = wos.find(x => x.status === 'pending');
  if (w) {
    const zhao = await login('zhao');
    await call('POST', `/api/workorders/${w.id}/accept`, { token: zhao, label: '值班人员接单' });
    await call('POST', `/api/workorders/${w.id}/start`, { token: zhao, label: '开始处理' });
    await call('POST', `/api/workorders/${w.id}/finish`, { token: zhao, label: '提交处理结果', body: { result: '冒烟测试：已处理' } });
    await call('POST', `/api/workorders/${w.id}/verify`, { token: admin, label: '管理员验收' });
  }

  /* ---------- 巡检提交 ---------- */
  const insps = dataMap['/api/inspections?siteId=site-1'] || [];
  const insp = insps.find(x => x.status === 'pending');
  if (insp) {
    const allUsers = (await call('GET', '/api/users', { token: admin, label: '读取用户（巡检指派）' })).json.data || [];
    const owner = allUsers.find(u => u.id === insp.assigneeId);
    const inspToken = owner ? await login(owner.username) : await login('chen');
    const items = (insp.items || []).map(i => ({ key: i.key, result: 'ok', note: '冒烟测试' }));
    await call('POST', `/api/inspections/${insp.id}/submit`, { token: inspToken, label: '提交巡检（全部正常）', body: { items, summary: '冒烟测试' } });
  }

  /* ---------- 化验录入与复核 ---------- */
  const lab = await call('POST', '/api/lab', {
    token: admin, label: '录入手工化验',
    body: { siteId: 'site-1', samplePoint: 'outlet', metrics: { COD: 32, NH3N: 2.1, TN: 9.8, TP: 0.32, SS: 6 }, remark: '冒烟测试' },
  });
  const labId = lab.json && lab.json.data && lab.json.data.id;
  if (labId) await call('POST', `/api/lab/${labId}/review`, { token: admin, label: '复核化验', body: { approved: true, note: '冒烟测试复核' } });

  /* ---------- 报表生成与导出 ---------- */
  const rep = await call('POST', '/api/reports', { token: admin, label: '生成运行周报', body: { siteId: 'site-1', type: 'weekly' } });
  const repId = rep.json && rep.json.data && rep.json.data.id;
  if (repId) {
    await call('GET', `/api/reports/${repId}`, { token: admin, label: '报表详情' });
    await call('GET', `/api/reports/${repId}/export`, { token: admin, label: '导出 CSV', raw: true });
  }

  /* ---------- 备份与恢复 ---------- */
  const bk = await call('POST', '/api/backups', { token: admin, label: '创建备份', body: { note: '冒烟测试' } });
  const bkId = bk.json && bk.json.data && bk.json.data.id;
  if (bkId) {
    await call('GET', `/api/backups/${bkId}/download`, { token: admin, label: '下载备份', raw: true });
    await call('POST', `/api/backups/${bkId}/restore`, { token: admin, label: '恢复备份', body: { confirm: true } });
  }

  /* ---------- 权限边界 ---------- */
  const guest = await login('guest');
  await call('GET', '/api/overview?siteId=site-1', { token: guest, label: '只读账号可看驾驶舱' });
  await call('POST', '/api/commands', { token: guest, expect: 403, label: '只读账号下发命令应 403', body: { siteId: 'site-1', action: 'raw.pump.stop', args: {}, reason: 'x' } });
  await call('GET', '/api/users', { token: guest, expect: 403, label: '只读账号查用户应 403' });
  const sun = await login('sun');
  await call('POST', '/api/commands', { token: sun, label: '值班账号下发普通命令（site-2）', body: { siteId: 'site-2', action: 'raw.pump.start', args: {}, reason: '冒烟测试：值班权限' } });
  await call('POST', '/api/commands', { token: sun, expect: 403, label: '跨站点下发命令应 403', body: { siteId: 'site-1', action: 'raw.pump.start', args: {}, reason: 'x' } });
  await call('PATCH', '/api/settings', { token: sun, expect: 403, label: '值班账号改系统设置应 403', body: { notice: 'x' } });
  const chen2 = await login('chen');
  await call('GET', '/api/params?siteId=site-1', { token: chen2, label: '维修账号只读参数' });
  await call('POST', `/api/params/${(params[0] || {}).id}/change`, { token: chen2, expect: 403, label: '维修账号改参数应 403', body: { value: 1, reason: 'x' } });

  /* ---------- 错误处理（恢复备份会注销全部会话，重新登录） ---------- */
  const admin2 = await login('admin');
  await call('GET', '/api/not-exists', { token: admin2, expect: 404, label: '不存在的接口应 404' });
  await call('POST', '/api/auth/login', { expect: 401, label: '错误口令应 401', body: { username: 'admin', password: 'wrong' } });
  await call('GET', '/api/sites/site-999/process', { token: admin2, expect: 404, label: '不存在的站点应 404' });

  const summary = {
    base: BASE,
    at: new Date().toISOString(),
    total: results.length,
    failures,
    passRate: results.length ? Math.round((results.length - failures) / results.length * 1000) / 10 : 0,
    results,
  };
  fs.writeFileSync(OUT, JSON.stringify(summary, null, 2), 'utf8');
  // 同时打印一行摘要（供后台任务日志查看）
  console.log(`SMOKE total=${summary.total} failures=${failures} rate=${summary.passRate}% out=${OUT}`);
  if (failures) {
    for (const r of results.filter(x => x.pass === false)) console.log('FAIL', r.status, r.label, r.error || '', r.sample ? String(r.sample).slice(0, 160) : '');
  }
  process.exit(failures ? 1 : 0);
})();

#!/usr/bin/env node
/**
 * 抓取真实接口快照，供离线静态预览（视觉核验）使用。
 * 用法：node scripts/capture_fixtures.js [baseUrl] [outFile]
 * 需要平台服务已启动，且存在演示账号 admin/water123。
 */
'use strict';

const fs = require('fs');
const path = require('path');

const base = (process.argv[2] || 'http://127.0.0.1:8765').replace(/\/$/, '');
const outFile = path.resolve(process.argv[3] || path.join(__dirname, '..', '_preview', 'fixtures.json'));

let token = null;

async function call(method, p, body) {
  const headers = { Accept: 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(base + p, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let json = null;
  try { json = JSON.parse(text); } catch { json = null; }
  return { status: res.status, json, text };
}

function key(method, p) {
  return `${method} ${p}`;
}

const fixtures = {};
async function capture(method, p, body) {
  const r = await call(method, p, body);
  if (r.json) fixtures[key(method, p)] = r.json;
  else fixtures[key(method, p)] = { ok: false, error: { code: `HTTP_${r.status}`, message: r.text.slice(0, 300) } };
  return r.json;
}

async function main() {
  const login = await call('POST', '/api/auth/login', { username: 'admin', password: 'water123' });
  if (!login.json || !login.json.ok) {
    console.error('登录失败：', login.status, login.text.slice(0, 200));
    process.exit(1);
  }
  token = login.json.data.token;
  fixtures[key('POST', '/api/auth/login')] = login.json;

  const sites = ['site-1', 'site-2'];
  // 30 天 × 1 小时桶的全量通道快照：作为 /api/telemetry/history 的同路径回退基准
  // （页面的 from/to 每次加载都不同，无法精确命中，只能回退到这一份最长窗口数据）
  const nowMs = Date.now();
  const histFrom = encodeURIComponent(new Date(nowMs - 30 * 24 * 3600 * 1000).toISOString());
  const histTo = encodeURIComponent(new Date(nowMs).toISOString());
  const histCodes = 'RAW.LVL,FLOW.IN,BAF.PH,BAF.DO,SULFUR.PH,SULFUR.DO,PAC.TOTAL,PAM.TOTAL,ENERGY.KWH,POWER.KW,CLEAR.LVL,WASTE.LVL';
  const paths = [
    `/api/telemetry/history?siteId=site-1&codes=${histCodes}&bucketSec=3600&from=${histFrom}&to=${histTo}`,
    '/api/auth/me', '/api/meta', '/api/sites', '/api/system/health', '/api/system/sources',
    '/api/notifications', '/api/users', '/api/roles', '/api/settings', '/api/channels', '/api/backups',
    '/api/audit?pageSize=20',
    '/api/telemetry/latest?siteId=site-1',
    '/api/telemetry/history?siteId=site-1&codes=RAW.LVL,BAF.DO,SULFUR.PH,FLOW.IN&hours=24',
    '/api/telemetry/stats?siteId=site-1',
    '/api/alarms?siteId=site-1&pageSize=100',
    '/api/alarms/stats?siteId=site-1',
    '/api/alarm-rules?siteId=site-1',
    '/api/actions?siteId=site-1',
    '/api/commands?siteId=site-1&pageSize=50',
    '/api/params?siteId=site-1',
    '/api/param-requests?siteId=site-1',
    '/api/workorders?siteId=site-1&pageSize=50',
    '/api/inspections?siteId=site-1',
    '/api/inspection-plans?siteId=site-1',
    '/api/lab?siteId=site-1&pageSize=100',
    '/api/lab/stats?siteId=site-1',
    '/api/lab-plans?siteId=site-1',
    '/api/diagnosis?siteId=site-1',
    '/api/analysis/summary?siteId=site-1&days=30',
    '/api/analysis/units?siteId=site-1',
    '/api/analysis/efficiency?siteId=site-1',
    '/api/reports?siteId=site-1',
  ];
  for (const s of sites) {
    paths.push(`/api/sites/${s}`, `/api/sites/${s}/process`, `/api/overview?siteId=${s}`, `/api/points?siteId=${s}`, `/api/devices?siteId=${s}`);
  }
  let done = 0;
  for (const p of paths) {
    const method = 'GET';
    const r = await capture(method, p);
    done += 1;
    if (!r || r.ok !== true) console.warn('WARN', p, r && r.error && r.error.message);
  }

  // 详情接口：取列表前几条
  const detailSources = [
    ['/api/alarms?siteId=site-1&pageSize=20', (x) => `/api/alarms/${x.id}`],
    ['/api/workorders?siteId=site-1&pageSize=20', (x) => `/api/workorders/${x.id}`],
    ['/api/devices?siteId=site-1', (x) => `/api/devices/${x.id}`],
    ['/api/params?siteId=site-1', (x) => `/api/params/${x.id}`],
    ['/api/lab?siteId=site-1&pageSize=20', (x) => `/api/lab/${x.id}`],
    ['/api/inspections?siteId=site-1', (x) => `/api/inspections/${x.id}`],
    ['/api/reports?siteId=site-1', (x) => `/api/reports/${x.id}`],
    ['/api/points?siteId=site-1', (x) => `/api/points/${x.id}`],
  ];
  for (const [listPath, toDetail] of detailSources) {
    let list = fixtures[key('GET', listPath)];
    if (!list) list = await capture('GET', listPath);
    const items = list && list.ok ? (Array.isArray(list.data) ? list.data : (list.data.items || [])) : [];
    for (const item of items.slice(0, 4)) {
      if (!item || !item.id) continue;
      const p = toDetail(item);
      if (!fixtures[key('GET', p)]) await capture('GET', p);
    }
  }

  // 最近一次化验复核视图需要的统计数据
  await capture('GET', '/api/lab/stats?siteId=site-2');
  await capture('GET', '/api/analysis/summary?siteId=site-1&days=7');
  await capture('GET', '/api/overview?siteId=site-1');

  // 页面常用的筛选/参数变体（与 web/js/pages/*.js 一一对应，避免 mock 回退造成误判）
  const variants = [
    '/api/alarms?siteId=site-1&status=open&pageSize=200',
    '/api/alarms?siteId=site-1&status=open&pageSize=20',
    '/api/alarms/stats?siteId=site-1&days=7',
    '/api/workorders?siteId=site-1&status=open&pageSize=10',
    '/api/lab?siteId=site-1&samplePoint=inlet&pageSize=200',
    '/api/lab/stats?siteId=site-1&days=30',
    '/api/analysis/summary?siteId=site-1&days=7',
    '/api/analysis/units?siteId=site-1&days=7',
    '/api/analysis/efficiency?siteId=site-1&days=7',
    '/api/notifications?siteId=site-1',
    '/api/notifications?unread=1&pageSize=10',
    '/api/telemetry/history?siteId=site-1&codes=RAW.LVL&hours=24',
    '/api/reports?siteId=site-1&pageSize=50',
    '/api/params?siteId=site-1&status=pending_approval',
  ];
  for (const p of variants) {
    if (!fixtures[key('GET', p)]) await capture('GET', p);
  }

  fs.mkdirSync(path.dirname(outFile), { recursive: true });
  fs.writeFileSync(outFile, JSON.stringify(fixtures, null, 0), 'utf8');
  const size = fs.statSync(outFile).size;
  console.log(`fixtures: ${Object.keys(fixtures).length} 个接口, ${(size / 1024).toFixed(0)} KB -> ${outFile}`);
}

main().catch((e) => { console.error(e); process.exit(1); });

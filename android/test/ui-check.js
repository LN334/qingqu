#!/usr/bin/env node
/**
 * 安卓端界面引用核对（不需要设备/模拟器）：
 *   1) 所有 U.xxx(...) 调用在 U.java 里都有对应方法；
 *   2) 所有 U.XXX 常量在 U.java 里都有定义；
 *   3) 所有 R.drawable.x / R.string.x 资源都存在；
 *   4) 新增「更多」二级页面在 Manifest 里注册；
 *   5) 本机种子文件（devices / lab）条数与字段齐全。
 *
 * 用法：node test/ui-check.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const SRC = path.join(ROOT, 'src', 'com', 'ln334', 'zhifuwater');
let pass = 0;
let fail = 0;
const ok = (name) => { pass++; console.log('  ok   ' + name); };
const bad = (name, detail) => { fail++; console.log('  FAIL ' + name + (detail ? ' :: ' + detail : '')); };
const check = (name, cond, detail) => (cond ? ok(name) : bad(name, detail));

const files = fs.readdirSync(SRC).filter((f) => f.endsWith('.java'));

/* ---------------- 1&2. U 的方法与常量 ---------------- */

const uText = fs.readFileSync(path.join(SRC, 'U.java'), 'utf8');
const uMethods = new Set();
const methodRe = /public\s+static\s+[\w.<>\[\],\s]+?\s(\w+)\s*\(/g;
let m;
while ((m = methodRe.exec(uText)) !== null) uMethods.add(m[1]);
const ifaceRe = /public\s+interface\s+(\w+)/g;
while ((m = ifaceRe.exec(uText)) !== null) uMethods.add(m[1]);

const uConsts = new Set();
const constRe = /public\s+static\s+final\s+(?:int|String|float|double)\s+(\w+)\s*=/g;
while ((m = constRe.exec(uText)) !== null) uConsts.add(m[1]);

const missingMethods = new Map();
const missingConsts = new Map();
files.forEach((f) => {
  const text = fs.readFileSync(path.join(SRC, f), 'utf8');
  let r;
  const callRe = /\bU\.(\w+)\s*\(/g;
  while ((r = callRe.exec(text)) !== null) {
    if (!uMethods.has(r[1])) {
      if (!missingMethods.has(r[1])) missingMethods.set(r[1], new Set());
      missingMethods.get(r[1]).add(f);
    }
  }
  const useRe = /\bU\.([A-Z][A-Z0-9_]*)\b(?!\s*\()/g;
  while ((r = useRe.exec(text)) !== null) {
    if (!uConsts.has(r[1])) {
      if (!missingConsts.has(r[1])) missingConsts.set(r[1], new Set());
      missingConsts.get(r[1]).add(f);
    }
  }
});
const describe = (map) => [...map.entries()].map(([k, v]) => k + ' <- ' + [...v].join(',')).join(' | ');
check('U 方法调用全部存在', missingMethods.size === 0, describe(missingMethods));
check('U 常量引用全部存在', missingConsts.size === 0, describe(missingConsts));

/* ---------------- 3. 资源引用 ---------------- */

const drawables = new Set(fs.readdirSync(path.join(ROOT, 'res', 'drawable'))
  .filter((f) => f.endsWith('.xml')).map((f) => f.replace(/\.xml$/, '')));
const stringsText = fs.readFileSync(path.join(ROOT, 'res', 'values', 'strings.xml'), 'utf8');
const strings = new Set();
const strRe = /<string\s+name="([^"]+)"/g;
while ((m = strRe.exec(stringsText)) !== null) strings.add(m[1]);

const missingDraw = new Map();
const missingStr = new Map();
files.forEach((f) => {
  const text = fs.readFileSync(path.join(SRC, f), 'utf8');
  let r;
  const dRe = /R\.drawable\.(\w+)/g;
  while ((r = dRe.exec(text)) !== null) {
    if (!drawables.has(r[1])) {
      if (!missingDraw.has(r[1])) missingDraw.set(r[1], new Set());
      missingDraw.get(r[1]).add(f);
    }
  }
  const sRe = /R\.string\.(\w+)/g;
  while ((r = sRe.exec(text)) !== null) {
    if (!strings.has(r[1])) {
      if (!missingStr.has(r[1])) missingStr.set(r[1], new Set());
      missingStr.get(r[1]).add(f);
    }
  }
});
check('R.drawable 引用全部存在', missingDraw.size === 0, describe(missingDraw));
check('R.string 引用全部存在', missingStr.size === 0, describe(missingStr));

/* ---------------- 4. Manifest 注册 ---------------- */

const manifest = fs.readFileSync(path.join(ROOT, 'AndroidManifest.xml'), 'utf8');
['MainActivity', 'InspectionActivity', 'SubActivity'].forEach((a) => {
  check('Manifest 注册 ' + a, manifest.includes('android:name=".' + a + '"'));
});

/* ---------------- 5. 本机种子 ---------------- */

const demo = path.join(ROOT, 'assets', 'demo');
const load = (name) => JSON.parse(fs.readFileSync(path.join(demo, name), 'utf8'));
const arr = (o) => (Array.isArray(o) ? o : o.data || []);

const devices = arr(load('devices.json'));
check('devices.json 24 台', devices.length === 24, String(devices.length));
const deviceFields = ['id', 'code', 'name', 'type', 'unitCode', 'unitName', 'health', 'runtimeHours', 'starts'];
const deviceMissing = devices.filter((d) => deviceFields.some((k) => d[k] === undefined));
check('设备字段齐全', deviceMissing.length === 0, deviceMissing.map((d) => d.code).join(','));

const labs = arr(load('lab.json'));
check('lab.json 20 条', labs.length === 20, String(labs.length));
const labFields = ['id', 'samplePoint', 'samplePointName', 'sampledAt', 'metrics', 'status', 'operatorName'];
const labMissing = labs.filter((r) => labFields.some((k) => r[k] === undefined));
check('化验字段齐全', labMissing.length === 0, labMissing.map((r) => r.id).join(','));
const labStatuses = new Set(labs.map((r) => r.status));
check('化验状态取值合法', [...labStatuses].every((s) => s === 'pending_review' || s === 'reviewed'),
  [...labStatuses].join(','));
const metricMissing = labs.filter((r) => !r.metrics || ['COD', 'NH3N', 'TN', 'TP', 'SS']
  .some((k) => r.metrics[k] === undefined || r.metrics[k] === null));
check('化验五项指标齐全', metricMissing.length === 0, metricMissing.map((r) => r.id).join(','));

console.log('UI-CHECK pass=' + pass + ' fail=' + fail);
process.exit(fail === 0 ? 0 : 1);

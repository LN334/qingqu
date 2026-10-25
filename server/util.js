'use strict';
/**
 * 通用工具：时间、随机数、口令散列、CSV、数值格式化。
 * 零第三方依赖，仅使用 Node 内置模块。
 */
const crypto = require('crypto');

const TZ_OFFSET_MIN = 8 * 60; // Asia/Shanghai

function pad(n) { return String(n).padStart(2, '0'); }

/** 当前时间（毫秒） */
function now() { return Date.now(); }

/** ISO 字符串（UTC），用于存储 */
function iso(t = Date.now()) { return new Date(t).toISOString(); }

/** 本地（UTC+8）日期键 YYYY-MM-DD */
function dayKey(t = Date.now()) {
  const d = new Date(t + TZ_OFFSET_MIN * 60000);
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
}

/** 本地日期时间字符串 YYYY-MM-DD HH:mm:ss */
function localTime(t = Date.now()) {
  const d = new Date(t + TZ_OFFSET_MIN * 60000);
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ` +
    `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
}

function localShort(t = Date.now()) {
  const d = new Date(t + TZ_OFFSET_MIN * 60000);
  return `${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
}

/** 本地日期的 0 点毫秒时间戳 */
function startOfDay(t = Date.now(), offsetDays = 0) {
  const d = new Date(t + TZ_OFFSET_MIN * 60000);
  const base = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()) - offsetDays * 86400000;
  return base - TZ_OFFSET_MIN * 60000;
}

function round(n, digits = 1) {
  const f = Math.pow(10, digits);
  return Math.round(n * f) / f;
}

function clamp(n, min, max) { return Math.min(max, Math.max(min, n)); }

/** 可复现随机数（mulberry32） */
function rng(seed = 20261025) {
  let a = seed >>> 0;
  return function () {
    a |= 0; a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function uid(prefix, seq) {
  if (seq != null) return `${prefix}${String(seq).padStart(4, '0')}`;
  return `${prefix}${crypto.randomBytes(4).toString('hex')}`;
}

function hashPassword(password, salt = crypto.randomBytes(16).toString('hex')) {
  const hash = crypto.scryptSync(password, salt, 32).toString('hex');
  return `scrypt$${salt}$${hash}`;
}

function verifyPassword(password, stored) {
  try {
    const [kind, salt, hash] = String(stored).split('$');
    if (kind !== 'scrypt' || !salt || !hash) return false;
    const test = crypto.scryptSync(password, salt, 32);
    return crypto.timingSafeEqual(Buffer.from(hash, 'hex'), test);
  } catch { return false; }
}

function token(bytes = 24) { return crypto.randomBytes(bytes).toString('base64url'); }

/** CSV（Excel 可直接打开，带 BOM） */
function toCSV(headers, rows) {
  const esc = (v) => {
    const s = v === null || v === undefined ? '' : String(v);
    return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const lines = [headers.map(esc).join(',')];
  for (const r of rows) lines.push(r.map(esc).join(','));
  return '\uFEFF' + lines.join('\r\n');
}

function deepClone(o) { return o == null ? o : JSON.parse(JSON.stringify(o)); }

function pick(obj, keys) {
  const out = {};
  for (const k of keys) if (obj && obj[k] !== undefined) out[k] = obj[k];
  return out;
}

module.exports = {
  now, iso, dayKey, localTime, localShort, startOfDay, round, clamp, rng, uid,
  hashPassword, verifyPassword, token, toCSV, deepClone, pick, TZ_OFFSET_MIN,
};

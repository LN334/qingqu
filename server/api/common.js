'use strict';
const { sendJSON } = require('../http');
const { writeAudit } = require('../audit');

class ApiError extends Error {
  constructor(status, message, code = 'ERROR', details = null) {
    super(message);
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

function ok(res, data, meta) {
  sendJSON(res, 200, { ok: true, data, ...(meta ? { meta } : {}) });
}

function fail(res, status, message, code = 'ERROR', details = null) {
  sendJSON(res, status, { ok: false, error: { code, message, details } });
}

function requireAuth(ctx) {
  if (!ctx.user) throw new ApiError(401, '请先登录', 'UNAUTHORIZED');
  return ctx.user;
}

function requirePerm(ctx, perm) {
  const user = requireAuth(ctx);
  if (!ctx.auth.can(user, perm)) throw new ApiError(403, '当前账号没有该操作权限', 'FORBIDDEN');
  return user;
}

function getSite(ctx, siteId) {
  const site = ctx.db.sites.find(s => s.id === siteId);
  if (!site) throw new ApiError(404, '站点不存在', 'SITE_NOT_FOUND');
  if (!ctx.auth.canSite(ctx.user, siteId)) throw new ApiError(403, '无权访问该站点数据', 'SITE_FORBIDDEN');
  return site;
}

function scopedSites(ctx) {
  const ids = ctx.auth.siteScope(ctx.user);
  return ctx.db.sites.filter(s => ids.includes(s.id));
}

function paginate(list, query = {}) {
  const page = Math.max(1, parseInt(query.page, 10) || 1);
  const pageSize = Math.min(200, Math.max(1, parseInt(query.pageSize, 10) || 20));
  const total = list.length;
  const pages = Math.max(1, Math.ceil(total / pageSize));
  const items = list.slice((page - 1) * pageSize, page * pageSize);
  return { items, total, page, pageSize, pages };
}

function parseTime(v, fallback) {
  if (v == null || v === '') return fallback;
  if (/^\d+$/.test(String(v))) return new Date(Number(v)).getTime();
  const t = new Date(v).getTime();
  return Number.isFinite(t) ? t : fallback;
}

function rangeFromQuery(query, defaultHours = 24) {
  const to = parseTime(query.to, Date.now());
  const from = parseTime(query.from, to - defaultHours * 3600000);
  return { from: Math.min(from, to), to };
}

function audit(ctx, payload) {
  return writeAudit(ctx.store, { ...payload, ip: ctx.ip });
}

function sortByTimeDesc(items, key = 'at') {
  return items.slice().sort((a, b) => new Date(b[key] || 0) - new Date(a[key] || 0));
}

function paramValue(ctx, siteId, key) {
  const p = ctx.db.params.find(x => x.siteId === siteId && x.key === key);
  return p ? p.currentValue : null;
}

module.exports = {
  ApiError, ok, fail, requireAuth, requirePerm, getSite, scopedSites,
  paginate, parseTime, rangeFromQuery, audit, sortByTimeDesc, paramValue,
};

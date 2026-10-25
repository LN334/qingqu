'use strict';
/**
 * 登录、会话与权限。权限按方案第 9 章：功能权限 + 数据范围双重校验。
 */
const crypto = require('crypto');
const { iso, token, verifyPassword } = require('./util');

const SESSION_TTL_MS = 12 * 3600 * 1000;
const TOUCH_INTERVAL_MS = 5 * 60 * 1000;

function sha256(s) { return crypto.createHash('sha256').update(s).digest('hex'); }

class Auth {
  constructor(store) {
    this.store = store;
    this.attempts = new Map(); // username -> { count, lockedUntil }
    this.cleanupSessions();
  }

  get db() { return this.store.data; }

  cleanupSessions() {
    const now = Date.now();
    const before = (this.db.sessions || []).length;
    this.db.sessions = (this.db.sessions || []).filter(s => new Date(s.expiresAt).getTime() > now);
    if (this.db.sessions.length !== before) this.store.saveSoon();
  }

  roleOf(user) {
    return (this.db.roles && this.db.roles[user.role]) || { perms: [], sites: 'assigned', name: user.role };
  }

  siteScope(user) {
    const role = this.roleOf(user);
    if (role.sites === 'all' || user.role === 'super') return this.db.sites.map(s => s.id);
    return (user.siteIds || []).slice();
  }

  permissions(user) {
    return this.roleOf(user).perms.slice();
  }

  can(user, perm) {
    return this.permissions(user).includes(perm);
  }

  canSite(user, siteId) {
    return this.siteScope(user).includes(siteId);
  }

  sanitize(user) {
    if (!user) return null;
    const { password, ...rest } = user;
    const role = this.roleOf(user);
    return {
      ...rest,
      roleName: role.name,
      perms: this.permissions(user),
      siteScope: this.siteScope(user),
    };
  }

  /** 登录：5 次失败锁定 60 秒；成功返回 token 与用户视图 */
  login(username, password, meta = {}) {
    const key = String(username || '').trim().toLowerCase();
    const rec = this.attempts.get(key);
    if (rec && rec.lockedUntil > Date.now()) {
      const wait = Math.ceil((rec.lockedUntil - Date.now()) / 1000);
      return { error: { status: 429, code: 'LOCKED', message: `尝试次数过多，请 ${wait} 秒后再试。` } };
    }
    const user = this.db.users.find(u => u.username.toLowerCase() === key);
    if (!user || !verifyPassword(password, user.password)) {
      const count = (rec && rec.count ? rec.count : 0) + 1;
      const lockedUntil = count >= 5 ? Date.now() + 60000 : 0;
      this.attempts.set(key, { count: count >= 5 ? 0 : count, lockedUntil });
      return { error: { status: 401, code: 'BAD_CREDENTIALS', message: '账号或密码不正确。' } };
    }
    if (user.status !== 'active') {
      return { error: { status: 403, code: 'INACTIVE', message: '账号已停用，请联系管理员。' } };
    }
    this.attempts.delete(key);
    const raw = token(24);
    const now = Date.now();
    const session = {
      id: 'sess-' + raw.slice(0, 12),
      tokenHash: sha256(raw),
      userId: user.id,
      createdAt: iso(now),
      expiresAt: iso(now + SESSION_TTL_MS),
      lastSeenAt: iso(now),
      ip: meta.ip || '',
      agent: String(meta.agent || '').slice(0, 160),
    };
    this.db.sessions = (this.db.sessions || []).filter(s => s.userId !== user.id || new Date(s.expiresAt).getTime() > now);
    this.db.sessions.push(session);
    if (this.db.sessions.length > 200) this.db.sessions = this.db.sessions.slice(-200);
    user.lastLoginAt = iso(now);
    this.store.saveSoon();
    return { token: raw, user: this.sanitize(user) };
  }

  logout(rawToken) {
    if (!rawToken) return;
    const h = sha256(rawToken);
    const before = this.db.sessions.length;
    this.db.sessions = this.db.sessions.filter(s => s.tokenHash !== h);
    if (this.db.sessions.length !== before) this.store.saveSoon();
  }

  /** 注销某用户的全部会话（停用账号、重置口令后调用） */
  logoutAllFor(userId) {
    const before = this.db.sessions.length;
    this.db.sessions = this.db.sessions.filter(s => s.userId !== userId);
    if (this.db.sessions.length !== before) this.store.saveSoon();
  }

  /** 从请求解析用户；同时续期 lastSeenAt */
  fromRequest(req) {
    const raw = this.rawToken(req);
    if (!raw) return null;
    const h = sha256(raw);
    const s = (this.db.sessions || []).find(x => x.tokenHash === h);
    if (!s) return null;
    if (new Date(s.expiresAt).getTime() <= Date.now()) return null;
    const user = this.db.users.find(u => u.id === s.userId);
    if (!user || user.status !== 'active') return null;
    if (Date.now() - new Date(s.lastSeenAt).getTime() > TOUCH_INTERVAL_MS) {
      s.lastSeenAt = iso();
      this.store.saveSoon();
    }
    return user;
  }

  rawToken(req) {
    const auth = req.headers['authorization'] || '';
    if (/^Bearer\s+/i.test(auth)) return auth.replace(/^Bearer\s+/i, '').trim();
    const cookie = req.headers['cookie'] || '';
    const m = cookie.match(/(?:^|;\s*)zfs_session=([^;]+)/);
    return m ? decodeURIComponent(m[1]) : '';
  }
}

module.exports = { Auth, SESSION_TTL_MS };

'use strict';
/** 站内通知：告警、工单、参数审批、系统消息。外部通道演示环境不真实发送。 */
const { nextCode } = require('./codes');
const { iso } = require('./util');

class Notifier {
  constructor(store, publish = () => {}) {
    this.store = store;
    this.publish = publish;
  }

  get db() { return this.store.data; }

  send(userIds, { type, title, body, link = '', siteId = null }) {
    const list = (Array.isArray(userIds) ? userIds : [userIds]).filter(Boolean);
    const out = [];
    for (const userId of [...new Set(list)]) {
      const n = {
        id: nextCode(this.db, 'notifications', 'NT'),
        at: iso(),
        userId,
        siteId,
        type,
        title,
        body,
        read: false,
        link,
      };
      this.db.notifications.push(n);
      out.push(n);
    }
    if (this.db.notifications.length > 800) this.db.notifications.splice(0, this.db.notifications.length - 800);
    if (out.length) this.store.saveSoon();
    return out;
  }

  /** 按站点与角色发通知；排除停用账号 */
  sendRoles(siteId, roles, payload) {
    const users = this.db.users.filter(u => u.status === 'active'
      && roles.includes(u.role)
      && (u.role === 'super' || (u.siteIds || []).includes(siteId)));
    return this.send(users.map(u => u.id), { ...payload, siteId });
  }

  markRead(userId, ids = null) {
    const db = this.db;
    let n = 0;
    for (const item of db.notifications) {
      if (item.userId !== userId || item.read) continue;
      if (ids && !ids.includes(item.id)) continue;
      item.read = true;
      n++;
    }
    if (n) this.store.saveSoon();
    return n;
  }
}

module.exports = { Notifier };

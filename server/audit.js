'use strict';
/** 审计日志：所有控制、参数、账号、告警处置动作必须留痕（方案第 9/14 章）。 */
const { nextCode } = require('./codes');
const { iso } = require('./util');

function writeAudit(store, { actor, action, targetType, targetId, detail, siteId = null, result = 'success', ip = '', before = null, after = null }) {
  const db = store.data;
  const actorId = actor && actor.id ? actor.id : 'system';
  const actorName = actor && actor.name ? actor.name : (actor === 'system' ? '系统' : String(actor || 'system'));
  const entry = {
    id: nextCode(db, 'auditLogs', 'AU'),
    at: iso(),
    siteId,
    actorId,
    actorName,
    action,
    targetType,
    targetId,
    detail,
    ip,
    result,
    before,
    after,
  };
  db.auditLogs.push(entry);
  if (db.auditLogs.length > 5000) db.auditLogs.splice(0, db.auditLogs.length - 5000);
  store.saveSoon();
  return entry;
}

module.exports = { writeAudit };

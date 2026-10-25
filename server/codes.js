'use strict';
/** 业务编号：类型-日期-序号，如 AL-20261025-001 */
const { dayKey } = require('./util');

function nextCode(db, collection, prefix, width = 3) {
  const day = dayKey().replace(/-/g, '');
  const head = `${prefix}-${day}-`;
  let max = 0;
  for (const item of db[collection] || []) {
    const id = String(item.id || '');
    if (!id.startsWith(head)) continue;
    const n = parseInt(id.slice(head.length), 10);
    if (Number.isFinite(n) && n > max) max = n;
  }
  return head + String(max + 1).padStart(width, '0');
}

module.exports = { nextCode };

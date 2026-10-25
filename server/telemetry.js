'use strict';
/**
 * 遥测存储：最新值 + 历史序列（内存）。
 * 最新值 3 秒级刷新；历史按 5 分钟桶落点，30 天保留（支撑分析页 30 天窗口）。
 */
const HISTORY_DAYS = 30;
const LIVE_BUCKET_MS = 5 * 60 * 1000;

class Telemetry {
  constructor() {
    this.latest = {};      // pointId -> { v, t, q, source }
    this.hist = {};        // pointId -> [{ t, v, q }, ...]
    this.recvAt = {};      // pointId -> 服务端接收时间
  }

  setLatest(pointId, value, { t = Date.now(), quality = 'good', source = 'gateway' } = {}) {
    this.latest[pointId] = { v: value, t, q: quality, source };
    this.recvAt[pointId] = Date.now();
  }

  getLatest(pointId) {
    const l = this.latest[pointId];
    if (!l) return null;
    // 对外同时提供 v/q 与 value/quality 两套字段名，避免各消费方口径不一致
    return { ...l, value: l.v, quality: l.q, at: l.t };
  }

  /** 直接写入历史点（种子/补传使用） */
  pushHist(pointId, t, v, q = 'good') {
    const arr = this.hist[pointId] || (this.hist[pointId] = []);
    const last = arr[arr.length - 1];
    if (last && t <= last.t) {
      // 保持时间有序；重复时间戳以新值覆盖
      if (t === last.t) { last.v = v; last.q = q; }
      return;
    }
    arr.push({ t, v: typeof v === 'number' ? Math.round(v * 1000) / 1000 : v, q });
  }

  /** 5 分钟节流落历史 */
  record(pointId, value, quality = 'good', t = Date.now()) {
    const arr = this.hist[pointId] || (this.hist[pointId] = []);
    const last = arr[arr.length - 1];
    if (last && t - last.t < LIVE_BUCKET_MS) return false;
    this.pushHist(pointId, t, value, quality);
    this.trim(pointId);
    return true;
  }

  trim(pointId) {
    const arr = this.hist[pointId];
    if (!arr || !arr.length) return;
    const minT = Date.now() - HISTORY_DAYS * 86400000;
    let i = 0;
    while (i < arr.length && arr[i].t < minT) i++;
    if (i > 0) arr.splice(0, i);
  }

  /** 历史区间查询，按 bucketSec 聚合；measure 取均值，state/counter 取末值 */
  range(pointIds, from, to, bucketSec = 300, types = {}) {
    const out = {};
    for (const pid of pointIds) {
      const arr = this.hist[pid] || [];
      const type = types[pid] || 'measure';
      const buckets = new Map();
      for (const p of arr) {
        if (p.t < from || p.t > to) continue;
        const key = Math.floor(p.t / (bucketSec * 1000)) * bucketSec * 1000;
        const cur = buckets.get(key);
        if (!cur) buckets.set(key, { t: key, sum: typeof p.v === 'number' ? p.v : 0, n: typeof p.v === 'number' ? 1 : 0, last: p.v, q: p.q });
        else {
          if (typeof p.v === 'number') { cur.sum += p.v; cur.n += 1; }
          cur.last = p.v; cur.q = p.q;
        }
      }
      const points = [];
      for (const [t, b] of [...buckets.entries()].sort((a, b2) => a[0] - b2[0])) {
        let v;
        if (type === 'measure') v = b.n ? b.sum / b.n : b.last;
        else v = b.last;
        points.push({ t, v: Math.round(v * 1000) / 1000, q: b.q || 'good' });
      }
      out[pid] = points;
    }
    return out;
  }

  /** 统计区间内有效数据点数 */
  count(pointId, from, to) {
    return (this.hist[pointId] || []).filter(p => p.t >= from && p.t <= to).length;
  }
}

module.exports = { Telemetry, HISTORY_DAYS };

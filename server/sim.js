'use strict';
/**
 * 内置工艺模拟器（演示用「模拟网关 + PLC」）。
 * 说明：
 *  - 只模拟图纸已确认/演示选配点位的工程量，不伪造 TN/COD/氨氮/总磷/SS 在线值；
 *  - 所有输出都带时间戳与来源，前端必须展示「最后更新」；
 *  - 控制动作执行前做联锁校验，与真实 PLC 的安全职责边界一致。
 */
const { clamp, round, rng } = require('./util');

const TICK_MS = 5000;

const PROFILES = {
  'site-1': {
    raw: { level: 22.6, pumpRun: true, pumpMode: 0, fault: false, hours: 1132.5, starts: 187 },
    baf: { ph: 7.12, do: 1.78, blower: true, dp: 18.2, bwPhase: 0, bwCount: 12, bwAt: null },
    pac: { level: 18.4, dose: 0.06, run: true, total: 412.6, mixer: true },
    pam: { level: 62.3, dose: 0.02, run: true, total: 210.4, mixer: true },
    sulfur: { ph: 6.76, do: 0.29, p: 0.41, dp: 16.4, bwPhase: 0, bwCount: 9, bwAt: null },
    clear: { level: 58 },
    waste: { level: 44 },
    power: { kw: 1.36, kwh: 1284.6 },
  },
  'site-2': {
    raw: { level: 24.1, pumpRun: true, pumpMode: 0, fault: false, hours: 486.2, starts: 92 },
    baf: { ph: 7.2, do: 3.05, blower: true, dp: 17.1, bwPhase: 0, bwCount: 5, bwAt: null },
    pac: { level: 55.2, dose: 0.05, run: true, total: 168.2, mixer: true },
    pam: { level: 48.7, dose: 0.02, run: true, total: 88.5, mixer: true },
    sulfur: { ph: 7.02, do: 0.31, p: 0.38, dp: 15.2, bwPhase: 0, bwCount: 4, bwAt: null },
    clear: { level: 51 },
    waste: { level: 31 },
    power: { kw: 1.05, kwh: 640.2 },
  },
};

const BW_PHASES = [
  { phase: 1, name: '气冲', sec: 30 },
  { phase: 2, name: '气水联合冲洗', sec: 90 },
  { phase: 3, name: '漂洗', sec: 45 },
];

class Simulator {
  constructor({ store, telemetry, log = console }) {
    this.store = store;
    this.telemetry = telemetry;
    this.log = log;
    this.state = JSON.parse(JSON.stringify(PROFILES));
    this.timer = null;
    this.listeners = new Set();
    this.pointsBySite = {};
    for (const p of this.db.points) {
      (this.pointsBySite[p.siteId] = this.pointsBySite[p.siteId] || []).push(p);
    }
  }

  get db() { return this.store.data; }

  on(fn) { this.listeners.add(fn); return () => this.listeners.delete(fn); }

  emit(event) {
    for (const fn of this.listeners) { try { fn(event); } catch (e) { /* 单个订阅者异常不影响主循环 */ } }
  }

  /** 用 30 天小时级 + 24 小时 5 分钟级回填历史，保证曲线一打开就有内容 */
  backfill(now = Date.now()) {
    const rand = rng(20261025);
    for (const site of this.db.sites) {
      const points = this.pointsBySite[site.id] || [];
      const st = this.state[site.id];
      for (const p of points) {
        const series = [];
        const start = now - 30 * 86400000;
        // 先小时级铺 30 天（分析页 30 天窗口与逐日曲线需要）
        for (let t = start; t < now - 86400000; t += 3600000) series.push([t, this.histValue(p.code, t, now, st, rand)]);
        // 最近 24 小时 5 分钟级
        for (let t = now - 86400000; t < now - 300000; t += 300000) series.push([t, this.histValue(p.code, t, now, st, rand)]);
        for (const [t, v] of series) this.telemetry.pushHist(p.id, t, v, 'good');
        // 最新值 = 当前状态，保证首屏与告警一致
        const cur = this.currentValue(site.id, p);
        this.telemetry.setLatest(p.id, cur, { t: now, quality: 'good', source: p.source });
      }
    }
    this.log.info?.(`[sim] 已回填 30 天历史（${this.db.points.length} 个点位）`);
  }

  /** 历史曲线：以当前状态为锚的解析序列，事件窗口用于让种子告警在曲线上可解释 */
  histValue(code, t, now, st, rand) {
    const ago = (now - t) / 60000; // 分钟
    const noise = (rand() - 0.5);
    switch (code) {
      case 'RAW.LVL': {
        // 10 小时缓慢充放周期 + 日间波动，避免 2 小时锯齿；最近 2 小时线性降到告警值 22.6
        const base = 46 + 14 * Math.sin((t / 36000000) * Math.PI * 2 - 1.15) + 5 * Math.sin((t / 86400000) * Math.PI * 2);
        if (ago < 120) return round(22.6 + (ago / 120) * 21.4 + noise * 0.4, 1);
        return round(clamp(base + noise * 2, 15, 92), 1);
      }
      case 'RAW.PUMP.RUN': return ago < 18 ? 0 : 1;
      case 'RAW.PUMP.FAULT': return 0;
      case 'RAW.PUMP.MODE': return 0;
      case 'RAW.PUMP.HOURS': return round(st.raw.hours - (ago / 60) * 0.18, 1);
      case 'RAW.PUMP.STARTS': return Math.max(0, st.raw.starts - Math.floor(ago / 180));
      case 'FLOW.IN': return round(clamp(0.1 + noise * 0.012, 0.07, 0.13), 3);
      case 'BAF.PH': {
        if (ago < 25) return round(7.05 + (25 - ago) * 0.0028 + noise * 0.01, 2);
        return round(7.15 + 0.08 * Math.sin((t / 28000000) * Math.PI * 2) + noise * 0.04, 2);
      }
      case 'BAF.DO': {
        if (ago < 12) return round(1.78 + (12 - ago) * 0.1, 2);
        return round(clamp(3.0 + 0.3 * Math.sin((t / 14400000) * Math.PI * 2) + noise * 0.15, 1.4, 6.4), 2);
      }
      case 'BAF.BLOWER.RUN': return ago < 52 * 60 && ago > 50 * 60 ? 0 : 1;
      case 'BAF.BLOWER.CURRENT': return round(1.62 + noise * 0.06, 2);
      case 'BAF.BW.STATE': {
        // 26 小时前安排过反冲洗，用于解释压差回落
        if (ago > 25.9 * 60 && ago < 26.05 * 60) return 1;
        if (ago >= 25.85 * 60 && ago <= 25.9 * 60) return 2;
        return 0;
      }
      case 'BAF.BW.COUNT': return st.baf.bwCount - (ago > 25.9 * 60 ? 1 : 0);
      case 'BAF.DP': {
        if (ago > 25.9 * 60) return round(26.2 + noise * 0.5, 1);
        return round(18.2 + noise * 0.8, 1);
      }
      case 'PAC.PUMP.RUN': return 1;
      case 'PAC.DOSE': return 0.06;
      case 'PAC.LEVEL': {
        if (ago < 30) return round(18.4 + (30 - ago) * 0.02, 1);
        return round(clamp(58 + 32 * Math.sin((t / 12000000) * Math.PI * 2) + noise * 3, 16, 92), 1);
      }
      case 'PAC.TOTAL': return round(st.pac.total - (ago / 60) * 0.06, 1);
      case 'PAC.MIXER.RUN': return 1;
      case 'PAM.PUMP.RUN': return 1;
      case 'PAM.DOSE': return 0.02;
      case 'PAM.LEVEL': return round(clamp(60 + 25 * Math.sin((t / 15000000) * Math.PI * 2) + noise * 3, 15, 92), 1);
      case 'PAM.TOTAL': return round(st.pam.total - (ago / 60) * 0.02, 1);
      case 'PAM.MIXER.RUN': return 1;
      case 'SULFUR.PH': {
        if (ago < 90) return round(6.76 + (90 - ago) * 0.0018 + noise * 0.01, 2);
        return round(7.05 + 0.05 * Math.sin((t / 30000000) * Math.PI * 2) + noise * 0.03, 2);
      }
      case 'SULFUR.DO': return round(clamp(0.32 + 0.04 * Math.sin((t / 10000000) * Math.PI * 2) + noise * 0.02, 0.15, 0.8), 2);
      case 'SULFUR.P': return round(clamp(0.4 + 0.05 * Math.sin((t / 5000000) * Math.PI * 2) + noise * 0.03, 0.25, 0.58), 2);
      case 'SULFUR.BW.STATE': return 0;
      case 'SULFUR.BW.COUNT': return st.sulfur.bwCount;
      case 'SULFUR.DP': return round(16.4 + noise * 0.8, 1);
      case 'CLEAR.LVL': return round(clamp(58 + 6 * Math.sin((t / 20000000) * Math.PI * 2) + noise * 2, 20, 95), 1);
      case 'WASTE.LVL': return round(clamp(40 + 18 * Math.sin((t / 18000000) * Math.PI * 2) + noise * 3, 8, 80), 1);
      case 'POWER.KW': return round(clamp(1.3 + 0.18 * Math.sin((t / 16000000) * Math.PI * 2) + noise * 0.05, 0.9, 1.8), 2);
      case 'ENERGY.KWH': return round(st.power.kwh - (ago / 60) * 1.38, 1);
      default: return 0;
    }
  }

  /** 当前点位值（由工艺状态派生） */
  currentValue(siteId, point) {
    const s = this.state[siteId];
    if (!s) return null;
    const map = {
      'RAW.LVL': round(s.raw.level, 1),
      'RAW.PUMP.RUN': s.raw.pumpRun ? 1 : 0,
      'RAW.PUMP.FAULT': s.raw.fault ? 1 : 0,
      'RAW.PUMP.MODE': s.raw.pumpMode,
      'RAW.PUMP.HOURS': round(s.raw.hours, 1),
      'RAW.PUMP.STARTS': s.raw.starts,
      'FLOW.IN': s.raw.pumpRun ? round(0.1 + Math.sin(Date.now() / 4000) * 0.003, 3) : 0,
      'BAF.PH': round(s.baf.ph, 2),
      'BAF.DO': round(s.baf.do, 2),
      'BAF.BLOWER.RUN': s.baf.blower ? 1 : 0,
      'BAF.BLOWER.CURRENT': s.baf.blower ? 1.62 : 0,
      'BAF.BW.STATE': s.baf.bwPhase,
      'BAF.BW.COUNT': s.baf.bwCount,
      'BAF.DP': round(s.baf.dp, 1),
      'PAC.PUMP.RUN': s.pac.run ? 1 : 0,
      'PAC.DOSE': round(s.pac.dose, 2),
      'PAC.LEVEL': round(s.pac.level, 1),
      'PAC.TOTAL': round(s.pac.total, 1),
      'PAC.MIXER.RUN': s.pac.mixer ? 1 : 0,
      'PAM.PUMP.RUN': s.pam.run ? 1 : 0,
      'PAM.DOSE': round(s.pam.dose, 2),
      'PAM.LEVEL': round(s.pam.level, 1),
      'PAM.TOTAL': round(s.pam.total, 1),
      'PAM.MIXER.RUN': s.pam.mixer ? 1 : 0,
      'SULFUR.PH': round(s.sulfur.ph, 2),
      'SULFUR.DO': round(s.sulfur.do, 2),
      'SULFUR.P': round(s.sulfur.p, 2),
      'SULFUR.BW.STATE': s.sulfur.bwPhase,
      'SULFUR.BW.COUNT': s.sulfur.bwCount,
      'SULFUR.DP': round(s.sulfur.dp, 1),
      'CLEAR.LVL': round(s.clear.level, 1),
      'WASTE.LVL': round(s.waste.level, 1),
      'POWER.KW': round(s.power.kw, 2),
      'ENERGY.KWH': round(s.power.kwh, 1),
    };
    return Object.prototype.hasOwnProperty.call(map, point.code) ? map[point.code] : null;
  }

  start() {
    if (this.timer) return;
    this.backfill();
    this.writeTelemetry(Date.now());
    this.timer = setInterval(() => this.tick(), TICK_MS);
    if (this.timer.unref) this.timer.unref();
    this.log.info?.('[sim] 模拟网关已启动，采样周期 5 s');
  }

  stop() {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  tick(dtSec = TICK_MS / 1000) {
    const now = Date.now();
    const updates = [];
    for (const site of this.db.sites) {
      const s = this.state[site.id];
      if (!s) continue;
      this.stepSite(site.id, s, dtSec, now);
      for (const p of this.pointsBySite[site.id] || []) {
        const v = this.currentValue(site.id, p);
        if (v == null) continue;
        this.telemetry.setLatest(p.id, v, { t: now, quality: 'good', source: p.source });
        updates.push({ pointId: p.id, siteId: site.id, code: p.code, v, t: now });
        this.telemetry.record(p.id, v, 'good', now);
      }
    }
    this.emit({ type: 'telemetry', t: now, updates });
  }

  stepSite(siteId, s, dt, now) {
    // —— 原料罐与提升泵 ——
    const r = s.raw;
    const wasRun = r.pumpRun;
    if (r.pumpRun) r.level -= 0.012 * dt;
    // 补水阀：低液位自动补水，到 70% 关闭
    if (r.level < 25) r.refill = true;
    if (r.refill && r.level >= 70) r.refill = false;
    if (r.refill && !r.pumpRun) r.level += 0.03 * dt;
    if (r.level <= 20 && r.pumpRun) { r.pumpRun = false; r.trip = now; }
    if (!r.pumpRun && !r.refill && r.level >= 30 && r.pumpMode === 0 && !r.fault) r.pumpRun = true;
    r.level = clamp(r.level, 0, 100);
    if (r.pumpRun) r.hours += dt / 3600;
    if (!wasRun && r.pumpRun) r.starts++;
    if (r.pumpRun) r.starts = Math.max(r.starts, 1);

    // —— BAF ——
    const b = s.baf;
    if (b.bwPhase) {
      this.stepBackwash(b, dt, now, () => { b.bwCount++; b.bwPhase = 0; b.dp = Math.max(8, b.dp - 12); });
      b.do = clamp(b.do + 0.02 * dt, 0, 12);
    } else {
      const target = b.blower ? 3.0 : 0.6;
      b.do += (target - b.do) * 0.012 * dt;
      b.do = clamp(b.do + (Math.random() - 0.5) * 0.01, 0, 12);
      b.ph += (7.15 - b.ph) * 0.0008 * dt + (Math.random() - 0.5) * 0.002;
      b.ph = clamp(b.ph, 4, 10);
      b.dp = clamp(b.dp + 0.002 * dt, 2, 45);
      if (b.dp > 24 && now - (b.bwAt || 0) > 20 * 3600000) { this.startBackwash(siteId, 'baf'); }
    }
    // —— PAC / PAM ——
    for (const [key, max] of [['pac', 0.2], ['pam', 0.1]]) {
      const d = s[key];
      if (d.run) {
        d.level -= d.dose * 0.006 * dt;
        d.total += d.dose * dt / 3600;
        if (d.level <= 3) d.level = 3;
      }
      d.level = clamp(d.level, 0, 100);
      d.dose = clamp(d.dose, 0, max);
    }
    // —— 硫自养池 ——
    const su = s.sulfur;
    if (su.bwPhase) {
      this.stepBackwash(su, dt, now, () => { su.bwCount++; su.bwPhase = 0; su.dp = Math.max(6, su.dp - 10); su.p = 0.4; });
      su.p = clamp(su.p + 0.01 * dt, 0.2, 0.85);
    } else {
      su.ph += (7.05 - su.ph) * 0.0006 * dt + (Math.random() - 0.5) * 0.0015;
      su.ph = clamp(su.ph, 4, 10);
      su.do += (0.32 - su.do) * 0.02 * dt + (Math.random() - 0.5) * 0.004;
      su.do = clamp(su.do, 0, 3);
      su.p = clamp(0.4 + (Math.random() - 0.5) * 0.008, 0.25, 0.6);
      su.dp = clamp(su.dp + 0.0015 * dt, 2, 40);
    }
    // —— 液位与能耗 ——
    s.clear.level = clamp(s.clear.level + (s.raw.pumpRun ? 0.004 : -0.002) * dt, 0, 100);
    s.waste.level = clamp(s.waste.level + (s.baf.bwPhase ? 0.02 : -0.003) * dt, 0, 100);
    const baseKw = 0.55 + (s.baf.blower ? 0.38 : 0) + (s.pac.run ? 0.12 : 0) + (s.pam.run ? 0.1 : 0);
    const bwKw = (s.baf.bwPhase || s.sulfur.bwPhase) ? 0.35 : 0;
    s.power.kw = clamp(baseKw + bwKw + (Math.random() - 0.5) * 0.04, 0.3, 5);
    s.power.kwh += s.power.kw * dt / 3600;
  }

  stepBackwash(unit, dt, now, done) {
    if (!unit.bwPhase) return;
    if (!unit.bwRemain) unit.bwRemain = BW_PHASES[unit.bwPhase - 1].sec;
    unit.bwRemain -= dt;
    if (unit.bwRemain <= 0) {
      if (unit.bwPhase < 3) {
        unit.bwPhase += 1;
        unit.bwRemain = BW_PHASES[unit.bwPhase - 1].sec;
      } else {
        unit.bwRemain = 0;
        done();
      }
    }
  }

  startBackwash(siteId, unitCode) {
    const s = this.state[siteId];
    if (!s) return { ok: false, error: '站点不存在' };
    const unit = unitCode === 'sulfur' ? s.sulfur : s.baf;
    if (unit.bwPhase) return { ok: false, error: '反冲洗正在进行中' };
    if (s.waste.level >= 92) return { ok: false, error: '废水箱液位过高，排水阀联锁禁止启动反冲洗' };
    unit.bwPhase = 1;
    unit.bwRemain = BW_PHASES[0].sec;
    unit.bwAt = Date.now();
    if (unitCode === 'sulfur') { unit.p = 0.55; }
    return { ok: true, detail: `${unitCode === 'sulfur' ? '硫自养池' : 'BAF'} 反冲洗已启动：气冲 → 气水联合 → 漂洗` };
  }

  stopBackwash(siteId, unitCode) {
    const s = this.state[siteId];
    const unit = unitCode === 'sulfur' ? s.sulfur : s.baf;
    if (!unit.bwPhase) return { ok: false, error: '当前没有进行中的反冲洗' };
    unit.bwPhase = 0;
    unit.bwRemain = 0;
    return { ok: true, detail: '反冲洗已停止（现场确认后执行）' };
  }

  /** 命令执行前的联锁校验 */
  precheck(siteId, action, args = {}) {
    const s = this.state[siteId];
    if (!s) return { ok: false, error: '站点不存在' };
    switch (action) {
      case 'raw.pump.start':
        if (s.raw.level < 5) return { ok: false, error: '原料罐液位低于 5%，低液位保护禁止启泵' };
        if (s.raw.fault) return { ok: false, error: '提升泵故障信号存在，禁止启动' };
        return { ok: true };
      case 'raw.pump.stop': return { ok: true };
      case 'raw.pump.mode.set':
        if (![0, 1].includes(Number(args.mode))) return { ok: false, error: '模式值必须是 0（自动）或 1（手动）' };
        return { ok: true, warning: Number(args.mode) === 1 ? '切换手动模式需现场确认，平台将记录审计' : null };
      case 'baf.blower.start': return { ok: true };
      case 'baf.blower.stop':
        if (s.baf.do < 2.5) return { ok: false, error: `当前溶解氧 ${s.baf.do.toFixed(2)} mg/L，DO 联锁禁止停曝气风机` };
        return { ok: true, warning: '停风机将影响硝化效果，请确认工艺允许' };
      case 'baf.backwash.start':
      case 'sulfur.backwash.start': {
        const u = action.startsWith('sulfur') ? s.sulfur : s.baf;
        if (u.bwPhase) return { ok: false, error: '反冲洗正在进行中' };
        if (s.waste.level >= 92) return { ok: false, error: '废水箱液位过高，排水阀联锁禁止启动' };
        return { ok: true, warning: '反冲洗期间出水水质可能短时波动，请确认下游可接受' };
      }
      case 'pac.dose.set':
      case 'pam.dose.set': {
        const v = Number(args.value);
        const max = action.startsWith('pac') ? 0.2 : 0.1;
        if (!Number.isFinite(v) || v < 0 || v > max) return { ok: false, error: `投加量须在 0–${max} L/h 之间` };
        return { ok: true };
      }
      default: return { ok: false, error: `未知控制动作：${action}` };
    }
  }

  execute(siteId, action, args = {}) {
    const pre = this.precheck(siteId, action, args);
    if (!pre.ok) return pre;
    const s = this.state[siteId];
    switch (action) {
      case 'raw.pump.start': s.raw.pumpRun = true; break;
      case 'raw.pump.stop': s.raw.pumpRun = false; break;
      case 'raw.pump.mode.set': s.raw.pumpMode = Number(args.mode); break;
      case 'baf.blower.start': s.baf.blower = true; break;
      case 'baf.blower.stop': s.baf.blower = false; break;
      case 'baf.backwash.start': return this.startBackwash(siteId, 'baf');
      case 'sulfur.backwash.start': return this.startBackwash(siteId, 'sulfur');
      case 'baf.backwash.stop': return this.stopBackwash(siteId, 'baf');
      case 'sulfur.backwash.stop': return this.stopBackwash(siteId, 'sulfur');
      case 'pac.dose.set': s.pac.dose = Number(args.value); break;
      case 'pam.dose.set': s.pam.dose = Number(args.value); break;
      default: return { ok: false, error: '未知控制动作' };
    }
    return { ok: true, detail: '已执行', warning: pre.warning };
  }

  writeTelemetry(now) {
    for (const site of this.db.sites) {
      for (const p of this.pointsBySite[site.id] || []) {
        const v = this.currentValue(site.id, p);
        if (v != null) this.telemetry.setLatest(p.id, v, { t: now, quality: 'good', source: p.source });
      }
    }
  }

  status(siteId) {
    const s = this.state[siteId];
    if (!s) return null;
    return {
      raw: { ...s.raw },
      baf: { ...s.baf },
      pac: { ...s.pac },
      pam: { ...s.pam },
      sulfur: { ...s.sulfur },
      clear: { ...s.clear },
      waste: { ...s.waste },
      power: { ...s.power },
    };
  }
}

module.exports = { Simulator, BW_PHASES, TICK_MS };

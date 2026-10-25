'use strict';
/**
 * 智能诊断（方案第 6.5 节）：现象 → 可能原因 → 检查项 → 处置边界。
 * 只依据当前可解释的数据给建议，不替代工艺判断；每条结论都附带触发依据与数据时间。
 */
const { localTime } = require('./util');

function trendText(series) {
  if (!series || series.length < 2) return '数据点不足，暂无法判断趋势';
  const first = series[0].v;
  const last = series[series.length - 1].v;
  const delta = last - first;
  const pct = first ? (delta / Math.abs(first)) * 100 : 0;
  if (Math.abs(pct) < 1) return `近 ${series.length} 个采样点基本平稳（${first} → ${last}）`;
  return `近 ${series.length} 个采样点${delta > 0 ? '上升' : '下降'} ${Math.abs(delta).toFixed(2)}（${first} → ${last}）`;
}

class Diagnosis {
  constructor({ store, telemetry }) {
    this.store = store;
    this.telemetry = telemetry;
  }

  get db() { return this.store.data; }

  point(siteId, code) {
    return this.db.points.find(p => p.siteId === siteId && p.code === code) || null;
  }

  reading(siteId, code) {
    const p = this.point(siteId, code);
    if (!p) return null;
    const l = this.telemetry.getLatest(p.id);
    if (!l) return null;
    return { point: p, value: l.v, t: l.t, time: localTime(l.t), q: l.q };
  }

  series(siteId, code, minutes = 60) {
    const p = this.point(siteId, code);
    if (!p) return [];
    const now = Date.now();
    return (this.telemetry.range([p.id], now - minutes * 60000, now, 300, { [p.id]: p.type })[p.id] || []);
  }

  related(siteId, codes) {
    return codes.map(c => this.reading(siteId, c)).filter(Boolean).map(r => ({
      code: r.point.code, name: r.point.name, value: r.value, unit: r.point.unit,
      time: r.time, quality: r.q,
    }));
  }

  list(siteId) {
    const out = [];
    const push = (d) => { if (d) out.push({ ...d, id: `DG-${out.length + 1}`, siteId, generatedAt: localTime() }); };
    const site = this.db.sites.find(s => s.id === siteId);
    if (!site) return [];

    // 1. 硫自养池 pH 偏低
    const sp = this.reading(siteId, 'SULFUR.PH');
    if (sp && sp.value < 6.9) {
      push({
        level: sp.value < 6.6 ? 'high' : 'medium',
        title: '硫自养池 pH 偏低，脱氮效率存在下降风险',
        phenomenon: `硫自养池 pH 当前 ${sp.value}，低于运行区间下限 6.8（数据时间 ${sp.time}）。`,
        causes: [
          '进水碱度不足，硝化与反硝化消耗后缓冲能力下降',
          '硫源（单质硫/硫代硫酸盐）投加偏多，产酸超过系统缓冲能力',
          '反冲洗后水力停留时间变化，短时 pH 波动',
          '在线 pH 计未按期校准，示值偏低',
        ],
        checks: [
          { item: '核对 pH 计状态', how: '查看最近一次校准记录，用 6.86 标准液核查；便携表与在线表差值应 ≤ 0.1 pH', expect: '偏差 ≤ 0.1 pH' },
          { item: '检测进水碱度', how: '取原料罐水样，滴定法测碱度（以 CaCO₃ 计）', expect: '结合工艺要求核算，必要时补充碱度' },
          { item: '核算硫源投配比', how: '按近 24 h 进水 TN 与硫源消耗量复核投配比，单次调整不超过 10%', expect: '与设计投配比一致' },
          { item: '复核反冲洗记录', how: '查看近 2 h 反冲洗阶段与时长，确认未长时间停留在冲洗态', expect: '反冲洗按时结束' },
        ],
        boundary: '若 pH < 6.5 且持续 30 分钟，停止进水并投加碱度调节；pH < 6.0 立即停进水、停硫源，待化验确认后再恢复。',
        basis: {
          points: this.related(siteId, ['SULFUR.PH', 'SULFUR.DO', 'SULFUR.P']),
          trend: trendText(this.series(siteId, 'SULFUR.PH', 60)),
          rule: '规则 rule-1：工作区间 6.8–7.6 pH，持续 300 s 触发重要告警',
        },
        suggestion: '先确认仪表可信，再核查碱度与硫源；不要在未查明原因时大幅调整加药。',
      });
    }

    // 2. BAF 溶解氧不足
    const bd = this.reading(siteId, 'BAF.DO');
    if (bd && bd.value < 2.2) {
      const blower = this.reading(siteId, 'BAF.BLOWER.RUN');
      push({
        level: bd.value < 1.2 ? 'high' : 'medium',
        title: 'BAF 溶解氧不足，硝化过程可能受抑制',
        phenomenon: `BAF 溶解氧当前 ${bd.value} mg/L，低于 2.0 mg/L 的告警线（数据时间 ${bd.time}）。`,
        causes: [
          blower && Number(blower.value) === 1
            ? '曝气风机运行但风量不足：曝气头堵塞、支管阀门开度偏小或风机皮带打滑'
            : '曝气风机未运行或运行反馈丢失',
          '进水负荷短时升高，耗氧速率超过供氧能力',
          'DO 探头污染或膜片老化，读数偏低',
          '反冲洗后滤料层扰动，短时氧传递效率下降',
        ],
        checks: [
          { item: '确认风机与阀门状态', how: '查看风机运行反馈、出口压力与曝气支管阀门开度', expect: '风机运行、阀门开度与工艺卡一致' },
          { item: '核对风量与电流', how: '比较风机电流与历史正常值，偏差超过 10% 需现场检查', expect: '电流在正常范围' },
          { item: '检查 DO 探头', how: '取出探头观察膜片与电解液，必要时用空气饱和法核查', expect: '空气中读数接近饱和值' },
          { item: '复测进水负荷', how: '查看近 2 h 进水流量与人工化验 COD/氨氮', expect: '与设计负荷偏离不超过 20%' },
        ],
        boundary: 'DO < 1.0 mg/L 持续 15 分钟，应降低进水量并增开曝气；DO < 0.5 mg/L 立即停止进水，防止滤池厌氧与堵塞。',
        basis: {
          points: this.related(siteId, ['BAF.DO', 'BAF.BLOWER.RUN', 'BAF.BLOWER.CURRENT', 'FLOW.IN']),
          trend: trendText(this.series(siteId, 'BAF.DO', 60)),
          rule: '规则 rule-3：DO < 2.0 mg/L 持续 240 s 触发重要告警',
        },
        suggestion: '不要直接大幅提高风机频率；先确认曝气头与阀门，再按 5% 步长调整。',
      });
    }

    // 3. 原料罐液位偏低
    const rl = this.reading(siteId, 'RAW.LVL');
    if (rl && rl.value < 25) {
      const pump = this.reading(siteId, 'RAW.PUMP.RUN');
      push({
        level: rl.value < 12 ? 'high' : 'medium',
        title: '原料罐液位偏低，提升泵存在低液位保护停机风险',
        phenomenon: `原料罐液位 ${rl.value} %，低于提醒值 25 %（数据时间 ${rl.time}）；提升泵${pump && Number(pump.value) === 1 ? '运行中' : '当前停止'}。`,
        causes: [
          '进水/补水未及时开启或补水管路流量不足',
          '提升泵流量大于补水流量，液位持续下降',
          '液位计漂移或卡滞，实际液位高于示值',
          '罐体或管路渗漏',
        ],
        checks: [
          { item: '现场核对液位', how: '与罐体磁翻板/标尺比对，偏差 > 2 % 需重新标定', expect: '偏差 ≤ 2 %' },
          { item: '检查补水阀与管路', how: '确认补水阀开度、过滤器是否堵塞', expect: '补水流量满足 0.1 m³/h' },
          { item: '核算进出水平衡', how: '对比近 1 h 提升泵运行时长与补水记录', expect: '液位可维持 30–80 %' },
        ],
        boundary: '液位 ≤ 20 % 时低液位保护自动停泵；≤ 10 % 必须停止进水并人工补水，禁止短接保护强行启泵。',
        basis: {
          points: this.related(siteId, ['RAW.LVL', 'RAW.PUMP.RUN', 'RAW.PUMP.MODE']),
          trend: trendText(this.series(siteId, 'RAW.LVL', 60)),
          rule: '规则 rule-6：液位 < 25 % 持续 120 s 触发；20 % 联锁停泵',
        },
        suggestion: '优先现场核对液位，再确认补水；恢复后观察一个完整补水周期。',
      });
    }

    // 4. PAC / PAM 药箱液位
    for (const [code, name, param] of [['PAC.LEVEL', 'PAC', 'pac.dose'], ['PAM.LEVEL', 'PAM', 'pam.dose']]) {
      const d = this.reading(siteId, code);
      if (d && d.value < 25) {
        push({
          level: 'low',
          title: `${name} 药箱液位偏低，需安排配药`,
          phenomenon: `${name} 药箱液位 ${d.value} %，低于 25 %（数据时间 ${d.time}）。`,
          causes: ['按计划配药周期已到', '计量泵流量偏大或药箱存在渗漏', '液位计示值偏差'],
          checks: [
            { item: '现场核对药箱液位', how: '与标尺比对并检查箱体渗漏', expect: '偏差 ≤ 3 %' },
            { item: '核对投加量与配药记录', how: '按 24 h 累计投加量核算剩余可用时间', expect: '剩余 ≥ 8 h' },
          ],
          boundary: `液位 ≤ 10 % 时停加药泵，避免空转损坏；恢复后按 ${param} 设定值重新校准流量。`,
          basis: { points: this.related(siteId, [code, 'PAC.TOTAL', 'PAM.TOTAL'].filter(c => this.point(siteId, c))), trend: trendText(this.series(siteId, code, 120)), rule: '规则 rule-8 / rule-9：药箱液位 < 20 % 提醒' },
          suggestion: '配药后记录批次与浓度，并核对计量泵冲程。',
        });
      }
    }

    // 5. 滤池压差偏高
    const dp = this.reading(siteId, 'BAF.DP');
    if (dp && dp.value > 22) {
      push({
        level: 'medium',
        title: 'BAF 滤池压差偏高，建议安排反冲洗',
        phenomenon: `BAF 压差 ${dp.value} kPa，接近 25 kPa 提醒值（数据时间 ${dp.time}）。`,
        causes: ['滤层截留悬浮物趋于饱和', '反冲洗周期设置偏长或反冲洗强度不足', '进水 SS 升高', '压差变送器零点漂移'],
        checks: [
          { item: '核对压差与反冲洗记录', how: '查看上次反冲洗时间、时长与压差恢复情况', expect: '反冲洗后压差下降 ≥ 30 %' },
          { item: '检查进水 SS', how: '查看最近一次人工化验 SS 与近期趋势', expect: '在设计范围内' },
          { item: '检查变送器引压管', how: '确认引压管无堵塞、无气泡', expect: '两侧压力显示正常' },
        ],
        boundary: '压差 > 30 kPa 或出水 SS 明显升高时必须反冲洗；反冲洗后压差仍不下降，安排检查滤料板结。',
        basis: { points: this.related(siteId, ['BAF.DP', 'BAF.BW.COUNT']), trend: trendText(this.series(siteId, 'BAF.DP', 120)), rule: '规则 rule-15：压差 > 25 kPa 持续 300 s 提醒' },
        suggestion: '反冲洗按「先气冲、后气水联合」执行，观察排水浊度变化。',
      });
    }

    // 6. 流量异常
    const flow = this.reading(siteId, 'FLOW.IN');
    const pump = this.reading(siteId, 'RAW.PUMP.RUN');
    if (flow && pump && Number(pump.value) === 1 && flow.value < 0.02) {
      push({
        level: 'high',
        title: '提升泵运行但进水流量接近零',
        phenomenon: `提升泵运行中，进水流量 ${flow.value} m³/h（数据时间 ${flow.time}）。`,
        causes: ['进水管路堵塞或阀门误关', '泵体气蚀/空转，未建立有效流量', '流量计故障或信号丢失', '原料罐液位过低导致吸空'],
        checks: [
          { item: '检查管路与阀门', how: '确认进水阀、过滤器与管路无堵塞', expect: '阀门开度正确、管路通畅' },
          { item: '检查泵体', how: '听泵体声音、查看出口压力，必要时排气', expect: '无气蚀异响、出口压力正常' },
          { item: '核对流量计', how: '用便携超声流量计或容积法比对', expect: '偏差 ≤ 5 %' },
        ],
        boundary: '确认泵空转时立即停泵，禁止干运转超过 30 秒；管路堵塞未排除前不得反复启泵。',
        basis: { points: this.related(siteId, ['FLOW.IN', 'RAW.PUMP.RUN', 'RAW.LVL']), trend: trendText(this.series(siteId, 'FLOW.IN', 30)), rule: '规则 rule-14：提升泵运行且流量 < 0.02 m³/h 持续 300 s' },
        suggestion: '按「先停泵保护、再查管路、最后核对仪表」的顺序处理。',
      });
    }

    // 7. 数据时效
    const all = this.db.points.filter(p => p.siteId === siteId);
    const stale = all.filter(p => {
      const l = this.telemetry.getLatest(p.id);
      return !l || Date.now() - l.t > 60000;
    });
    if (stale.length) {
      push({
        level: stale.length > all.length / 3 ? 'high' : 'low',
        title: `${stale.length} 个点位数据超过 60 秒未更新`,
        phenomenon: `站点共 ${all.length} 个点位，其中 ${stale.length} 个最新数据时间超过 60 秒（检查时间 ${localTime()}）。`,
        causes: ['边缘网关离线或网络中断', '仪表通讯故障（RS-485 接线/地址冲突）', '采集任务异常'],
        checks: [
          { item: '查看网关在线状态', how: '在设备管理页确认网关与 PLC 在线状态', expect: '在线且心跳正常' },
          { item: '检查仪表通讯', how: '现场查看仪表通讯指示灯与 485 接线', expect: '通讯灯闪烁正常' },
        ],
        boundary: '数据中断超过 5 分钟必须记录并通知值班；恢复后核对补传数据完整性。',
        basis: { points: stale.slice(0, 5).map(p => ({ code: p.code, name: p.name, value: null, unit: p.unit, time: '—', quality: 'stale' })), trend: '数据中断', rule: '数据时效约束（方案第 8 章）' },
        suggestion: '先确认网关供电与网络，再逐台排查仪表。',
      });
    }

    if (!out.length) {
      out.push({
        id: 'DG-1', siteId, generatedAt: localTime(),
        level: 'none',
        title: '当前未发现需要处置的异常',
        phenomenon: '已核对 pH、DO、液位、流量、压差与数据时效，未见超过诊断阈值的组合。',
        causes: [],
        checks: [],
        boundary: '保持每班巡检；如出现感官异常或化验超标，请以人工化验结果为准发起诊断。',
        basis: { points: this.related(siteId, ['BAF.PH', 'BAF.DO', 'SULFUR.PH', 'SULFUR.DO', 'RAW.LVL']), trend: '各参数在运行区间内', rule: '诊断规则库 v1（演示）' },
        suggestion: '继续观察趋势，不建议在无依据时调整工艺参数。',
      });
    }
    return out;
  }
}

module.exports = { Diagnosis };

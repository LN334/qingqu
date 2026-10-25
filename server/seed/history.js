'use strict';
/**
 * 种子数据（三）：历史控制命令与报表记录。
 * 目的：让远程控制中心与报表中心首次打开就有可追溯的历史，而不是空表。
 * 命令状态与回执与真实状态机一致（待确认/待下发/已下发/执行中/成功/失败/超时/取消）。
 */
const M = 60000;
const H = 3600000;
const D = 86400000;

function seedHistory(now, assets) {
  const { devices, users } = assets;
  const iso = t => new Date(t).toISOString();
  const site1 = 'site-1';
  const dev = key => devices.find(d => d.siteId === site1 && d.key === key) || {};
  const user = id => users.find(u => u.id === id) || {};
  const target = key => {
    const d = dev(key);
    return { id: d.id || null, name: d ? `${d.name}（${d.code}）` : '' };
  };

  const rawPump = target('raw-pump');
  const bafBlower = target('baf-blower');
  const bafBw = target('baf-bw-pump');
  const pacPump = target('pac-pump');
  const pamPump = target('pam-pump');
  const sulfurBw = target('sulfur-bw-pump');

  /** 构造一条历史命令；timeline 按状态机顺序补齐 */
  function cmd({
    id, action, actionLabel, risk, target: tgt, args, reason, status,
    createdBy, ago, confirmed = false, failReason = null, result = '', feedback = null,
  }) {
    const at = now - ago;
    const u = user(createdBy);
    const timeline = [{ at: iso(at), status: 'pending_confirm', by: u.name, note: '命令已创建，等待二次确认' }];
    let t = at + 20 * 1000;
    if (confirmed || risk !== 'high') {
      timeline.push({ at: iso(t), status: 'pending_dispatch', by: u.name, note: '二次确认通过，进入下发队列' });
      t += 1 * 1000;
      timeline.push({ at: iso(t), status: 'sent', by: '内置模拟网关', note: '网关已接收，等待 PLC 回执' });
      t += 1 * 1000;
    }
    if (status === 'failed' || status === 'timeout' || status === 'success') {
      timeline.push({ at: iso(t), status: 'executing', by: 'PLC', note: 'PLC 已执行，等待结果回执' });
    }
    if (status === 'success') {
      timeline.push({ at: iso(t + 3 * 1000), status: 'success', by: 'PLC', note: feedback || '执行完成，反馈正常' });
    } else if (status === 'failed') {
      timeline.push({ at: iso(t + 2 * 1000), status: 'failed', by: 'PLC', note: failReason || '执行失败' });
    } else if (status === 'timeout') {
      timeline.push({ at: iso(t + 30 * 1000), status: 'timeout', by: '平台', note: '30 秒内未收到 PLC 回执，已置为超时' });
    } else if (status === 'cancelled') {
      timeline.push({ at: iso(at + 40 * 1000), status: 'cancelled', by: u.name, note: '操作人取消，命令未下发' });
    } else if (status === 'executing') {
      // 保持执行中
    }
    return {
      id, siteId: site1, action, actionLabel,
      targetName: tgt.name, deviceId: tgt.id, args, risk,
      reason, status,
      failReason: status === 'failed' ? (failReason || '执行失败') : null,
      warning: null,
      createdBy, createdByName: u.name,
      createdAt: iso(at),
      confirmedAt: (confirmed || risk !== 'high') ? iso(at + 20 * 1000) : null,
      sentAt: (confirmed || risk !== 'high') ? iso(at + 21 * 1000) : null,
      executingAt: ['success', 'failed', 'timeout'].includes(status) ? iso(at + 22 * 1000) : null,
      finishedAt: status === 'success' ? iso(at + 25 * 1000)
        : status === 'failed' ? iso(at + 24 * 1000)
          : status === 'timeout' ? iso(at + 52 * 1000)
            : status === 'cancelled' ? iso(at + 40 * 1000) : null,
      result: status === 'success' ? (feedback || '执行成功') : status === 'failed' ? (failReason || '执行失败') : status === 'timeout' ? '等待回执超时' : status === 'cancelled' ? '已取消，未下发' : '',
      feedback: status === 'success' ? (feedback || '执行成功') : null,
      timeline,
    };
  }

  const commands = [
    cmd({
      id: 'CMD-20261025-001', action: 'baf.backwash.start', actionLabel: '启动 BAF 反冲洗', risk: 'high',
      target: bafBw, args: {}, status: 'success', createdBy: 'u-4', ago: 95 * M, confirmed: true,
      reason: 'BAF 压差升至 26.2 kPa，按规程执行气冲→气水联合→漂洗。',
      feedback: '反冲洗完成，压差由 26.2 kPa 降至 13.8 kPa，阶段用时 165 s。',
    }),
    cmd({
      id: 'CMD-20261025-002', action: 'raw.pump.mode.set', actionLabel: '切换提升泵运行模式', risk: 'high',
      target: rawPump, args: { mode: 0 }, status: 'success', createdBy: 'u-1', ago: 26 * M, confirmed: true,
      reason: '现场检修结束，恢复自动运行模式并核对联锁。',
      feedback: '模式已切换为自动，液位联锁正常。',
    }),
    cmd({
      id: 'CMD-20261025-003', action: 'pac.dose.set', actionLabel: '调整 PAC 投加量', risk: 'medium',
      target: pacPump, args: { value: 0.06 }, status: 'success', createdBy: 'u-3', ago: 3 * H,
      reason: '进水浊度稳定、出水 COD 32 mg/L，按小试结果将 PAC 投加量复核为 0.06 L/h。',
      feedback: '投加量已更新，计量泵反馈 0.06 L/h。',
    }),
    cmd({
      id: 'CMD-20261025-004', action: 'pam.dose.set', actionLabel: '调整 PAM 投加量', risk: 'medium',
      target: pamPump, args: { value: 0.02 }, status: 'success', createdBy: 'u-3', ago: 5 * H,
      reason: '按助凝效果微调 PAM，单次调整幅度 20% 以内。',
      feedback: '投加量已更新，搅拌与计量泵运行正常。',
    }),
    cmd({
      id: 'CMD-20261024-005', action: 'baf.blower.stop', actionLabel: '停止 BAF 曝气风机', risk: 'high',
      target: bafBlower, args: {}, status: 'failed', createdBy: 'u-4', ago: 20 * H - 30 * M, confirmed: true,
      reason: '计划检修风机皮带，拟短时停风机。',
      failReason: 'DO 联锁：BAF 溶解氧 1.78 mg/L 低于 2.5 mg/L，禁止停风机（平台拒绝执行）。',
      feedback: null,
    }),
    cmd({
      id: 'CMD-20261024-006', action: 'sulfur.backwash.start', actionLabel: '启动硫自养池反冲洗', risk: 'high',
      target: sulfurBw, args: {}, status: 'cancelled', createdBy: 'u-4', ago: 25 * H,
      reason: '按反冲洗周期准备执行，等待排水阀反馈后确认。',
      feedback: null,
    }),
    cmd({
      id: 'CMD-20261023-007', action: 'sulfur.backwash.start', actionLabel: '启动硫自养池反冲洗', risk: 'high',
      target: sulfurBw, args: {}, status: 'success', createdBy: 'u-4', ago: 2 * D + 4 * H, confirmed: true,
      reason: '硫自养池压差 18.9 kPa，按周期执行反冲洗。',
      feedback: '反冲洗完成，压差降至 9.6 kPa，压力 0.42 MPa。',
    }),
    cmd({
      id: 'CMD-20261023-008', action: 'baf.backwash.start', actionLabel: '启动 BAF 反冲洗', risk: 'high',
      target: bafBw, args: {}, status: 'timeout', createdBy: 'u-4', ago: 2 * D + 10 * H, confirmed: true,
      reason: '压差超限，执行反冲洗。',
      failReason: null,
      feedback: null,
    }),
    cmd({
      id: 'CMD-20261022-009', action: 'pac.dose.set', actionLabel: '调整 PAC 投加量', risk: 'medium',
      target: pacPump, args: { value: 0.08 }, status: 'success', createdBy: 'u-3', ago: 3 * D,
      reason: '进水浊度升高，临时上调 PAC 投加量。',
      feedback: '投加量已调整为 0.08 L/h。',
    }),
    // 当前挂起的高风险命令：等待确认，用于演示二次确认流程
    cmd({
      id: 'CMD-20261025-010', action: 'baf.backwash.start', actionLabel: '启动 BAF 反冲洗', risk: 'high',
      target: bafBw, args: {}, status: 'pending_confirm', createdBy: 'u-4', ago: 4 * M,
      reason: 'BAF 压差 26.0 kPa 超限，等待工艺确认后下发。',
      feedback: null,
    }),
  ];

  /* ---------------- 报表记录（sections 由接口按区间实时生成） ---------------- */
  const reports = [
    {
      id: 'RP-20261025-001', siteId: site1, type: 'daily',
      title: '2026-10-25 运行日报',
      periodFrom: iso(now - 12 * H), periodTo: iso(now),
      createdBy: 'u-3', createdAt: iso(now - 2 * H), status: 'ready', format: 'csv',
      note: '当班运行日报，含水量、能耗、水质与告警摘要。',
    },
    {
      id: 'RP-20261023-002', siteId: site1, type: 'weekly',
      title: '2026-10-18 至 2026-10-24 运行周报',
      periodFrom: iso(now - 8 * D), periodTo: iso(now - 1 * D),
      createdBy: 'u-1', createdAt: iso(now - 1 * D - 3 * H), status: 'ready', format: 'csv',
      note: '周报：用于工艺例会与运维复盘。',
    },
    {
      id: 'RP-20260930-003', siteId: site1, type: 'monthly',
      title: '2026-09 运行月报',
      periodFrom: iso(now - 56 * D), periodTo: iso(now - 26 * D),
      createdBy: 'u-2', createdAt: iso(now - 25 * D), status: 'ready', format: 'csv',
      note: '月度汇总：水量、能耗、药耗与达标率。',
    },
  ];

  return { commands, reports };
}

module.exports = { seedHistory };

import * as ui from '../ui.js';

export const meta = { title: '远程控制' };

const { el, fmt } = ui;

const CMD_ORDER = [
  'pending_confirm', 'pending_dispatch', 'sent', 'executing', 'success', 'failed', 'timeout', 'cancelled',
];
const CMD_NAME = {
  pending_confirm: '待确认', pending_dispatch: '待下发', sent: '已下发', executing: '执行中',
  success: '成功', failed: '失败', timeout: '超时', cancelled: '已取消',
};
const RISK = {
  high: { label: '高风险', kind: 'bad' },
  medium: { label: '中风险', kind: 'warn' },
  low: { label: '低风险', kind: 'info' },
};
const FALLBACK_UNIT = { raw: '原料罐', baf: '曝气生物滤池', pac: 'PAC 反应池', pam: 'PAM 反应池', sulfur: '硫自养反硝化池' };

function riskMeta(risk) {
  return RISK[risk] || { label: risk || '未知风险', kind: 'muted' };
}

function cmdName(status, meta) {
  const names = (meta && meta.statusNames) || {};
  return names[status] || CMD_NAME[status] || status || '—';
}

export async function render(root, ctx) {
  const perms = (ctx.user && ctx.user.perms) || [];
  const canControl = perms.includes('control');
  const units = (ctx.meta && ctx.meta.units) || [];
  const unitByCode = new Map(units.map((u) => [u.code, u]));

  const state = {
    actions: [],
    gateway: null,
    actionsLoading: true,
    actionsError: null,
    actionsAt: 0,
    commands: [],
    cmdMeta: null,
    cmdLoading: true,
    cmdError: null,
    cmdPage: 1,
    cmdPageSize: 15,
    busy: false,
    lastUpdated: 0,
  };

  const updatedLabel = el('span', { class: 'tiny muted mono' }, '');
  const refreshBtn = el('button', { class: 'btn ghost', type: 'button' }, ui.icon('refresh'), '刷新状态');
  const statsHost = el('div', { class: 'stat-strip' });
  const actionsHost = el('div', { class: 'actions-host' }, ui.skeleton(3, 'block'));
  const legendHost = el('div', { class: 'cmd-legend' });
  const cmdTableHost = el('div', { class: 'table-host' }, ui.skeleton(6, 'line'));
  const pagerHost = el('div', { class: 'pager' });

  const page = el('div', { class: 'page p-control' },
    ui.pageHead(
      '远程控制',
      `按工艺单元下发控制命令，全部命令仅作用于本机模拟网关。站点：${ctx.site ? ctx.site.name : ctx.siteId}。`,
      el('div', { class: 'row' }, updatedLabel, refreshBtn),
    ),
    canControl ? null : ui.notice('当前角色无云端控制权限', 'bad'),
    statsHost,
    ui.card({
      title: '控制动作面板',
      sub: '高风险动作需二次确认',
      actions: el('span', { class: `badge ${canControl ? 'good' : 'muted'}` }, canControl ? '可下发命令' : '只读'),
      body: actionsHost,
    }),
    ui.card({
      title: '命令记录',
      sub: '下发 30 秒无回执记为超时',
      actions: legendHost,
      flush: true,
      body: el('div', { class: 'cmd-body' }, cmdTableHost, pagerHost),
    }),
  );

  ui.mount(root, page);

  /* ---------- 工具 ---------- */
  function groupKeyOf(action) {
    const key = String(action.key || '');
    const idx = key.indexOf('.');
    return idx > 0 ? key.slice(0, idx) : 'other';
  }

  function groupTitle(code) {
    const unit = unitByCode.get(code);
    return (unit && (unit.short || unit.name)) || FALLBACK_UNIT[code] || '其他设备';
  }

  /* 状态量读数按名称语义翻译成中文，避免 0/1 难扫读 */
  const STATE_WORDS = [
    { test: /故障|报警/, pair: ['正常', '故障'] },
    { test: /模式|自动|手动/, pair: ['自动', '手动'] },
    { test: /阀门|开关|启闭/, pair: ['关闭', '开启'] },
    { test: /运行状态|运行|启停|泵|风机/, pair: ['停止', '运行'] },
  ];

  function readingValueText(r) {
    const raw = r ? r.value : null;
    if (raw === null || raw === undefined || raw === '') return '待接入';
    const num = Number(raw);
    const word = STATE_WORDS.find((w) => w.test.test(String(r.name || r.code || '')));
    if (word && (num === 0 || num === 1)) return word.pair[num];
    if (!Number.isFinite(num)) return String(raw);
    /* 小数位：整数不带小数；<1 的量（如投加量 0.06 L/h）保留 2 位，避免四舍五入误导设定 */
    const digits = Number.isInteger(num) ? 0 : Math.abs(num) < 1 ? 2 : 1;
    const text = fmt.num(num, digits);
    return r.unit ? `${text} ${r.unit}` : text;
  }

  function readingList(readings) {
    if (!Array.isArray(readings) || !readings.length) return el('p', { class: 'ac-locks-empty' }, '暂无关键读数');
    return el('div', { class: 'ac-reading-grid' }, readings.map((r) => el('div', { class: 'ac-reading' },
      el('span', { class: 'v' }, readingValueText(r)),
      el('span', { class: 'k' }, r.name || r.code || '读数'),
    )));
  }

  function readingsText(readings) {
    if (!Array.isArray(readings) || !readings.length) return '暂无关键读数';
    return readings.map((r) => `${r.name || r.code || '读数'}：${readingValueText(r)}`).join('；');
  }

  function argHint(arg) {
    const bits = [];
    if (arg.min != null && arg.max != null) bits.push(`范围 ${arg.min} – ${arg.max}`);
    if (arg.unit) bits.push(`单位 ${arg.unit}`);
    if (arg.step) bits.push(`步长 ${arg.step}`);
    return bits.join(' · ');
  }

  /* ---------- 加载 ---------- */
  async function loadActions() {
    state.actionsLoading = true;
    try {
      const data = await ctx.api.get('/api/actions', { siteId: ctx.siteId });
      state.actions = (data && data.actions) || [];
      state.gateway = (data && data.status) || null;
      state.actionsError = null;
      state.actionsAt = Date.now();
    } catch (err) {
      state.actionsError = err;
      state.actions = [];
      state.gateway = null;
    } finally {
      state.actionsLoading = false;
      renderActions();
      renderStats();
      renderUpdated();
    }
  }

  async function loadCommands() {
    state.cmdLoading = true;
    renderCommands();
    try {
      const res = await ctx.api.getFull('/api/commands', {
        siteId: ctx.siteId,
        page: state.cmdPage,
        pageSize: state.cmdPageSize,
      });
      state.commands = res.data || [];
      state.cmdMeta = res.meta || {};
      state.cmdError = null;
      state.lastUpdated = Date.now();
    } catch (err) {
      state.cmdError = err;
      state.commands = [];
    } finally {
      state.cmdLoading = false;
      renderCommands();
      renderLegend();
      renderStats();
      renderUpdated();
    }
  }

  function renderUpdated() {
    const at = state.lastUpdated || state.actionsAt;
    updatedLabel.textContent = at ? `最近更新 ${fmt.timeSec(at)}` : '';
  }

  /* ---------- 统计 ---------- */
  function renderStats() {
    ui.clear(statsHost);
    if (state.actionsError && !state.actions.length) {
      statsHost.append(ui.errorState(state.actionsError, () => loadActions()));
      return;
    }
    const counts = (state.cmdMeta && state.cmdMeta.counts) || {};
    const pending = (counts.pending_confirm || 0) + (counts.pending_dispatch || 0);
    const running = (counts.sent || 0) + (counts.executing || 0);
    const ok = counts.success || 0;
    const bad = (counts.failed || 0) + (counts.timeout || 0);
    const done = ok + bad;
    const rate = done ? Math.round((ok / done) * 100) : null;
    const gw = state.gateway || {};
    statsHost.append(
      ui.kpi({ label: '待处置命令', value: pending, unit: '条', tone: pending ? 'warn' : '', hint: '待确认 + 待下发，可取消', iconName: 'control' }),
      ui.kpi({ label: '执行中', value: running, unit: '条', hint: '已下发 / 执行中，等待网关回执', iconName: 'process' }),
      ui.kpi({ label: '累计成功', value: ok, unit: '条', tone: 'good', hint: `失败与超时 ${bad} 条`, iconName: 'check' }),
      ui.kpi({
        label: '执行成功率',
        value: rate == null ? '—' : rate,
        unit: rate == null ? '' : '%',
        tone: rate == null ? '' : rate >= 95 ? 'good' : 'warn',
        hint: '成功 /（成功 + 失败 + 超时）',
        iconName: 'quality',
      }),
      ui.kpi({
        label: '模拟网关状态',
        value: gw.fault ? '故障' : gw.pumpRun ? '运行中' : '待机',
        tone: gw.fault ? 'bad' : 'good',
        hint: gw.hours != null ? `累计运行 ${fmt.auto(gw.hours)} 小时` : '命令落在本机模拟网关',
        iconName: 'device',
      }),
    );
  }

  /* ---------- 动作面板 ---------- */
  function renderActions() {
    ui.clear(actionsHost);
    if (state.actionsLoading && !state.actions.length) {
      actionsHost.append(ui.skeleton(3, 'block'));
      return;
    }
    if (state.actionsError && !state.actions.length) {
      actionsHost.append(ui.errorState(state.actionsError, () => loadActions()));
      return;
    }
    if (!state.actions.length) {
      actionsHost.append(ui.empty('当前站点没有可执行动作', '动作由模拟网关按设备能力动态生成'));
      return;
    }
    const groups = new Map();
    for (const action of state.actions) {
      const key = groupKeyOf(action);
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(action);
    }
    const order = units.map((u) => u.code);
    const keys = [...groups.keys()].sort((a, b) => {
      const ia = order.indexOf(a);
      const ib = order.indexOf(b);
      if (ia === -1 && ib === -1) return a.localeCompare(b);
      if (ia === -1) return 1;
      if (ib === -1) return -1;
      return ia - ib;
    });
    for (const key of keys) {
      const list = groups.get(key);
      actionsHost.append(el('section', { class: 'unit-group' },
        el('div', { class: 'unit-group-head' },
          el('h4', { class: 'unit-group-title' }, groupTitle(key)),
          ui.badge(`${list.length} 个动作`, 'muted'),
        ),
        el('div', { class: 'action-grid' }, list.map((action) => actionCard(action))),
      ));
    }
  }

  function actionCard(action) {
    const risk = riskMeta(action.risk);
    const device = action.device || {};
    const locks = Array.isArray(action.constraints) ? action.constraints : [];
    /* 停止类动作（stop / backwash.stop）用 danger 描边，与启动/设定类动作区分 */
    const isStop = /\.stop$/.test(String(action.key || ''));
    const execBtn = el('button', {
      class: `btn sm ${isStop ? 'ghost ac-stop' : 'primary'}`,
      type: 'button',
      disabled: !canControl,
      title: canControl ? '填写理由后下发命令' : '当前角色无云端控制权限',
    }, '执行');
    execBtn.addEventListener('click', () => openCommandModal(action));

    return el('article', { class: `action-card risk-${action.risk || 'medium'}` },
      el('div', { class: 'ac-head' },
        el('h5', { class: 'ac-title' }, action.label || action.key),
        el('div', { class: 'ac-badges' },
          ui.badge(risk.label, risk.kind, '风险等级'),
          action.needsConfirm ? ui.badge('需二次确认', 'bad', '提交后进入待确认，需再次确认下发') : null,
        ),
      ),
      el('p', { class: 'ac-effect' }, action.effect || '—'),
      el('div', { class: 'ac-device' },
        ui.dot(device.online === false ? 'bad' : 'good', true),
        el('span', {}, device.name || action.deviceKey || '关联设备'),
        device.code ? el('span', { class: 'mono tiny muted' }, device.code) : null,
      ),
      el('div', { class: 'ac-readings' },
        el('span', { class: 'tiny muted' }, '关键读数'),
        readingList(action.readings),
      ),
      locks.length
        ? el('ul', { class: 'ac-locks' }, locks.map((text) => el('li', {}, el('span', { class: 'ac-lock-tag' }, '联锁'), String(text))))
        : el('p', { class: 'ac-locks-empty' }, '无联锁条件'),
      el('div', { class: 'ac-foot' },
        el('span', { class: 'tiny muted' }, canControl ? '需填写操作理由' : '当前角色无云端控制权限'),
        el('span', { class: 'spacer' }),
        execBtn,
      ),
    );
  }

  /* ---------- 执行弹窗 ---------- */
  function openCommandModal(action) {
    if (!canControl) {
      ui.toast('当前角色无云端控制权限', 'error');
      return;
    }
    const risk = riskMeta(action.risk);
    const device = action.device || {};
    const args = Array.isArray(action.args) ? action.args : [];
    const inputs = new Map();

    const argNodes = args.map((arg) => {
      let control;
      if (Array.isArray(arg.options) && arg.options.length) {
        control = ui.select(arg.options.map((o) => ({ value: String(o.value), label: String(o.label) })), { 'aria-label': arg.label || arg.key });
        control.value = String(arg.options[0].value);
      } else {
        control = ui.input({
          type: 'number',
          min: arg.min,
          max: arg.max,
          step: arg.step || 1,
          placeholder: arg.min != null && arg.max != null ? `${arg.min} ~ ${arg.max}` : '',
        });
      }
      const node = ui.field(arg.label || arg.key, control, argHint(arg));
      inputs.set(arg.key, { arg, control });
      return node;
    });

    const reason = ui.textarea({
      rows: 3,
      maxlength: 300,
      placeholder: '必填：说明本次操作的原因与预期，例如「进水液位连续 40 分钟高于 3.2 m，需降低泵频防止溢流」',
    });
    const errBox = el('div', { class: 'error-text', hidden: true });
    const cancelBtn = el('button', { class: 'btn', type: 'button' }, '取消');
    const submitBtn = el('button', { class: 'btn primary', type: 'button' }, '提交命令');

    const body = el('div', { class: 'stack tight' },
      el('div', { class: 'row wrap' },
        ui.badge(risk.label, risk.kind),
        action.needsConfirm ? ui.badge('提交后需二次确认', 'bad') : ui.badge('提交后直接下发', 'info'),
      ),
      ui.kv([
        ['动作', action.label || action.key],
        ['动作标识', el('span', { class: 'mono tiny' }, action.key || '—')],
        ['目标设备', `${device.name || '—'}${device.code ? `（${device.code}）` : ''}`],
        ['当前读数', readingsText(action.readings)],      ]),
      el('div', { class: 'impact-box' },
        el('div', { class: 'impact-title' }, '操作影响'),
        el('p', {}, action.effect || '—'),
      ),
      el('div', { class: 'impact-box' },
        el('div', { class: 'impact-title' }, '联锁条件'),
        Array.isArray(action.constraints) && action.constraints.length
          ? el('ul', { class: 'ac-locks' }, action.constraints.map((c) => el('li', {}, String(c))))
          : el('p', { class: 'tiny muted' }, '无联锁条件'),
        el('p', { class: 'tiny muted' }, '联锁不满足时命令会被拒绝并返回具体原因，不会下发到网关。'),
      ),
      args.length ? el('div', { class: 'form-grid' }, argNodes) : null,
      ui.field('操作理由（必填）', reason, '写入命令记录与审计日志，请说明原因与预期结果，最多 300 字。'),
      ui.notice('所有命令仅作用于本机模拟网关，不涉及真实设备。', ''),
      errBox,
    );

    const m = ui.modal({
      title: `执行：${action.label || action.key}`,
      sub: `模拟网关 · ${device.name || action.deviceKey || ''}`,
      body,
      footer: el('div', { class: 'row', style: { width: '100%' } }, el('span', { class: 'spacer' }), cancelBtn, submitBtn),
    });
    cancelBtn.addEventListener('click', () => m.close());

    const showErr = (msg) => {
      errBox.hidden = false;
      errBox.textContent = msg;
    };

    submitBtn.addEventListener('click', async () => {
      if (state.busy) return;
      errBox.hidden = true;
      const text = reason.value.trim();
      if (!text) {
        showErr('请填写操作理由（审计要求）');
        reason.focus();
        return;
      }
      if (text.length > 300) {
        showErr('操作理由最多 300 字');
        return;
      }
      const payload = {};
      for (const [key, item] of inputs.entries()) {
        const { arg, control } = item;
        const raw = String(control.value == null ? '' : control.value).trim();
        if (Array.isArray(arg.options) && arg.options.length) {
          payload[key] = Number.isNaN(Number(raw)) ? raw : Number(raw);
          continue;
        }
        if (raw === '') {
          showErr(`请填写「${arg.label || key}」`);
          control.focus();
          return;
        }
        const num = Number(raw);
        if (!Number.isFinite(num)) {
          showErr(`「${arg.label || key}」需为数字`);
          control.focus();
          return;
        }
        if (arg.min != null && num < Number(arg.min)) {
          showErr(`「${arg.label || key}」不得小于 ${arg.min}${arg.unit ? ` ${arg.unit}` : ''}`);
          control.focus();
          return;
        }
        if (arg.max != null && num > Number(arg.max)) {
          showErr(`「${arg.label || key}」不得大于 ${arg.max}${arg.unit ? ` ${arg.unit}` : ''}`);
          control.focus();
          return;
        }
        payload[key] = num;
      }

      state.busy = true;
      submitBtn.disabled = true;
      cancelBtn.disabled = true;
      const submitLabel = submitBtn.textContent;
      submitBtn.textContent = '提交中…';
      try {
        const res = await ctx.api.post('/api/commands', {
          siteId: ctx.siteId,
          action: action.key,
          args: payload,
          reason: text,
        });
        const command = (res && res.command) || null;
        if (res && res.blocked) {
          const why = (command && command.failReason) || (res && res.message) || '联锁条件不满足';
          showErr(`命令被拒绝：${why}`);
          ui.toast('联锁条件不满足，命令未下发', 'error');
          await loadCommands();
          return;
        }
        if (res && res.needsConfirm && command) {
          m.close();
          const ok = await ctx.confirm({
            title: '二次确认下发',
            message: `确认向模拟网关下发「${action.label || action.key}」？`,
            detail: `影响：${action.effect || '—'}\n理由：${text}\n命令编号：${command.id}\n\n高风险动作需再次确认后才进入下发队列；取消后仍可在命令记录中确认或取消。`,
            confirmText: '确认下发',
            danger: true,
          });
          if (!ok) {
            ui.toast('已暂缓下发，命令处于「待确认」，可在命令记录中处理', 'info');
            await loadCommands();
            return;
          }
          try {
            await ctx.api.post(`/api/commands/${encodeURIComponent(command.id)}/confirm`);
            ui.toast('命令已确认，进入下发队列', 'success');
          } catch (err) {
            ui.toast(err.message || '确认失败', 'error');
          }
          await loadCommands();
          return;
        }
        m.close();
        ui.toast('命令已下发到模拟网关', 'success');
        await loadCommands();
      } catch (err) {
        showErr(err && err.message ? err.message : '命令提交失败，请稍后重试');
      } finally {
        state.busy = false;
        submitBtn.disabled = false;
        cancelBtn.disabled = false;
        submitBtn.textContent = submitLabel;
      }
    });
  }

  /* ---------- 命令记录 ---------- */
  function renderLegend() {
    ui.clear(legendHost);
    const meta = state.cmdMeta || {};
    const counts = meta.counts || {};
    legendHost.append(el('div', { class: 'status-chips' }, CMD_ORDER.map((key) => {
      const n = counts[key] || 0;
      const tone = key === 'success' ? 'good' : key === 'failed' || key === 'timeout' ? 'bad' : key === 'cancelled' ? 'muted' : 'info';
      return el('span', { class: `chip${n ? '' : ' dim'}` }, ui.dot(tone), `${cmdName(key, meta)} ${n}`);
    })));
  }

  function renderCommands() {
    ui.clear(cmdTableHost);
    if (state.cmdLoading && !state.commands.length) {
      cmdTableHost.append(ui.skeleton(5, 'line'));
      ui.clear(pagerHost);
      return;
    }
    if (state.cmdError) {
      cmdTableHost.append(ui.errorState(state.cmdError, () => loadCommands()));
      ui.clear(pagerHost);
      return;
    }
    ui.clear(pagerHost);
    cmdTableHost.append(ui.table({
      columns: [
        { key: 'createdAt', label: '时间', width: '124px', render: (row) => el('span', { class: 'mono' }, fmt.dateTime(row.createdAt)) },
        {
          key: 'action',
          label: '动作 / 设备',
          render: (row) => el('div', { class: 'cell-main' },
            el('span', { class: 'cell-title' }, row.actionLabel || row.action || '—'),
            el('span', { class: 'cell-sub mono' }, `${(row.device && row.device.name) || row.targetName || '—'}${row.device && row.device.code ? ` · ${row.device.code}` : ''}`),
          ),
        },
        { key: 'by', label: '操作人', width: '92px', render: (row) => row.byName || row.createdByName || '—' },
        { key: 'reason', label: '理由', render: (row) => el('span', { class: 'reason-cell', title: row.reason || '' }, row.reason || '—') },
        { key: 'status', label: '状态', width: '90px', render: (row) => ui.cmdStatus(row.status) },
        {
          key: 'durationSec',
          label: '耗时',
          width: '74px',
          cls: 'num',
          render: (row) => (row.durationSec == null ? '—' : `${fmt.auto(row.durationSec)} 秒`),
        },
        {
          key: 'op',
          label: '',
          width: '140px',
          render: (row) => el('div', { class: 'row wrap op-cell' }, rowActions(row)),
        },
      ],
      rows: state.commands,
      emptyText: '暂无命令记录',
      emptySub: '在动作面板执行一次操作后，这里会显示完整的状态时间线',
      onRow: (row) => openCommandDrawer(row),
    }));
    renderPager();
  }

  function rowActions(row) {
    const nodes = [];
    if (canControl && row.status === 'pending_confirm') {
      nodes.push(el('button', {
        class: 'btn primary sm', type: 'button',
        onclick: (ev) => { ev.stopPropagation(); confirmCommand(row); },
      }, '确认'));
    }
    if (canControl && (row.status === 'pending_confirm' || row.status === 'pending_dispatch')) {
      nodes.push(el('button', {
        class: 'btn ghost sm', type: 'button',
        onclick: (ev) => { ev.stopPropagation(); cancelCommand(row); },
      }, '取消'));
    }
    return nodes.length ? nodes : el('span', { class: 'tiny muted' }, '—');
  }

  function renderPager() {
    ui.clear(pagerHost);
    const meta = state.cmdMeta || {};
    const total = meta.total || 0;
    if (!total) return;
    const pages = Math.max(1, meta.pages || 1);
    const pageNo = Math.min(state.cmdPage, pages);
    pagerHost.append(
      el('span', { class: 'tiny muted' }, `共 ${total} 条命令 · 第 ${pageNo} / ${pages} 页`),
      el('span', { class: 'spacer' }),
      el('button', {
        class: 'btn ghost sm', type: 'button', disabled: pageNo <= 1,
        onclick: () => { if (pageNo > 1) { state.cmdPage = pageNo - 1; loadCommands(); } },
      }, '上一页'),
      el('button', {
        class: 'btn ghost sm', type: 'button', disabled: pageNo >= pages,
        onclick: () => { if (pageNo < pages) { state.cmdPage = pageNo + 1; loadCommands(); } },
      }, '下一页'),
    );
  }

  async function confirmCommand(row) {
    const ok = await ctx.confirm({
      title: '确认下发命令',
      message: `确认「${row.actionLabel || row.action}」下发到本机模拟网关？`,
      detail: `命令编号：${row.id}\n操作人：${row.byName || row.createdByName || '—'}\n理由：${row.reason || '—'}\n\n确认后进入下发队列，网关回执会写入状态时间线。`,
      confirmText: '确认下发',
      danger: row.risk === 'high',
    });
    if (!ok) return;
    try {
      await ctx.api.post(`/api/commands/${encodeURIComponent(row.id)}/confirm`);
      ui.toast('命令已确认，进入下发队列', 'success');
    } catch (err) {
      ui.toast(err.message || '确认失败', 'error');
    }
    await loadCommands();
  }

  async function cancelCommand(row) {
    const ok = await ctx.confirm({
      title: '取消命令',
      message: `取消「${row.actionLabel || row.action}」（${row.statusName || CMD_NAME[row.status] || row.status}）？`,
      detail: `命令编号：${row.id}\n取消后不会下发到网关，记录中保留取消时间与操作人。`,
      confirmText: '取消命令',
      cancelText: '返回',
      danger: true,
    });
    if (!ok) return;
    try {
      await ctx.api.post(`/api/commands/${encodeURIComponent(row.id)}/cancel`);
      ui.toast('命令已取消', 'success');
    } catch (err) {
      ui.toast(err.message || '取消失败', 'error');
    }
    await loadCommands();
  }

  /* ---------- 时间线抽屉 ---------- */
  async function openCommandDrawer(row) {
    const body = el('div', { class: 'stack drawer-stack' }, ui.loading('正在读取命令详情…'));
    const dr = ui.drawer({ title: '命令详情', sub: row.actionLabel || row.action || row.id, body });
    let detail = row;
    try {
      const fetched = await ctx.api.get(`/api/commands/${encodeURIComponent(row.id)}`);
      if (fetched) detail = fetched;
    } catch (err) {
      /* 列表行已包含时间线，详情接口异常时回退 */
    }
    ui.clear(body);
    const timeline = (detail.timeline || []).map((t) => ({
      time: fmt.dateTime(t.at),
      title: t.statusName || CMD_NAME[t.status] || t.status,
      note: [t.by, t.note].filter(Boolean).join(' · '),
      tone: t.status === 'failed' || t.status === 'timeout'
        ? 'bad'
        : t.status === 'success'
          ? 'good'
          : t.status === 'pending_confirm'
            ? 'warn'
            : '',
    }));
    const fb = detail.feedback && typeof detail.feedback === 'object' ? detail.feedback : {};
    const fbEntries = Object.keys(fb).map((k) => [k, typeof fb[k] === 'object' ? JSON.stringify(fb[k]) : String(fb[k])]);
    const argsEntries = detail.args && typeof detail.args === 'object'
      ? Object.keys(detail.args).map((k) => [k, String(detail.args[k])])
      : [];
    const canAct = canControl && (detail.status === 'pending_confirm' || detail.status === 'pending_dispatch');

    body.append(
      el('div', { class: 'detail-top' },
        el('div', { class: 'row wrap' },
          ui.cmdStatus(detail.status),
          detail.risk ? ui.badge(`${riskMeta(detail.risk).label}`, riskMeta(detail.risk).kind) : null,
          detail.durationSec != null ? ui.badge(`耗时 ${fmt.auto(detail.durationSec)} 秒`, 'muted') : null,
        ),
        el('h3', { class: 'detail-title' }, detail.actionLabel || detail.action || '—'),
        el('p', { class: 'detail-desc mono' }, detail.id || ''),
      ),
      detail.failReason ? ui.notice(`失败原因：${detail.failReason}`, 'bad') : null,
      detail.warning ? ui.notice(String(detail.warning), '') : null,
      ui.card({
        title: '基本信息',
        body: ui.kv([
          ['命令编号', el('span', { class: 'mono' }, detail.id || '—')],
          ['站点', detail.siteName || ctx.siteId],
          ['动作标识', el('span', { class: 'mono tiny' }, detail.action || '—')],
          ['目标设备', `${(detail.device && detail.device.name) || detail.targetName || '—'}${detail.device && detail.device.code ? `（${detail.device.code}）` : ''}`],
          ['操作人', detail.byName || detail.createdByName || '—'],
          ['操作理由', detail.reason || '—'],
          ['当前状态', detail.statusName || CMD_NAME[detail.status] || detail.status || '—'],
          ['创建时间', fmt.dateTime(detail.createdAt)],
          ['确认时间', detail.confirmedAt ? fmt.dateTime(detail.confirmedAt) : '—'],
          ['下发时间', detail.sentAt ? fmt.dateTime(detail.sentAt) : '—'],
          ['完成时间', detail.finishedAt ? fmt.dateTime(detail.finishedAt) : '—'],
        ]),
      }),
      argsEntries.length
        ? ui.card({ title: '命令参数', body: ui.kv(argsEntries.map(([k, v]) => [k, el('span', { class: 'mono' }, v)])) })
        : null,
      fbEntries.length
        ? ui.card({ title: '网关回执', body: ui.kv(fbEntries.map(([k, v]) => [k, el('span', { class: 'mono tiny' }, v)])) })
        : null,
      ui.card({
        title: '状态时间线',
        sub: '各状态由下发流程与网关回执写入，按发生顺序排列',
        body: timeline.length ? ui.timeline(timeline) : ui.empty('暂无状态记录', '命令尚未进入下发队列'),
      }),
      el('div', { class: 'drawer-actions' },
        canAct
          ? el('div', { class: 'row wrap' },
            detail.status === 'pending_confirm'
              ? el('button', { class: 'btn primary', type: 'button', onclick: () => { dr.close(); confirmCommand(detail); } }, '确认下发')
              : null,
            el('button', { class: 'btn ghost', type: 'button', onclick: () => { dr.close(); cancelCommand(detail); } }, '取消命令'),
          )
          : el('p', { class: 'tiny muted' }, canControl ? '当前状态不可操作（仅待确认 / 待下发可取消）。' : '当前角色无云端控制权限，仅可查看。'),
        el('p', { class: 'tiny muted' }, '所有命令仅作用于本机模拟网关。'),
      ),
    );
  }

  /* ---------- 刷新与实时 ---------- */
  async function refreshAll(manual) {
    await Promise.all([loadActions(), loadCommands()]);
    if (manual) ui.toast('已刷新控制状态', 'success');
  }

  refreshBtn.addEventListener('click', () => refreshAll(true));

  renderStats();
  renderLegend();
  renderCommands();
  await refreshAll(false);
  renderUpdated();

  const liveCmd = ui.debounce(() => { loadCommands().catch(() => {}); }, 400);
  const liveAct = ui.debounce(() => { loadActions().catch(() => {}); }, 1200);

  ctx.subscribe((evt) => {
    if (!evt || evt.type !== 'command') return;
    liveCmd();
    if (evt.action === 'success' || evt.action === 'failed' || evt.action === 'sent') liveAct();
  });

  ctx.every(30000, () => {
    loadCommands().catch(() => {});
    loadActions().catch(() => {});
  });
}

/* 工单管理：告警 / 巡检转入的维修工单闭环（接单 → 处理 → 送验 → 验收 → 关闭） */
import * as ui from '../ui.js';

export const meta = { title: '工单管理' };

const PRIORITY = {
  urgent: ['紧急', 'bad'],
  high: ['高', 'warn'],
  normal: ['普通', 'info'],
  low: ['低', 'muted'],
};
const TYPE_TONE = { repair: 'warn', maintain: 'info', calibrate: 'accent', inspect: 'muted' };
const STATUS_TEXT = {
  pending: '待接单', accepted: '已接单', processing: '处理中',
  done: '待验收', verified: '已验收', closed: '已关闭',
};
const OPEN = ['pending', 'accepted', 'processing', 'done'];
const ACTION_TEXT = {
  create: '创建工单', accept: '接单', start: '开始处理', finish: '完成送验',
  verify: '验收通过', close: '关闭工单', assign: '指派工单',
};
const ACTION_TONE = { accept: 'info', start: 'info', finish: 'good', verify: 'good', assign: 'warn' };

const hasPerm = (ctx, key) => ((ctx.user && ctx.user.perms) || []).includes(key);
const canAssignOthers = (ctx) => ['super', 'region'].includes(ctx.user && ctx.user.role);
const typeBadge = (w) => ui.badge(w.typeName || w.type || '—', TYPE_TONE[w.type] || 'muted');
const priBadge = (w) => {
  const hit = PRIORITY[w.priority] || [w.priorityName || '—', 'muted'];
  return ui.badge(hit[0], hit[1], w.priorityName || '');
};
const pad2 = (n) => String(n).padStart(2, '0');
function toLocalInput(t) {
  const d = new Date(t);
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}T${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
}
function fromNow(hours) { return toLocalInput(Date.now() + hours * 3600000); }

function dueCell(w) {
  if (!w.dueAt) return '—';
  const h = w.dueInHours;
  const open = OPEN.includes(w.status);
  const late = open && typeof h === 'number' && h < 0;
  const box = ui.el('div', { class: 'due-cell' },
    ui.el('span', { class: w.overdue || late ? 'text-bad' : '' }, ui.fmt.time(w.dueAt)),
    w.overdue ? ui.badge('已超时', 'bad')
      : late
        ? ui.el('span', { class: 'tiny text-bad' }, `超时 ${ui.fmt.num(Math.abs(h), 1)} 小时`)
        : (open && typeof h === 'number' && h < 8
          ? ui.el('span', { class: 'tiny muted' }, `余 ${ui.fmt.num(h, 1)} 小时`) : null),
  );
  return box;
}

function candidatesFor(ctx, siteId) {
  const seen = new Set();
  const out = [];
  const push = (u) => {
    if (!u || !u.id || seen.has(u.id)) return;
    seen.add(u.id);
    out.push({ value: u.id, label: `${u.name || u.id}${u.title ? `（${u.title}）` : ''}` });
  };
  return (async () => {
    if (hasPerm(ctx, 'admin.users')) {
      try {
        const users = await ctx.api.get('/api/users');
        (users || [])
          .filter((u) => u.status === 'active' && (u.siteIds || []).includes(siteId))
          .forEach(push);
      } catch (e) { /* 无用户列表权限时退回工单候选人 */ }
    }
    if (!out.length) {
      try {
        const list = await ctx.api.get('/api/workorders', { siteId, pageSize: 10 });
        const sample = (list || []).find((w) => w.assigneeId) || (list || [])[0];
        if (sample) {
          const detail = await ctx.api.get(`/api/workorders/${encodeURIComponent(sample.id)}`);
          (detail.candidates || []).forEach(push);
        }
      } catch (e) { /* 无候选人时仅支持“不指派” */ }
    }
    return out;
  })();
}

export async function render(root, ctx) {
  const canHandle = hasPerm(ctx, 'workorder.handle');
  const state = { status: ctx.query.status || 'open', type: '', priority: '', q: '', mine: false, page: 1 };
  let counts = { pending: 0, processing: 0, overdue: 0, mine: 0 };
  let currentDrawer = null;

  const kpiBox = ui.el('div', { class: 'grid cols-4' });
  const listBox = ui.el('div', {});
  const countBadge = ui.el('span', { class: 'badge muted' }, '— 条');
  const footBox = ui.el('div', {});

  function renderKpis() {
    ui.mount(kpiBox,
      ui.kpi({ label: '待接单', value: counts.pending || 0, unit: '单', tone: 'warn', hint: '当前站点待接单工单', iconName: 'workorder' }),
      ui.kpi({ label: '处理中', value: counts.processing || 0, unit: '单', tone: 'blue', hint: '已开始处理、尚未送验' }),
      ui.kpi({ label: '超时未闭环', value: counts.overdue || 0, unit: '单', tone: 'bad', hint: '已过截止时间且未闭环', iconName: 'warning' }),
      ui.kpi({ label: '我的工单', value: counts.mine || 0, unit: '单', hint: `${ctx.user.name || ''} 名下未闭环工单` }),
    );
  }

  const statusOptions = [
    { value: 'open', label: '未闭环（全部待办）', selected: state.status === 'open' },
    { value: 'all', label: '全部状态（含历史）', selected: state.status === 'all' },
  ].concat(Object.keys(STATUS_TEXT).map((k) => ({ value: k, label: STATUS_TEXT[k], selected: state.status === k })));

  const typeOptions = [{ value: '', label: '全部类型', selected: true }].concat(
    ((ctx.meta && ctx.meta.workorderTypes) || []).map((t) => ({ value: t.key, label: t.name })),
  );
  const priOptions = [{ value: '', label: '全部优先级', selected: true }].concat(
    ((ctx.meta && ctx.meta.priorities) || []).map((p) => ({ value: p.key, label: `${p.name}优先级` })),
  );

  const searchInput = ui.input({
    class: 'input', placeholder: '搜索编号 / 标题 / 描述', type: 'search',
    oninput: ui.debounce((e) => { state.q = e.target.value.trim(); state.page = 1; load(); }, 300),
  });

  const toolbar = ui.el('div', { class: 'toolbar' },
    ui.select(statusOptions, {
      onchange: (e) => { state.status = e.target.value; state.page = 1; load(); },
    }),
    ui.select(typeOptions, { onchange: (e) => { state.type = e.target.value; state.page = 1; load(); } }),
    ui.select(priOptions, { onchange: (e) => { state.priority = e.target.value; state.page = 1; load(); } }),
    searchInput,
    ui.el('label', { class: 'check-field' },
      ui.el('input', {
        type: 'checkbox',
        onchange: (e) => { state.mine = e.target.checked; state.page = 1; load(); },
      }),
      '只看我的',
    ),
    ui.el('span', { class: 'spacer' }),
    countBadge,
    ui.el('button', {
      class: 'btn sm', type: 'button',
      onclick: () => ctx.refresh(),
    }, ui.icon('refresh'), '刷新'),
  );

  ui.mount(root,
    ui.el('div', { class: 'p-workorders stack' },
      ui.pageHead(
        '工单管理',
        `告警与巡检异常转入的维修工单闭环 · 当前站点 ${ctx.site ? ctx.site.name : '—'}`,
        canHandle
          ? [ui.el('button', { class: 'btn primary', type: 'button', onclick: openCreate }, ui.icon('workorder'), '新建工单')]
          : null,
      ),
      kpiBox,
      ui.card({
        title: '筛选',
        sub: '点击任意行查看详情',
        body: toolbar,
        class: 'wo-filter',
      }),
      ui.card({ title: '工单列表', body: listBox, foot: footBox, flush: false }),
      canHandle ? null : ui.notice('当前账号没有工单处置权限（workorder.handle），仅可查看工单与时间线。', 'warn'),
    ),
  );

  renderKpis();
  await load();

  /* ---------------- 列表 ---------------- */
  async function load() {
    ui.mount(listBox, ui.skeleton(5, 'line'));
    ui.mount(footBox);
    const params = { siteId: ctx.siteId, page: state.page, pageSize: 30 };
    if (state.status && state.status !== 'all') params.status = state.status;
    if (state.type) params.type = state.type;
    if (state.priority) params.priority = state.priority;
    if (state.q) params.q = state.q;
    if (state.mine) params.assignee = 'me';
    try {
      const [res, countRes] = await Promise.all([
        ctx.api.getFull('/api/workorders', params),
        ctx.api.getFull('/api/workorders', { siteId: ctx.siteId, pageSize: 1 }),
      ]);
      if (countRes.meta && countRes.meta.counts) counts = countRes.meta.counts;
      renderKpis();
      countBadge.textContent = `${res.meta ? res.meta.total : res.data.length} 条`;
      const rows = res.data || [];
      if (!rows.length) {
        ui.mount(listBox, ui.empty(
          state.mine || state.q || state.type || state.priority || state.status !== 'open' ? '没有符合条件的工单' : '当前没有待办工单',
          '调整筛选条件，或点击右上角新建工单。',
          'workorder',
        ));
      } else {
        ui.mount(listBox, buildTable(rows));
      }
      renderPager(res.meta);
    } catch (err) {
      ui.mount(listBox, ui.errorState(err, load));
    }
  }

  function buildTable(rows) {
    return ui.table({
      columns: [
        { key: 'id', label: '编号', width: '132px', cls: 'mono', render: (w) => ui.el('span', { class: 'mono strong' }, w.id) },
        {
          key: 'title', label: '标题', render: (w) => ui.el('div', {},
            ui.el('div', { class: 'cell-main' }, w.title),
            ui.el('div', { class: 'cell-sub' }, [w.description ? String(w.description).slice(0, 42) : '（无描述）']),
          ),
        },
        { key: 'typeName', label: '类型', width: '78px', render: (w) => typeBadge(w) },
        { key: 'priorityName', label: '优先级', width: '78px', render: (w) => priBadge(w) },
        { key: 'unitName', label: '单元', width: '110px', render: (w) => w.unitName || w.unitCode || '—' },
        { key: 'status', label: '状态', width: '86px', render: (w) => ui.woStatus(w.status) },
        { key: 'assigneeName', label: '负责人', width: '92px', render: (w) => w.assigneeName || ui.badge('待指派', 'warn') },
        { key: 'dueAt', label: '截止时间', width: '120px', render: (w) => dueCell(w) },
        { key: 'createdAt', label: '创建时间', width: '108px', cls: 'mono tiny', render: (w) => ui.fmt.time(w.createdAt) },
        {
          key: 'op', label: '', width: '64px', render: (w) => ui.el('button', {
            class: 'btn sm', type: 'button', onclick: () => openDetail(w.id),
          }, '详情'),
        },
      ],
      rows,
      rowClass: (w) => (w.overdue ? 'row-alarm' : (w.priority === 'urgent' ? 'row-attention' : '')),
      onRow: (w) => openDetail(w.id),
    });
  }

  function renderPager(metaInfo) {
    ui.mount(footBox);
    if (!metaInfo || !metaInfo.pages || metaInfo.pages <= 1) return;
    ui.mount(footBox, ui.el('div', { class: 'pager' },
      ui.el('button', {
        class: 'btn sm', type: 'button', disabled: metaInfo.page <= 1,
        onclick: () => { state.page = metaInfo.page - 1; load(); },
      }, '上一页'),
      ui.el('span', { class: 'tiny muted' }, `第 ${metaInfo.page} / ${metaInfo.pages} 页 · 共 ${metaInfo.total} 条`),
      ui.el('button', {
        class: 'btn sm', type: 'button', disabled: metaInfo.page >= metaInfo.pages,
        onclick: () => { state.page = metaInfo.page + 1; load(); },
      }, '下一页'),
    ));
  }

  /* ---------------- 详情抽屉 ---------------- */
  async function openDetail(id) {
    const body = ui.el('div', { class: 'wo-detail' });
    const draw = ui.drawer({ title: '工单详情', sub: '正在加载…', body });
    currentDrawer = draw;
    let latest = null;
    const reload = async () => { await load(); await fill(); };

    async function fill() {
      ui.mount(body, ui.skeleton(4, 'line'));
      try {
        const w = await ctx.api.get(`/api/workorders/${encodeURIComponent(id)}`);
        latest = w;
        const h3 = draw.box.querySelector('.drawer-head h3');
        if (h3) h3.textContent = w.title;
        const sub = draw.box.querySelector('.drawer-head .card-sub');
        if (sub) sub.textContent = `${w.id} · ${w.siteName || ''} · ${w.unitName || w.unitCode || ''}`;
        ui.mount(body, detailBody(w, reload));
      } catch (err) {
        ui.mount(body, ui.errorState(err, fill));
      }
    }

    await fill();
    return { draw, getLatest: () => latest };
  }

  function detailBody(w, reload) {
    const head = ui.el('div', { class: 'row wrap wo-head-badges' },
      ui.woStatus(w.status),
      priBadge(w),
      typeBadge(w),
      w.overdue ? ui.badge('已超时', 'bad') : null,
      w.source === 'alarm' ? ui.badge('告警转入', 'accent') : null,
      w.source === 'inspection' ? ui.badge('巡检异常', 'accent') : null,
      ui.badge(`SLA ${w.slaHours || 24} 小时`, 'muted'),
    );

    const info = ui.kv([
      ['工单编号', ui.el('span', { class: 'mono strong' }, w.id)],
      ['站点 / 单元', `${w.siteName || '—'} · ${w.unitName || w.unitCode || '—'}`],
      ['负责人', w.assigneeName || ui.badge('待指派', 'warn')],
      ['创建', `${w.creatorName || '—'} · ${ui.fmt.time(w.createdAt)}`],
      ['截止时间', dueCell(w)],
      ['来源', w.alarmId ? `告警 ${w.alarmId}` : (w.source === 'inspection' ? '巡检异常自动创建' : '手工创建')],
      ['时间线', `${(w.timelineView || []).length} 条处理记录`],
    ]);

    const parts = [head, info];

    if (!hasPerm(ctx, 'workorder.handle')) {
      parts.push(ui.notice('当前账号没有工单处置权限（workorder.handle），仅可查看。', 'warn'));
    } else if (w.assigneeId && w.assigneeId !== ctx.user.id && !canAssignOthers(ctx)) {
      parts.push(ui.notice(`该工单已指派给 ${w.assigneeName || w.assigneeId}，仅负责人可开始处理 / 完成送验；管理员可重新指派或验收。`, 'warn'));
    }

    parts.push(ui.card({
      title: '故障描述',
      body: ui.el('p', { class: 'pre-wrap' }, w.description || '（未填写描述）'),
      class: 'wo-section',
    }));

    if (w.alarm || w.deviceName) {
      parts.push(ui.card({
        title: '关联对象',
        body: ui.el('div', { class: 'stack-sm' },
          w.alarm ? ui.kvRows([
            ['关联告警', ui.el('div', { class: 'row wrap' }, ui.levelBadge(w.alarm.level), ui.el('span', {}, w.alarm.title), ui.alarmStatus(w.alarm.status))],
          ]) : null,
          w.deviceName ? ui.kvRows([
            ['关联设备', ui.el('span', {}, `${w.deviceName}${w.unitName ? `（${w.unitName}）` : ''}`)],
          ]) : null,
        ),
      }));
    }

    parts.push(ui.card({
      title: '处理时间线',
      sub: '按时间顺序记录接单、处理与验收动作',
      body: (w.timelineView || []).length
        ? ui.timeline(w.timelineView.map((t) => ({
          time: ui.fmt.time(t.at),
          title: `${t.byName || t.by || '系统'} · ${ACTION_TEXT[t.action] || t.action}`,
          note: t.note || '',
          tone: ACTION_TONE[t.action] || '',
        })))
        : ui.empty('暂无处理记录', '工单创建后会在此显示完整流转记录。', 'workorder'),
    }));

    if (w.result || w.parts || w.verifyNote) {
      parts.push(ui.card({
        title: '处理结果',
        body: ui.kvRows([
          ['处理结果', w.result || '—'],
          ['备件使用', w.parts || '—'],
          ['验收意见', w.verifyNote || '—'],
        ]),
      }));
    }

    parts.push(ui.el('div', { class: 'wo-actions' }, actionButtons(w, reload)));
    return ui.el('div', { class: 'stack' }, parts);
  }

  function actionButtons(w, reload) {
    const btns = [];
    if (!hasPerm(ctx, 'workorder.handle')) return btns;
    const isMine = !w.assigneeId || w.assigneeId === ctx.user.id;
    const canAct = isMine || canAssignOthers(ctx);
    const add = (label, cls, fn, opts = {}) => btns.push(ui.el('button', {
      class: `btn ${cls || ''}`.trim(), type: 'button', disabled: !!opts.disabled, title: opts.title || '',
      onclick: fn,
    }, label));

    if (w.status === 'pending') {
      add('接单', 'primary', () => accept(w, reload));
    }
    if (OPEN.includes(w.status) && w.status !== 'done') {
      add('开始处理', w.status === 'pending' ? '' : 'primary', () => startModal(w, reload), {
        disabled: !canAct && w.status !== 'pending',
        title: canAct ? '' : '仅负责人或管理员可开始处理',
      });
    }
    if (['accepted', 'processing'].includes(w.status)) {
      add('完成并送验', 'primary', () => finishModal(w, reload), {
        disabled: !canAct,
        title: canAct ? '' : '仅负责人或管理员可完成工单',
      });
    }
    if (w.status === 'done') add('验收通过', 'primary', () => verifyModal(w, reload));
    if (['pending', 'accepted', 'processing'].includes(w.status)) add('指派', '', () => assignModal(w, reload));
    if (['verified', 'done'].includes(w.status)) add('关闭工单', '', () => closeModal(w, reload));
    if (!btns.length) return ui.el('div', { class: 'muted small' }, '该工单已闭环，无可执行动作。');
    return btns;
  }

  function noteModal({ title, sub, label, placeholder, defaultValue = '', confirmText = '确认', onSubmit }) {
    const box = ui.textarea({ rows: 3, placeholder });
    if (defaultValue) box.value = defaultValue;
    const err = ui.el('div', { class: 'error-text' });
    let busy = false;
    const submitBtn = ui.el('button', { class: 'btn primary', type: 'button' }, confirmText);
    const m = ctx.modal({
      title, sub,
      body: ui.el('div', { class: 'form-grid one' }, ui.field(label, box), err),
      footer: ui.el('div', { class: 'row', style: { width: '100%' } },
        ui.el('span', { class: 'spacer' }),
        ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
        submitBtn,
      ),
    });
    submitBtn.addEventListener('click', async () => {
      if (busy) return;
      busy = true;
      submitBtn.disabled = true;
      submitBtn.textContent = '提交中…';
      err.textContent = '';
      try {
        await onSubmit(box.value.trim());
        m.close();
      } catch (e) {
        err.textContent = (e && e.message) || '提交失败';
        busy = false;
        submitBtn.disabled = false;
        submitBtn.textContent = confirmText;
      }
    });
  }

  async function run(action, body, label, reload) {
    try {
      await ctx.api.post(`/api/workorders/${encodeURIComponent(action.id)}/${action.act}`, body);
      ctx.toast(`${label}成功`, 'success');
      await reload();
    } catch (e) {
      ctx.toast((e && e.message) || `${label}失败`, 'error');
      throw e;
    }
  }

  async function accept(w, reload) {
    const ok = await ctx.confirm({
      title: '接单确认',
      message: `确认接受工单「${w.title}」？接单后负责人将变更为当前账号（${ctx.user.name || ''}）。`,
      confirmText: '接受工单',
    });
    if (!ok) return;
    try {
      await ctx.api.post(`/api/workorders/${encodeURIComponent(w.id)}/accept`, { note: '已接单' });
      ctx.toast('接单成功', 'success');
      await reload();
    } catch (e) { ctx.toast((e && e.message) || '接单失败', 'error'); }
  }

  function startModal(w, reload) {
    noteModal({
      title: '开始处理', sub: w.title, label: '处理说明（可选）',
      placeholder: '例如：携带万用表与备用曝气头到场检查', defaultValue: '开始处理', confirmText: '开始处理',
      onSubmit: (note) => run({ id: w.id, act: 'start' }, { note }, '开始处理', reload),
    });
  }

  function finishModal(w, reload) {
    const result = ui.textarea({ rows: 4, placeholder: '处理过程、更换部件、复测数据…' });
    const parts = ui.input({ placeholder: '更换备件（可选）' });
    const err = ui.el('div', { class: 'error-text' });
    let busy = false;
    const submitBtn = ui.el('button', { class: 'btn primary', type: 'button' }, '完成并送验');
    const m = ctx.modal({
      title: '完成并送验', sub: w.title,
      body: ui.el('div', { class: 'form-grid one' },
        ui.field('处理结果（必填）', result, '写入工单处理结果，验收人将据此核对'),
        ui.field('备件使用', parts),
        err,
      ),
      footer: ui.el('div', { class: 'row', style: { width: '100%' } },
        ui.el('span', { class: 'spacer' }),
        ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
        submitBtn,
      ),
    });
    submitBtn.addEventListener('click', async () => {
      if (!result.value.trim()) { err.textContent = '请填写处理结果'; result.focus(); return; }
      if (busy) return;
      busy = true;
      submitBtn.disabled = true;
      submitBtn.textContent = '提交中…';
      try {
        await ctx.api.post(`/api/workorders/${encodeURIComponent(w.id)}/finish`, {
          result: result.value.trim(), parts: parts.value.trim(),
        });
        ctx.toast('已提交处理结果，等待验收', 'success');
        m.close();
        await reload();
      } catch (e) {
        err.textContent = (e && e.message) || '提交失败';
        busy = false;
        submitBtn.disabled = false;
        submitBtn.textContent = '完成并送验';
      }
    });
  }

  function verifyModal(w, reload) {
    noteModal({
      title: '验收通过', sub: w.title, label: '验收意见',
      placeholder: '验收依据与结论', defaultValue: '验收通过：现场复核运行正常', confirmText: '验收通过',
      onSubmit: (note) => run({ id: w.id, act: 'verify' }, { note }, '验收', reload),
    });
  }

  function closeModal(w, reload) {
    noteModal({
      title: '关闭工单', sub: w.title, label: '关闭说明',
      placeholder: '例如：已验收并归档，无需继续跟踪', defaultValue: '工单闭环', confirmText: '关闭工单',
      onSubmit: (note) => run({ id: w.id, act: 'close' }, { note }, '关闭', reload),
    });
  }

  async function assignModal(w, reload) {
    const list = w.candidates || [];
    if (!list.length) {
      ctx.toast('没有可指派的现场人员', 'error');
      return;
    }
    const sel = ui.select(list.map((u) => ({ value: u.id, label: `${u.name}${u.title ? `（${u.title}）` : ''}`, selected: u.id === w.assigneeId })), {});
    const err = ui.el('div', { class: 'error-text' });
    let busy = false;
    const submitBtn = ui.el('button', { class: 'btn primary', type: 'button' }, '确认指派');
    const m = ctx.modal({
      title: '指派工单', sub: `${w.id} · ${w.title}`,
      body: ui.el('div', { class: 'form-grid one' },
        ui.field('指派给', sel, '仅站点内在岗的运维 / 维修 / 管理员账号'),
        err,
      ),
      footer: ui.el('div', { class: 'row', style: { width: '100%' } },
        ui.el('span', { class: 'spacer' }),
        ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
        submitBtn,
      ),
    });
    submitBtn.addEventListener('click', async () => {
      if (!sel.value) { err.textContent = '请选择指派对象'; return; }
      if (busy) return;
      busy = true;
      submitBtn.disabled = true;
      try {
        await ctx.api.post(`/api/workorders/${encodeURIComponent(w.id)}/assign`, { assigneeId: sel.value });
        ctx.toast('指派成功', 'success');
        m.close();
        await reload();
      } catch (e) {
        err.textContent = (e && e.message) || '指派失败';
        busy = false;
        submitBtn.disabled = false;
      }
    });
  }

  /* ---------------- 新建工单 ---------------- */
  async function openCreate() {
    const candidates = await candidatesFor(ctx, ctx.siteId);
    const title = ui.input({ placeholder: '如：BAF 曝气支管阀门卡涩处理', maxlength: 120 });
    const type = ui.select(((ctx.meta && ctx.meta.workorderTypes) || []).map((t) => ({ value: t.key, label: t.name, selected: t.key === 'repair' })), {});
    const priority = ui.select(((ctx.meta && ctx.meta.priorities) || []).map((p) => ({ value: p.key, label: p.name, selected: p.key === 'normal' })), {});
    const unit = ui.select([{ value: 'plant', label: '全厂 / 未指定' }].concat(
      ((ctx.meta && ctx.meta.units) || []).map((u) => ({ value: u.code, label: u.name })),
    ), {});
    const assignee = ui.select([{ value: '', label: '暂不指派（生成待接单）' }].concat(candidates), {});
    const due = ui.input({ type: 'datetime-local', value: fromNow(24) });
    const desc = ui.textarea({ rows: 4, placeholder: '现象、影响范围、已做处置…' });
    const err = ui.el('div', { class: 'error-text' });
    let busy = false;
    const submitBtn = ui.el('button', { class: 'btn primary', type: 'button' }, '创建工单');
    const m = ctx.modal({
      title: '新建工单', sub: ctx.site ? ctx.site.name : '',
      body: ui.el('div', { class: 'form-grid' },
        ui.el('div', { class: 'span-2' }, ui.field('标题（必填）', title)),
        ui.field('类型', type),
        ui.field('优先级', priority),
        ui.field('所属单元', unit),
        ui.field('负责人', assignee, '不指派则进入待接单池'),
        ui.field('截止时间', due),
        ui.el('div', { class: 'span-2' }, ui.field('描述', desc)),
        ui.el('div', { class: 'span-2' }, err),
      ),
      wide: true,
      footer: ui.el('div', { class: 'row', style: { width: '100%' } },
        ui.el('span', { class: 'spacer' }),
        ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
        submitBtn,
      ),
    });
    submitBtn.addEventListener('click', async () => {
      if (!title.value.trim()) { err.textContent = '请填写工单标题'; title.focus(); return; }
      if (busy) return;
      busy = true;
      submitBtn.disabled = true;
      submitBtn.textContent = '创建中…';
      try {
        const w = await ctx.api.post('/api/workorders', {
          siteId: ctx.siteId,
          title: title.value.trim(),
          type: type.value,
          priority: priority.value,
          unitCode: unit.value,
          assigneeId: assignee.value || null,
          dueAt: due.value ? new Date(due.value).toISOString() : undefined,
          description: desc.value.trim(),
        });
        ctx.toast(`工单 ${w.id} 已创建`, 'success');
        m.close();
        await load();
        openDetail(w.id);
      } catch (e) {
        err.textContent = (e && e.message) || '创建失败';
        busy = false;
        submitBtn.disabled = false;
        submitBtn.textContent = '创建工单';
      }
    });
  }

  ctx.subscribe((evt) => {
    if (evt && evt.type === 'workorder') load();
  });

  if (ctx.query.id) openDetail(ctx.query.id);
  if (currentDrawer) currentDrawer = null;
}

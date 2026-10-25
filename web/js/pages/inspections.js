/* 巡检管理：巡检计划维护 + 巡检任务执行（逐项判定，异常自动转工单） */
import * as ui from '../ui.js';

export const meta = { title: '巡检管理' };

const RESULTS = {
  ok: ['正常', 'good'],
  attention: ['需关注', 'warn'],
  abnormal: ['异常', 'bad'],
};
const TASK_STATUS = { pending: ['待执行', 'warn'], done: ['已完成', 'good'] };
const CYCLE_TEXT = { daily: '每日', weekly: '每周', monthly: '每月', shift: '每班', hourly: '每小时' };
const hasPerm = (ctx, key) => ((ctx.user && ctx.user.perms) || []).includes(key);
const isManager = (ctx) => ['super', 'region'].includes(ctx.user && ctx.user.role);
const pad2 = (n) => String(n).padStart(2, '0');

function toLocalInput(t) {
  const d = new Date(t);
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}T${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
}

function resultBadge(result) {
  const hit = RESULTS[result];
  return hit ? ui.badge(hit[0], hit[1]) : ui.badge('未填写', 'muted');
}

/* 执行人候选：优先用户列表（需 admin.users），否则退回工单候选人 */
async function assigneeOptions(ctx, siteId) {
  const seen = new Set();
  const out = [];
  const push = (u) => {
    if (!u || !u.id || seen.has(u.id)) return;
    seen.add(u.id);
    out.push({ value: u.id, label: `${u.name || u.id}${u.title ? `（${u.title}）` : ''}` });
  };
  if (hasPerm(ctx, 'admin.users')) {
    try {
      const users = await ctx.api.get('/api/users');
      (users || [])
        .filter((u) => u.status === 'active' && (u.siteIds || []).includes(siteId))
        .forEach(push);
    } catch (e) { /* 忽略，退回候选人 */ }
  }
  if (!out.length) {
    try {
      const list = await ctx.api.get('/api/workorders', { siteId, pageSize: 10 });
      const sample = (list || []).find((w) => w.assigneeId) || (list || [])[0];
      if (sample) {
        const detail = await ctx.api.get(`/api/workorders/${encodeURIComponent(sample.id)}`);
        (detail.candidates || []).forEach(push);
      }
    } catch (e) { /* 默认由本人执行 */ }
  }
  return out;
}

export async function render(root, ctx) {
  const canExecute = hasPerm(ctx, 'inspection.execute');
  const state = { tab: 'tasks', status: '', mine: false };
  let tasks = [];
  let plans = [];
  let taskMeta = { total: 0, pending: 0 };

  const kpiBox = ui.el('div', { class: 'grid cols-4' });
  const tabsBox = ui.el('div', {});
  const mainBox = ui.el('div', {});

  ui.mount(root, ui.el('div', { class: 'p-inspections stack' },
    ui.pageHead(
      '巡检管理',
      `巡检计划与任务执行 · 当前站点 ${ctx.site ? ctx.site.name : '—'}`,
      [ui.el('button', { class: 'btn', type: 'button', onclick: () => ctx.refresh() }, ui.icon('refresh'), '刷新')],
    ),
    canExecute ? null : ui.notice('当前账号没有巡检执行权限（inspection.execute），仅可查看计划与已完成的巡检记录。', 'warn'),
    kpiBox,
    tabsBox,
    mainBox,
  ));

  renderKpis();
  renderTabs();
  await Promise.all([loadTasks(), loadPlans()]);
  renderBody();

  function renderKpis() {
    const minePending = tasks.filter((t) => t.status === 'pending'
      && (!t.assigneeId || t.assigneeId === ctx.user.id) && canExecute).length;
    ui.mount(kpiBox,
      ui.kpi({ label: '待执行巡检', value: taskMeta.pending || 0, unit: '项', tone: 'warn', hint: '本站点待执行任务', iconName: 'inspection' }),
      ui.kpi({ label: '已完成', value: Math.max(0, (taskMeta.total || 0) - (taskMeta.pending || 0)), unit: '项', tone: 'good', hint: '历史巡检记录' }),
      ui.kpi({ label: '我的待办', value: minePending, unit: '项', hint: '指派给我或待执行的任务' }),
      ui.kpi({ label: '启用计划', value: plans.filter((p) => p.enabled).length, unit: '个', hint: '本站点启用的巡检计划' }),
    );
  }

  function renderTabs() {
    ui.mount(tabsBox, ui.tabs([
      { value: 'tasks', label: '巡检任务' },
      { value: 'plans', label: '巡检计划' },
    ], state.tab, (v) => { state.tab = v; renderTabs(); renderBody(); }));
  }

  function renderBody() {
    if (state.tab === 'plans') renderPlans();
    else renderTasks();
  }

  /* ---------------- 巡检任务 ---------------- */
  function renderTasks() {
    const rows = tasks.filter((t) => (!state.status || t.status === state.status)
      && (!state.mine || t.assigneeId === ctx.user.id));
    const toolbar = ui.el('div', { class: 'toolbar' },
      ui.select([
        { value: '', label: '全部状态', selected: !state.status },
        { value: 'pending', label: '待执行', selected: state.status === 'pending' },
        { value: 'done', label: '已完成', selected: state.status === 'done' },
      ], { onchange: (e) => { state.status = e.target.value; renderTasks(); } }),
      ui.el('label', { class: 'check-field' },
        ui.el('input', {
          type: 'checkbox', checked: state.mine,
          onchange: (e) => { state.mine = e.target.checked; renderTasks(); },
        }),
        '只看我的',
      ),
      ui.el('span', { class: 'spacer' }),
      ui.el('span', { class: 'tiny muted' }, `共 ${rows.length} 项`),
      canExecute
        ? ui.el('button', { class: 'btn primary sm', type: 'button', onclick: () => openCreateTask() }, ui.icon('inspection'), '新建任务')
        : null,
    );

    const content = rows.length
      ? ui.table({
        columns: [
          { key: 'id', label: '编号', width: '128px', render: (t) => ui.el('span', { class: 'mono strong' }, t.id) },
          {
            key: 'title', label: '巡检任务', render: (t) => ui.el('div', {},
              ui.el('div', { class: 'cell-main' }, t.title),
              ui.el('div', { class: 'cell-sub' }, `${CYCLE_TEXT[t.type] || t.type || '巡检'} · ${t.siteName || ''}`),
            ),
          },
          {
            key: 'status', label: '状态', width: '84px', render: (t) => {
              const hit = TASK_STATUS[t.status] || ['未知', 'muted'];
              return ui.badge(hit[0], hit[1]);
            },
          },
          { key: 'assigneeName', label: '执行人', width: '96px', render: (t) => t.assigneeName || '—' },
          {
            key: 'dueAt', label: '截止时间', width: '150px', render: (t) => ui.el('div', { class: 'due-cell' },
              ui.el('span', { class: t.overdue ? 'text-bad' : '' }, ui.fmt.time(t.dueAt)),
              t.overdue ? ui.badge('已超时', 'bad') : null,
            ),
          },
          {
            key: 'progress', label: '完成进度', width: '130px', render: (t) => ui.progress(t.doneCount || 0, {
              max: t.totalCount || 0,
              tone: t.totalCount && t.doneCount === t.totalCount ? 'good' : '',
              showLabel: true,
            }),
          },
          { key: 'doneAt', label: '完成时间', width: '108px', render: (t) => ui.el('span', { class: 'mono tiny' }, t.doneAt ? ui.fmt.time(t.doneAt) : '—') },
          {
            key: 'op', label: '', width: '70px', render: (t) => ui.el('button', {
              class: 'btn sm', type: 'button', onclick: () => openTask(t.id),
            }, t.status === 'done' ? '查看' : '执行'),
          },
        ],
        rows,
        rowClass: (t) => (t.overdue ? 'row-alarm' : ''),
        onRow: (t) => openTask(t.id),
      })
      : ui.empty('没有符合条件的巡检任务', canExecute ? '切换筛选条件，或点击「新建任务」从计划生成。' : '切换筛选条件查看其他任务。', 'inspection');

    ui.mount(mainBox, ui.card({
      title: '巡检任务',
      sub: '按截止时间排序，逐项判定后提交；异常项将自动转工单',
      body: ui.el('div', { class: 'stack-sm' }, toolbar, content),
    }));
  }

  /* ---------------- 巡检计划 ---------------- */
  function renderPlans() {
    const cards = plans.map((p) => ui.card({
      title: p.name,
      sub: `${CYCLE_TEXT[p.cycle] || p.cycle} · ${p.timeOfDay || '—'} 开始 · 共 ${(p.items || []).length} 项`,
      actions: [
        p.enabled ? ui.badge('已启用', 'good') : ui.badge('已停用', 'muted'),
        canExecute ? ui.el('button', {
          class: 'btn sm primary', type: 'button', onclick: () => openCreateTask(p),
        }, '创建任务') : null,
      ],
      body: ui.el('div', { class: 'plan-items' }, (p.items || []).map((it, i) => ui.el('div', { class: 'plan-item' },
        ui.el('span', { class: 'plan-idx mono' }, String(i + 1).padStart(2, '0')),
        ui.el('div', { class: 'plan-item-text' },
          ui.el('div', { class: 'plan-item-label' }, it.label),
          it.standard ? ui.el('div', { class: 'plan-item-standard' }, `标准：${it.standard}`) : null,
        ),
      ))),
    }));

    ui.mount(mainBox, cards.length
      ? ui.el('div', { class: 'grid cols-2 plan-grid' }, cards)
      : ui.card({ body: ui.empty('暂无巡检计划', '巡检计划由系统预置，可在任务页签生成巡检任务。', 'inspection') }));
  }

  /* ---------------- 加载 ---------------- */
  async function loadTasks() {
    try {
      const res = await ctx.api.getFull('/api/inspections', { siteId: ctx.siteId });
      tasks = res.data || [];
      taskMeta = res.meta || taskMeta;
    } catch (e) {
      tasks = [];
      ui.mount(mainBox, ui.errorState(e, async () => { await loadTasks(); renderBody(); }));
      return;
    }
    renderKpis();
    if (state.tab === 'tasks') renderTasks();
  }

  async function loadPlans() {
    try {
      plans = (await ctx.api.get('/api/inspection-plans', { siteId: ctx.siteId })) || [];
    } catch (e) {
      plans = [];
    }
    renderKpis();
    if (state.tab === 'plans') renderPlans();
  }

  async function refreshAll() {
    await Promise.all([loadTasks(), loadPlans()]);
    renderBody();
  }

  /* ---------------- 任务详情 / 执行 ---------------- */
  async function openTask(id) {
    const body = ui.el('div', {});
    const draw = ui.drawer({ title: '巡检任务', sub: '正在加载…', body });
    const fill = async () => {
      ui.mount(body, ui.skeleton(5, 'line'));
      try {
        const task = await ctx.api.get(`/api/inspections/${encodeURIComponent(id)}`);
        const h3 = draw.box.querySelector('.drawer-head h3');
        if (h3) h3.textContent = task.title;
        const sub = draw.box.querySelector('.drawer-head .card-sub');
        if (sub) sub.textContent = `${task.id} · ${task.siteName || ''} · ${task.assigneeName || '未指派'}`;
        ui.mount(body, executionView(task, draw));
      } catch (err) {
        ui.mount(body, ui.errorState(err, fill));
      }
    };
    await fill();
  }

  function executionView(task, draw) {
    const done = task.status === 'done';
    const mine = !task.assigneeId || task.assigneeId === ctx.user.id || isManager(ctx);
    const editable = !done && canExecute && mine;
    const items = task.items || [];
    const inputs = new Map();
    for (const it of items) inputs.set(it.key, { result: it.result || '', note: it.note || '' });
    const rowEls = new Map();

    const parts = [];
    parts.push(ui.el('div', { class: 'stack-sm' },
      ui.el('div', { class: 'row wrap' },
        done ? ui.badge('已完成', 'good') : ui.badge('待执行', 'warn'),
        ui.badge(`${task.doneCount || 0}/${task.totalCount || items.length} 项已填`, 'muted'),
        task.overdue ? ui.badge('已超时', 'bad') : null,
      ),
      ui.kv([
        ['任务编号', ui.el('span', { class: 'mono strong' }, task.id)],
        ['所属计划', task.planName || '—'],
        ['站点 / 类型', `${task.siteName || '—'} · ${CYCLE_TEXT[task.type] || task.type || '—'}`],
        ['执行人', task.assigneeName || '未指派'],
        ['截止时间', ui.el('span', { class: task.overdue ? 'text-bad' : '' }, ui.fmt.time(task.dueAt))],
        ['完成时间', task.doneAt ? ui.fmt.time(task.doneAt) : '—'],
      ]),
    ));

    if (!canExecute) parts.push(ui.notice('当前账号没有巡检执行权限（inspection.execute），仅可查看。', 'warn'));
    else if (!mine) parts.push(ui.notice(`该任务已指派给 ${task.assigneeName || task.assigneeId}，仅执行人或管理员可提交。`, 'warn'));
    else if (done) parts.push(ui.notice('该巡检已完成，以下为提交结果。', ''));

    const list = ui.el('div', { class: 'insp-items' });
    items.forEach((it, idx) => {
      const stateObj = inputs.get(it.key);
      const row = ui.el('div', { class: 'insp-item' });
      row.appendChild(ui.el('div', { class: 'insp-item-head' },
        ui.el('span', { class: 'plan-idx mono' }, String(idx + 1).padStart(2, '0')),
        ui.el('div', { class: 'insp-item-title' },
          ui.el('div', { class: 'cell-main' }, it.label),
          it.standard ? ui.el('div', { class: 'cell-sub' }, `标准：${it.standard}`) : null,
        ),
      ));

      if (done) {
        row.appendChild(ui.el('div', { class: 'insp-item-body' },
          ui.el('div', { class: 'row wrap' },
            resultBadge(it.result),
            it.note ? ui.el('span', { class: 'small muted' }, it.note) : null,
          ),
        ));
      } else {
        const segBox = ui.el('div', {});
        const note = ui.textarea({ rows: 2, placeholder: '现场情况说明（异常项建议填写）' });
        note.value = stateObj.note;
        note.disabled = !editable;
        note.addEventListener('input', () => { stateObj.note = note.value; });
        const paint = () => {
          ui.mount(segBox, ui.segmented(
            [
              { value: 'ok', label: '正常' },
              { value: 'attention', label: '需关注' },
              { value: 'abnormal', label: '异常' },
            ],
            stateObj.result,
            (v) => {
              if (!editable) return;
              stateObj.result = v;
              row.classList.remove('missing');
              paint();
            },
          ));
        };
        paint();
        row.appendChild(ui.el('div', { class: 'insp-item-body' }, segBox, note));
      }
      rowEls.set(it.key, row);
      list.appendChild(row);
    });

    parts.push(ui.card({
      title: '巡检项',
      sub: `共 ${items.length} 项，逐项选择判定结果并填写备注`,
      body: list,
    }));

    if (done) {
      parts.push(ui.card({
        title: '巡检小结',
        body: ui.el('p', { class: 'pre-wrap' }, task.summary || '（未填写小结）'),
      }));
      return ui.el('div', { class: 'stack' }, parts);
    }

    if (!editable) return ui.el('div', { class: 'stack' }, parts);

    const summary = ui.textarea({ rows: 2, placeholder: '本次巡检小结（可选）' });
    summary.value = task.summary || '';
    let busy = false;
    const submitBtn = ui.el('button', { class: 'btn primary', type: 'button' }, `提交巡检（${items.length} 项）`);
    submitBtn.addEventListener('click', async () => {
      const missing = items.filter((it) => !inputs.get(it.key).result);
      if (missing.length) {
        missing.forEach((it) => {
          const row = rowEls.get(it.key);
          if (row) row.classList.add('missing');
        });
        ctx.toast(`还有 ${missing.length} 项未判定`, 'error');
        const first = rowEls.get(missing[0].key);
        if (first && first.scrollIntoView) first.scrollIntoView({ block: 'center', behavior: 'smooth' });
        return;
      }
      if (busy) return;
      busy = true;
      submitBtn.disabled = true;
      submitBtn.textContent = '提交中…';
      try {
        const res = await ctx.api.post(`/api/inspections/${encodeURIComponent(task.id)}/submit`, {
          items: items.map((it) => ({
            key: it.key,
            result: inputs.get(it.key).result,
            note: inputs.get(it.key).note,
          })),
          summary: summary.value.trim(),
        });
        const abnormal = (res && res.abnormal) || [];
        const workorder = res && res.workorder;
        draw.close();
        await refreshAll();
        if (workorder) {
          let m = null;
          m = ctx.modal({
            title: '巡检已提交',
            body: ui.el('div', { class: 'stack-sm' },
              ui.notice(`发现 ${abnormal.length} 项异常，系统已自动生成工单 ${workorder.id}。`, 'warn'),
              ui.el('p', { class: 'small muted' }, `异常项：${abnormal.join('、')}`),
            ),
            footer: ui.el('div', { class: 'row', style: { width: '100%' } },
              ui.el('span', { class: 'spacer' }),
              ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '关闭'),
              ui.el('button', {
                class: 'btn primary', type: 'button',
                onclick: () => { m.close(); ctx.go(`#/workorders?id=${encodeURIComponent(workorder.id)}`); },
              }, '查看工单'),
            ),
          });
        } else {
          ctx.toast('巡检已提交，未发现异常', 'success');
        }
      } catch (e) {
        ctx.toast((e && e.message) || '提交失败', 'error');
        busy = false;
        submitBtn.disabled = false;
        submitBtn.textContent = `提交巡检（${items.length} 项）`;
      }
    });

    parts.push(ui.card({
      title: '提交',
      sub: '提交后不可修改；若存在异常项，系统会同步生成待接单工单',
      body: ui.el('div', { class: 'form-grid one' },
        ui.field('巡检小结', summary),
        ui.el('div', { class: 'row' }, ui.el('span', { class: 'spacer' }), submitBtn),
      ),
    }));

    return ui.el('div', { class: 'stack' }, parts);
  }

  /* ---------------- 新建巡检任务 ---------------- */
  async function openCreateTask(presetPlan) {
    if (!canExecute) { ctx.toast('没有巡检执行权限', 'error'); return; }
    if (!plans.length) await loadPlans();
    if (!plans.length) { ctx.toast('暂无巡检计划，无法创建任务', 'error'); return; }
    const candidates = await assigneeOptions(ctx, ctx.siteId);
    const planSel = ui.select(plans.map((p) => ({
      value: p.id,
      label: `${p.name}（${CYCLE_TEXT[p.cycle] || p.cycle}）`,
      selected: presetPlan ? p.id === presetPlan.id : p.enabled,
    })), {});
    const assignee = ui.select([{ value: '', label: '由我执行（默认）' }].concat(candidates), {});
    const due = ui.input({ type: 'datetime-local', value: toLocalInput(Date.now() + 8 * 3600000) });
    const err = ui.el('div', { class: 'error-text' });
    let busy = false;
    const submitBtn = ui.el('button', { class: 'btn primary', type: 'button' }, '创建任务');
    let m = null;
    m = ctx.modal({
      title: '新建巡检任务',
      sub: ctx.site ? ctx.site.name : '',
      body: ui.el('div', { class: 'form-grid one' },
        ui.field('巡检计划', planSel, '按计划内的检查项生成任务'),
        ui.field('执行人', assignee, '不指定时由当前账号执行'),
        ui.field('截止时间', due),
        err,
      ),
      footer: ui.el('div', { class: 'row', style: { width: '100%' } },
        ui.el('span', { class: 'spacer' }),
        ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
        submitBtn,
      ),
    });
    submitBtn.addEventListener('click', async () => {
      if (!planSel.value) { err.textContent = '请选择巡检计划'; return; }
      if (busy) return;
      busy = true;
      submitBtn.disabled = true;
      submitBtn.textContent = '创建中…';
      try {
        const task = await ctx.api.post('/api/inspections', {
          planId: planSel.value,
          assigneeId: assignee.value || undefined,
          dueAt: due.value ? new Date(due.value).toISOString() : undefined,
        });
        ctx.toast(`巡检任务 ${task.id} 已创建`, 'success');
        m.close();
        state.tab = 'tasks';
        renderTabs();
        await refreshAll();
        openTask(task.id);
      } catch (e) {
        err.textContent = (e && e.message) || '创建失败';
        busy = false;
        submitBtn.disabled = false;
        submitBtn.textContent = '创建任务';
      }
    });
  }
}

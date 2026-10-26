/* 手机端视图：面向现场值班的窄屏工作台（工单、告警、巡检、消息） */
import * as ui from '../ui.js';

export const meta = { title: '手机端视图' };

const hasPerm = (ctx, key) => ((ctx.user && ctx.user.perms) || []).includes(key);
const OPEN_STATUS = new Set(['pending', 'accepted', 'processing', 'done']);
const ALARM_LEVEL = {
  critical: ['紧急', 'bad'],
  major: ['重要', 'warn'],
  minor: ['次要', 'info'],
  info: ['提示', 'muted'],
  warn: ['警告', 'warn'],
  bad: ['严重', 'bad'],
};

function levelBadge(level) {
  const hit = ALARM_LEVEL[level] || [level || '—', 'muted'];
  return ui.badge(hit[0], hit[1]);
}

function taskStatusBadge(status) {
  const map = { pending: ['待执行', 'warn'], done: ['已完成', 'good'] };
  const hit = map[status] || ['—', 'muted'];
  return ui.badge(hit[0], hit[1]);
}

export async function render(root, ctx) {
  const canWorkorder = hasPerm(ctx, 'workorder.handle');
  const canAlarm = hasPerm(ctx, 'alarm.handle');
  const canInspect = hasPerm(ctx, 'inspection.execute');
  const boxes = {};

  const refreshAll = ui.debounce(() => {
    loadWorkorders();
    loadAlarms();
    loadInspections();
    loadNotifications();
  }, 300);

  ui.mount(root, ui.el('div', { class: 'p-mobile' },
    ui.el('div', { class: 'mobile-frame' },
      ui.el('div', { class: 'mobile-preview-tip' },
        ui.el('button', {
          class: 'btn sm ghost', type: 'button',
          onclick: () => ctx.go('#/dashboard'),
        }, ui.icon('dashboard'), '返回桌面端'),
      ),
      ui.el('div', { class: 'mobile-head' },
        ui.el('div', {},
          ui.el('div', { class: 'mobile-title' }, '现场值班台'),
          ui.el('div', { class: 'mobile-sub' },
            `${(ctx.user && ctx.user.name) || '值班人员'} · ${ctx.site ? ctx.site.name : '当前站点'}`),
        ),
        ui.el('button', {
          class: 'btn sm', type: 'button', onclick: refreshAll,
        }, ui.icon('refresh'), '刷新'),
      ),
      ui.el('div', { class: 'm-body' },
        (boxes.kpis = ui.el('div', { class: 'm-kpis' })),
        (boxes.workorders = ui.el('div', { class: 'm-section', dataset: { section: 'workorders' } })),
        (boxes.alarms = ui.el('div', { class: 'm-section', dataset: { section: 'alarms' } })),
        (boxes.inspections = ui.el('div', { class: 'm-section', dataset: { section: 'inspections' } })),
        (boxes.notifications = ui.el('div', { class: 'm-section', dataset: { section: 'notifications' } })),
        ui.el('div', { class: 'mobile-foot' },
          ui.el('button', {
            class: 'btn block', type: 'button', onclick: () => ctx.go('#/dashboard'),
          }, ui.icon('dashboard'), '返回桌面端'),
          ui.el('div', { class: 'tiny muted mobile-foot-note' }, '数据与桌面端同源；操作受账号权限与站点范围限制。'),
        ),
      ),
      ui.el('nav', { class: 'm-tabbar' },
        navBtn(boxes.workorders, 'workorder', '工单'),
        navBtn(boxes.alarms, 'alarm', '告警'),
        navBtn(boxes.inspections, 'inspection', '巡检'),
        navBtn(boxes.notifications, 'bell', '消息'),
      ),
    ),
  ));

  ui.mount(boxes.kpis, [0, 1, 2, 3].map(() => ui.skeleton(1, 'kpi')));
  boxes.workorders.appendChild(skeletonCard('工单待办'));
  boxes.alarms.appendChild(skeletonCard('待确认告警'));
  boxes.inspections.appendChild(skeletonCard('今日巡检'));
  boxes.notifications.appendChild(skeletonCard('未读消息'));

  const stats = { pending: 0, active: 0, alarms: 0, tasks: 0 };
  await Promise.all([loadWorkorders(), loadAlarms(), loadInspections(), loadNotifications()]);
  renderKpis();

  ctx.subscribe((evt) => {
    const type = (evt && (evt.type || evt.event)) || '';
    if (/workorder|alarm|inspection|notification/i.test(type)) refreshAll();
  });

  function navBtn(target, iconName, label) {
    return ui.el('button', {
      class: 'm-tab', type: 'button',
      onclick: () => {
        if (target.scrollIntoView) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      },
    }, ui.icon(iconName), ui.el('span', {}, label));
  }

  function skeletonCard(title) {
    return ui.card({ title, body: ui.skeleton(3, 'line'), class: 'm-card' });
  }

  function renderKpis() {
    ui.mount(boxes.kpis,
      mobileKpi('待接单', stats.pending, 'warn'),
      mobileKpi('处理中', stats.active, 'info'),
      mobileKpi('待确认告警', stats.alarms, stats.alarms ? 'bad' : ''),
      mobileKpi('待执行巡检', stats.tasks, stats.tasks ? 'warn' : ''),
    );
  }

  function mobileKpi(label, value, tone) {
    return ui.el('div', { class: `m-kpi ${tone ? `tone-${tone}` : ''}` },
      ui.el('div', { class: 'm-kpi-value num' }, String(value)),
      ui.el('div', { class: 'm-kpi-label' }, label),
    );
  }

  /* ---------------- 我的工单 ---------------- */
  async function loadWorkorders() {
    try {
      const res = await ctx.api.getFull('/api/workorders', {
        siteId: ctx.siteId, status: 'open', pageSize: 50,
      });
      const meId = ctx.user && ctx.user.id;
      // 只保留与我相关且当前角色可处理的：指派给我，或未指派且我有处置权
      const rows = (res.data || []).filter((w) => OPEN_STATUS.has(w.status)
        && (w.assigneeId === meId || (!w.assigneeId && canWorkorder)));
      stats.pending = rows.filter((w) => w.status === 'pending').length;
      stats.active = rows.filter((w) => w.status === 'accepted' || w.status === 'processing').length;
      renderKpis();
      ui.mount(boxes.workorders, ui.card({
        title: '工单待办',
        sub: canWorkorder
          ? `与我相关（可处理）的未关闭工单 · 共 ${rows.length} 条`
          : `指派给我的未关闭工单 · 共 ${rows.length} 条`,
        class: 'm-card',
        actions: [ui.el('button', { class: 'btn sm ghost', type: 'button', onclick: () => ctx.go('#/workorders') }, '全部')],
        body: rows.length
          ? ui.el('div', { class: 'm-list' }, rows.slice(0, 6).map(workorderItem))
          : ui.empty('暂无相关工单', canWorkorder ? '当前没有指派给你或你可接单的未关闭工单。' : '当前没有指派给你的未关闭工单。', 'workorder'),
      }));
    } catch (e) {
      ui.mount(boxes.workorders, ui.card({ title: '工单待办', class: 'm-card', body: ui.errorState(e, loadWorkorders) }));
    }
  }

  function workorderItem(w) {
    const actions = ui.el('div', { class: 'm-actions' });
    if (canWorkorder) {
      if (w.status === 'pending') actions.appendChild(mBtn('接单', 'primary', () => transition(w, 'accept')));
      if (w.status === 'accepted') actions.appendChild(mBtn('开始处理', 'primary', () => transition(w, 'start')));
      if (w.status === 'processing') actions.appendChild(mBtn('完成', 'primary', () => finishWorkorder(w)));
      if (w.status === 'done') actions.appendChild(mBtn('验收', 'primary', () => transition(w, 'verify')));
    }
    actions.appendChild(mBtn('查看', 'ghost', () => ctx.go(`#/workorders?id=${encodeURIComponent(w.id)}`)));

    return ui.el('div', { class: 'm-item' },
      ui.el('div', { class: 'm-item-head' },
        ui.woStatus(w.status),
        ui.badge(w.priorityName || w.priority || '—', w.priority === 'high' || w.priority === 'urgent' ? 'bad' : 'muted'),
        ui.el('span', { class: 'spacer' }),
        ui.el('span', { class: 'mono tiny muted m-item-id' }, w.id),
      ),
      ui.el('div', { class: 'm-item-title' }, w.title),
      ui.el('div', { class: 'm-item-meta' },
        ui.el('span', { class: 'm-item-unit' }, `${w.unitName || w.unitCode || '未指定单元'} · ${w.assigneeName || '未指派'}`),
        ui.el('span', { class: w.overdue ? 'amount-bad' : '' },
          `截止 ${w.dueAt ? ui.fmt.time(w.dueAt) : '—'}${w.overdue ? '（已超时）' : ''}`),
      ),
      actions,
    );
  }

  function mBtn(label, kind, onclick) {
    return ui.el('button', {
      class: `btn sm m-btn ${kind === 'primary' ? 'primary' : 'ghost'}`, type: 'button', onclick,
    }, label);
  }

  async function transition(w, action) {
    try {
      await ctx.api.post(`/api/workorders/${encodeURIComponent(w.id)}/${action}`, {});
      ctx.toast(`工单 ${w.id} ${{ accept: '已接单', start: '已开始处理', verify: '已验收' }[action]}`, 'success');
      loadWorkorders();
      loadNotifications();
    } catch (e) {
      ctx.toast((e && e.message) || '操作失败', 'error');
    }
  }

  function finishWorkorder(w) {
    const result = ui.textarea({ rows: 3, placeholder: '处理结果与现场情况（必填）' });
    const err = ui.el('div', { class: 'error-text' });
    const submit = ui.el('button', { class: 'btn primary', type: 'button' }, '提交完成');
    let m = null;
    m = ctx.modal({
      title: `完成工单 · ${w.id}`,
      sub: '提交后工单进入待验收，由负责人验收关闭',
      body: ui.el('div', { class: 'form-grid one' }, ui.field('处理结果', result, '必填'), err),
      footer: ui.el('div', { class: 'row', style: { width: '100%' } },
        ui.el('span', { class: 'spacer' }),
        ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
        submit,
      ),
    });
    submit.addEventListener('click', async () => {
      if (!result.value.trim()) { err.textContent = '请填写处理结果'; return; }
      submit.disabled = true;
      try {
        await ctx.api.post(`/api/workorders/${encodeURIComponent(w.id)}/finish`, { result: result.value.trim() });
        ctx.toast(`工单 ${w.id} 已提交完成`, 'success');
        m.close();
        loadWorkorders();
      } catch (e) {
        err.textContent = (e && e.message) || '提交失败';
        submit.disabled = false;
      }
    });
  }

  /* ---------------- 待确认告警 ---------------- */
  async function loadAlarms() {
    try {
      const res = await ctx.api.getFull('/api/alarms', { siteId: ctx.siteId, status: 'open', pageSize: 20 });
      const rows = res.data || [];
      stats.alarms = (res.meta && res.meta.total !== undefined) ? res.meta.total : rows.length;
      renderKpis();
      ui.mount(boxes.alarms, ui.card({
        title: '待确认告警',
        sub: `未关闭告警（含已确认） · ${stats.alarms} 条`,
        class: 'm-card',
        actions: [ui.el('button', { class: 'btn sm ghost', type: 'button', onclick: () => ctx.go('#/alarms') }, '全部')],
        body: rows.length
          ? ui.el('div', { class: 'm-list' }, rows.slice(0, 5).map(alarmItem))
          : ui.empty('暂无待确认告警', '当前没有未关闭的告警。', 'alarm'),
      }));
    } catch (e) {
      ui.mount(boxes.alarms, ui.card({ title: '待确认告警', class: 'm-card', body: ui.errorState(e, loadAlarms) }));
    }
  }

  function alarmItem(a) {
    return ui.el('div', { class: 'm-item' },
      ui.el('div', { class: 'm-item-head' },
        levelBadge(a.level),
        ui.alarmStatus(a.status),
        ui.el('span', { class: 'spacer' }),
        ui.el('span', { class: 'mono tiny muted' }, a.id),
      ),
      ui.el('div', { class: 'm-item-title' }, a.title || a.message || '告警'),
      ui.el('div', { class: 'm-item-meta' },
        ui.el('span', {}, `${a.unitName || a.unitCode || a.source || '—'} · ${ui.fmt.time(a.at || a.createdAt)}`),
      ),
      ui.el('div', { class: 'm-actions' },
        canAlarm ? mBtn('确认', 'primary', () => ackAlarm(a)) : null,
        mBtn('查看', 'ghost', () => ctx.go('#/alarms')),
      ),
    );
  }

  async function ackAlarm(a) {
    try {
      await ctx.api.post(`/api/alarms/${encodeURIComponent(a.id)}/ack`, { note: '现场值班确认' });
      ctx.toast(`告警 ${a.id} 已确认`, 'success');
      loadAlarms();
    } catch (e) {
      ctx.toast((e && e.message) || '确认失败', 'error');
    }
  }

  /* ---------------- 今日巡检 ---------------- */
  async function loadInspections() {
    try {
      const res = await ctx.api.getFull('/api/inspections', {
        siteId: ctx.siteId, status: 'pending', assignee: 'me',
      });
      let rows = res.data || [];
      stats.tasks = rows.length;
      renderKpis();
      const today = new Date();
      const sameDay = (t) => {
        if (!t) return false;
        const d = new Date(t);
        return d.getFullYear() === today.getFullYear() && d.getMonth() === today.getMonth() && d.getDate() === today.getDate();
      };
      rows = rows.sort((a, b) => String(a.dueAt).localeCompare(String(b.dueAt)));
      const todayRows = rows.filter((t) => sameDay(t.dueAt)).concat(rows.filter((t) => !sameDay(t.dueAt)));
      ui.mount(boxes.inspections, ui.card({
        title: '今日巡检任务',
        sub: `指派给我且未执行 · ${rows.length} 项`,
        class: 'm-card',
        actions: [ui.el('button', { class: 'btn sm ghost', type: 'button', onclick: () => ctx.go('#/inspections') }, '全部')],
        body: todayRows.length
          ? ui.el('div', { class: 'm-list' }, todayRows.slice(0, 5).map(inspectionItem))
          : ui.empty('今日暂无巡检任务', '可在桌面端巡检管理生成任务。', 'inspection'),
      }));
    } catch (e) {
      ui.mount(boxes.inspections, ui.card({ title: '今日巡检任务', class: 'm-card', body: ui.errorState(e, loadInspections) }));
    }
  }

  function inspectionItem(t) {
    return ui.el('div', { class: 'm-item' },
      ui.el('div', { class: 'm-item-head' },
        taskStatusBadge(t.status),
        t.overdue ? ui.badge('已超时', 'bad') : null,
        ui.el('span', { class: 'spacer' }),
        ui.el('span', { class: 'mono tiny muted' }, t.id),
      ),
      ui.el('div', { class: 'm-item-title' }, t.title),
      ui.el('div', { class: 'm-item-meta' },
        ui.el('span', {}, `${t.doneCount || 0}/${t.totalCount || 0} 项 · 截止 ${t.dueAt ? ui.fmt.time(t.dueAt) : '—'}`),
      ),
      ui.el('div', { class: 'm-actions' },
        canInspect ? mBtn('进入执行', 'primary', () => ctx.go('#/inspections')) : null,
        mBtn('查看', 'ghost', () => ctx.go('#/inspections')),
      ),
    );
  }

  /* ---------------- 未读消息 ---------------- */
  async function loadNotifications() {
    try {
      const res = await ctx.api.getFull('/api/notifications', { unread: 1, pageSize: 10 });
      const rows = res.data || [];
      const unread = (res.meta && res.meta.unread !== undefined) ? res.meta.unread : rows.length;
      ui.mount(boxes.notifications, ui.card({
        title: '未读消息',
        sub: unread ? `${unread} 条未读 · 点击消息标记已读` : '全部消息已读',
        class: 'm-card',
        actions: unread ? [ui.el('button', {
          class: 'btn sm ghost', type: 'button', onclick: readAll,
        }, '全部已读')] : [],
        body: rows.length
          ? ui.el('div', { class: 'm-list' }, rows.map(notificationItem))
          : ui.empty('没有未读消息', '新的告警、工单与化验通知会显示在这里。', 'bell'),
      }));
    } catch (e) {
      ui.mount(boxes.notifications, ui.card({ title: '未读消息', class: 'm-card', body: ui.errorState(e, loadNotifications) }));
    }
  }

  function notificationItem(n) {
    return ui.el('div', {
      class: 'm-item m-msg',
      onclick: async () => {
        try {
          await ctx.api.post('/api/notifications/read', { ids: [n.id] });
          loadNotifications();
          const to = linkHash(n.link);
          if (to) ctx.go(to);
        } catch (e) {
          ctx.toast((e && e.message) || '操作失败', 'error');
        }
      },
    },
    ui.el('div', { class: 'm-item-head' },
      ui.dot(n.read ? 'muted' : 'info', !n.read),
      ui.el('span', { class: 'tiny muted' }, ui.fmt.time(n.at)),
      ui.el('span', { class: 'spacer' }),
      ui.el('span', { class: 'tiny muted' }, n.type || ''),
    ),
    ui.el('div', { class: 'm-item-title' }, n.title || '消息'),
    ui.el('div', { class: 'm-item-body small muted' }, n.body || ''));
  }

  function linkHash(link) {
    if (!link) return '';
    if (link.startsWith('#')) return link;
    if (link.startsWith('/lab')) return `#/quality${link.includes('id=') ? `?${link.split('?')[1]}` : ''}`;
    if (link.startsWith('/workorders')) return `#/workorders${link.includes('id=') ? `?${link.split('?')[1]}` : ''}`;
    if (link.startsWith('/alarms')) return '#/alarms';
    return '';
  }

  async function readAll() {
    try {
      await ctx.api.post('/api/notifications/read', { all: true });
      ctx.toast('已全部标记为已读', 'success');
      loadNotifications();
    } catch (e) {
      ctx.toast((e && e.message) || '操作失败', 'error');
    }
  }
}

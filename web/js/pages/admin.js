/* 系统管理：用户、角色、平台设置、通知通道、审计日志与备份 */
import * as ui from '../ui.js';

export const meta = { title: '系统管理' };

const PERM_DEFS = [
  ['view', '查看数据'],
  ['control', '设备控制'],
  ['alarm.handle', '告警处置'],
  ['workorder.handle', '工单处置'],
  ['param.edit', '工艺参数编辑'],
  ['report.export', '报表导出'],
  ['lab.review', '化验复核'],
  ['inspection.execute', '巡检执行'],
  ['admin.users', '用户管理'],
  ['admin.config', '系统配置'],
  ['admin.audit', '审计查看'],
];
const ROLE_STATUS = { active: ['正常', 'good'], disabled: ['已停用', 'muted'] };
const AUDIT_RESULT = { success: ['成功', 'good'], fail: ['失败', 'bad'], denied: ['拒绝', 'warn'] };
const CHANNEL_TYPE = { inapp: '站内', sms: '短信', wechat: '企业微信', dingtalk: '钉钉', email: '邮件', webhook: 'Webhook' };

const hasPerm = (ctx, key) => ((ctx.user && ctx.user.perms) || []).includes(key);

function guardBody(title, perm) {
  return ui.card({
    title,
    body: ui.notice(`当前账号没有「${title}」所需权限（${perm}），请联系平台管理员开通。`, 'warn'),
  });
}

function maskIp(ip) {
  return ip || '—';
}

export async function render(root, ctx) {
  const tabs = [
    { value: 'users', label: '用户' },
    { value: 'roles', label: '角色' },
    { value: 'settings', label: '设置' },
    { value: 'channels', label: '通知通道' },
    { value: 'audit', label: '审计日志' },
    { value: 'backups', label: '备份' },
  ];
  const state = { tab: 'users' };
  const tabsBox = ui.el('div', {});
  const bodyBox = ui.el('div', {});

  ui.mount(root, ui.el('div', { class: 'p-admin stack' },
    ui.pageHead('系统管理', '平台用户、角色权限、运行配置与数据安全 · 变更均写入审计日志', [
      ui.el('button', { class: 'btn', type: 'button', onclick: () => switchTab(state.tab) }, ui.icon('refresh'), '刷新'),
    ]),
    tabsBox,
    bodyBox,
  ));

  renderTabs();
  switchTab('users');

  function renderTabs() {
    ui.mount(tabsBox, ui.tabs(tabs, state.tab, (v) => switchTab(v)));
  }

  function switchTab(tab) {
    state.tab = tab;
    renderTabs();
    const fn = {
      users: usersTab,
      roles: rolesTab,
      settings: settingsTab,
      channels: channelsTab,
      audit: auditTab,
      backups: backupsTab,
    }[tab];
    ui.mount(bodyBox, ui.loading('加载中…'));
    fn();
  }

  /* ================= 用户 ================= */
  async function usersTab() {
    if (!hasPerm(ctx, 'admin.users')) { ui.mount(bodyBox, guardBody('用户管理', 'admin.users')); return; }
    let users = [];
    let roleList = [];
    try {
      const res = await ctx.api.getFull('/api/users');
      users = res.data || [];
      roleList = (res.meta && res.meta.roles) || [];
    } catch (e) {
      ui.mount(bodyBox, ui.errorState(e, usersTab));
      return;
    }
    const filters = { q: '', role: '' };
    const countLabel = ui.el('span', { class: 'tiny muted' }, '');
    const tableBox = ui.el('div', {});

    const search = ui.input({ type: 'search', placeholder: '搜索账号 / 姓名 / 手机号' });
    search.addEventListener('input', ui.debounce(() => {
      filters.q = search.value.trim().toLowerCase();
      paint();
    }, 180));

    ui.mount(bodyBox, ui.card({
      title: '用户列表',
      sub: `共 ${users.length} 个账号，数据范围由角色与所属站点共同决定`,
      actions: [ui.el('button', { class: 'btn primary sm', type: 'button', onclick: () => openUserModal(null) }, '新增用户')],
      body: ui.el('div', { class: 'stack-sm' },
        ui.el('div', { class: 'toolbar' },
          ui.el('div', { class: 'search-field' }, ui.icon('search'), search),
          ui.select([{ value: '', label: '全部角色', selected: true }].concat(roleList.map((r) => ({ value: r.id, label: r.name }))), {
            onchange: (e) => { filters.role = e.target.value; paint(); },
          }),
          ui.el('span', { class: 'spacer' }),
          countLabel,
        ),
        tableBox,
      ),
    }));

    function paint() {
      const rows = users.filter((u) => {
        if (filters.role && u.role !== filters.role) return false;
        if (!filters.q) return true;
        return [u.username, u.name, u.phone, u.title].filter(Boolean).join(' ').toLowerCase().includes(filters.q);
      });
      countLabel.textContent = `显示 ${rows.length} / ${users.length} 个`;
      if (!rows.length) {
        ui.mount(tableBox, ui.empty('没有符合条件的用户', '调整搜索或角色筛选。', 'admin'));
        return;
      }
      ui.mount(tableBox, ui.table({
        columns: [
          {
            key: 'username', label: '账号', width: '150px', render: (u) => ui.el('div', {},
              ui.el('div', { class: 'cell-main mono' }, u.username),
              ui.el('div', { class: 'cell-sub' }, u.title || '—'),
            ),
          },
          { key: 'name', label: '姓名', width: '96px', render: (u) => ui.el('span', { class: 'strong' }, u.name) },
          { key: 'roleName', label: '角色', width: '130px', render: (u) => ui.el('span', { class: 'small' }, u.roleName || u.role) },
          {
            key: 'siteNames', label: '站点范围', width: '180px', render: (u) => ui.el('div', { class: 'row wrap' },
              (u.siteNames && u.siteNames.length ? u.siteNames : ['—']).map((s) => ui.el('span', { class: 'chip' }, s)),
            ),
          },
          {
            key: 'status', label: '状态', width: '84px', render: (u) => {
              const hit = ROLE_STATUS[u.status] || ['未知', 'muted'];
              return ui.badge(hit[0], hit[1]);
            },
          },
          { key: 'lastLoginAt', label: '最近登录', width: '118px', render: (u) => ui.el('span', { class: 'mono tiny' }, u.lastLoginAt ? ui.fmt.time(u.lastLoginAt) : '从未登录') },
          {
            key: 'op', label: '操作', width: '210px', render: (u) => ui.el('div', { class: 'row wrap' },
              ui.el('button', { class: 'btn sm', type: 'button', onclick: () => openUserModal(u) }, '编辑'),
              ui.el('button', { class: 'btn sm', type: 'button', onclick: () => openResetModal(u) }, '重置密码'),
              ui.el('button', {
                class: `btn sm ${u.status === 'active' ? 'danger' : ''}`, type: 'button',
                onclick: () => toggleUser(u),
              }, u.status === 'active' ? '停用' : '启用'),
            ),
          },
        ],
        rows,
        rowClass: (u) => (u.status === 'disabled' ? 'row-attention' : ''),
      }));
    }
    paint();

    function openUserModal(user) {
      const editing = !!(user && user.id);
      const username = ui.input({ placeholder: '登录账号，3-20 位小写字母/数字/下划线', value: editing ? user.username : '', disabled: editing });
      const name = ui.input({ placeholder: '真实姓名', value: editing ? user.name : '' });
      const title = ui.input({ placeholder: '岗位，如：运维值班员', value: editing ? user.title || '' : '' });
      const phone = ui.input({ placeholder: '手机号（可选）', value: editing ? user.phone || '' : '' });
      const roleSel = ui.select(roleList.map((r) => ({ value: r.id, label: r.name, selected: editing && user.role === r.id })), {});
      const password = ui.input({ type: 'password', placeholder: '至少 6 位' });
      const siteBox = ui.el('div', { class: 'check-grid' }, (ctx.sites || []).map((s) => {
        const checked = editing ? (user.siteIds || []).includes(s.id) : true;
        return ui.el('label', { class: 'check-field' },
          ui.el('input', { type: 'checkbox', value: s.id, checked }),
          s.name || s.id,
        );
      }));
      const err = ui.el('div', { class: 'error-text' });
      const submit = ui.el('button', { class: 'btn primary', type: 'button' }, editing ? '保存修改' : '创建用户');
      let m = null;
      m = ctx.modal({
        title: editing ? `编辑用户 · ${user.username}` : '新增用户',
        sub: editing ? '修改角色与站点范围后，下次登录生效' : '新账号默认启用，登录后按角色获得权限',
        wide: true,
        body: ui.el('div', { class: 'form-grid' },
          ui.field('账号', username, editing ? '账号创建后不可修改' : '3-20 位，小写字母/数字/下划线'),
          ui.field('姓名', name),
          ui.field('岗位', title),
          ui.field('手机号', phone),
          ui.field('角色', roleSel),
          ui.field(editing ? '重置密码' : '初始密码', password, editing ? '留空则不修改密码' : '至少 6 位'),
          ui.field('数据范围（所属站点）', siteBox, '仅可查看所选站点的数据'),
          err,
        ),
        footer: ui.el('div', { class: 'row', style: { width: '100%' } },
          ui.el('span', { class: 'spacer' }),
          ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
          submit,
        ),
      });

      submit.addEventListener('click', async () => {
        const siteIds = Array.from(siteBox.querySelectorAll('input:checked')).map((i) => i.value);
        if (!editing && !/^[a-z0-9_]{3,20}$/.test(username.value.trim())) { err.textContent = '账号需为 3-20 位小写字母、数字或下划线'; return; }
        if (!editing && password.value.length < 6) { err.textContent = '初始密码至少 6 位'; return; }
        if (!name.value.trim()) { err.textContent = '请填写姓名'; return; }
        if (!roleSel.value) { err.textContent = '请选择角色'; return; }
        if (!siteIds.length) { err.textContent = '至少选择一个站点'; return; }
        submit.disabled = true;
        submit.textContent = '提交中…';
        try {
          if (editing) {
            const patch = { name: name.value.trim(), title: title.value.trim(), phone: phone.value.trim(), role: roleSel.value, siteIds };
            await ctx.api.patch(`/api/users/${encodeURIComponent(user.id)}`, patch);
            if (password.value) {
              await ctx.api.post(`/api/users/${encodeURIComponent(user.id)}/reset-password`, { password: password.value });
            }
            ctx.toast('用户信息已更新', 'success');
          } else {
            await ctx.api.post('/api/users', {
              username: username.value.trim(),
              name: name.value.trim(),
              title: title.value.trim(),
              phone: phone.value.trim(),
              role: roleSel.value,
              password: password.value,
              siteIds,
            });
            ctx.toast('用户已创建', 'success');
          }
          m.close();
          usersTab();
        } catch (e) {
          err.textContent = (e && e.message) || '保存失败';
          submit.disabled = false;
          submit.textContent = editing ? '保存修改' : '创建用户';
        }
      });
    }

    function openResetModal(user) {
      const pwd = ui.input({ type: 'password', placeholder: '新密码，至少 6 位' });
      const err = ui.el('div', { class: 'error-text' });
      const submit = ui.el('button', { class: 'btn danger', type: 'button' }, '确认重置');
      let m = null;
      m = ctx.modal({
        title: `重置密码 · ${user.username}`,
        sub: '重置后该账号需使用新密码重新登录',
        body: ui.el('div', { class: 'form-grid one' }, ui.field('新密码', pwd), err),
        footer: ui.el('div', { class: 'row', style: { width: '100%' } },
          ui.el('span', { class: 'spacer' }),
          ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
          submit,
        ),
      });
      submit.addEventListener('click', async () => {
        if (pwd.value.length < 6) { err.textContent = '密码至少 6 位'; return; }
        submit.disabled = true;
        try {
          await ctx.api.post(`/api/users/${encodeURIComponent(user.id)}/reset-password`, { password: pwd.value });
          ctx.toast(`已重置 ${user.username} 的密码`, 'success');
          m.close();
        } catch (e) {
          err.textContent = (e && e.message) || '重置失败';
          submit.disabled = false;
        }
      });
    }

    async function toggleUser(user) {
      if (user.id === ctx.user.id) { ctx.toast('不能停用当前登录账号', 'error'); return; }
      const next = user.status === 'active' ? 'disabled' : 'active';
      const ok = await ctx.confirm({
        title: next === 'disabled' ? '停用账号' : '启用账号',
        message: next === 'disabled'
          ? `确认停用 ${user.name}（${user.username}）？停用后该账号将无法登录，历史操作记录保留。`
          : `确认启用 ${user.name}（${user.username}）？`,
        confirmText: next === 'disabled' ? '停用' : '启用',
        danger: next === 'disabled',
      });
      if (!ok) return;
      try {
        await ctx.api.patch(`/api/users/${encodeURIComponent(user.id)}`, { status: next });
        ctx.toast(next === 'disabled' ? '账号已停用' : '账号已启用', 'success');
        usersTab();
      } catch (e) {
        ctx.toast((e && e.message) || '操作失败', 'error');
      }
    }
  }

  /* ================= 角色 ================= */
  async function rolesTab() {
    let roles = [];
    try {
      roles = (await ctx.api.get('/api/roles')) || [];
    } catch (e) {
      ui.mount(bodyBox, ui.errorState(e, rolesTab));
      return;
    }
    if (!roles.length) {
      ui.mount(bodyBox, ui.card({ title: '角色与权限', body: ui.empty('暂无角色数据', '请检查后端角色配置。', 'admin') }));
      return;
    }
    const matrix = ui.table({
      columns: [
        { key: 'perm', label: '功能权限', width: '180px', render: (p) => ui.el('div', {},
          ui.el('div', { class: 'cell-main' }, p.label),
          ui.el('div', { class: 'cell-sub mono' }, p.key),
        ) },
      ].concat(roles.map((r) => ({
        key: r.id,
        label: r.name,
        width: '120px',
        cls: 'perm-cell',
        render: (p) => (r.perms || []).includes(p.key)
          ? ui.el('span', { class: 'perm-yes' }, '✓')
          : ui.el('span', { class: 'perm-no' }, '—'),
      }))),
      rows: PERM_DEFS.map(([key, label]) => ({ key, label })),
      flush: true,
    });

    ui.mount(bodyBox, ui.el('div', { class: 'stack' },
      ui.card({
        title: '角色与权限矩阵',
        sub: '功能权限按角色分配，数据范围按站点',
        body: matrix,
        flush: false,
      }),
      ui.card({
        title: '角色说明与数据范围',
        body: ui.el('div', { class: 'role-list' }, roles.map((r) => ui.el('div', { class: 'role-item' },
          ui.el('div', { class: 'role-item-head' },
            ui.el('span', { class: 'strong' }, r.name),
            ui.badge(r.sites === 'all' ? '全平台站点' : '授权站点', r.sites === 'all' ? 'accent' : 'info'),
            ui.el('span', { class: 'tiny muted mono' }, r.id),
          ),
          ui.el('p', { class: 'small muted' }, r.desc || '—'),
          ui.el('div', { class: 'row wrap role-perms' }, (r.perms || []).map((p) => {
            const hit = PERM_DEFS.find((x) => x[0] === p);
            return ui.el('span', { class: 'chip' }, hit ? hit[1] : p);
          })),
        ))),
      }),
    ));
  }

  /* ================= 设置 ================= */
  async function settingsTab() {
    const canEdit = hasPerm(ctx, 'admin.config');
    let settings;
    try {
      settings = await ctx.api.get('/api/settings');
    } catch (e) {
      ui.mount(bodyBox, ui.errorState(e, settingsTab));
      return;
    }
    const platformName = ui.input({ value: settings.platformName || '' });
    const shortName = ui.input({ value: settings.shortName || '' });
    const orgName = ui.input({ value: settings.orgName || '' });
    const notice = ui.textarea({ rows: 3 });
    notice.value = settings.notice || '';
    const autoRefresh = ui.input({ type: 'number', min: '2', max: '60', value: settings.autoRefreshSec });
    const cmdTimeout = ui.input({ type: 'number', min: '10', max: '180', value: settings.commandTimeoutSec });
    const retention = ui.input({ type: 'number', min: '1', max: '90', value: settings.dataRetentionDays });
    const offlineCache = ui.input({ type: 'number', min: '5', max: '240', value: settings.offlineCacheMinutes });
    const backupAuto = ui.el('input', { type: 'checkbox', checked: !!(settings.backup && settings.backup.auto) });
    const backupKeep = ui.input({ type: 'number', min: '1', max: '90', value: (settings.backup && settings.backup.keepDays) || 7 });
    const err = ui.el('div', { class: 'error-text' });
    const saveBtn = ui.el('button', { class: 'btn primary', type: 'button', disabled: !canEdit }, '保存设置');

    [platformName, shortName, orgName, notice, autoRefresh, cmdTimeout, retention, offlineCache, backupKeep].forEach((n) => { n.disabled = !canEdit; });
    backupAuto.disabled = !canEdit;

    ui.mount(bodyBox, ui.el('div', { class: 'stack' },
      canEdit ? null : ui.notice('当前账号没有系统配置权限（admin.config），以下设置只读。', 'warn'),
      ui.card({
        title: '平台信息',
        sub: '显示在登录页、侧边栏与对外报表页眉',
        body: ui.el('div', { class: 'form-grid' },
          ui.field('平台名称', platformName, '完整名称，用于页面标题'),
          ui.field('平台简称', shortName, '侧边栏与窄屏显示'),
          ui.field('机构名称', orgName, ''),
          ui.field('平台公告', notice, '展示在仪表盘顶部，留空则不展示'),
          ui.el('div', { class: 'span-2' }, ui.el('div', { class: 'row wrap' },
            ui.kv([
              ['版本', ui.el('span', { class: 'mono' }, settings.version || '—')],
              ['文档版本', ui.el('span', { class: 'mono' }, settings.docVersion || '—')],
              ['演示模式', settings.demoMode ? ui.badge('开启', 'warn') : ui.badge('关闭', 'muted')],
              ['告警 P95 目标', `${settings.alarmP95TargetSec || '—'} 秒`],
            ]),
          )),
        ),
      }),
      ui.card({
        title: '运行参数',
        sub: '影响前端刷新频率、指令超时与数据留存',
        body: ui.el('div', { class: 'form-grid' },
          ui.field('自动刷新秒数', autoRefresh, '2 - 60 秒，仪表盘与实时页轮询间隔'),
          ui.field('命令超时秒数', cmdTimeout, '10 - 180 秒，控制指令等待回执上限'),
          ui.field('数据保留天数', retention, '1 - 90 天，遥测与审计历史留存'),
          ui.field('离线缓存分钟', offlineCache, '5 - 240 分钟，断网时读缓存的有效期'),
        ),
      }),
      ui.card({
        title: '备份策略',
        sub: '备份文件保存在服务器 data/backups 目录',
        body: ui.el('div', { class: 'form-grid' },
          ui.field('自动备份', ui.el('label', { class: 'check-field' }, backupAuto, '每日凌晨自动创建备份'), '关闭时仅支持手动创建'),
          ui.field('保留天数', backupKeep, '1 - 90 天，超出自动清理'),
          ui.el('div', { class: 'span-2' }, ui.el('div', { class: 'row wrap' },
            ui.kv([
              ['最近备份', settings.backup && settings.backup.lastAt ? ui.fmt.time(settings.backup.lastAt) : '—'],
              ['存储文件', ui.el('span', { class: 'mono tiny' }, (settings.storage && settings.storage.file) || '—')],
              ['文件大小', settings.storage && settings.storage.size ? `${(settings.storage.size / 1024).toFixed(1)} KB` : '—'],
            ]),
          )),
        ),
      }),
      ui.card({
        title: '保存',
        body: ui.el('div', { class: 'form-grid one' },
          err,
          ui.el('div', { class: 'row' },
            ui.el('span', { class: 'tiny muted' }, '保存后写入审计日志，即时生效'),
            ui.el('span', { class: 'spacer' }),
            saveBtn,
          ),
        ),
      }),
    ));

    saveBtn.addEventListener('click', async () => {
      const payload = {
        platformName: platformName.value.trim(),
        shortName: shortName.value.trim(),
        orgName: orgName.value.trim(),
        notice: notice.value.trim(),
        autoRefreshSec: Number(autoRefresh.value),
        commandTimeoutSec: Number(cmdTimeout.value),
        dataRetentionDays: Number(retention.value),
        offlineCacheMinutes: Number(offlineCache.value),
        backup: { auto: backupAuto.checked, keepDays: Number(backupKeep.value) },
      };
      if (!payload.platformName) { err.textContent = '平台名称不能为空'; return; }
      if (payload.autoRefreshSec < 2 || payload.autoRefreshSec > 60) { err.textContent = '自动刷新需在 2 - 60 秒之间'; return; }
      if (payload.commandTimeoutSec < 10 || payload.commandTimeoutSec > 180) { err.textContent = '命令超时需在 10 - 180 秒之间'; return; }
      if (payload.dataRetentionDays < 1 || payload.dataRetentionDays > 90) { err.textContent = '数据保留需在 1 - 90 天之间'; return; }
      if (payload.offlineCacheMinutes < 5 || payload.offlineCacheMinutes > 240) { err.textContent = '离线缓存需在 5 - 240 分钟之间'; return; }
      saveBtn.disabled = true;
      saveBtn.textContent = '保存中…';
      try {
        await ctx.api.patch('/api/settings', payload);
        ctx.toast('设置已保存', 'success');
        err.textContent = '';
      } catch (e) {
        err.textContent = (e && e.message) || '保存失败';
      } finally {
        saveBtn.disabled = !canEdit;
        saveBtn.textContent = '保存设置';
      }
    });
  }

  /* ================= 通知通道 ================= */
  async function channelsTab() {
    if (!hasPerm(ctx, 'admin.config')) { ui.mount(bodyBox, guardBody('通知通道', 'admin.config')); return; }
    let channels = [];
    try {
      channels = (await ctx.api.get('/api/channels')) || [];
    } catch (e) {
      ui.mount(bodyBox, ui.errorState(e, channelsTab));
      return;
    }
    const testing = new Set();

    ui.mount(bodyBox, ui.card({
      title: '通知通道',
      sub: '修改后即时生效',
      body: channels.length ? ui.table({
        columns: [
          {
            key: 'name', label: '通道', width: '150px', render: (c) => ui.el('div', {},
              ui.el('div', { class: 'cell-main' }, c.name),
              ui.el('div', { class: 'cell-sub' }, CHANNEL_TYPE[c.type] || c.type),
            ),
          },
          { key: 'target', label: '接收目标', render: (c) => ui.el('span', { class: 'small' }, c.target || '—') },
          { key: 'note', label: '说明', render: (c) => ui.el('span', { class: 'tiny muted' }, c.note || '—') },
          {
            key: 'enabled', label: '启用', width: '92px', render: (c) => ui.el('button', {
              class: `btn sm ${c.enabled ? '' : 'ghost'}`, type: 'button',
              onclick: () => toggleChannel(c),
            }, c.enabled ? '已启用' : '已停用'),
          },
          {
            key: 'status', label: '状态', width: '90px', render: (c) => {
              const bad = !c.enabled || c.status !== '正常';
              return ui.badge(c.status || (c.enabled ? '正常' : '已停用'), bad ? 'warn' : 'good');
            },
          },
          { key: 'testedAt', label: '最近测试', width: '116px', render: (c) => ui.el('span', { class: 'tiny muted' }, c.testedAt ? ui.fmt.time(c.testedAt) : '未测试') },
          {
            key: 'op', label: '操作', width: '168px', render: (c) => ui.el('div', { class: 'row wrap' },
              ui.el('button', { class: 'btn sm', type: 'button', onclick: () => testChannel(c) }, '测试发送'),
              ui.el('button', { class: 'btn sm', type: 'button', onclick: () => editChannel(c) }, '编辑'),
            ),
          },
        ],
        rows: channels,
      }) : ui.empty('暂无通知通道', '通知通道由后端配置提供。', 'admin'),
    }));

    async function toggleChannel(c) {
      try {
        await ctx.api.patch(`/api/channels/${encodeURIComponent(c.id)}`, { enabled: !c.enabled });
        ctx.toast(`${c.name} 已${c.enabled ? '停用' : '启用'}`, 'success');
        channelsTab();
      } catch (e) {
        ctx.toast((e && e.message) || '操作失败', 'error');
      }
    }

    async function testChannel(c) {
      if (testing.has(c.id)) return;
      testing.add(c.id);
      try {
        const res = await ctx.api.post(`/api/channels/${encodeURIComponent(c.id)}/test`, {});
        const msg = (res && (res.message || res.detail)) || '测试消息已发送';
        ctx.toast(`${c.name}：${msg}`, 'success');
      } catch (e) {
        ctx.toast(`${c.name}：${(e && e.message) || '测试失败'}`, 'error');
      } finally {
        testing.delete(c.id);
        if (state.tab === 'channels') channelsTab();
      }
    }

    function editChannel(c) {
      const target = ui.input({ value: c.target || '', placeholder: '手机号 / 群机器人地址 / 邮箱' });
      const note = ui.input({ value: c.note || '', placeholder: '用途说明' });
      const enabled = ui.el('input', { type: 'checkbox', checked: !!c.enabled });
      const err = ui.el('div', { class: 'error-text' });
      const submit = ui.el('button', { class: 'btn primary', type: 'button' }, '保存');
      let m = null;
      m = ctx.modal({
        title: `编辑通知通道 · ${c.name}`,
        body: ui.el('div', { class: 'form-grid one' },
          ui.field('接收目标', target, c.available === false ? '该通道未配置服务端凭据，可能不可用' : ''),
          ui.field('用途说明', note),
          ui.field('启用状态', ui.el('label', { class: 'check-field' }, enabled, '启用该通道')),
          err,
        ),
        footer: ui.el('div', { class: 'row', style: { width: '100%' } },
          ui.el('span', { class: 'spacer' }),
          ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
          submit,
        ),
      });
      submit.addEventListener('click', async () => {
        submit.disabled = true;
        try {
          await ctx.api.patch(`/api/channels/${encodeURIComponent(c.id)}`, {
            target: target.value.trim(), note: note.value.trim(), enabled: enabled.checked,
          });
          ctx.toast('通道已保存', 'success');
          m.close();
          channelsTab();
        } catch (e) {
          err.textContent = (e && e.message) || '保存失败';
          submit.disabled = false;
        }
      });
    }
  }

  /* ================= 审计日志 ================= */
  async function auditTab() {
    if (!hasPerm(ctx, 'admin.audit')) { ui.mount(bodyBox, guardBody('审计日志', 'admin.audit')); return; }
    const filters = { action: '', actor: '', from: '', to: '', q: '', page: 1 };
    let actionsMap = {};
    let meta = { total: 0, page: 1, pages: 1 };
    const tableBox = ui.el('div', {});
    const countLabel = ui.el('span', { class: 'tiny muted' }, '');
    const pagerBox = ui.el('div', {});

    const actionSel = ui.select([{ value: '', label: '全部动作', selected: true }], {
      onchange: (e) => { filters.action = e.target.value; filters.page = 1; load(); },
    });
    const actorInput = ui.input({ placeholder: '操作人账号或姓名' });
    const fromInput = ui.input({ type: 'date' });
    const toInput = ui.input({ type: 'date' });
    const qInput = ui.input({ type: 'search', placeholder: '搜索详情 / 对象' });
    const queryBtn = ui.el('button', { class: 'btn primary sm', type: 'button' }, '查询');
    const resetBtn = ui.el('button', { class: 'btn sm', type: 'button' }, '重置');

    ui.mount(bodyBox, ui.card({
      title: '审计日志',
      sub: '日志不可修改',
      body: ui.el('div', { class: 'stack-sm' },
        ui.el('div', { class: 'toolbar audit-toolbar' },
          actionSel,
          ui.el('span', { class: 'tiny muted' }, '操作人'),
          actorInput,
          ui.el('span', { class: 'tiny muted' }, '时间'),
          fromInput,
          ui.el('span', { class: 'tiny muted' }, '至'),
          toInput,
          qInput,
          queryBtn,
          resetBtn,
        ),
        countLabel,
        tableBox,
        pagerBox,
      ),
    }));

    queryBtn.addEventListener('click', () => {
      filters.actor = actorInput.value.trim();
      filters.from = fromInput.value;
      filters.to = toInput.value;
      filters.q = qInput.value.trim();
      filters.page = 1;
      load();
    });
    resetBtn.addEventListener('click', () => {
      actionSel.value = '';
      actorInput.value = '';
      fromInput.value = '';
      toInput.value = '';
      qInput.value = '';
      Object.assign(filters, { action: '', actor: '', from: '', to: '', q: '', page: 1 });
      load();
    });

    async function load() {
      ui.mount(tableBox, ui.skeleton(5, 'line'));
      const params = { page: filters.page, pageSize: 15 };
      if (filters.action) params.action = filters.action;
      if (filters.actor) params.actor = filters.actor;
      if (filters.q) params.q = filters.q;
      if (filters.from) params.from = new Date(`${filters.from}T00:00:00`).toISOString();
      if (filters.to) params.to = new Date(`${filters.to}T23:59:59`).toISOString();
      try {
        const res = await ctx.api.getFull('/api/audit', params);
        meta = res.meta || meta;
        if (meta.actions && !Object.keys(actionsMap).length) {
          actionsMap = meta.actions;
          while (actionSel.firstChild) actionSel.removeChild(actionSel.firstChild);
          const all = document.createElement('option');
          all.value = '';
          all.textContent = '全部动作';
          all.selected = !filters.action;
          actionSel.appendChild(all);
          Object.keys(actionsMap)
            .sort((a, b) => String(actionsMap[a]).localeCompare(String(actionsMap[b]), 'zh'))
            .forEach((k) => {
              const opt = document.createElement('option');
              opt.value = k;
              opt.textContent = `${actionsMap[k]}（${k}）`;
              opt.selected = filters.action === k;
              actionSel.appendChild(opt);
            });
        }
        paint(res.data || []);
      } catch (e) {
        ui.mount(tableBox, ui.errorState(e, load));
        ui.mount(pagerBox, ui.el('div'));
      }
    }

    function paint(rows) {
      countLabel.textContent = `共 ${meta.total || 0} 条 · 第 ${meta.page || 1} / ${meta.pages || 1} 页`;
      ui.mount(tableBox, rows.length ? ui.table({
        columns: [
          { key: 'at', label: '时间', width: '112px', render: (r) => ui.el('span', { class: 'mono tiny' }, ui.fmt.time(r.at)) },
          { key: 'actorName', label: '操作人', width: '110px', render: (r) => ui.el('div', {}, ui.el('div', { class: 'cell-main' }, r.actorName || r.actorId || '系统')) },
          {
            key: 'action', label: '动作', width: '150px', render: (r) => ui.el('div', {},
              ui.el('div', { class: 'cell-main' }, r.actionName || r.action),
              ui.el('div', { class: 'cell-sub mono' }, r.action),
            ),
          },
          {
            key: 'target', label: '对象', width: '150px', render: (r) => ui.el('span', { class: 'mono tiny' },
              [r.targetType, r.targetId].filter(Boolean).join(' / ') || '—'),
          },
          {
            key: 'result', label: '结果', width: '84px', render: (r) => {
              const hit = AUDIT_RESULT[r.result] || [r.result || '—', r.result === 'fail' ? 'bad' : 'muted'];
              return ui.badge(hit[0], hit[1]);
            },
          },
          { key: 'ip', label: 'IP', width: '108px', render: (r) => ui.el('span', { class: 'mono tiny' }, maskIp(r.ip)) },
          { key: 'detail', label: '详情', render: (r) => ui.el('span', { class: 'small muted detail-cell', title: r.detail || '' }, r.detail || '—') },
        ],
        rows,
      }) : ui.empty('没有匹配的审计记录', '调整动作、操作人或时间范围后重试。', 'admin'));

      ui.mount(pagerBox, ui.el('div', { class: 'pager' },
        ui.el('span', { class: 'tiny muted' }, `第 ${meta.page || 1} 页 / 共 ${meta.pages || 1} 页`),
        ui.el('span', { class: 'spacer' }),
        ui.el('button', {
          class: 'btn sm', type: 'button', disabled: (meta.page || 1) <= 1,
          onclick: () => { filters.page = Math.max(1, (meta.page || 1) - 1); load(); },
        }, '上一页'),
        ui.el('button', {
          class: 'btn sm', type: 'button', disabled: (meta.page || 1) >= (meta.pages || 1),
          onclick: () => { filters.page = Math.min(meta.pages || 1, (meta.page || 1) + 1); load(); },
        }, '下一页'),
      ));
    }

    load();
  }

  /* ================= 备份 ================= */
  async function backupsTab() {
    if (!hasPerm(ctx, 'admin.config')) { ui.mount(bodyBox, guardBody('备份', 'admin.config')); return; }
    let list = [];
    let meta = { count: 0, lastAt: null, dir: '' };
    try {
      const res = await ctx.api.getFull('/api/backups');
      list = res.data || [];
      meta = res.meta || meta;
    } catch (e) {
      ui.mount(bodyBox, ui.errorState(e, backupsTab));
      return;
    }
    const tableBox = ui.el('div', {});

    ui.mount(bodyBox, ui.el('div', { class: 'stack' },
      ui.card({
        title: '数据备份',
        sub: `目录 ${meta.dir || 'data/backups'} · 共 ${meta.count || list.length} 个备份${meta.lastAt ? ` · 最近 ${ui.fmt.time(meta.lastAt)}` : ''}`,
        actions: [ui.el('button', { class: 'btn primary sm', type: 'button', onclick: createBackup }, '创建备份')],
        body: ui.el('div', { class: 'stack-sm' },
          ui.notice('恢复备份会用备份时点覆盖当前数据，并注销所有登录会话（含本人），恢复后需重新登录。请谨慎操作。', 'warn'),
          tableBox,
        ),
      }),
    ));
    paint();

    function paint() {
      ui.mount(tableBox, list.length ? ui.table({
        columns: [
          { key: 'file', label: '备份文件', render: (r) => ui.el('div', {},
            ui.el('div', { class: 'cell-main mono' }, r.file || r.id),
            ui.el('div', { class: 'cell-sub' }, r.note || '—'),
          ) },
          { key: 'createdAt', label: '创建时间', width: '128px', render: (r) => ui.el('span', { class: 'mono tiny' }, ui.fmt.time(r.createdAt)) },
          { key: 'size', label: '大小', width: '96px', cls: 'num', render: (r) => ui.el('span', { class: 'mono tiny' }, r.size ? `${(r.size / 1024).toFixed(1)} KB` : '—') },
          { key: 'auto', label: '方式', width: '84px', render: (r) => ui.badge(r.auto ? '自动' : '手动', r.auto ? 'info' : 'muted') },
          { key: 'createdBy', label: '创建人', width: '110px', render: (r) => ui.el('span', { class: 'small' }, r.createdBy || '系统') },
          {
            key: 'op', label: '操作', width: '220px', render: (r) => ui.el('div', { class: 'row wrap' },
              ui.el('button', { class: 'btn sm', type: 'button', onclick: () => downloadBackup(r) }, '下载'),
              ui.el('button', { class: 'btn sm danger', type: 'button', onclick: () => restoreBackup(r) }, '恢复'),
              ui.el('button', { class: 'btn sm danger', type: 'button', onclick: () => deleteBackup(r) }, '删除'),
            ),
          },
        ],
        rows: list,
      }) : ui.empty('还没有备份文件', '点击「创建备份」生成当前数据的快照。', 'admin'));
    }

    function createBackup() {
      const note = ui.input({ placeholder: '备注，如：升级前快照' });
      const err = ui.el('div', { class: 'error-text' });
      const submit = ui.el('button', { class: 'btn primary', type: 'button' }, '开始备份');
      let m = null;
      m = ctx.modal({
        title: '创建备份',
        sub: '备份包含平台配置与业务数据，写入服务器备份目录',
        body: ui.el('div', { class: 'form-grid one' }, ui.field('备注（可选）', note), err),
        footer: ui.el('div', { class: 'row', style: { width: '100%' } },
          ui.el('span', { class: 'spacer' }),
          ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
          submit,
        ),
      });
      submit.addEventListener('click', async () => {
        submit.disabled = true;
        submit.textContent = '备份中…';
        try {
          await ctx.api.post('/api/backups', { note: note.value.trim() });
          ctx.toast('备份已创建', 'success');
          m.close();
          backupsTab();
        } catch (e) {
          err.textContent = (e && e.message) || '创建失败';
          submit.disabled = false;
          submit.textContent = '开始备份';
        }
      });
    }

    function downloadBackup(r) {
      try {
        ctx.api.download(`/api/backups/${encodeURIComponent(r.id)}/download`, r.file || `${r.id}.json`);
      } catch (e) {
        ctx.toast((e && e.message) || '下载失败', 'error');
      }
    }

    function restoreBackup(r) {
      const ack = ui.el('input', { type: 'checkbox' });
      const err = ui.el('div', { class: 'error-text' });
      const submit = ui.el('button', { class: 'btn danger', type: 'button', disabled: true }, '确认恢复');
      ack.addEventListener('change', () => { submit.disabled = !ack.checked; });
      let m = null;
      m = ctx.modal({
        title: `恢复备份 · ${r.file || r.id}`,
        sub: '此操作不可撤销',
        body: ui.el('div', { class: 'form-grid one' },
          ui.el('div', { class: 'danger-box' },
            ui.el('div', { class: 'strong' }, '恢复会覆盖当前数据库，并注销所有登录会话（包括你本人）。'),
            ui.el('p', { class: 'small' }, `备份时间：${ui.fmt.time(r.createdAt)} · 大小：${r.size ? `${(r.size / 1024).toFixed(1)} KB` : '未知'}`),
            ui.el('p', { class: 'small' }, '恢复完成后需要重新登录平台。'),
          ),
          ui.el('label', { class: 'check-field' }, ack, '我已知悉恢复将注销所有会话，并确认覆盖当前数据'),
          err,
        ),
        footer: ui.el('div', { class: 'row', style: { width: '100%' } },
          ui.el('span', { class: 'spacer' }),
          ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
          submit,
        ),
      });
      submit.addEventListener('click', async () => {
        submit.disabled = true;
        submit.textContent = '恢复中…';
        try {
          await ctx.api.post(`/api/backups/${encodeURIComponent(r.id)}/restore`, { confirm: true });
          let done = null;
          done = ctx.modal({
            title: '恢复完成',
            body: ui.el('div', { class: 'stack-sm' },
              ui.notice('备份已恢复，所有登录会话已注销，请重新登录。', 'warn'),
              ui.el('p', { class: 'small muted' }, '重新登录后将按恢复时点的数据展示。'),
            ),
            footer: ui.el('div', { class: 'row', style: { width: '100%' } },
              ui.el('span', { class: 'spacer' }),
              ui.el('button', {
                class: 'btn primary', type: 'button',
                onclick: () => { done.close(); window.location.reload(); },
              }, '重新登录'),
            ),
            closeOnBackdrop: false,
          });
        } catch (e) {
          err.textContent = (e && e.message) || '恢复失败';
          submit.disabled = false;
          submit.textContent = '确认恢复';
        }
      });
    }

    async function deleteBackup(r) {
      const ok = await ctx.confirm({
        title: '删除备份',
        message: `确认删除备份 ${r.file || r.id}？删除后无法恢复。`,
        detail: `创建时间：${ui.fmt.time(r.createdAt)}`,
        confirmText: '删除',
        danger: true,
      });
      if (!ok) return;
      try {
        await ctx.api.del(`/api/backups/${encodeURIComponent(r.id)}`);
        ctx.toast('备份已删除', 'success');
        backupsTab();
      } catch (e) {
        ctx.toast((e && e.message) || '删除失败', 'error');
      }
    }
  }
}

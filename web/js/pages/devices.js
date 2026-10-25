/* 设备台账：设备清单、健康度、实时点位与保养登记 */
import * as ui from '../ui.js';

export const meta = { title: '设备台账' };

const TYPE_TEXT = {
  METER: '计量仪表',
  PUMP: '水泵',
  BLOWER: '风机',
  VALVE: '阀门',
  DOSER: '加药装置',
  MIXER: '搅拌设备',
  PLC: 'PLC 控制器',
  GATEWAY: '网关',
};
const STATUS_BADGE = {
  在线: 'good',
  运行: 'good',
  待机: 'muted',
  关闭: 'muted',
  停机: 'muted',
  故障: 'bad',
  离线: 'bad',
  保养中: 'warn',
  未安装: 'muted',
};
const HEALTH_TEXT = {
  good: ['健康', 'good'],
  warn: ['关注', 'warn'],
  attention: ['关注', 'warn'],
  bad: ['风险', 'bad'],
  risk: ['风险', 'bad'],
};
const QUALITY_TEXT = { good: ['正常', 'good'], fair: ['一般', 'warn'], bad: ['异常', 'bad'], stale: ['陈旧', 'warn'] };
const hasPerm = (ctx, key) => ((ctx.user && ctx.user.perms) || []).includes(key);

function healthTone(h) {
  if (!h) return '';
  if (h.overdue) return 'bad';
  if (h.needMaint) return 'warn';
  if (h.level === 'bad' || h.level === 'risk') return 'bad';
  if (h.level === 'warn' || h.level === 'attention') return 'warn';
  return 'good';
}

function healthBadge(device) {
  const h = device.health || {};
  const hit = HEALTH_TEXT[h.level] || ['—', 'muted'];
  const score = typeof h.score === 'number' ? h.score : null;
  const hint = h.daysToMaint === null || h.daysToMaint === undefined ? '' : `距下次保养 ${h.daysToMaint} 天`;
  return ui.badge(score === null ? hit[0] : `${hit[0]} ${score}`, hit[1], hint);
}

function nextMaintAt(device) {
  if (device.lifecycle && device.lifecycle.nextMaintAt) return device.lifecycle.nextMaintAt;
  if (!device.lastMaintAt || !device.maintCycleDays) return null;
  const d = new Date(device.lastMaintAt);
  d.setDate(d.getDate() + Number(device.maintCycleDays));
  return d.toISOString();
}

/* 仪表类设备不做运行统计（无累计运行小时 / 启停次数语义） */
const RUNTIME_HINT = '运行统计仅对泵、风机、阀门有效，仪表类设备不统计';
function isInstrument(d) {
  const typeText = TYPE_TEXT[d.type] || '';
  if ((d.type || '').toUpperCase() === 'METER' || /仪表/.test(typeText)) return true;
  return /(分析仪|液位计|流量计|压力变送器|温度计|计$|仪$)/.test(d.name || '');
}

function pointValue(p) {
  const v = p.value !== undefined ? p.value : p.v;
  if (v === null || v === undefined) return '—';
  const txt = typeof v === 'number' ? ui.fmt.num(v) : String(v);
  return `${txt}${p.unit ? ` ${p.unit}` : ''}`;
}

function qualityBadge(q) {
  const hit = QUALITY_TEXT[q];
  return hit ? ui.badge(hit[0], hit[1]) : ui.badge(q || '—', 'muted');
}

export async function render(root, ctx) {
  const state = { q: '', unit: '', type: '', status: '' };
  let devices = [];
  let summary = {};

  const kpiBox = ui.el('div', { class: 'grid cols-3 dev-kpis' });
  const filterBox = ui.el('div', { class: 'toolbar dev-filters' });
  const tableBox = ui.el('div', {});
  const countLabel = ui.el('span', { class: 'tiny muted' }, '');

  ui.mount(root, ui.el('div', { class: 'p-devices stack' },
    ui.pageHead(
      '设备台账',
      `全站设备基础信息、健康度与保养记录 · 当前站点 ${ctx.site ? ctx.site.name : '—'}`,
      [ui.el('button', { class: 'btn', type: 'button', onclick: () => ctx.refresh() }, ui.icon('refresh'), '刷新')],
    ),
    kpiBox,
    ui.card({ body: ui.el('div', { class: 'stack-sm' }, filterBox, tableBox) }),
  ));

  await load();

  async function load() {
    ui.mount(kpiBox, [0, 1, 2].map(() => ui.skeleton(1, 'kpi')));
    ui.mount(tableBox, ui.skeleton(6, 'line'));
    try {
      const res = await ctx.api.getFull('/api/devices', { siteId: ctx.siteId });
      devices = res.data || [];
      summary = res.meta ? res.meta.summary || {} : {};
    } catch (e) {
      ui.mount(tableBox, ui.errorState(e, load));
      ui.mount(kpiBox, ui.errorState(e, load));
      return;
    }
    renderKpis();
    renderFilters();
    renderTable();
  }

  function statusCounts() {
    const counts = { total: devices.length, running: 0, stopped: 0, fault: 0, notInstalled: 0, needMaint: 0 };
    for (const d of devices) {
      const s = d.status || '';
      if (s === '故障') counts.fault += 1;
      else if (s === '未安装') counts.notInstalled += 1;
      else if (s === '运行' || s === '在线') counts.running += 1;
      else counts.stopped += 1;
      const h = d.health || {};
      const days = h.daysToMaint;
      if (h.needMaint || h.overdue || (days !== null && days !== undefined && days <= 0)) counts.needMaint += 1;
    }
    return counts;
  }

  function renderKpis() {
    const c = statusCounts();
    const online = devices.filter((d) => d.online).length;
    const needMaint = summary.needMaint !== undefined ? summary.needMaint : c.needMaint;
    ui.mount(kpiBox,
      ui.kpi({ label: '设备总数', value: summary.total !== undefined ? summary.total : c.total, unit: '台', hint: `在线 ${online} 台 · 已安装 ${summary.installed !== undefined ? summary.installed : c.total} 台`, iconName: 'device' }),
      ui.kpi({ label: '运行中', value: c.running, unit: '台', tone: 'good', hint: '状态为运行 / 在线' }),
      ui.kpi({ label: '停机', value: c.stopped, unit: '台', hint: '待机、停机或关闭' }),
      ui.kpi({ label: '故障', value: c.fault, unit: '台', tone: c.fault ? 'bad' : '', hint: '需现场处理' }),
      ui.kpi({ label: '未安装', value: c.notInstalled, unit: '台', hint: '规划中尚未安装' }),
      ui.kpi({ label: '保养到期', value: needMaint, unit: '台', tone: needMaint ? 'warn' : '', hint: '需在近期登记保养' }),
    );
  }

  function renderFilters() {
    const units = [];
    const unitSeen = new Set();
    for (const d of devices) {
      if (d.unitCode && !unitSeen.has(d.unitCode)) {
        unitSeen.add(d.unitCode);
        units.push({ value: d.unitCode, label: d.unitName || d.unitCode, selected: state.unit === d.unitCode });
      }
    }
    const types = Object.keys(summary.byType || {}).map((k) => ({
      value: k, label: TYPE_TEXT[k] || k, selected: state.type === k,
    }));
    const statuses = ['运行', '在线', '待机', '停机', '关闭', '故障', '离线', '保养中', '未安装'];

    const search = ui.input({ type: 'search', placeholder: '搜索设备名称 / 编号 / 型号', value: state.q });
    search.addEventListener('input', ui.debounce(() => {
      state.q = search.value.trim();
      renderTable();
    }, 200));

    ui.mount(filterBox,
      ui.el('div', { class: 'search-field' }, ui.icon('search'), search),
      ui.select([{ value: '', label: '全部单元', selected: !state.unit }].concat(units), {
        onchange: (e) => { state.unit = e.target.value; renderTable(); },
      }),
      ui.select([{ value: '', label: '全部类型', selected: !state.type }].concat(types), {
        onchange: (e) => { state.type = e.target.value; renderTable(); },
      }),
      ui.select([{ value: '', label: '全部状态', selected: !state.status }].concat(statuses.map((s) => ({
        value: s, label: s, selected: state.status === s,
      }))), { onchange: (e) => { state.status = e.target.value; renderTable(); } }),
      ui.el('span', { class: 'spacer' }),
      countLabel,
    );
  }

  function filtered() {
    const q = state.q.toLowerCase();
    return devices.filter((d) => {
      if (state.unit && d.unitCode !== state.unit) return false;
      if (state.type && d.type !== state.type) return false;
      if (state.status && d.status !== state.status) return false;
      if (!q) return true;
      const hay = [d.name, d.code, d.model, d.unitName, d.vendor].filter(Boolean).join(' ').toLowerCase();
      return hay.includes(q);
    });
  }

  function renderTable() {
    const rows = filtered();
    countLabel.textContent = `显示 ${rows.length} / ${devices.length} 台`;
    if (!rows.length) {
      ui.mount(tableBox, ui.empty('没有符合条件的设备', '调整筛选条件或清空搜索关键字。', 'device'));
      return;
    }
    const tbl = ui.table({
      columns: [
        { key: 'code', label: '设备编号', width: '90px', cls: 'col-code', render: (d) => ui.el('span', { class: 'mono strong dev-code', title: d.code || '' }, d.code || '—') },
        {
          key: 'name', label: '设备名称', width: '180px', cls: 'col-name',
          render: (d) => {
            const sub = [d.id, d.vendor].filter(Boolean).join(' · ');
            return ui.el('div', { class: 'dev-name-cell' },
              ui.el('div', { class: 'dev-name', title: d.name || '' }, d.name || '—'),
              ui.el('div', { class: 'dev-key', title: sub }, sub || '—'),
            );
          },
        },
        { key: 'unitName', label: '单元', width: '86px', cls: 'col-unit', render: (d) => ui.el('span', { class: 'small dev-unit', title: d.unitName || d.unitCode || '' }, d.unitName || d.unitCode || '—') },
        { key: 'type', label: '类型', width: '90px', cls: 'col-type', render: (d) => ui.el('span', { class: 'small' }, TYPE_TEXT[d.type] || d.type || '—') },
        { key: 'model', label: '型号', width: '140px', cls: 'col-model', render: (d) => ui.el('span', { class: 'small mono dev-model', title: d.model || '' }, d.model || '—') },
        {
          key: 'status', label: '状态 / 在线', width: '116px', cls: 'col-status', render: (d) => {
            const note = (!d.online && d.status !== '离线' && d.status !== '未安装')
              ? ui.el('span', { class: 'dot-line' }, ui.dot('bad'), ui.el('span', { class: 'tiny muted' }, '采集离线'))
              : null;
            return ui.el('div', { class: 'status-cell' }, ui.badge(d.status || '—', STATUS_BADGE[d.status] || 'muted'), note);
          },
        },
        {
          key: 'runtimeHours', label: ui.el('span', { class: 'th-hint', title: RUNTIME_HINT }, '累计运行'),
          width: '80px', cls: 'num col-run',
          render: (d) => (isInstrument(d)
            ? ui.el('span', { class: 'mono muted', title: RUNTIME_HINT }, '—')
            : ui.el('span', { class: 'mono' }, ui.fmt.hours(d.runtimeHours))),
        },
        {
          key: 'starts', label: ui.el('span', { class: 'th-hint', title: RUNTIME_HINT }, '启停次数'),
          width: '72px', cls: 'num col-starts',
          render: (d) => (isInstrument(d)
            ? ui.el('span', { class: 'mono muted', title: RUNTIME_HINT }, '—')
            : ui.el('span', { class: 'mono' }, ui.fmt.int(d.starts))),
        },
        { key: 'lastMaintAt', label: '上次保养', width: '92px', cls: 'col-last', render: (d) => ui.el('span', { class: 'mono tiny' }, d.lastMaintAt ? ui.fmt.date(d.lastMaintAt) : '—') },
        {
          key: 'nextMaintAt', label: '下次保养', width: '96px', cls: 'col-next', render: (d) => {
            const next = nextMaintAt(d);
            const overdue = next && new Date(next).getTime() < Date.now();
            return ui.el('div', { class: 'due-cell' },
              ui.el('span', { class: overdue ? 'amount-bad' : 'mono tiny' }, next ? ui.fmt.date(next) : '—'),
              overdue ? ui.badge('已到期', 'warn') : null,
            );
          },
        },
        {
          key: 'health', label: '健康分', width: '108px', cls: 'col-health', render: (d) => {
            const score = d.health && typeof d.health.score === 'number' ? d.health.score : 0;
            return ui.el('div', { class: 'health-cell' },
              ui.progress(score, { max: 100, tone: healthTone(d.health) }),
              healthBadge(d),
            );
          },
        },
      ],
      rows,
      onRow: (d) => openDetail(d),
    });
    const ths = tbl.querySelectorAll ? tbl.querySelectorAll('thead th') : [];
    [6, 7].forEach((i) => { if (ths[i]) ths[i].setAttribute('title', RUNTIME_HINT); });
    ui.mount(tableBox,
      ui.el('div', { class: 'dev-table' }, tbl),
      ui.el('div', { class: 'table-hint' }, '注：累计运行与启停次数仅对泵、风机、阀门有效；仪表类设备以「—」表示不统计。'),
    );
  }

  /* ---------------- 设备详情 ---------------- */
  async function openDetail(device) {
    const body = ui.el('div', {});
    const draw = ui.drawer({ title: device.name, sub: `${device.code} · ${device.unitName || device.unitCode || ''}`, body });
    const canMaintain = hasPerm(ctx, 'param.edit');
    let current = device;

    const paint = (d) => {
      const h = d.health || {};
      const next = nextMaintAt(d);
      const points = d.points || [];
      const records = d.maintenance || [];
      const overdue = next && new Date(next).getTime() < Date.now();

      ui.mount(body, ui.el('div', { class: 'stack dev-detail' },
        ui.el('div', { class: 'row wrap dev-head-badges' },
          ui.badge(d.status || '—', STATUS_BADGE[d.status] || 'muted'),
          ui.el('span', { class: 'dot-line' }, ui.dot(d.online ? 'good' : 'muted', !!d.online), ui.el('span', { class: 'tiny muted' }, d.online ? '在线采集' : '离线')),
          healthBadge(d),
          overdue ? ui.badge('保养已到期', 'warn') : null,
          d.activeAlarms ? ui.badge(`活动告警 ${d.activeAlarms}`, 'bad') : null,
          d.openWorkorders ? ui.badge(`未关闭工单 ${d.openWorkorders}`, 'warn') : null,
        ),

        ui.card({
          title: '基本信息',
          body: ui.kvRows([
            ['设备编号', ui.el('span', { class: 'mono strong' }, d.code || '—')],
            ['所属单元', d.unitName || d.unitCode || '—'],
            ['设备类型', TYPE_TEXT[d.type] || d.type || '—'],
            ['型号', d.model || '—'],
            ['规格', d.spec || '—'],
            ['生产厂家', d.vendor || '—'],
            ['通讯协议', d.protocol || '—'],
            ['PLC 地址', ui.el('span', { class: 'mono' }, d.plAddress || '—')],
            ['安装日期', d.installDate ? ui.fmt.date(d.installDate) : '—'],
            ['保修至', d.warrantyUntil ? ui.fmt.date(d.warrantyUntil) : '—'],
            ['累计运行', isInstrument(d)
              ? ui.el('span', { class: 'muted' }, '—（仪表类设备不统计运行数据）')
              : ui.el('span', { class: 'mono' }, `${ui.fmt.hours(d.runtimeHours)} · 启停 ${ui.fmt.int(d.starts)} 次`)],
            ['保养周期', d.maintCycleDays ? `每 ${d.maintCycleDays} 天` : '—'],
            ['上次保养', d.lastMaintAt ? ui.fmt.date(d.lastMaintAt) : '—'],
            ['下次保养', next ? ui.el('span', { class: overdue ? 'amount-bad' : '' }, ui.fmt.date(next)) : '—'],
            ['健康度', ui.el('div', { class: 'row health-inline' }, ui.progress(h.score || 0, { max: 100, tone: healthTone(h) }), healthBadge(d))],
          ]),
        }),

        ui.card({
          title: '关联点位实时值',
          sub: points.length ? `${points.length} 个点位，数据来自采集网关` : '暂无关联点位',
          body: points.length
            ? ui.table({
              columns: [
                {
                  key: 'name', label: '点位', render: (p) => ui.el('div', {},
                    ui.el('div', { class: 'cell-main' }, p.name || p.pointId),
                    ui.el('div', { class: 'cell-sub mono' }, p.code || p.pointId),
                  ),
                },
                { key: 'value', label: '实时值', width: '110px', cls: 'num', render: (p) => ui.el('span', { class: 'mono strong' }, pointValue(p)) },
                { key: 'quality', label: '质量', width: '82px', render: (p) => qualityBadge(p.quality || p.q) },
                { key: 't', label: '更新时间', width: '104px', render: (p) => ui.el('span', { class: 'tiny muted' }, p.t ? ui.fmt.timeSec(p.t) : '—') },
                { key: 'source', label: '来源', width: '120px', render: (p) => ui.el('span', { class: 'tiny muted' }, ui.sourceText(p)) },
              ],
              rows: points,
            })
            : ui.empty('暂无关联点位', '该设备未配置采集点位。', 'device'),
        }),

        ui.card({
          title: '保养记录',
          sub: records.length ? `共 ${records.length} 条，最近 ${ui.fmt.date(records[0].at || records[0].createdAt)}` : '暂无保养记录',
          actions: canMaintain
            ? ui.el('button', { class: 'btn sm primary', type: 'button', onclick: () => openMaintenance(d) }, '登记保养')
            : null,
          body: records.length
            ? ui.table({
              columns: [
                { key: 'at', label: '时间', width: '112px', render: (r) => ui.el('span', { class: 'mono tiny' }, ui.fmt.time(r.at || r.createdAt)) },
                { key: 'type', label: '类型', width: '70px', render: (r) => ui.badge(r.type || '保养', 'info') },
                { key: 'note', label: '说明', render: (r) => ui.el('span', { class: 'small' }, r.note || '—') },
                { key: 'parts', label: '备件', width: '130px', render: (r) => ui.el('span', { class: 'tiny muted' }, r.parts || '—') },
                { key: 'cost', label: '费用', width: '84px', cls: 'num', render: (r) => ui.el('span', { class: 'mono' }, r.cost ? ui.fmt.money(r.cost) : '—') },
              ],
              rows: records,
            })
            : ui.empty('暂无保养记录', canMaintain ? '点击「登记保养」记录本次维护。' : '还没有保养记录。', 'device'),
        }),

        canMaintain ? null : ui.notice('当前账号没有工艺参数编辑权限（param.edit），可查看台账但无法登记保养。', 'warn'),
      ));
    };

    const reload = async () => {
      try {
        current = await ctx.api.get(`/api/devices/${encodeURIComponent(device.id)}`);
        paint(current);
        const h3 = draw.box.querySelector('.drawer-head h3');
        if (h3 && current.name) h3.textContent = current.name;
        const sub = draw.box.querySelector('.drawer-head .card-sub');
        if (sub && current.code) sub.textContent = `${current.code} · ${current.unitName || current.unitCode || ''}`;
      } catch (e) {
        ui.mount(body, ui.errorState(e, reload));
      }
    };

    const openMaintenance = (d) => {
      const typeSel = ui.select(['保养', '维修', '更换'], {});
      const note = ui.textarea({ rows: 3, placeholder: '本次保养/维修内容、发现的问题与处理结果（必填）' });
      const parts = ui.input({ placeholder: '更换的备件，如：机械密封 1 套' });
      const cost = ui.input({ type: 'number', min: '0', step: '0.01', placeholder: '费用（元，可选）' });
      const err = ui.el('div', { class: 'error-text' });
      let busy = false;
      const submit = ui.el('button', { class: 'btn primary', type: 'button' }, '提交登记');
      let m = null;
      m = ctx.modal({
        title: `登记保养 · ${d.name}`,
        sub: `${d.code} · 提交后写入设备档案与审计日志`,
        body: ui.el('div', { class: 'form-grid one' },
          ui.field('作业类型', typeSel),
          ui.field('作业说明', note, '必填，将记入保养记录'),
          ui.field('更换备件', parts),
          ui.field('费用（元）', cost),
          err,
        ),
        footer: ui.el('div', { class: 'row', style: { width: '100%' } },
          ui.el('span', { class: 'spacer' }),
          ui.el('button', { class: 'btn', type: 'button', onclick: () => m.close() }, '取消'),
          submit,
        ),
      });
      submit.addEventListener('click', async () => {
        if (!note.value.trim()) { err.textContent = '请填写作业说明'; note.focus(); return; }
        if (busy) return;
        busy = true;
        submit.disabled = true;
        submit.textContent = '提交中…';
        try {
          await ctx.api.post(`/api/devices/${encodeURIComponent(d.id)}/maintenance`, {
            type: typeSel.value,
            note: note.value.trim(),
            parts: parts.value.trim(),
            cost: cost.value ? Number(cost.value) : undefined,
          });
          ctx.toast('保养记录已登记', 'success');
          m.close();
          await load();
          await reload();
        } catch (e) {
          err.textContent = (e && e.message) || '登记失败';
          busy = false;
          submit.disabled = false;
          submit.textContent = '提交登记';
        }
      });
    };

    ui.mount(body, ui.skeleton(4, 'line'));
    await reload();
  }
}

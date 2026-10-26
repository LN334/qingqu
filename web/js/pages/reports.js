import * as ui from '../ui.js';

export const meta = { title: '报表中心' };

const TYPE_FALLBACK = { daily: '运行日报', weekly: '运行周报', monthly: '运行月报', custom: '自定义报表' };

const STATUS = {
  ready: { label: '已生成', kind: 'good' },
  generating: { label: '生成中', kind: 'warn' },
  failed: { label: '生成失败', kind: 'bad' },
  draft: { label: '草稿', kind: 'muted' },
};

function pad2(n) { return String(n).padStart(2, '0'); }

function dateValue(d) {
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
}

function hasPerm(ctx, perm) {
  return (ctx.user && Array.isArray(ctx.user.perms) ? ctx.user.perms : []).includes(perm);
}

function statusBadge(status) {
  const s = STATUS[status] || { label: status || '未知', kind: 'muted' };
  return ui.el('span', { class: `badge ${s.kind}` }, s.label);
}

function cellValue(v) {
  if (v === undefined || v === null || v === '') return '—';
  if (typeof v === 'number') {
    if (!Number.isFinite(v)) return '—';
    return Number.isInteger(v) ? String(v) : String(Math.round(v * 1000) / 1000);
  }
  return String(v);
}

/* 表格内时间范围用单行短格式：同日只显示一次日期，同年省略结束年份。 */
function periodText(from, to) {
  const a = ui.fmt.date(from);
  const b = ui.fmt.date(to);
  if (!a || a === '—') return '—';
  if (!b || b === '—' || a === b) return a;
  if (a.slice(0, 4) === b.slice(0, 4)) return `${a} ~ ${b.slice(5)}`;
  return `${a} ~ ${b}`;
}

function sectionTable(section) {
  const columns = Array.isArray(section.columns) ? section.columns : [];
  const rows = Array.isArray(section.rows) ? section.rows : [];
  if (!columns.length) return ui.empty('该段没有定义列', '报表结构由服务端生成');
  const cols = columns.map((label, i) => ({
    key: `c${i}`,
    label,
    render: (row) => cellValue(row.cells[i]),
  }));
  const dataRows = rows.map((cells, i) => ({ id: i, cells: Array.isArray(cells) ? cells : [cells] }));
  return ui.table({
    columns: cols,
    rows: dataRows,
    emptyText: '该段无数据',
    emptySub: `统计窗口内没有${section.title || '相关'}记录`,
  });
}

async function exportReport(ctx, rep) {
  try {
    await ctx.api.download(`/api/reports/${encodeURIComponent(rep.id)}/export`, `report-${rep.id}.csv`);
    ctx.toast('报表已开始下载（CSV）', 'success');
  } catch (err) {
    ctx.toast(err && err.message ? err.message : '导出失败', 'error');
  }
}

async function openDetail(ctx, id, canExport) {
  const host = ui.el('div', { class: 'stack r-detail' });
  ctx.drawer({ title: '报表详情', sub: id, body: host });
  ui.mount(host, ui.loading('加载报表明细…'));
  let rep;
  try {
    rep = await ctx.api.get(`/api/reports/${encodeURIComponent(id)}`);
  } catch (err) {
    ui.mount(host, ui.errorState(err, () => openDetail(ctx, id, canExport)));
    return;
  }
  if (!rep) {
    ui.mount(host, ui.empty('报表不存在', '该报表可能已被删除'));
    return;
  }

  const typeName = rep.typeName || TYPE_FALLBACK[rep.type] || rep.type || '—';
  const sections = Array.isArray(rep.sections) ? rep.sections : [];
  const metaRows = [
    ['报表编号', rep.id],
    ['类型', typeName],
    ['站点', rep.siteName || ctx.site?.name || '—'],
    ['时间范围', `${ui.fmt.date(rep.periodFrom)} 至 ${ui.fmt.date(rep.periodTo)}`],
    ['生成人', rep.creatorName || '—'],
    ['生成时间', ui.fmt.dateTime(rep.createdAt)],
    ['状态', statusBadge(rep.status)],
    ['导出格式', (rep.format || 'csv').toUpperCase()],
    ['备注', rep.note || '—'],
  ];

  const actions = ui.el('div', { class: 'row wrap r-detail-actions' },
    canExport
      ? ui.el('button', { class: 'btn sm primary', type: 'button', onclick: () => exportReport(ctx, rep) }, '导出 CSV')
      : null,
    ui.el('span', { class: 'tiny muted' }, canExport ? '导出为 CSV（服务端生成，含全部报表段）' : '当前账号无 report.export 权限，无法导出'),
  );

  const sectionNodes = sections.length
    ? sections.map(sec => ui.el('div', { class: 'r-section' },
      ui.el('h4', {}, sec.title || '未命名段'),
      sectionTable(sec),
    ))
    : [ui.empty('报表暂无内容', '服务端未返回任何报表段')];

  ui.mount(host,
    actions,
    ui.kvRows(metaRows),
    ui.el('div', { class: 'stack r-sections' }, ...sectionNodes),
  );
}

function openCreate(ctx, types) {
  const typeMap = (types && Object.keys(types).length) ? types : TYPE_FALLBACK;
  const today = new Date();
  const from = new Date(today.getTime() - 6 * 24 * 3600 * 1000);
  const errBox = ui.el('div', { class: 'error-text' });

  const typeSel = ui.select(
    Object.keys(typeMap).map((value, i) => ({ value, label: typeMap[value], selected: i === 1 })),
    {},
  );
  const fromInput = ui.input({ type: 'date', value: dateValue(from) });
  const toInput = ui.input({ type: 'date', value: dateValue(today) });
  const titleInput = ui.input({ placeholder: '留空则由系统按类型与时间范围自动命名', maxlength: 120 });
  const noteInput = ui.textarea({ placeholder: '备注（可选，会写入报表记录）', rows: 2, maxlength: 300 });

  const body = ui.el('div', { class: 'stack' },
    ui.el('div', { class: 'form-grid' },
      ui.field('报表类型', typeSel, '服务端支持的类型：运行日报 / 运行周报 / 运行月报 / 自定义报表'),
      ui.field('标题（可选）', titleInput, '留空时按「类型 + 时间范围」自动命名'),
      ui.field('开始日期', fromInput),
      ui.field('结束日期', toInput),
      ui.el('div', { class: 'span-2' }, ui.field('备注（可选）', noteInput, '最多 300 字，写入报表记录')),
    ),
    ui.el('p', { class: 'tiny muted' }, '报表由服务端同步生成，通常数秒内完成；生成后可在线查看各段明细并导出 CSV。'),
    errBox,
  );

  const cancelBtn = ui.el('button', {
    class: 'btn ghost', type: 'button',
    onclick: () => { modal.close(); },
  }, '取消');
  const submitBtn = ui.el('button', { class: 'btn primary', type: 'button' }, '生成报表');

  const modal = ctx.modal({
    title: '生成报表',
    sub: '按所选类型与时间范围汇总运行数据',
    body,
    footer: ui.el('div', { class: 'row' }, ui.el('span', { class: 'spacer' }), cancelBtn, submitBtn),
  });

  submitBtn.addEventListener('click', async () => {
    const fromV = fromInput.value;
    const toV = toInput.value;
    if (!fromV || !toV) {
      errBox.textContent = '请选择完整的开始与结束日期';
      return;
    }
    if (new Date(fromV) > new Date(toV)) {
      errBox.textContent = '开始日期不能晚于结束日期';
      return;
    }
    errBox.textContent = '';
    submitBtn.disabled = true;
    submitBtn.textContent = '生成中…';
    try {
      await ctx.api.post('/api/reports', {
        type: typeSel.value,
        siteId: ctx.siteId,
        from: new Date(`${fromV}T00:00:00`).toISOString(),
        to: new Date(`${toV}T23:59:59`).toISOString(),
        title: titleInput.value.trim(),
        note: noteInput.value.trim(),
        format: 'csv',
      });
      modal.close();
      ctx.toast('报表已生成', 'success');
      ctx.refresh();
    } catch (err) {
      errBox.textContent = (err && err.message) ? err.message : '报表生成失败，请稍后重试';
      submitBtn.disabled = false;
      submitBtn.textContent = '生成报表';
    }
  });
}

async function removeReport(ctx, rep) {
  const ok = await ctx.confirm({
    title: '删除报表',
    message: `确定删除「${rep.title || rep.id}」吗？`,
    detail: '删除后记录不可恢复，已导出的 CSV 文件不受影响。',
    confirmText: '删除',
    danger: true,
  });
  if (!ok) return;
  try {
    await ctx.api.del(`/api/reports/${encodeURIComponent(rep.id)}`);
    ctx.toast('报表已删除', 'success');
    ctx.refresh();
  } catch (err) {
    ctx.toast((err && err.message) ? err.message : '删除失败', 'error');
  }
}

export async function render(root, ctx) {
  const page = ui.el('div', { class: 'p-reports stack' });
  ui.mount(root, page);
  const canExport = hasPerm(ctx, 'report.export');

  let loaded;
  try {
    loaded = await ctx.api.getFull('/api/reports', { siteId: ctx.siteId, pageSize: 50 });
  } catch (err) {
    ui.mount(page,
      ui.pageHead('报表中心', '运行报表按时间窗汇总水量、水质、化验、告警与工单数据，可在线查看明细并导出 CSV。'),
      ui.card({ body: ui.errorState(err, () => ctx.refresh()) }),
    );
    return;
  }

  const rows = Array.isArray(loaded.data) ? loaded.data : [];
  const listMeta = loaded.meta || {};
  const types = listMeta.types || TYPE_FALLBACK;
  const total = Number(listMeta.total);
  const count = Number.isFinite(total) ? total : rows.length;

  const head = ui.pageHead(
    '报表中心',
    '运行报表按时间窗汇总水量、水质、化验、告警与工单数据，可在线查看各段明细并导出 CSV。',
    [
      canExport
        ? ui.el('button', { class: 'btn primary sm', type: 'button', onclick: () => openCreate(ctx, types) }, ui.icon('report'), '生成报表')
        : null,
      ui.el('button', { class: 'btn ghost sm', type: 'button', onclick: () => ctx.refresh() }, ui.icon('refresh'), '刷新'),
    ],
  );

  const table = ui.table({
    columns: [
      {
        key: 'title', label: '标题', width: '32%',
        render: (row) => ui.el('div', {},
          ui.el('div', { class: 'cell-main r-title', style: { minWidth: '260px' } }, row.title || '（未命名报表）'),
          ui.el('div', { class: 'cell-sub' }, row.id || ''),
        ),
      },
      {
        key: 'type', label: '类型', width: '76px',
        render: (row) => ui.el('span', { class: 'badge accent' }, row.typeName || TYPE_FALLBACK[row.type] || row.type || '—'),
      },
      { key: 'site', label: '站点', width: '118px', render: (row) => ui.el('span', { style: { whiteSpace: 'nowrap' } }, row.siteName || '—') },
      {
        key: 'period', label: '时间范围',
        render: (row) => ui.el('div', { class: 'mono small', style: { whiteSpace: 'nowrap' } },
          periodText(row.periodFrom, row.periodTo)),
      },
      { key: 'creator', label: '生成人', width: '104px', render: (row) => ui.el('span', { style: { whiteSpace: 'nowrap' } }, row.creatorName || '—') },
      { key: 'createdAt', label: '生成时间', render: (row) => ui.el('span', { class: 'mono small', style: { whiteSpace: 'nowrap' } }, ui.fmt.dateTime(row.createdAt)) },
      { key: 'status', label: '状态', width: '76px', render: (row) => statusBadge(row.status) },
      {
        key: 'ops', label: '操作', width: '150px',
        render: (row) => ui.el('div', { class: 'row r-ops', style: { whiteSpace: 'nowrap', flexWrap: 'nowrap' } },
          ui.el('button', {
            class: 'btn ghost sm', type: 'button',
            onclick: () => openDetail(ctx, row.id, canExport),
          }, '查看'),
          canExport
            ? ui.el('button', {
              class: 'btn ghost sm', type: 'button',
              onclick: () => exportReport(ctx, row),
            }, '导出')
            : null,
          canExport
            ? ui.el('button', {
              class: 'btn danger sm', type: 'button',
              onclick: () => removeReport(ctx, row),
            }, '删除')
            : null,
        ),
      },
    ],
    rows,
    emptyText: '暂无报表',
    emptySub: '点击右上角「生成报表」，按时间窗汇总运行数据',
    onRow: (row) => openDetail(ctx, row.id, canExport),
    rowClass: () => 'r-row',
  });

  const canCreate = canExport;
  const listBody = rows.length
    ? ui.el('div', { class: 'r-table-wrap' }, table)
    : ui.el('div', { class: 'stack' },
      ui.empty('暂无报表', '报表为零，点击下方按钮按时间窗生成第一份运行报表。', 'report'),
      canCreate
        ? ui.el('div', { class: 'row r-empty-actions' },
          ui.el('button', { class: 'btn primary', type: 'button', onclick: () => openCreate(ctx, types) }, '生成报表'))
        : ui.el('p', { class: 'tiny muted' }, '当前账号无 report.export 权限，无法生成或导出报表。'),
    );

  const listCard = ui.card({
    title: '报表列表',
    sub: `共 ${count} 份`,
    class: 'r-list',
    body: listBody,
  });

  ui.mount(page, head, listCard);
}

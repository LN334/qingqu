/* API 客户端：统一响应解包、错误对象、实时通道与文件下载 */

export class ApiError extends Error {
  constructor(message, { code, status, details } = {}) {
    super(message || '请求失败');
    this.name = 'ApiError';
    this.code = code || 'UNKNOWN';
    this.status = status || 0;
    this.details = details ?? null;
  }
}

const listeners = new Set();
function emit(event) {
  for (const fn of [...listeners]) {
    try { fn(event); } catch (e) { console.error('[api] listener error', e); }
  }
}

async function request(method, path, data, opts = {}) {
  const init = {
    method,
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  };
  if (data !== undefined && data !== null) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(data);
  }
  let res;
  try {
    res = await fetch(path, init);
  } catch (err) {
    throw new ApiError('无法连接平台服务，请确认服务已启动', { code: 'NETWORK', status: 0 });
  }

  const contentType = res.headers.get('content-type') || '';
  if (!contentType.includes('application/json')) {
    // 文件下载或静态资源
    if (opts.raw) return res;
    const text = await res.text().catch(() => '');
    if (!res.ok) throw new ApiError(text || `请求失败（${res.status}）`, { status: res.status });
    return text;
  }

  let payload = null;
  try { payload = await res.json(); } catch { payload = null; }

  if (!res.ok || (payload && payload.ok === false)) {
    const err = payload && payload.error ? payload.error : {};
    if (res.status === 401) emit({ type: 'auth:required' });
    throw new ApiError(err.message || `请求失败（${res.status}）`, {
      code: err.code || `HTTP_${res.status}`,
      status: res.status,
      details: err.details,
    });
  }
  return payload ? payload.data : null;
}

export const api = {
  get(path, params) {
    const url = buildUrl(path, params);
    return request('GET', url);
  },
  /** 返回完整响应包 {data, meta}，用于需要 meta 的列表 */
  async getFull(path, params) {
    const res = await fetch(buildUrl(path, params), { credentials: 'same-origin', headers: { Accept: 'application/json' } });
    const payload = await res.json().catch(() => null);
    if (!res.ok || !payload || payload.ok === false) {
      if (res.status === 401) emit({ type: 'auth:required' });
      throw new ApiError((payload && payload.error && payload.error.message) || `请求失败（${res.status}）`, {
        code: payload && payload.error ? payload.error.code : `HTTP_${res.status}`,
        status: res.status,
        details: payload && payload.error ? payload.error.details : null,
      });
    }
    return { data: payload.data, meta: payload.meta || {} };
  },
  post(path, data) { return request('POST', path, data); },
  patch(path, data) { return request('PATCH', path, data); },
  put(path, data) { return request('PUT', path, data); },
  del(path, data) { return request('DELETE', path, data); },
  raw(path) { return request('GET', path, null, { raw: true }); },

  on(fn) {
    listeners.add(fn);
    return () => listeners.delete(fn);
  },

  // 认证
  login(username, password) { return request('POST', '/api/auth/login', { username, password }); },
  logout() { return request('POST', '/api/auth/logout', {}); },
  me() { return request('GET', '/api/auth/me'); },
  changePassword(oldPassword, newPassword) { return request('POST', '/api/auth/password', { oldPassword, newPassword }); },
  logoutOthers() { return request('DELETE', '/api/auth/sessions', {}); },

  // 常用
  meta() { return request('GET', '/api/meta'); },
  sites() { return request('GET', '/api/sites'); },
  overview(siteId) { return request('GET', buildUrl('/api/overview', { siteId })); },
  process(siteId) { return request('GET', `/api/sites/${encodeURIComponent(siteId)}/process`); },
  latest(siteId, params) { return request('GET', buildUrl('/api/telemetry/latest', { siteId, ...params })); },
  history(siteId, params) { return request('GET', buildUrl('/api/telemetry/history', { siteId, ...params })); },
  notifications(unread) { return request('GET', buildUrl('/api/notifications', unread ? { unread: 1 } : {})); },
  readNotifications(body) { return request('POST', '/api/notifications/read', body); },

  // 原始响应（CSV/JSON 下载）
  async download(path, filename) {
    const res = await request('GET', path, null, { raw: true });
    if (!res.ok) throw new ApiError(`下载失败（${res.status}）`, { status: res.status });
    const blob = await res.blob();
    const disposition = res.headers.get('content-disposition') || '';
    const match = /filename\*=UTF-8''([^;]+)/i.exec(disposition) || /filename="?([^";]+)"?/i.exec(disposition);
    const name = filename || (match ? decodeURIComponent(match[1]) : 'download');
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = name;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
    return name;
  },
};

function buildUrl(path, params) {
  if (!params) return path;
  const search = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue;
    search.append(k, v);
  }
  const qs = search.toString();
  return qs ? `${path}${path.includes('?') ? '&' : '?'}${qs}` : path;
}

/* ---------------- 实时通道 ---------------- */
export function createLive({ onEvent, onStatus } = {}) {
  let ws = null;
  let retry = 0;
  let closed = false;
  let timer = null;

  function connect() {
    if (closed) return;
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    try {
      ws = new WebSocket(`${proto}//${location.host}/ws`);
    } catch {
      schedule();
      return;
    }
    ws.addEventListener('open', () => {
      retry = 0;
      if (onStatus) onStatus('online');
    });
    ws.addEventListener('message', (ev) => {
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }
      if (onEvent) onEvent(msg);
    });
    ws.addEventListener('close', () => {
      if (onStatus) onStatus('offline');
      if (!closed) schedule();
    });
    ws.addEventListener('error', () => { try { ws.close(); } catch {} });
  }

  function schedule() {
    clearTimeout(timer);
    retry += 1;
    const wait = Math.min(15000, 800 * 2 ** Math.min(retry, 4));
    timer = setTimeout(connect, wait);
  }

  connect();
  return {
    close() {
      closed = true;
      clearTimeout(timer);
      if (ws) { try { ws.close(); } catch {} }
    },
    get state() { return ws && ws.readyState === 1 ? 'online' : 'offline'; },
  };
}

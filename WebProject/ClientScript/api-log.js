(function (window, document) {
  'use strict';

  // Nhật ký request API (Main/zcrptapilog.aspx) — gọi AppHandler/ApiLogView.ashx (JSON).
  // Mọi chữ lấy từ server đi qua textContent/createTextNode; không dùng innerHTML với dữ liệu log, không ghi body ra console.
  var root = document.getElementById('apilog');
  if (!root) { return; }

  var HANDLER = root.getAttribute('data-handler');
  var FILTER_KEY = 'apilog.filters.v2';
  var WIDTH_KEY = 'apilog.detailWidth';
  var MAX_RANGE_DAYS = 31;
  var SLOW_MS = 1000;
  var HIGHLIGHT_LIMIT = 200000;
  var DRAWER_WIDTH = 1200;
  var GUID = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;

  var state = {
    page: 1, pageSize: 25, total: 0, rows: [], loaded: false,
    selected: null, detail: null, detailError: null, bodies: null, bodiesError: null, bodiesLoading: false,
    tab: 'overview', listXhr: null, detailXhr: null
  };

  var MESSAGES = {
    UNAUTHORIZED: 'Phiên đăng nhập đã hết hạn. Đăng nhập lại FBO rồi mở lại trang.',
    FORBIDDEN: 'Bạn chưa được cấp quyền menu Nhật ký request API (zcrptApiLog).',
    INVALID_DATE: 'Ngày không hợp lệ.',
    INVALID_RANGE: 'Khoảng ngày tối đa ' + MAX_RANGE_DAYS + ' ngày và "Đến ngày" không nhỏ hơn "Từ ngày".',
    INVALID_STATUS: 'HTTP status phải là số từ 100 đến 599, hoặc cả nhóm như 4xx.',
    INVALID_REQ_ID: 'Request ID phải đúng dạng GUID.',
    AUDIT_UNAVAILABLE: 'Không ghi được nhật ký truy cập body (bảng zcApiLogViewAudit) nên body không được trả về.',
    NOT_FOUND: 'Không tìm thấy request (có thể đã bị dọn theo hạn lưu log).',
    NO_DATABASE: 'Không xác định được cơ sở dữ liệu của đơn vị đang đăng nhập.',
    DATABASE_ERROR: 'Truy vấn cơ sở dữ liệu thất bại. Thử lại sau hoặc báo quản trị.',
    NETWORK: 'Không kết nối được máy chủ. Kiểm tra mạng rồi thử lại.',
    TIMEOUT: 'Máy chủ phản hồi quá lâu. Thu hẹp bộ lọc rồi thử lại.',
    BAD_RESPONSE: 'Máy chủ trả dữ liệu không đúng định dạng.'
  };

  // Icon nét mảnh (viewBox 24), chỉ dùng chuỗi tĩnh trong file này
  var ICONS = {
    search: ['M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14z', 'M21 21l-4.3-4.3'],
    x: ['M18 6L6 18', 'M6 6l12 12'],
    refresh: ['M21 12a9 9 0 1 1-2.64-6.36', 'M21 3v6h-6'],
    copy: ['M9 9h11v11H9z', 'M5 15H4V4h11v1'],
    check: ['M20 6L9 17l-5-5'],
    alert: ['M12 9v4', 'M12 17h.01', 'M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z'],
    info: ['M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z', 'M12 16v-4', 'M12 8h.01'],
    lock: ['M5 11h14v10H5z', 'M8 11V7a4 4 0 0 1 8 0v4'],
    inbox: ['M22 12h-6l-2 3h-4l-2-3H2', 'M5.5 5.1L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.5-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.7 1.1z'],
    left: ['M15 18l-6-6 6-6'],
    right: ['M9 18l6-6-6-6'],
    redirect: ['M15 10l5 5-5 5', 'M4 4v7a4 4 0 0 0 4 4h12'],
    eye: ['M1 12s4-8 11-8 11 8 11 8-4 8-11 8S1 12 1 12z', 'M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z'],
    code: ['M16 18l6-6-6-6', 'M8 6l-6 6 6 6'],
    wrap: ['M3 6h18', 'M3 12h15a3 3 0 0 1 0 6h-4', 'M16 16l-2 2 2 2', 'M3 18h7'],
    shield: ['M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z']
  };

  // ---------- DOM helpers ----------
  function $(id) { return document.getElementById(id); }

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) { e.className = cls; }
    if (text !== undefined && text !== null) { e.textContent = text; }
    return e;
  }

  function add(parent) {
    for (var i = 1; i < arguments.length; i++) {
      var child = arguments[i];
      if (child === null || child === undefined) { continue; }
      parent.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
    }
    return parent;
  }

  function clear(node) {
    while (node.firstChild) { node.removeChild(node.firstChild); }
  }

  function icon(name) {
    var ns = 'http://www.w3.org/2000/svg';
    var svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('class', 'apilog-icon');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('focusable', 'false');
    var paths = ICONS[name] || [];
    for (var i = 0; i < paths.length; i++) {
      var p = document.createElementNS(ns, 'path');
      p.setAttribute('d', paths[i]);
      svg.appendChild(p);
    }
    return svg;
  }

  function button(text, cls, iconName, onClick) {
    var b = el('button', 'apilog-btn ' + (cls || 'apilog-btn--secondary apilog-btn--sm'));
    b.type = 'button';
    if (iconName) { b.appendChild(icon(iconName)); }
    if (text) { b.appendChild(document.createTextNode(text)); }
    if (onClick) { b.onclick = onClick; }
    return b;
  }

  function dash() { return el('span', 'apilog-dash', '—'); }

  function valueOrDash(value, cls) {
    return value === null || value === undefined || value === '' ? dash() : el('span', cls || '', String(value));
  }

  // ---------- Định dạng ----------
  function pad(n) { return (n < 10 ? '0' : '') + n; }

  function todayIso() {
    var d = new Date();
    return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
  }

  // "yyyy-MM-dd HH:mm:ss.fff" (giờ máy chủ API) → phần ngày giờ dd/MM/yyyy HH:mm:ss và phần mili giây; không qua Date để khỏi lệch múi giờ
  function splitTime(text) {
    var m = /^(\d{4})-(\d{2})-(\d{2}) (\d{2}:\d{2}:\d{2})(\.\d+)?$/.exec(text || '');
    if (!m) { return { main: text || '', ms: '' }; }
    return { main: m[3] + '/' + m[2] + '/' + m[1] + ' ' + m[4], ms: m[5] || '' };
  }

  function formatTime(text) {
    var t = splitTime(text);
    return t.main + t.ms;
  }

  function num(n) {
    if (n === null || n === undefined || isNaN(n)) { return '—'; }
    return String(Math.round(n)).replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  }

  function bytes(n) {
    if (n === null || n === undefined) { return '—'; }
    if (n < 1024) { return num(n) + ' B'; }
    var kb = n / 1024;
    if (kb < 1024) { return kb.toFixed(1).replace('.', ',') + ' KB'; }
    return (kb / 1024).toFixed(2).replace('.', ',') + ' MB';
  }

  function unixToLocal(seconds) {
    if (!seconds) { return ''; }
    var d = new Date(seconds * 1000);
    return pad(d.getDate()) + '/' + pad(d.getMonth() + 1) + '/' + d.getFullYear() + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  }

  // ---------- Badge ----------
  function methodBadge(method) {
    var m = (method || '').toUpperCase();
    var known = { GET: 1, POST: 1, PUT: 1, PATCH: 1, DELETE: 1 };
    return el('span', 'apilog-badge apilog-method apilog-method--' + (known[m] ? m.toLowerCase() : 'other'), m || '?');
  }

  function statusInfo(code) {
    if (code >= 200 && code < 300) { return { tone: 'success', icon: 'check', text: 'Thành công' }; }
    if (code >= 300 && code < 400) { return { tone: 'info', icon: 'redirect', text: 'Chuyển hướng' }; }
    if (code >= 400 && code < 500) { return { tone: 'warning', icon: 'alert', text: 'Lỗi phía client' }; }
    if (code >= 500) { return { tone: 'danger', icon: 'x', text: 'Lỗi máy chủ' }; }
    return { tone: 'neutral', icon: 'info', text: 'Không xác định' };
  }

  function statusBadge(code) {
    var info = statusInfo(code);
    var b = el('span', 'apilog-badge apilog-status apilog-tone--' + info.tone);
    b.title = info.text;
    add(b, icon(info.icon), String(code), el('span', 'apilog-sr', ' ' + info.text));
    return b;
  }

  function resultTone(result, status) {
    if (!result) { return null; }
    if (result === 'SUCCESS') { return 'success'; }
    if (status >= 500) { return 'danger'; }
    if (status >= 400) { return 'warning'; }
    return 'neutral';
  }

  function resultBadge(result, status) {
    var tone = resultTone(result, status);
    if (!tone) { return dash(); }
    var b = el('span', 'apilog-badge apilog-result apilog-tone--' + tone, result);
    b.title = result;
    return b;
  }

  // Giá trị SignatureMiddleware / PortalEndpoints ghi vào signature_result mà không phải lỗi
  var SIGNATURE_OK = { VALID: 'Hợp lệ (VALID)', PORTAL: 'Phiên portal (PORTAL)', PORTAL_LOGIN: 'Đăng nhập portal (PORTAL_LOGIN)' };

  function signatureFailed(result) { return !!result && !SIGNATURE_OK.hasOwnProperty(result); }

  function signatureBadge(result) {
    if (!result) {
      var none = el('span', 'apilog-muted', 'Không áp dụng');
      none.title = 'Route không yêu cầu chữ ký';
      return none;
    }
    var failed = signatureFailed(result);
    var b = el('span', 'apilog-badge apilog-result apilog-tone--' + (failed ? 'danger' : result === 'VALID' ? 'success' : 'neutral'));
    add(b, icon(failed ? 'x' : 'check'), failed ? result : SIGNATURE_OK[result]);
    return b;
  }

  // ---------- Gọi handler ----------
  function request(params, onOk, onFail) {
    var parts = [];
    for (var key in params) {
      if (params.hasOwnProperty(key) && params[key] !== '' && params[key] !== null && params[key] !== undefined) {
        parts.push(encodeURIComponent(key) + '=' + encodeURIComponent(params[key]));
      }
    }
    var xhr = new XMLHttpRequest();
    xhr.open('GET', HANDLER + '?' + parts.join('&'), true);
    xhr.timeout = 60000;
    xhr.setRequestHeader('Accept', 'application/json');
    xhr.onreadystatechange = function () {
      if (xhr.readyState !== 4 || xhr.aborted) { return; }
      if (xhr.status === 0) { onFail('NETWORK', 0); return; }
      var data = null;
      try { data = JSON.parse(xhr.responseText); } catch (e) { data = null; }
      if (xhr.status === 200 && data && data.success) { onOk(data); return; }
      onFail((data && data.code) || (xhr.status === 200 ? 'BAD_RESPONSE' : 'HTTP_' + xhr.status), xhr.status);
    };
    xhr.ontimeout = function () { onFail('TIMEOUT', 0); };
    xhr.send();
    return xhr;
  }

  function abort(xhr) {
    if (xhr && xhr.readyState !== 4) { xhr.aborted = true; xhr.abort(); }
  }

  function message(code, status) {
    return MESSAGES[code] || ('Lỗi không xác định (' + (status ? 'HTTP ' + status : code) + ').');
  }

  // ---------- Bộ lọc ----------
  var FIELDS = { from: 'apilogFrom', to: 'apilogTo', user: 'apilogUser', path: 'apilogPath', status: 'apilogStatusCode', result: 'apilogResult', reqId: 'apilogReqId' };

  function readFilters() {
    var f = {};
    for (var key in FIELDS) { if (FIELDS.hasOwnProperty(key)) { f[key] = $(FIELDS[key]).value.replace(/^\s+|\s+$/g, ''); } }
    return f;
  }

  function writeFilters(f) {
    for (var key in FIELDS) { if (FIELDS.hasOwnProperty(key)) { $(FIELDS[key]).value = f[key] || ''; } }
  }

  function defaultFilters() {
    var t = todayIso();
    return { from: t, to: t, user: '', path: '', status: '', result: '', reqId: '' };
  }

  function utcDay(iso) {
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso);
    return m ? Date.UTC(+m[1], +m[2] - 1, +m[3]) : NaN;
  }

  // Kiểm tra giống ApiLogQuery phía server để báo ngay tại ô nhập
  function validate(f) {
    var from = utcDay(f.from), to = utcDay(f.to);
    if (isNaN(from)) { return { field: 'from', text: 'Chọn "Từ ngày" hợp lệ.' }; }
    if (isNaN(to)) { return { field: 'to', text: 'Chọn "Đến ngày" hợp lệ.' }; }
    if (to < from) { return { field: 'to', text: '"Đến ngày" phải lớn hơn hoặc bằng "Từ ngày".' }; }
    if ((to - from) / 86400000 + 1 > MAX_RANGE_DAYS) { return { field: 'to', text: 'Khoảng ngày tối đa ' + MAX_RANGE_DAYS + ' ngày.' }; }
    if (f.status && !/^[1-5](\d\d|xx)$/i.test(f.status)) { return { field: 'status', text: 'HTTP status là số từ 100 đến 599, hoặc cả nhóm như 4xx.' }; }
    if (f.reqId && !GUID.test(f.reqId)) { return { field: 'reqId', text: 'Request ID phải đúng dạng GUID, vd 9e972090-a334-46e2-ae26-80d55e56f523.' }; }
    return null;
  }

  function showFilterError(error) {
    for (var key in FIELDS) {
      if (FIELDS.hasOwnProperty(key)) {
        var input = $(FIELDS[key]);
        if (error && error.field === key) { input.setAttribute('aria-invalid', 'true'); } else { input.removeAttribute('aria-invalid'); }
      }
    }
    $('apilogDateError').textContent = error ? error.text : '';
    if (error) { $(FIELDS[error.field]).focus(); }
  }

  function saveFilters(f) {
    try { window.sessionStorage.setItem(FILTER_KEY, JSON.stringify({ filters: f, pageSize: state.pageSize })); } catch (e) { /* sessionStorage bị chặn: bỏ qua */ }
  }

  function loadFilters() {
    try {
      var saved = JSON.parse(window.sessionStorage.getItem(FILTER_KEY) || 'null');
      if (saved && saved.filters) {
        if (saved.pageSize) { state.pageSize = saved.pageSize; }
        return saved.filters;
      }
    } catch (e) { /* bỏ qua */ }
    return defaultFilters();
  }

  // ---------- Danh sách ----------
  function search(page) {
    var f = readFilters();
    var error = validate(f);
    showFilterError(error);
    if (error) { return; }

    state.page = page || 1;
    saveFilters(f);
    abort(state.listXhr);
    setListLoading(true);

    var params = { action: 'list', page: state.page, pageSize: state.pageSize };
    for (var key in f) { if (f.hasOwnProperty(key)) { params[key] = f[key]; } }

    state.listXhr = request(params, function (data) {
      setListLoading(false);
      state.loaded = true;
      state.rows = data.items || [];
      state.total = data.total || 0;
      var pages = Math.max(1, Math.ceil(state.total / state.pageSize));
      if (state.total > 0 && state.page > pages) { search(pages); return; }
      renderFacets(data, f);
      renderRows();
      renderPager();
      $('apilogCount').textContent = num(state.total) + ' request';
      $('apilogNotice').textContent = data.ignoredDateRange ? 'Đang tìm theo Request ID — bỏ qua khoảng ngày.' : '';
      var now = new Date();
      $('apilogLoadedAt').textContent = 'Cập nhật lúc ' + pad(now.getHours()) + ':' + pad(now.getMinutes()) + ':' + pad(now.getSeconds());
    }, function (code, status) {
      setListLoading(false);
      state.rows = [];
      state.total = 0;
      renderFacets(null, f);
      clear($('apilogRows'));
      renderPager();
      $('apilogCount').textContent = '';
      $('apilogNotice').textContent = '';
      if (code === 'UNAUTHORIZED' || code === 'FORBIDDEN') {
        showState('lock', code === 'UNAUTHORIZED' ? 'Phiên đăng nhập đã hết hạn' : 'Không có quyền truy cập', message(code, status), null);
      } else {
        showState('error', 'Không tải được nhật ký', message(code, status), button('Thử lại', 'apilog-btn--secondary apilog-btn--sm', 'refresh', function () { search(state.page); }));
      }
    });
  }

  function setListLoading(loading) {
    var list = root.querySelector('.apilog-list');
    $('apilogSearch').setAttribute('aria-busy', loading ? 'true' : 'false');
    $('apilogFacets').className = 'apilog-facets' + (loading ? ' is-loading' : '');
    if (loading && !state.rows.length) {
      list.className = 'apilog-card apilog-list';
      hideState();
      renderSkeleton();
    } else {
      list.className = 'apilog-card apilog-list' + (loading ? ' is-loading' : '');
    }
  }

  function renderSkeleton() {
    var body = $('apilogRows');
    clear(body);
    var widths = [80, 50, 40, 90, 40, 60, 40, 50];
    for (var r = 0; r < 8; r++) {
      var tr = el('tr', 'apilog-skel');
      tr.setAttribute('aria-hidden', 'true');
      for (var c = 0; c < widths.length; c++) {
        var td = el('td');
        var bar = el('span');
        bar.style.width = (widths[c] - (r % 3) * 8) + '%';
        td.appendChild(bar);
        tr.appendChild(td);
      }
      body.appendChild(tr);
    }
  }

  function showState(kind, title, text, action) {
    var box = $('apilogState');
    clear(box);
    box.className = 'apilog-state apilog-state--' + kind;
    var ic = el('span', 'apilog-state__icon');
    ic.appendChild(icon(kind === 'error' ? 'alert' : kind === 'lock' ? 'lock' : 'inbox'));
    add(box, ic, el('div', 'apilog-state__title', title), el('div', '', text), action);
    box.removeAttribute('hidden');
    box.setAttribute('role', kind === 'empty' ? 'status' : 'alert');
  }

  function hideState() { $('apilogState').setAttribute('hidden', 'hidden'); }

  function renderRows() {
    var body = $('apilogRows');
    clear(body);
    if (!state.rows.length) {
      showState('empty', 'Không có request phù hợp', 'Thử mở rộng khoảng ngày hoặc bỏ bớt điều kiện lọc.',
        button('Xóa bộ lọc', 'apilog-btn--secondary apilog-btn--sm', 'x', resetFilters));
      return;
    }
    hideState();
    for (var i = 0; i < state.rows.length; i++) { body.appendChild(rowFor(state.rows[i], i)); }
  }

  function rowFor(r, index) {
    var tr = el('tr', r.reqId === state.selected ? 'is-selected' : '');
    tr.tabIndex = 0;
    tr.setAttribute('data-index', String(index));
    if (r.reqId === state.selected) { tr.setAttribute('aria-current', 'true'); }

    var t = splitTime(r.createdAt);
    var time = el('td', 'apilog-cell-time');
    add(time, t.main, el('small', '', t.ms));
    time.title = 'Giờ máy chủ API';

    var user = el('td', 'apilog-cell-user');
    user.appendChild(valueOrDash(r.userName));
    if (r.userName) { user.title = r.userName; }

    var path = el('td', 'apilog-cell-path', r.path);
    path.title = r.path + (r.queryString || '');

    var duration = el('td', 'apilog-num' + (r.durationMs >= SLOW_MS ? ' apilog-slow' : ''), num(r.durationMs) + ' ms');
    if (r.durationMs >= SLOW_MS) { duration.title = 'Chậm (≥ ' + num(SLOW_MS) + ' ms)'; }

    var action = el('td', 'apilog-num');
    var open = button('Chi tiết', 'apilog-btn--secondary apilog-btn--sm apilog-row-btn', null, function (e) {
      e.stopPropagation();
      select(r, true);
    });
    open.setAttribute('aria-label', 'Xem chi tiết request ' + r.method + ' ' + r.path + ' lúc ' + t.main);
    action.appendChild(open);

    add(tr, time, user, add(el('td'), methodBadge(r.method)), path,
      add(el('td'), statusBadge(r.statusCode)), add(el('td'), resultBadge(r.resultCode, r.statusCode)), duration, action);

    tr.onclick = function () { select(r, false); };
    tr.onkeydown = function (e) {
      var key = e.keyCode || e.which;
      if (key === 13 || key === 32) { e.preventDefault(); select(r, true); }
      else if (key === 38 || key === 40) {
        e.preventDefault();
        var next = key === 40 ? tr.nextSibling : tr.previousSibling;
        if (next && next.focus) { next.focus(); }
      }
    };
    return tr;
  }

  function markSelected() {
    var rows = $('apilogRows').children;
    for (var i = 0; i < rows.length; i++) {
      var r = state.rows[+rows[i].getAttribute('data-index')];
      var on = !!r && r.reqId === state.selected;
      rows[i].className = on ? 'is-selected' : '';
      if (on) { rows[i].setAttribute('aria-current', 'true'); } else { rows[i].removeAttribute('aria-current'); }
    }
  }

  function renderPager() {
    var pages = Math.max(1, Math.ceil(state.total / state.pageSize));
    var nav = $('apilogPages');
    clear(nav);
    var first = state.total ? (state.page - 1) * state.pageSize + 1 : 0;
    var last = Math.min(state.total, state.page * state.pageSize);
    $('apilogRange').textContent = state.total ? 'Hiển thị ' + num(first) + '–' + num(last) + ' / ' + num(state.total) : '';
    if (pages <= 1) { return; }

    function pageButton(label, page, aria, iconName) {
      var b = el('button', 'apilog-page');
      b.type = 'button';
      if (iconName) { b.appendChild(icon(iconName)); } else { b.textContent = label; }
      b.setAttribute('aria-label', aria);
      if (page === state.page && !iconName) { b.setAttribute('aria-current', 'page'); }
      if (page < 1 || page > pages || (page === state.page && iconName)) { b.disabled = true; }
      b.onclick = function () { if (page !== state.page) { search(page); } };
      return b;
    }

    nav.appendChild(pageButton('', state.page - 1, 'Trang trước', 'left'));
    var shown = {};
    var list = [1, pages, state.page - 1, state.page, state.page + 1];
    if (state.page <= 3) { list.push(2, 3, 4); }
    if (state.page >= pages - 2) { list.push(pages - 1, pages - 2, pages - 3); }
    var nums = [];
    for (var i = 0; i < list.length; i++) { if (list[i] >= 1 && list[i] <= pages && !shown[list[i]]) { shown[list[i]] = 1; nums.push(list[i]); } }
    nums.sort(function (a, b) { return a - b; });
    for (var j = 0; j < nums.length; j++) {
      if (j > 0 && nums[j] - nums[j - 1] > 1) { nav.appendChild(el('span', 'apilog-pager__gap', '…')); }
      nav.appendChild(pageButton(String(nums[j]), nums[j], 'Trang ' + nums[j]));
    }
    nav.appendChild(pageButton('', state.page + 1, 'Trang sau', 'right'));
  }

  // Chip lọc nhanh: số đếm theo ngày / tài khoản / đường dẫn / Request ID (server không áp HTTP status và mã kết quả
  // khi đếm), bấm chip thì điền ô HTTP status + Mã kết quả rồi tìm lại
  var FACETS = [
    { label: 'Tất cả', status: '', result: '', count: function (s) { return s.all; } },
    { label: 'Thành công 2xx', tone: 'success', status: '2xx', result: '', count: function (s) { return s.http2xx; } },
    { label: 'Chuyển hướng 3xx', status: '3xx', result: '', count: function (s) { return s.http3xx; }, hideZero: true },
    { label: 'Lỗi client 4xx', tone: 'warning', status: '4xx', result: '', count: function (s) { return s.http4xx; } },
    { label: 'Lỗi máy chủ 5xx', tone: 'danger', status: '5xx', result: '', count: function (s) { return s.http5xx; } },
    { label: 'Mã kết quả khác SUCCESS', tone: 'violet', status: '', result: '!SUCCESS', count: function (s) { return s.resultNotSuccess; } }
  ];

  function facetActive(facet, f) {
    return (f.status || '').toLowerCase() === facet.status && (f.result || '').toUpperCase() === facet.result;
  }

  function renderFacets(data, f) {
    var box = $('apilogFacets');
    var s = data && data.stats;
    clear(box);
    for (var i = 0; i < FACETS.length; i++) {
      var facet = FACETS[i];
      var count = s ? facet.count(s) : null;
      if (facet.hideZero && !count && !facetActive(facet, f)) { continue; }
      box.appendChild(facetButton(facet, count, facetActive(facet, f)));
    }
    var avg = $('apilogAvg');
    avg.textContent = s && s.avgMs !== null ? 'TB ' + num(s.avgMs) + ' ms · chậm nhất ' + num(s.maxMs) + ' ms' : '';
    avg.title = 'Thời gian phản hồi theo khoảng lọc, không tính HTTP status / mã kết quả';
  }

  function facetButton(facet, count, active) {
    var b = el('button', 'apilog-facet' + (facet.tone ? ' apilog-facet--' + facet.tone : ''));
    b.type = 'button';
    b.setAttribute('aria-pressed', active ? 'true' : 'false');
    var dot = el('span', 'apilog-facet__dot');
    dot.setAttribute('aria-hidden', 'true');
    add(b, dot, facet.label, el('span', 'apilog-facet__count', num(count)));
    b.title = active ? 'Đang lọc theo nhóm này' : 'Lọc theo: ' + facet.label;
    b.onclick = function () {
      $('apilogStatusCode').value = facet.status;
      $('apilogResult').value = facet.result;
      search(1);
    };
    return b;
  }

  function resetFilters() {
    writeFilters(defaultFilters());
    showFilterError(null);
    search(1);
  }

  // ---------- Panel chi tiết ----------
  var detailTimer = null;
  var lastFocus = null;

  function isDrawer() { return window.innerWidth < DRAWER_WIDTH; }

  function setDetailOpen(open) {
    if (open) { root.classList.remove('is-detail-closed'); } else { root.classList.add('is-detail-closed'); }
  }

  function closeDetail() {
    setDetailOpen(false);
    if (lastFocus && lastFocus.focus) { lastFocus.focus(); }
  }

  function select(row, focusPanel) {
    var changed = state.selected !== row.reqId;
    state.selected = row.reqId;
    markSelected();
    lastFocus = document.activeElement;
    setDetailOpen(true);
    if (changed) {
      abort(state.detailXhr);
      state.detail = { summary: row, loading: true };
      state.detailError = null;
      state.bodies = null;
      state.bodiesError = null;
      state.bodiesLoading = false;
      renderDetail();
      window.clearTimeout(detailTimer);
      detailTimer = window.setTimeout(function () { loadDetail(row.reqId); }, 120);
    }
    if (focusPanel || isDrawer()) {
      var close = $('apilogDetail').querySelector('[data-close]');
      if (close) { close.focus(); }
    }
  }

  function loadDetail(reqId) {
    state.detailXhr = request({ action: 'detail', reqId: reqId }, function (data) {
      if (state.selected !== reqId) { return; }
      state.detail = data;
      renderDetail();
    }, function (code, status) {
      if (state.selected !== reqId) { return; }
      state.detail.loading = false;
      state.detailError = message(code, status);
      renderDetail();
    });
  }

  function loadBodies() {
    var reqId = state.selected;
    state.bodiesLoading = true;
    state.bodiesError = null;
    renderDetail();
    request({ action: 'body', reqId: reqId }, function (data) {
      if (state.selected !== reqId) { return; }
      state.bodiesLoading = false;
      state.bodies = data;
      renderDetail();
    }, function (code, status) {
      if (state.selected !== reqId) { return; }
      state.bodiesLoading = false;
      state.bodiesError = message(code, status);
      renderDetail();
    });
  }

  var TABS = [
    { id: 'overview', label: 'Tổng quan' },
    { id: 'request', label: 'Request' },
    { id: 'response', label: 'Response' },
    { id: 'headers', label: 'Headers' },
    { id: 'diagnostics', label: 'Lỗi / chẩn đoán' }
  ];

  function hasProblem(d) {
    var s = d.summary;
    return s.statusCode >= 400 || (s.resultCode && s.resultCode !== 'SUCCESS') || signatureFailed(s.signatureResult) || (d.attacks && d.attacks.length > 0);
  }

  function renderPlaceholder() {
    var box = $('apilogDetail');
    clear(box);
    var p = el('div', 'apilog-detail__placeholder');
    var ic = el('span', 'apilog-state__icon');
    ic.appendChild(icon('eye'));
    add(p, ic, el('div', 'apilog-state__title', 'Chưa chọn request'), el('div', '', 'Chọn một dòng trong danh sách để xem chi tiết, body và thông tin chẩn đoán.'));
    box.appendChild(p);
  }

  function renderDetail() {
    var box = $('apilogDetail');
    var d = state.detail;
    if (!d) { renderPlaceholder(); return; }
    var s = d.summary;
    var scroll = box.querySelector('.apilog-detail__body');
    var keepScroll = scroll ? scroll.scrollTop : 0;
    clear(box);

    var head = el('div', 'apilog-detail__head');
    var title = el('div', 'apilog-detail__title');
    var close = button('', 'apilog-btn--ghost apilog-btn--icon', 'x', closeDetail);
    close.setAttribute('aria-label', 'Đóng panel chi tiết');
    close.setAttribute('data-close', '1');
    add(title, methodBadge(s.method), el('h2', 'apilog-detail__path', s.path), close);
    var sub = el('div', 'apilog-detail__sub');
    add(sub, statusBadge(s.statusCode), resultBadge(s.resultCode, s.statusCode), el('span', '', formatTime(s.createdAt)), el('span', 'apilog-num', num(s.durationMs) + ' ms'));
    add(head, title, sub);
    box.appendChild(head);

    var tabs = el('div', 'apilog-tabs');
    tabs.setAttribute('role', 'tablist');
    tabs.setAttribute('aria-label', 'Chi tiết request');
    for (var i = 0; i < TABS.length; i++) { tabs.appendChild(tabButton(TABS[i], d)); }
    box.appendChild(tabs);

    var body = el('div', 'apilog-detail__body');
    body.id = 'apilogTabPanel';
    body.setAttribute('role', 'tabpanel');
    body.setAttribute('aria-labelledby', 'apilogTab-' + state.tab);
    body.tabIndex = 0;
    if (state.detailError) { body.appendChild(note(state.detailError, 'danger')); }
    var render = { overview: renderOverview, request: renderRequest, response: renderResponse, headers: renderHeaders, diagnostics: renderDiagnostics }[state.tab];
    render(body, d);
    box.appendChild(body);
    body.scrollTop = keepScroll;
  }

  function tabButton(tab, d) {
    var b = el('button', 'apilog-tab', tab.label);
    b.type = 'button';
    b.id = 'apilogTab-' + tab.id;
    b.setAttribute('role', 'tab');
    b.setAttribute('aria-controls', 'apilogTabPanel');
    var on = tab.id === state.tab;
    b.setAttribute('aria-selected', on ? 'true' : 'false');
    b.tabIndex = on ? 0 : -1;
    if (tab.id === 'diagnostics' && hasProblem(d)) {
      var dot = el('span', 'apilog-tab__dot');
      dot.setAttribute('aria-hidden', 'true');
      add(b, dot, el('span', 'apilog-sr', ' (có lỗi)'));
    }
    b.onclick = function () { switchTab(tab.id); };
    b.onkeydown = function (e) {
      var key = e.keyCode || e.which;
      if (key !== 37 && key !== 39) { return; }
      e.preventDefault();
      var index = 0;
      for (var i = 0; i < TABS.length; i++) { if (TABS[i].id === state.tab) { index = i; } }
      index = (index + (key === 39 ? 1 : TABS.length - 1)) % TABS.length;
      switchTab(TABS[index].id);
      $('apilogTab-' + TABS[index].id).focus();
    };
    return b;
  }

  function switchTab(id) {
    if (state.tab === id) { return; }
    state.tab = id;
    var scroll = $('apilogDetail').querySelector('.apilog-detail__body');
    if (scroll) { scroll.scrollTop = 0; }
    renderDetail();
  }

  function section(title) {
    var s = el('section', 'apilog-section');
    s.appendChild(el('h3', 'apilog-section__title', title));
    return s;
  }

  function note(text, tone) { return el('p', 'apilog-note' + (tone ? ' apilog-note--' + tone : ''), text); }

  function dl(pairs) {
    var list = el('dl', 'apilog-dl');
    for (var i = 0; i < pairs.length; i++) {
      var dd = el('dd');
      var value = pairs[i][1];
      if (value === null || value === undefined || value === '') { dd.appendChild(dash()); }
      else if (typeof value === 'string' || typeof value === 'number') { dd.textContent = String(value); }
      else { dd.appendChild(value); }
      if (pairs[i][2]) { dd.className = pairs[i][2]; }
      add(list, el('dt', '', pairs[i][0]), dd);
    }
    return list;
  }

  function copyable(text, label) {
    var wrap = document.createDocumentFragment();
    var b = button('', 'apilog-btn--ghost apilog-btn--icon', 'copy', function () { copyText(text, label); });
    b.setAttribute('aria-label', 'Sao chép ' + label);
    b.title = 'Sao chép ' + label;
    add(wrap, el('span', 'apilog-mono', text), b);
    return wrap;
  }

  function loadingNote() { return note('Đang tải thông tin chi tiết...'); }

  function renderOverview(body, d) {
    var s = d.summary;
    var st = statusInfo(s.statusCode);
    var status = document.createDocumentFragment();
    add(status, statusBadge(s.statusCode), el('span', 'apilog-muted', st.text));
    var time = document.createDocumentFragment();
    add(time, el('span', '', formatTime(s.createdAt)), el('span', 'apilog-muted', '(giờ máy chủ API)'));
    var sec = section('Tóm tắt');
    sec.appendChild(dl([
      ['Request ID', copyable(s.reqId, 'Request ID')],
      ['Thời gian', time],
      ['Tài khoản API', s.userName],
      ['IP', s.ipAddress, 'apilog-mono'],
      ['Method', methodBadge(s.method)],
      ['Đường dẫn', s.path, 'apilog-mono'],
      ['Query string', s.queryString, 'apilog-mono'],
      ['Form', s.form, 'apilog-mono'],
      ['HTTP status', status],
      ['Mã kết quả', resultBadge(s.resultCode, s.statusCode)],
      ['Thời lượng', num(s.durationMs) + ' ms', 'apilog-num'],
      ['Byte gửi / nhận', bytes(s.requestBytes) + ' / ' + bytes(s.responseBytes)],
      ['Kiểm tra chữ ký', signatureBadge(s.signatureResult)]
    ]));
    body.appendChild(sec);
  }

  function renderRequest(body, d) {
    var s = d.summary;
    var info = section('Request');
    info.appendChild(dl([
      ['Method', methodBadge(s.method)],
      ['Đường dẫn', s.path, 'apilog-mono'],
      ['Query string', s.queryString, 'apilog-mono'],
      ['Form', s.form, 'apilog-mono'],
      ['Kích thước body', bytes(s.requestBytes)]
    ]));
    body.appendChild(info);
    var headers = section('Headers');
    headers.appendChild(note('Header của request không được lưu đầy đủ; xem tab Headers cho các header bảo mật đã ghi nhận.'));
    body.appendChild(headers);
    var b = section('Request body');
    bodyPanel(b, d, 'request', 'Request body');
    body.appendChild(b);
  }

  function renderResponse(body, d) {
    var s = d.summary;
    var info = section('Response');
    info.appendChild(dl([
      ['HTTP status', statusBadge(s.statusCode)],
      ['Mã kết quả', resultBadge(s.resultCode, s.statusCode)],
      ['Thời lượng', num(s.durationMs) + ' ms', 'apilog-num'],
      ['Kích thước body', bytes(s.responseBytes)]
    ]));
    body.appendChild(info);
    var b = section('Response body');
    bodyPanel(b, d, 'response', 'Response body');
    body.appendChild(b);
  }

  var STATE_TEXT = {
    none: 'Không có body, hoặc route không lưu body (đăng nhập, cấp token).',
    missing: 'Chỉ ghi nhận mã SHA256: body vượt ngưỡng lưu log hoặc đã bị dọn theo hạn lưu trữ.',
    tooLarge: 'Body lớn hơn 256 KB nên không hiển thị trên trang.',
    notJson: 'Body không phải JSON hợp lệ nên không hiển thị (máy chủ không che được bí mật một cách an toàn).'
  };

  function bodyPanel(container, d, kind, title) {
    var meta = d[kind + 'Body'];
    if (state.bodies) {
      var item = state.bodies[kind];
      if (item.state === 'json') { container.appendChild(codeViewer(title, item.text, item.size)); }
      else { container.appendChild(note(STATE_TEXT[item.state] || STATE_TEXT.none)); }
      return;
    }
    if (d.loading) { container.appendChild(loadingNote()); return; }
    if (!meta) { return; }
    if (meta.state !== 'stored') { container.appendChild(note(STATE_TEXT[meta.state])); return; }
    if (meta.tooLarge) { container.appendChild(note(STATE_TEXT.tooLarge)); return; }

    var gate = el('div', 'apilog-note');
    add(gate,
      el('div', '', 'Body có thể chứa dữ liệu khách hàng. Mở body sẽ ghi nhật ký truy cập; khóa bí mật (password, token, chữ ký…) đã được che tại máy chủ.'),
      el('div', 'apilog-muted', 'Kích thước lưu: ' + bytes(meta.size)));
    container.appendChild(gate);
    if (state.bodiesError) { container.appendChild(note(state.bodiesError, 'danger')); }
    var open = button(state.bodiesLoading ? 'Đang tải body...' : 'Xem body request và response', 'apilog-btn--primary apilog-btn--sm', 'eye', loadBodies);
    open.disabled = state.bodiesLoading;
    container.appendChild(open);
  }

  function renderHeaders(body, d) {
    var s = d.summary;
    body.appendChild(note('Hệ thống không lưu toàn bộ header. Authorization, X-Fast-Signature và cookie không bao giờ được ghi vào log. Dưới đây là các header bảo mật đã ghi nhận.'));
    if (d.loading) { body.appendChild(loadingNote()); return; }
    var h = d.headers || {};
    var ts = null;
    if (h.clientTimestamp) {
      ts = document.createDocumentFragment();
      add(ts, el('span', 'apilog-mono', String(h.clientTimestamp)), el('span', 'apilog-muted', '→ ' + unixToLocal(h.clientTimestamp) + ' (giờ trình duyệt)'));
    }
    var sec = section('Header đã ghi nhận');
    sec.appendChild(dl([
      ['X-Request-ID', copyable(s.reqId, 'Request ID')],
      ['X-Fast-Nonce', h.nonce, 'apilog-mono'],
      ['Thời điểm ký (từ nonce)', ts],
      ['Idempotency-Key', h.idempotencyKey, 'apilog-mono'],
      ['X-Fast-Content-SHA256', h.contentSha256, 'apilog-mono'],
      ['Kết quả kiểm chữ ký', signatureBadge(s.signatureResult)]
    ]));
    body.appendChild(sec);
  }

  function renderDiagnostics(body, d) {
    var s = d.summary;
    if (!hasProblem(d) && !d.loading) { body.appendChild(note('Không ghi nhận lỗi cho request này.', 'success')); }
    else if (s.statusCode >= 500) { body.appendChild(note('Lỗi phía máy chủ (5xx): API gặp sự cố khi xử lý. Gửi Request ID cho đội phát triển để tra log máy chủ.', 'danger')); }
    else if (s.statusCode >= 400) { body.appendChild(note('Lỗi phía client (4xx): request bị từ chối do dữ liệu, quyền hoặc chữ ký. Xem mã kết quả và response body để biết lý do.', 'warning')); }
    else if (s.resultCode && s.resultCode !== 'SUCCESS') { body.appendChild(note('HTTP thành công nhưng mã kết quả nghiệp vụ khác SUCCESS.', 'warning')); }

    var sec = section('Chẩn đoán');
    sec.appendChild(dl([
      ['HTTP status', statusBadge(s.statusCode)],
      ['Mã kết quả', resultBadge(s.resultCode, s.statusCode)],
      ['Kiểm tra chữ ký', signatureBadge(s.signatureResult)],
      ['Request ID', copyable(s.reqId, 'Request ID')]
    ]));
    body.appendChild(sec);

    var events = section('Sự kiện bảo mật');
    if (d.loading) { events.appendChild(loadingNote()); }
    else if (!d.attacks || !d.attacks.length) { events.appendChild(el('p', 'apilog-muted', 'Không có sự kiện bảo mật gắn với request này.')); }
    else {
      var list = el('ul', 'apilog-list-plain');
      for (var i = 0; i < d.attacks.length; i++) {
        var a = d.attacks[i];
        var li = el('li');
        add(li, el('span', 'apilog-badge apilog-result apilog-tone--danger', a.code), el('span', '', formatTime(a.createdAt)), el('span', 'apilog-muted apilog-mono', a.ipAddress || ''));
        list.appendChild(li);
      }
      events.appendChild(list);
    }
    body.appendChild(events);
    body.appendChild(note('Log không lưu exception hay stack trace. Thông báo lỗi nghiệp vụ nằm trong response body (trường code / message) ở tab Response.'));
  }

  // ---------- JSON viewer ----------
  var TOKEN = /("(?:[^"\\]|\\.)*")(\s*:)?|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(true|false|null)|([{}\[\],:])|(\s+)/g;

  // Tách token từ JSON đã hợp lệ (server kiểm và che). Sai cú pháp → null
  function tokenize(text) {
    var tokens = [];
    var m, end = 0;
    TOKEN.lastIndex = 0;
    while ((m = TOKEN.exec(text)) !== null) {
      if (m.index !== end) { return null; }
      end = TOKEN.lastIndex;
      if (m[1]) { tokens.push({ t: 's', v: m[1] }); if (m[2]) { tokens.push({ t: 'p', v: ':' }); } }
      else if (m[3]) { tokens.push({ t: 'n', v: m[3] }); }
      else if (m[4]) { tokens.push({ t: 'l', v: m[4] }); }
      else if (m[5]) { tokens.push({ t: 'p', v: m[5] }); }
    }
    return end === text.length ? tokens : null;
  }

  // Định dạng lại từ token: giữ nguyên chữ của chuỗi và số (không qua JSON.parse nên số lớn không bị làm tròn)
  function prettify(tokens) {
    var out = [];
    var depth = 0;
    function newline() { out.push('\n'); for (var k = 0; k < depth; k++) { out.push('  '); } }
    for (var i = 0; i < tokens.length; i++) {
      var tk = tokens[i];
      if (tk.t === 'p' && (tk.v === '{' || tk.v === '[')) {
        var next = tokens[i + 1];
        if (next && next.t === 'p' && (next.v === '}' || next.v === ']')) { out.push(tk.v + next.v); i++; continue; }
        out.push(tk.v);
        depth++;
        newline();
      } else if (tk.t === 'p' && (tk.v === '}' || tk.v === ']')) {
        depth--;
        newline();
        out.push(tk.v);
      } else if (tk.t === 'p' && tk.v === ',') {
        out.push(',');
        newline();
      } else if (tk.t === 'p' && tk.v === ':') {
        out.push(': ');
      } else {
        out.push(tk.v);
      }
    }
    return out.join('');
  }

  function highlight(pre, text) {
    clear(pre);
    if (text.length > HIGHLIGHT_LIMIT) { pre.textContent = text; return false; }
    var frag = document.createDocumentFragment();
    var plain = '';
    var m, end = 0;
    function flush() { if (plain) { frag.appendChild(document.createTextNode(plain)); plain = ''; } }
    function span(cls, value) { flush(); frag.appendChild(el('span', cls, value)); }
    TOKEN.lastIndex = 0;
    while ((m = TOKEN.exec(text)) !== null) {
      if (m.index !== end) { plain += text.substring(end, m.index); }
      end = TOKEN.lastIndex;
      if (m[1]) {
        span(m[2] ? 'apilog-j-key' : (m[1] === '"***"' ? 'apilog-j-mask' : 'apilog-j-str'), m[1]);
        if (m[2]) { span('apilog-j-pun', m[2]); }
      }
      else if (m[3]) { span('apilog-j-num', m[3]); }
      else if (m[4]) { span('apilog-j-lit', m[4]); }
      else if (m[5]) { span('apilog-j-pun', m[5]); }
      else { plain += m[0]; }
    }
    plain += text.substring(end);
    flush();
    pre.appendChild(frag);
    return true;
  }

  function codeViewer(title, raw, size) {
    var tokens = tokenize(raw);
    var pretty = tokens ? prettify(tokens) : null;
    var view = { pretty: !!pretty, wrap: false };

    var box = el('div', 'apilog-code');
    var bar = el('div', 'apilog-code__bar');
    var heading = el('span', 'apilog-code__title', title);
    heading.appendChild(el('span', 'apilog-code__meta', bytes(size)));
    var formatBtn = button('', 'apilog-btn--ghost apilog-btn--sm', 'code');
    var wrapBtn = button('Xuống dòng', 'apilog-btn--ghost apilog-btn--sm', 'wrap');
    var copyBtn = button('Sao chép', 'apilog-btn--ghost apilog-btn--sm', 'copy');
    add(bar, heading, formatBtn, wrapBtn, copyBtn);
    var pre = el('pre');
    pre.tabIndex = 0;
    pre.setAttribute('aria-label', title);
    var hint = el('div', 'apilog-code__empty');
    add(box, bar, pre, hint);

    function current() { return view.pretty && pretty ? pretty : raw; }
    function draw() {
      formatBtn.lastChild.nodeValue = view.pretty ? 'Thu gọn' : 'Định dạng';
      formatBtn.disabled = !pretty;
      wrapBtn.setAttribute('aria-pressed', view.wrap ? 'true' : 'false');
      box.className = 'apilog-code' + (view.wrap ? ' is-wrap' : '');
      var colored = highlight(pre, current());
      var hints = [];
      if (!pretty) { hints.push('Không định dạng được JSON, đang hiển thị nguyên văn.'); }
      if (!colored) { hints.push('Body dài nên tắt tô màu để trình duyệt chạy mượt.'); }
      hint.textContent = hints.join(' ');
      if (hints.length) { hint.removeAttribute('hidden'); } else { hint.setAttribute('hidden', 'hidden'); }
    }
    formatBtn.appendChild(document.createTextNode(''));
    formatBtn.onclick = function () { view.pretty = !view.pretty; draw(); };
    wrapBtn.onclick = function () { view.wrap = !view.wrap; draw(); };
    copyBtn.onclick = function () { copyText(current(), title); };
    draw();
    return box;
  }

  // ---------- Sao chép ----------
  var toastTimer = null;

  function toast(text) {
    var t = document.querySelector('.apilog-toast');
    if (!t) {
      t = el('div', 'apilog-toast');
      t.setAttribute('role', 'status');
      document.body.appendChild(t);
    }
    t.textContent = text;
    t.removeAttribute('hidden');
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(function () { t.setAttribute('hidden', 'hidden'); }, 1800);
  }

  function copyText(text, label) {
    function fallback() {
      var area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', 'readonly');
      area.style.position = 'fixed';
      area.style.opacity = '0';
      document.body.appendChild(area);
      area.select();
      var ok = false;
      try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
      document.body.removeChild(area);
      toast(ok ? 'Đã sao chép ' + label : 'Trình duyệt chặn sao chép, hãy chọn và Ctrl+C.');
    }
    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(text).then(function () { toast('Đã sao chép ' + label); }, fallback);
    } else {
      fallback();
    }
  }

  // ---------- Bố cục: chiều cao vừa khung, kéo độ rộng panel ----------
  // Desktop: trang vừa khung, bảng và panel tự cuộn, footer FBO không che dòng cuối.
  // Không đủ chỗ cho bảng (cửa sổ thấp/hẹp): trả về cuộn trang bình thường, chừa chỗ cho footer.
  function fit() {
    var footer = document.getElementById('mpTableFooter');
    var reserve = footer ? footer.offsetHeight : 0;
    root.style.height = '';
    root.style.marginBottom = reserve + 'px';
    root.classList.remove('is-fitted');
    if (window.innerWidth < 768) { return; }
    var top = root.getBoundingClientRect().top + (window.pageYOffset || document.documentElement.scrollTop || 0);
    var height = window.innerHeight - top - reserve - 2;
    root.style.height = height + 'px';
    root.classList.add('is-fitted');
    if ($('apilogMain').offsetHeight < 340 || root.scrollHeight > root.clientHeight + 1) {
      root.style.height = '';
      root.classList.remove('is-fitted');
      return;
    }
    root.style.marginBottom = '';
  }

  function setDetailWidth(width, save) {
    var main = $('apilogMain');
    var max = Math.max(360, main.clientWidth * 0.7);
    var w = Math.round(Math.min(max, Math.max(340, width)));
    root.style.setProperty('--al-detail-width', w + 'px');
    if (save) { try { window.localStorage.setItem(WIDTH_KEY, String(w)); } catch (e) { /* bỏ qua */ } }
  }

  function initResizer() {
    var handle = $('apilogResizer');
    var saved = 0;
    try { saved = parseInt(window.localStorage.getItem(WIDTH_KEY), 10) || 0; } catch (e) { saved = 0; }
    if (saved) { setDetailWidth(saved, false); }

    handle.onmousedown = function (e) {
      e.preventDefault();
      handle.classList.add('is-dragging');
      document.body.style.userSelect = 'none';
      var right = $('apilogMain').getBoundingClientRect().right;
      function move(ev) { setDetailWidth(right - ev.clientX, false); }
      function up(ev) {
        handle.classList.remove('is-dragging');
        document.body.style.userSelect = '';
        setDetailWidth(right - ev.clientX, true);
        document.removeEventListener('mousemove', move);
        document.removeEventListener('mouseup', up);
      }
      document.addEventListener('mousemove', move);
      document.addEventListener('mouseup', up);
    };
    handle.onkeydown = function (e) {
      var key = e.keyCode || e.which;
      if (key !== 37 && key !== 39) { return; }
      e.preventDefault();
      var current = $('apilogDetail').offsetWidth;
      setDetailWidth(current + (key === 37 ? 24 : -24), true);
    };
  }

  // ---------- Khởi tạo ----------
  function init() {
    var icons = root.querySelectorAll('[data-icon]');
    for (var i = 0; i < icons.length; i++) { icons[i].insertBefore(icon(icons[i].getAttribute('data-icon')), icons[i].firstChild); }

    writeFilters(loadFilters());
    $('apilogPageSize').value = String(state.pageSize);
    if ($('apilogPageSize').value !== String(state.pageSize)) { state.pageSize = 25; $('apilogPageSize').value = '25'; }

    $('apilogSearch').onclick = function () { search(1); };
    $('apilogClear').onclick = resetFilters;
    $('apilogRefresh').onclick = function () { search(state.page); };
    $('apilogPageSize').onchange = function () { state.pageSize = parseInt(this.value, 10) || 25; search(1); };
    $('apilogBackdrop').onclick = closeDetail;

    var inputs = root.querySelectorAll('.apilog-filter input');
    for (var j = 0; j < inputs.length; j++) {
      inputs[j].onkeydown = function (e) { if ((e.keyCode || e.which) === 13) { e.preventDefault(); search(1); } };
      inputs[j].oninput = function () { if (this.getAttribute('aria-invalid')) { showFilterError(null); } };
    }
    document.addEventListener('keydown', function (e) {
      if ((e.keyCode || e.which) === 27 && !root.classList.contains('is-detail-closed') && state.detail) { closeDetail(); }
    });

    initResizer();
    renderPlaceholder();
    setDetailOpen(!isDrawer());
    fit();
    var resizeTimer = null;
    window.addEventListener('resize', function () {
      window.clearTimeout(resizeTimer);
      resizeTimer = window.setTimeout(function () {
        if (isDrawer() && !state.detail) { setDetailOpen(false); }
        fit();
      }, 100);
    });
    search(1);
  }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', init); } else { init(); }
})(window, document);

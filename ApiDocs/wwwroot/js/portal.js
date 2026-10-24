// Portal đối tác Fast API (KE_HOACH 5.3). Trang một màn, định tuyến bằng hash.
// JWT portal giữ trong sessionStorage (chỉ đọc _meta); private_key và JWT API chỉ nằm trong biến của trang, tải lại là mất.
import * as sign from "./fast-sign.js";

const apiBase = (document.querySelector('meta[name="fast-api-base"]')?.content || "").replace(/\/$/, "");
const view = document.getElementById("view");
const SESSION_KEY = "fast-portal";

const state = {
    session: loadSession(),   // { userName, token, expires }
    clockOffset: 0,           // giây: giờ server - giờ máy
    tryIt: null,              // { keys, apiToken, apiTokenExpires } - chỉ trong bộ nhớ
    resources: null,
    errors: null
};

// ---------- DOM: dựng bằng textContent, không bao giờ chèn HTML từ dữ liệu ----------
function h(tag, attrs = {}, ...children) {
    const el = document.createElement(tag);
    for (const [name, value] of Object.entries(attrs)) {
        if (value === null || value === undefined || value === false) continue;
        if (name.startsWith("on")) el.addEventListener(name.slice(2), value);
        else if (name === "class") el.className = value;
        else if (value === true) el.setAttribute(name, "");
        else el.setAttribute(name, value);
    }
    for (const child of children.flat()) {
        if (child === null || child === undefined || child === false) continue;
        el.append(child instanceof Node ? child : String(child));
    }
    return el;
}

function render(...nodes) {
    view.replaceChildren(...nodes);
}

function alertBox(kind, ...content) {
    return h("div", { class: `alert alert-${kind}`, role: "alert" }, ...content);
}

function pre(text, cls = "") {
    return h("pre", { class: `code-block ${cls}` }, text);
}

function copyButton(getText) {
    return h("button", {
        type: "button", class: "btn btn-sm btn-outline-secondary",
        onclick: async e => {
            await navigator.clipboard.writeText(getText());
            e.target.textContent = "Đã chép";
            setTimeout(() => (e.target.textContent = "Chép"), 1500);
        }
    }, "Chép");
}

// ---------- Phiên portal ----------
function loadSession() {
    try {
        const session = JSON.parse(sessionStorage.getItem(SESSION_KEY) || "null");
        return session && session.expires > Date.now() ? session : null;
    } catch {
        return null;
    }
}

function saveSession(session) {
    state.session = session;
    if (session) sessionStorage.setItem(SESSION_KEY, JSON.stringify(session));
    else sessionStorage.removeItem(SESSION_KEY);
    document.getElementById("nav").hidden = !session;
    document.getElementById("account").hidden = !session;
    document.getElementById("account-name").textContent = session ? session.userName : "";
}

function logout() {
    saveSession(null);
    state.tryIt = null;
    state.resources = null;
    location.hash = "#/login";
}

const serverNow = () => Math.floor(Date.now() / 1000) + state.clockOffset;

async function fetchServer() {
    const response = await fetch(apiBase + "/api/_meta/server");
    if (!response.ok) throw new Error(`/_meta/server trả ${response.status}`);
    const server = await response.json();
    state.clockOffset = server.serverTime - Math.floor(Date.now() / 1000);
    return server;
}

/** GET _meta bằng JWT portal; 401 thì về trang đăng nhập */
async function portalGet(path) {
    if (!state.session) throw new SessionExpired();
    const response = await fetch(apiBase + path, { headers: { Authorization: "Bearer " + state.session.token } });
    const body = await response.json().catch(() => ({}));
    if (response.status === 401) {
        saveSession(null);
        throw new SessionExpired();
    }
    if (!response.ok || body.success === false) throw new Error(`${body.code || response.status}: ${body.message || ""}`);
    return body.data;
}

class SessionExpired extends Error {}

// ---------- Đăng nhập: mật khẩu mã hóa RSA-OAEP SHA-256 bằng khóa công khai của server ----------
function loginView(message) {
    const userName = h("input", { class: "form-control", id: "userName", autocomplete: "username", required: true, maxlength: 32 });
    const password = h("input", { class: "form-control", id: "password", type: "password", autocomplete: "current-password" });
    const status = h("div");
    const button = h("button", { class: "btn btn-primary w-100", type: "submit" }, "Đăng nhập");

    const form = h("form", {
        class: "login-card",
        onsubmit: async e => {
            e.preventDefault();
            button.disabled = true;
            status.replaceChildren();
            try {
                await login(userName.value.trim(), password.value);
                password.value = "";
                location.hash = "#/resources";
            } catch (error) {
                status.replaceChildren(alertBox("danger", error.message));
            } finally {
                button.disabled = false;
            }
        }
    },
        h("h1", { class: "h4 mb-1" }, "Cổng đối tác Fast API"),
        h("p", { class: "text-secondary small" }, "Đăng nhập bằng tài khoản API được cấp. Mật khẩu được mã hóa ngay trên trình duyệt trước khi gửi."),
        message ? alertBox("warning", message) : null,
        h("label", { class: "form-label", for: "userName" }, "Tài khoản"), userName,
        h("label", { class: "form-label mt-3", for: "password" }, "Mật khẩu"), password,
        h("div", { class: "mt-4" }, button),
        status);
    render(form);
    userName.focus();
}

async function login(userName, password) {
    if (!globalThis.crypto?.subtle) throw new Error("Trình duyệt không có WebCrypto: portal phải mở qua HTTPS (hoặc localhost).");
    const server = await fetchServer();
    const jwk = server.portalKey;
    const publicKey = await crypto.subtle.importKey("jwk", { kty: jwk.kty, n: jwk.n, e: jwk.e, alg: "RSA-OAEP-256", ext: true },
        { name: "RSA-OAEP", hash: "SHA-256" }, false, ["encrypt"]);
    const plain = new TextEncoder().encode(JSON.stringify({ password, timestamp: server.serverTime, nonce: sign.newNonce(server.serverTime) }));
    const credential = sign.toBase64(await crypto.subtle.encrypt({ name: "RSA-OAEP" }, publicKey, plain));

    const response = await fetch(apiBase + "/api/auth/portal", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ userName, kid: jwk.kid, credential })
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok || !body.accessToken) throw new Error(body.message || body.code || `Đăng nhập lỗi ${response.status}`);
    saveSession({ userName, token: body.accessToken, expires: Date.now() + (body.expiresIn - 30) * 1000 });
}

// ---------- Danh sách tài nguyên ----------
async function resourcesView() {
    state.resources ??= await portalGet("/api/_meta/resources");
    const filter = h("input", { class: "form-control", placeholder: "Lọc theo tên, route, mô tả", type: "search" });
    const body = h("tbody");
    const fill = () => {
        const text = filter.value.trim().toLowerCase();
        body.replaceChildren(...state.resources
            .filter(r => !text || [r.form, r.route, r.description].some(v => (v || "").toLowerCase().includes(text)))
            .map(r => h("tr", {},
                h("td", {}, h("a", { href: `#/resources/${encodeURIComponent(r.form)}` }, r.form)),
                h("td", {}, h("span", { class: "badge bg-primary me-1" }, r.method), h("code", {}, r.route)),
                h("td", {}, r.type),
                h("td", {}, r.description || ""),
                h("td", {}, r.unitRequired ? "Có" : ""),
                h("td", {}, r.paging ? `${r.paging.pageSize} dòng` : ""))));
    };
    filter.addEventListener("input", fill);
    fill();
    render(
        h("h1", { class: "h4" }, "Tài nguyên được cấp quyền"),
        state.resources.length === 0
            ? alertBox("info", "Tài khoản chưa được cấp quyền form nào. Liên hệ quản trị để cấp quyền.")
            : h("div", {}, h("div", { class: "mb-3" }, filter),
                h("div", { class: "table-responsive" }, h("table", { class: "table table-hover align-middle" },
                    h("thead", {}, h("tr", {}, ...["Form", "Route", "Loại", "Mô tả", "unitCode", "Phân trang"].map(t => h("th", {}, t)))),
                    body))));
}

// ---------- Chi tiết tài nguyên + gọi thử ----------
async function resourceView(form) {
    const detail = await portalGet(`/api/_meta/resources/${encodeURIComponent(form)}`);
    const s = detail.summary;
    const sampleText = JSON.stringify(detail.sample, null, 2);

    render(
        h("p", {}, h("a", { href: "#/resources" }, "← Tài nguyên")),
        h("h1", { class: "h4" }, s.form, " ", h("small", { class: "text-secondary" }, s.description || "")),
        h("dl", { class: "row summary" },
            h("dt", { class: "col-sm-3" }, "Route"), h("dd", { class: "col-sm-9" }, h("code", {}, `${s.method} ${s.route}`)),
            h("dt", { class: "col-sm-3" }, "Loại"), h("dd", { class: "col-sm-9" }, s.type),
            h("dt", { class: "col-sm-3" }, "unitCode"), h("dd", { class: "col-sm-9" }, s.unitRequired ? `Bắt buộc (?unitCode=…, quyền ${s.unitAction || "đơn vị"})` : "Không cần"),
            h("dt", { class: "col-sm-3" }, "Phân trang"), h("dd", { class: "col-sm-9" }, s.paging
                ? `${s.paging.pageSize} dòng mỗi trang theo khóa ${s.paging.key}; trang sau gọi lại với ?cursor=…&page=n`
                : "Không")),
        h("h2", { class: "h5 mt-4" }, "Cấu trúc body"),
        ...detail.blocks.map(blockTable),
        h("h2", { class: "h5 mt-4 d-flex align-items-center gap-2" }, "Body mẫu", copyButton(() => sampleText)),
        pre(sampleText),
        tryItPanel(s, sampleText));
}

function blockTable(block) {
    return h("div", { class: "mb-3" },
        h("h3", { class: "h6" }, h("code", {}, block.name), block.isRoot ? " (khối gốc)" : " (mảng)", block.key ? ` · khóa ${block.key}` : ""),
        h("div", { class: "table-responsive" }, h("table", { class: "table table-sm" },
            h("thead", {}, h("tr", {}, ...["Field", "Kiểu", "Độ dài", "Bắt buộc", "Định dạng / mặc định", "Mô tả", "Mã lỗi"].map(t => h("th", {}, t)))),
            h("tbody", {}, ...block.fields.map(f => h("tr", {},
                h("td", {}, h("code", {}, f.name)),
                h("td", {}, f.type),
                h("td", {}, f.maxLength ?? ""),
                h("td", {}, f.required ? "Có" : ""),
                h("td", {}, [f.format, f.defaultValue !== null && f.defaultValue !== undefined && f.defaultValue !== "" ? `mặc định ${f.defaultValue}` : null, f.upperCase ? "chữ hoa" : null].filter(Boolean).join("; ")),
                h("td", {}, f.description || "", f.example ? h("div", { class: "small text-secondary" }, `Ví dụ: ${f.example}`) : null),
                h("td", {}, (f.errorCodes || []).map(code => h("code", { class: "me-1" }, code)))))))));
}

function tryItPanel(summary, sampleText) {
    const privateKey = h("input", { class: "form-control", type: "password", autocomplete: "off", spellcheck: "false", placeholder: "private_key (base64) được cấp" });
    const keyStatus = h("span", { class: "small" });
    const unitCode = h("input", { class: "form-control", placeholder: "unitCode", value: "" });
    const extraQuery = h("input", { class: "form-control", placeholder: "Tham số thêm, vd: a=1&b=2" });
    const body = h("textarea", { class: "form-control code-input", rows: 12, spellcheck: "false" }, sampleText);
    const output = h("div", { class: "mt-3" });
    // List, Report chỉ đọc: gọi thẳng; Voucher, Transfer có ghi: kiểm dryRun trước, gửi thật phải xác nhận và kèm Idempotency-Key
    const readOnly = summary.type === "List" || summary.type === "Report";
    let lastPaging = null, lastQuery = "";

    const pageButton = h("button", { type: "button", class: "btn btn-outline-secondary", disabled: true, onclick: () => nextPage() }, "Trang tiếp");

    function refreshKeyStatus() {
        keyStatus.textContent = state.tryIt ? "Đã nạp khóa (chỉ trong bộ nhớ trang này)" : "";
        keyStatus.className = state.tryIt ? "small text-success" : "small";
    }

    async function loadKey() {
        output.replaceChildren();
        try {
            const keys = await sign.deriveKeys(privateKey.value);
            privateKey.value = "";
            state.tryIt = { keys, apiToken: null, apiTokenExpires: 0 };
            await ensureApiToken(output);
        } catch (error) {
            state.tryIt = null;
            output.replaceChildren(alertBox("danger", error.message));
        }
        refreshKeyStatus();
    }

    function buildQuery(extra) {
        const params = [];
        if (unitCode.value.trim()) params.push(`unitCode=${encodeURIComponent(unitCode.value.trim())}`);
        if (extraQuery.value.trim()) params.push(extraQuery.value.trim().replace(/^\?/, ""));
        params.push(...extra);
        return params.join("&");
    }

    async function call(query, bodyText, idempotencyKey) {
        if (!state.tryIt) {
            output.replaceChildren(alertBox("warning", "Nạp private_key trước khi gọi thử."));
            return;
        }
        output.replaceChildren(h("div", { class: "text-secondary" }, "Đang gọi…"));
        try {
            await ensureApiToken(output);
            const result = await signedCall("POST", summary.route + (query ? "?" + query : ""), bodyText, idempotencyKey);
            output.replaceChildren(callResult(result));
            const paging = result.json?.paging;
            lastPaging = paging && paging.cursor && paging.pageIndex + 1 < paging.pageCount ? paging : null;
            lastQuery = query;
            pageButton.disabled = !lastPaging;
        } catch (error) {
            output.replaceChildren(alertBox("danger", error.message));
        }
    }

    function nextPage() {
        if (!lastPaging) return;
        const base = lastQuery.split("&").filter(p => !/^(cursor|page|dryRun)=/.test(p));
        call([...base, `cursor=${encodeURIComponent(lastPaging.cursor)}`, `page=${lastPaging.pageIndex + 1}`].join("&"), "", "");
    }

    refreshKeyStatus();
    return h("section", { class: "try-it mt-5" },
        h("h2", { class: "h5" }, "Gọi thử"),
        h("p", { class: "small text-secondary" },
            "private_key chỉ dùng để tính HMAC ngay trên trình duyệt, không gửi lên server và mất khi tải lại trang. ",
            "Portal lấy JWT API bằng /api/auth/token (ký bằng khóa này) rồi ký từng request như code mẫu."),
        h("div", { class: "row g-2 align-items-center" },
            h("div", { class: "col-md-8" }, privateKey),
            h("div", { class: "col-md-4 d-flex gap-2 align-items-center" },
                h("button", { type: "button", class: "btn btn-outline-primary", onclick: loadKey }, "Nạp khóa"), keyStatus)),
        h("div", { class: "row g-2 mt-1" },
            summary.unitRequired ? h("div", { class: "col-md-3" }, unitCode) : null,
            h("div", { class: "col" }, extraQuery)),
        h("div", { class: "mt-2" }, body),
        h("div", { class: "d-flex gap-2 mt-2" },
            readOnly
                ? h("button", { type: "button", class: "btn btn-primary", onclick: () => call(buildQuery([]), body.value, "") }, "Gọi")
                : [
                    h("button", { type: "button", class: "btn btn-primary", onclick: () => call(buildQuery(["dryRun=true"]), body.value, "") }, "Kiểm tra (dryRun)"),
                    h("button", {
                        type: "button", class: "btn btn-danger",
                        onclick: () => {
                            const effect = summary.type === "Transfer" ? "Các chứng từ trả về sẽ được đánh dấu đã chuyển." : "Dữ liệu sẽ được ghi vào ERP.";
                            if (confirm(`Gửi thật tới ${summary.route}? ${effect}`)) call(buildQuery([]), body.value, crypto.randomUUID());
                        }
                    }, "Gửi thật")
                ],
            pageButton),
        output);
}

/** JWT API (scope=api) lấy bằng auth/token ký HMAC; giữ trong bộ nhớ, hết hạn thì lấy lại */
async function ensureApiToken(output) {
    const tryIt = state.tryIt;
    if (tryIt.apiToken && tryIt.apiTokenExpires > Date.now()) return;
    await fetchServer();
    const userName = state.session.userName;
    const result = await signedCall("POST", "/api/auth/token", JSON.stringify({ userName }), "", {}, false);
    if (!result.json?.accessToken) {
        output.replaceChildren(callResult(result));
        throw new Error(`Không lấy được JWT API: ${result.json?.code || result.status} ${result.json?.message || ""}`);
    }
    tryIt.apiToken = result.json.accessToken;
    tryIt.apiTokenExpires = Date.now() + (result.json.expiresIn - 30) * 1000;
}

async function signedCall(method, pathAndQuery, bodyText, idempotencyKey, extraHeaders = {}, withToken = true) {
    const { keys, apiToken } = state.tryIt;
    const signed = await sign.signRequest(keys, { body: bodyText, timestamp: serverNow(), idempotencyKey });
    const headers = { "Content-Type": "application/json", ...signed.headers, ...extraHeaders };
    if (withToken) headers.Authorization = "Bearer " + apiToken;

    const response = await fetch(apiBase + pathAndQuery, { method, headers, body: bodyText });
    const bytes = new Uint8Array(await response.arrayBuffer());
    const verify = await sign.verifyResponse(keys, {
        status: response.status,
        requestId: response.headers.get("X-Request-ID"),
        requestNonce: signed.nonce,
        timestamp: response.headers.get("X-Fast-Timestamp"),
        contentSha256: response.headers.get("X-Fast-Content-SHA256"),
        signature: response.headers.get("X-Fast-Signature"),
        bodyBytes: bytes
    });
    const text = new TextDecoder().decode(bytes);
    let json = null;
    try { json = JSON.parse(text); } catch { /* body không phải JSON */ }

    const shown = { ...headers };
    if (shown.Authorization) shown.Authorization = "Bearer …";
    return {
        method, pathAndQuery, message: signed.message, requestHeaders: shown, status: response.status, verify,
        signed: response.headers.has("X-Fast-Signature"), json, text
    };
}

function callResult(r) {
    const verdict = !r.signed
        ? alertBox("warning", "Response không có chữ ký (lỗi trước khi server xác định được khóa, vd UNAUTHORIZED).")
        : r.verify.checksumOk && r.verify.signatureOk
            ? alertBox("success", "Chữ ký response hợp lệ.")
            : alertBox("danger", `Chữ ký response SAI (checksum ${r.verify.checksumOk ? "đúng" : "sai"}, HMAC ${r.verify.signatureOk ? "đúng" : "sai"}): không tin nội dung.`);
    const headerText = Object.entries(r.requestHeaders).map(([k, v]) => `${k}: ${v}`).join("\n");
    return h("div", {},
        h("div", { class: "d-flex align-items-center gap-2" },
            h("span", { class: `badge ${r.status < 300 ? "bg-success" : "bg-danger"}` }, r.status),
            h("code", {}, `${r.method} ${r.pathAndQuery}`)),
        h("div", { class: "mt-2" }, verdict),
        h("details", { open: true }, h("summary", {}, "Thông điệp đã ký (md5(body) + nonce)"), pre(r.message)),
        h("details", {}, h("summary", {}, "Header request"), pre(headerText)),
        r.signed ? h("details", {}, h("summary", {}, "canonicalResponse (portal tự dựng để kiểm)"), pre(r.verify.canonical)) : null,
        h("h3", { class: "h6 mt-3" }, "Body response"),
        pre(r.json ? JSON.stringify(r.json, null, 2) : r.text));
}

// ---------- Bảng mã lỗi ----------
async function errorsView() {
    state.errors ??= await portalGet("/api/_meta/errors");
    const filter = h("input", { class: "form-control", type: "search", placeholder: "Lọc theo mã hoặc nội dung" });
    const body = h("tbody");
    const fill = () => {
        const text = filter.value.trim().toLowerCase();
        body.replaceChildren(...state.errors
            .filter(e => !text || e.code.toLowerCase().includes(text) || (e.message || "").toLowerCase().includes(text))
            .map(e => h("tr", {}, h("td", {}, h("code", {}, e.code)), h("td", {}, e.httpStatus), h("td", {}, e.message || ""))));
    };
    filter.addEventListener("input", fill);
    fill();
    render(
        h("h1", { class: "h4" }, "Mã lỗi"),
        h("p", { class: "small text-secondary" }, "Mọi response có thuộc tính code. Lỗi kiểm dữ liệu trả VALIDATION_FAILED kèm danh sách lỗi theo dòng, field và mã."),
        h("div", { class: "mb-3" }, filter),
        h("div", { class: "table-responsive" }, h("table", { class: "table table-sm" },
            h("thead", {}, h("tr", {}, h("th", {}, "Mã"), h("th", {}, "HTTP"), h("th", {}, "Nội dung"))), body)));
}

// ---------- Code mẫu + vector thử ----------
const SAMPLES = [
    { name: "cURL (bash + OpenSSL)", file: "fast-sign.sh" },
    { name: "C#", file: "FastSign.cs" },
    { name: "JavaScript (Node.js)", file: "fast-sign.mjs" },
    { name: "Python", file: "fast_sign.py" },
    { name: "Java", file: "FastSign.java" }
];

async function samplesView() {
    const v = sign.TEST_VECTOR;
    const checks = await sign.selfTest();
    const code = h("div");
    const tabs = h("div", { class: "nav nav-tabs", role: "tablist" });

    async function show(sample) {
        for (const tab of tabs.children) tab.classList.toggle("active", tab.dataset.file === sample.file);
        const response = await fetch(`samples/${sample.file}`);
        const text = await response.text();
        code.replaceChildren(
            h("div", { class: "d-flex gap-2 my-2" },
                h("a", { class: "btn btn-sm btn-outline-primary", href: `samples/${sample.file}`, download: sample.file }, `Tải ${sample.file}`),
                copyButton(() => text)),
            pre(text));
    }

    for (const sample of SAMPLES) {
        tabs.append(h("button", { type: "button", class: "nav-link", "data-file": sample.file, onclick: () => show(sample) }, sample.name));
    }

    const rows = [
        ["private_key (base64)", v.privateKey],
        ["K_request = HMAC(private_key, \"FAST-API-V3-REQUEST\")", v.requestKeyHex],
        ["K_response = HMAC(private_key, \"FAST-API-V3-RESPONSE\")", v.responseKeyHex],
        ["Body", v.body],
        ["md5Hex(body)", v.bodyMd5],
        ["X-Fast-Nonce = \"<unix giây>-<ngẫu nhiên>\"", v.nonce],
        ["X-Fast-Signature (request)", v.requestSignature],
        ["Response: status, X-Request-ID, X-Fast-Timestamp", `${v.responseStatus}, ${v.responseRequestId}, ${v.responseTimestamp}`],
        ["Response body", v.responseBody],
        ["X-Fast-Signature (response)", v.responseSignature]
    ];
    const message = v.bodyMd5 + v.nonce;

    render(
        h("h1", { class: "h4" }, "Ký request và kiểm chữ ký response"),
        h("ol", { class: "small" },
            h("li", {}, "Lấy giờ server: GET /api/_meta/server (serverTime) để tránh lệch giờ."),
            h("li", {}, "Dẫn xuất K_request, K_response từ private_key (HMAC-SHA256 với hai nhãn cố định)."),
            h("li", {}, "Dựng nonce = \"<serverTime>-<chuỗi ngẫu nhiên 8–48 ký tự A-Z a-z 0-9 _ ->\". Giây trong nonce phải lệch giờ server không quá clockSkewSeconds, mỗi nonce chỉ dùng một lần."),
            h("li", {}, "X-Fast-Signature = hex(HMAC-SHA256(K_request, md5Hex(body) + nonce)); body là đúng các byte gửi đi (body rỗng vẫn md5 chuỗi rỗng). Request chỉ cần hai header X-Fast-Nonce và X-Fast-Signature. Lấy JWT ở POST /api/auth/token với body {\"userName\"}, các request sau gửi kèm Authorization: Bearer."),
            h("li", {}, "Kiểm response: HMAC-SHA256(K_response, canonicalResponse) với status, X-Request-ID, nonce của request, X-Fast-Timestamp, SHA-256 body. Sai thì không tin nội dung.")),
        h("h2", { class: "h5 mt-4" }, "Vector thử cố định"),
        h("p", { class: "small text-secondary" }, "Mọi code mẫu đều tự kiểm bằng vector này khi chạy không tham số. Code của bạn ra đúng các giá trị dưới đây là ký đúng."),
        h("div", { class: "table-responsive" }, h("table", { class: "table table-sm vector" },
            h("tbody", {}, ...rows.map(([k, val]) => h("tr", {}, h("th", {}, k), h("td", {}, h("code", {}, String(val)))))))),
        h("h3", { class: "h6" }, "Thông điệp ký của vector (md5Hex(body) + nonce)"),
        pre(message),
        h("p", { class: "small" }, "Trình duyệt này tự kiểm: ",
            ...checks.map(c => h("span", { class: `badge me-1 ${c.ok ? "bg-success" : "bg-danger"}` }, `${c.name} ${c.ok ? "đúng" : "SAI"}`))),
        h("h2", { class: "h5 mt-4" }, "Code mẫu"),
        tabs,
        code);
    await show(SAMPLES[0]);
}

// ---------- Định tuyến ----------
async function route() {
    const [, page, arg] = (location.hash || "#/").slice(1).split("/");
    if (!state.session && page !== "login") {
        location.hash = "#/login";
        return;
    }
    try {
        switch (page) {
            case "login": return loginView();
            case "resources": return arg ? await resourceView(decodeURIComponent(arg)) : await resourcesView();
            case "errors": return await errorsView();
            case "samples": return await samplesView();
            default: location.hash = "#/resources";
        }
    } catch (error) {
        if (error instanceof SessionExpired) loginView("Phiên đăng nhập đã hết hạn, đăng nhập lại.");
        else render(alertBox("danger", error.message));
    } finally {
        for (const link of document.querySelectorAll("#nav a")) link.classList.toggle("active", link.getAttribute("href") === `#/${page}`);
    }
}

document.getElementById("logout").addEventListener("click", logout);
window.addEventListener("hashchange", route);
saveSession(state.session);
if (state.session) fetchServer().catch(() => {});
route();

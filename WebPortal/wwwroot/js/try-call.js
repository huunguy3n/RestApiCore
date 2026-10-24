// Gọi thử một form của WebApi dự án ngay trên trình duyệt: private_key chỉ ở bộ nhớ trang, HMAC tính bằng WebCrypto (fast-sign.js).
// Lấy JWT bằng POST /api/auth/token (ký bằng khóa), rồi ký request form và kiểm chữ ký response. WebApi phải mở CORS cho origin portal.
import * as sign from "./fast-sign.js";

const panel = document.getElementById("try-call");
if (panel) {
    const apiBase = panel.dataset.apiBase.replace(/\/+$/, "");
    const userName = panel.dataset.user;
    const route = panel.dataset.route;
    const type = panel.dataset.type;
    const keyInput = document.getElementById("try-key");
    const unitInput = document.getElementById("try-unit");
    const queryInput = document.getElementById("try-query");
    const bodyInput = document.getElementById("try-body");
    const output = document.getElementById("try-output");
    const nextButton = panel.querySelector("[data-action=next]");
    let state = null, clockOffset = 0, lastPaging = null, lastQuery = "";

    function el(tag, attrs = {}, ...children) {
        const node = document.createElement(tag);
        for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
        for (const child of children.flat()) {
            if (child == null) continue;
            node.append(child instanceof Node ? child : document.createTextNode(String(child)));
        }
        return node;
    }

    function show(...nodes) { output.replaceChildren(...nodes); }

    async function serverNow() {
        if (!clockOffset) {
            const response = await fetch(apiBase + "/api/_meta/server");
            const data = await response.json();
            clockOffset = data.serverTime - Math.floor(Date.now() / 1000);
        }
        return Math.floor(Date.now() / 1000) + clockOffset;
    }

    async function ensureKeys() {
        if (state) return state;
        if (!keyInput.value.trim()) throw new Error("Nhập private_key trước khi gọi.");
        const keys = await sign.deriveKeys(keyInput.value.trim());
        keyInput.value = "";
        state = { keys, token: null, expires: 0 };
        return state;
    }

    async function signedCall(pathAndQuery, bodyText, idempotencyKey, withToken) {
        const signed = await sign.signRequest(state.keys, { body: bodyText, timestamp: await serverNow(), idempotencyKey });
        const headers = { "Content-Type": "application/json", ...signed.headers };
        if (withToken) headers.Authorization = "Bearer " + state.token;
        const response = await fetch(apiBase + pathAndQuery, { method: "POST", headers, body: bodyText });
        const bytes = new Uint8Array(await response.arrayBuffer());
        const verify = await sign.verifyResponse(state.keys, { requestNonce: signed.nonce, signature: response.headers.get("X-Fast-Signature"), bodyBytes: bytes });
        const text = new TextDecoder().decode(bytes);
        let json = null;
        try { json = JSON.parse(text); } catch { /* không phải JSON */ }
        return { pathAndQuery, status: response.status, signed: response.headers.has("X-Fast-Signature"), verify, json, text, message: signed.message };
    }

    async function ensureToken() {
        if (state.token && state.expires > Date.now()) return;
        const result = await signedCall("/api/auth/token", JSON.stringify({ userName }), "", false);
        if (!result.json || !result.json.accessToken) {
            render(result);
            throw new Error(`Không lấy được JWT: ${(result.json && result.json.code) || result.status}`);
        }
        state.token = result.json.accessToken;
        state.expires = Date.now() + (result.json.expiresIn - 30) * 1000;
    }

    function render(r) {
        const verdict = !r.signed
            ? el("p", { class: "adm__warn" }, "Response không có chữ ký (lỗi trước khi server xác định được khóa, vd UNAUTHORIZED).")
            : r.verify.signatureOk
                ? el("p", { class: "adm__ok" }, "Chữ ký response hợp lệ.")
                : el("p", { class: "adm__alert" }, "Chữ ký response SAI: không tin nội dung.");
        show(
            el("p", {}, `HTTP ${r.status} · POST ${r.pathAndQuery}`),
            verdict,
            el("details", {}, el("summary", {}, "Thông điệp đã ký (md5(body) + nonce)"), el("pre", { class: "adm__code" }, r.message)),
            el("pre", { class: "adm__code" }, r.json ? JSON.stringify(r.json, null, 2) : r.text));
    }

    function buildQuery(extra) {
        const params = [];
        if (unitInput.value.trim()) params.push(`unitCode=${encodeURIComponent(unitInput.value.trim())}`);
        if (queryInput.value.trim()) params.push(queryInput.value.trim().replace(/^\?/, ""));
        params.push(...extra);
        return params.join("&");
    }

    async function call(query, bodyText, idempotencyKey) {
        show(el("p", { class: "adm__crumb" }, "Đang gọi…"));
        try {
            await ensureKeys();
            await ensureToken();
            const result = await signedCall(route + (query ? "?" + query : ""), bodyText, idempotencyKey, true);
            render(result);
            const paging = result.json && result.json.paging;
            lastPaging = paging && paging.cursor && paging.pageIndex + 1 < paging.pageCount ? paging : null;
            lastQuery = query;
            nextButton.disabled = !lastPaging;
        } catch (error) {
            show(el("p", { class: "adm__alert" }, error.message));
        }
    }

    panel.addEventListener("click", event => {
        const action = event.target.dataset && event.target.dataset.action;
        if (!action) return;
        if (action === "call") call(buildQuery([]), bodyInput.value, "");
        if (action === "dry") call(buildQuery(["dryRun=true"]), bodyInput.value, "");
        if (action === "send") {
            const effect = type === "Transfer" ? "Các chứng từ trả về sẽ được đánh dấu đã chuyển." : "Dữ liệu sẽ được ghi vào ERP.";
            if (confirm(`Gửi thật tới ${route}? ${effect}`)) call(buildQuery([]), bodyInput.value, crypto.randomUUID());
        }
        if (action === "next" && lastPaging) {
            const base = lastQuery.split("&").filter(p => p && !/^(cursor|page|dryRun)=/.test(p));
            call([...base, `cursor=${encodeURIComponent(lastPaging.cursor)}`, `page=${lastPaging.pageIndex + 1}`].join("&"), "", "");
        }
    });
}

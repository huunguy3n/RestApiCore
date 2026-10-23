// Ký và kiểm thông điệp Fast API v3 bằng WebCrypto (KE_HOACH_V3 4.2), cùng thuật toán với Security/MessageSignature.cs.
// Dùng được trong trình duyệt và Node 20+ (globalThis.crypto). private_key chỉ nằm trong bộ nhớ, không gửi đi đâu.

export const REQUEST_ALGORITHM = "FAST-HMAC-SHA256";
export const RESPONSE_ALGORITHM = "FAST-HMAC-SHA256-RESPONSE";

const subtle = globalThis.crypto.subtle;
const utf8 = new TextEncoder();

export function toHex(bytes) {
    return Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("");
}

export function fromBase64(text) {
    const binary = atob(text.trim());
    return Uint8Array.from(binary, c => c.charCodeAt(0));
}

export function toBase64(bytes) {
    return btoa(String.fromCharCode(...new Uint8Array(bytes)));
}

export function fromBase64Url(text) {
    const base64 = text.replace(/-/g, "+").replace(/_/g, "/");
    return fromBase64(base64 + "=".repeat((4 - base64.length % 4) % 4));
}

export async function sha256Hex(data) {
    const bytes = typeof data === "string" ? utf8.encode(data) : data;
    return toHex(await subtle.digest("SHA-256", bytes));
}

async function hmac(keyBytes, message) {
    const key = await subtle.importKey("raw", keyBytes, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
    return new Uint8Array(await subtle.sign("HMAC", key, typeof message === "string" ? utf8.encode(message) : message));
}

/** K_request, K_response dẫn xuất từ private_key (base64 32 byte) */
export async function deriveKeys(privateKeyBase64) {
    const privateKey = fromBase64(privateKeyBase64);
    return {
        request: await hmac(privateKey, "FAST-API-V3-REQUEST"),
        response: await hmac(privateKey, "FAST-API-V3-RESPONSE")
    };
}

export async function hmacHex(keyBytes, message) {
    return toHex(await hmac(keyBytes, message));
}

/** Encode RFC 3986 trên byte UTF-8: giữ A-Z a-z 0-9 - . _ ~, còn lại %XX hex hoa */
export function rfc3986(value) {
    let result = "";
    for (const b of utf8.encode(value)) {
        const c = String.fromCharCode(b);
        result += /[A-Za-z0-9\-._~]/.test(c) ? c : "%" + b.toString(16).toUpperCase().padStart(2, "0");
    }
    return result;
}

// Giải như ASP.NET đọc query: '+' là dấu cách, %XX theo UTF-8; chuỗi % hỏng giữ nguyên
function formDecode(value) {
    const text = value.replace(/\+/g, " ");
    try {
        return decodeURIComponent(text);
    } catch {
        return text;
    }
}

/** canonicalQuery: tên và giá trị encode RFC 3986, sắp theo tên rồi giá trị (ordinal), nối bằng & */
export function canonicalQuery(rawQuery) {
    if (!rawQuery) {
        return "";
    }
    const query = rawQuery.startsWith("?") ? rawQuery.slice(1) : rawQuery;
    const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
    return query.split("&").filter(part => part.length > 0)
        .map(part => {
            const index = part.indexOf("=");
            const name = index < 0 ? part : part.slice(0, index);
            const value = index < 0 ? "" : part.slice(index + 1);
            return { name: rfc3986(formDecode(name)), value: rfc3986(formDecode(value)) };
        })
        .sort((a, b) => ordinal(a.name, b.name) || ordinal(a.value, b.value))
        .map(p => `${p.name}=${p.value}`)
        .join("&");
}

export function canonicalRequest(method, path, query, timestamp, nonce, idempotencyKey, contentSha256) {
    return [REQUEST_ALGORITHM, method.toUpperCase(), path.toLowerCase(), query, String(timestamp), nonce, idempotencyKey || "", contentSha256.toLowerCase()].join("\n");
}

export function canonicalResponse(statusCode, requestId, requestNonce, timestamp, contentSha256) {
    return [RESPONSE_ALGORITHM, String(statusCode), requestId, requestNonce, String(timestamp), contentSha256.toLowerCase()].join("\n");
}

/** Nonce 32 ký tự hex (khớp ^[A-Za-z0-9_-]{16,64}$) */
export function newNonce() {
    return toHex(globalThis.crypto.getRandomValues(new Uint8Array(16)));
}

/**
 * Ký một request. url là URL đầy đủ hoặc path kèm query; body là chuỗi gửi đi nguyên văn ("" nếu không có).
 * Trả các header cần gửi kèm canonicalRequest để hiện khi debug
 */
export async function signRequest(keys, { method, url, body = "", timestamp, nonce = newNonce(), idempotencyKey = "" }) {
    const parsed = new URL(url, "http://placeholder");
    const contentSha256 = await sha256Hex(body);
    const canonical = canonicalRequest(method, parsed.pathname, canonicalQuery(parsed.search), timestamp, nonce, idempotencyKey, contentSha256);
    const headers = {
        "X-Fast-Timestamp": String(timestamp),
        "X-Fast-Nonce": nonce,
        "X-Fast-Content-SHA256": contentSha256,
        "X-Fast-Signature": await hmacHex(keys.request, canonical)
    };
    if (idempotencyKey) {
        headers["Idempotency-Key"] = idempotencyKey;
    }
    return { headers, canonical, nonce };
}

/** Kiểm chữ ký response; bodyBytes là byte thô nhận được */
export async function verifyResponse(keys, { status, requestId, requestNonce, timestamp, contentSha256, signature, bodyBytes }) {
    const actualSha256 = await sha256Hex(bodyBytes);
    const canonical = canonicalResponse(status, requestId || "", requestNonce, timestamp || "", actualSha256);
    const expected = await hmacHex(keys.response, canonical);
    return {
        canonical,
        checksumOk: actualSha256 === (contentSha256 || "").toLowerCase(),
        signatureOk: expected === (signature || "").trim().toLowerCase()
    };
}

/** Vector thử cố định, dùng chung với Tests/MessageSignatureTests.cs và code mẫu */
export const TEST_VECTOR = {
    privateKey: "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
    requestKeyHex: "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f",
    responseKeyHex: "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd",
    method: "POST",
    url: "/api/v3/sale-invoice?unitCode=CTY&dryRun=false",
    body: "{\"master\":{\"YourID\":\"HD001\"},\"detail\":[{\"ItemID\":\"VT01\",\"Quantity\":2}]}",
    bodySha256: "534275e0a5da4d457614f2f130e8db3dfe3a21cef90e3f5e13ae77c3d60ed12f",
    timestamp: 1792574400,
    nonce: "9b2e4c1f0a7d4e3b",
    idempotencyKey: "6f1c2d3e-0000-4000-8000-000000000001",
    requestSignature: "6728c67bb7eded95a6b13b56c3bcb13378c521fe55b0f23b97a3581918adfa54",
    responseStatus: 200,
    responseRequestId: "8c2f0000-0000-4000-8000-000000000002",
    responseTimestamp: 1792574401,
    responseBody: "{\"code\":\"SUCCESS\"}",
    responseSignature: "1972c2ae0b53f37dd745becadfd19669a8da4d968fb282891ed43fddec17fd68"
};

/** Chạy lại vector thử bằng chính các hàm trên; trả danh sách { name, ok } */
export async function selfTest() {
    const v = TEST_VECTOR;
    const keys = await deriveKeys(v.privateKey);
    const signed = await signRequest(keys, { method: v.method, url: v.url, body: v.body, timestamp: v.timestamp, nonce: v.nonce, idempotencyKey: v.idempotencyKey });
    const verified = await verifyResponse(keys, {
        status: v.responseStatus, requestId: v.responseRequestId, requestNonce: v.nonce, timestamp: v.responseTimestamp,
        contentSha256: await sha256Hex(v.responseBody), signature: v.responseSignature, bodyBytes: utf8.encode(v.responseBody)
    });
    return [
        { name: "K_request", ok: toHex(keys.request) === v.requestKeyHex },
        { name: "K_response", ok: toHex(keys.response) === v.responseKeyHex },
        { name: "SHA-256 body", ok: signed.headers["X-Fast-Content-SHA256"] === v.bodySha256 },
        { name: "Chữ ký request", ok: signed.headers["X-Fast-Signature"] === v.requestSignature },
        { name: "Chữ ký response", ok: verified.checksumOk && verified.signatureOk }
    ];
}

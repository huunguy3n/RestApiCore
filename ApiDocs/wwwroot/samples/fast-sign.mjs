// Fast API v3 - ký request, kiểm chữ ký response (Node.js 18+, chỉ dùng node:crypto và fetch)
// Chạy: node fast-sign.mjs                               -> kiểm vector thử
//       node fast-sign.mjs <base_url> <user> <private_key> -> lấy token rồi gọi _meta/ping
import { createHash, createHmac, randomBytes, timingSafeEqual } from "node:crypto";

export function deriveKeys(privateKeyBase64) {
    const key = Buffer.from(privateKeyBase64, "base64");
    return {
        request: createHmac("sha256", key).update("FAST-API-V3-REQUEST").digest(),
        response: createHmac("sha256", key).update("FAST-API-V3-RESPONSE").digest()
    };
}

const sha256Hex = data => createHash("sha256").update(data).digest("hex");
const hmacHex = (key, text) => createHmac("sha256", key).update(text, "utf8").digest("hex");
const same = (a, b) => a.length === b.length && timingSafeEqual(Buffer.from(a), Buffer.from(b));

// RFC 3986: giữ A-Z a-z 0-9 - . _ ~, còn lại %XX hex hoa trên byte UTF-8
const rfc3986 = value => encodeURIComponent(value).replace(/[!'()*]/g, c => "%" + c.charCodeAt(0).toString(16).toUpperCase());

function formDecode(value) {
    try {
        return decodeURIComponent(value.replace(/\+/g, " "));
    } catch {
        return value.replace(/\+/g, " ");
    }
}

export function canonicalQuery(raw) {
    const query = (raw || "").replace(/^\?/, "");
    const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
    return query.split("&").filter(Boolean)
        .map(part => {
            const i = part.indexOf("=");
            return [rfc3986(formDecode(i < 0 ? part : part.slice(0, i))), rfc3986(formDecode(i < 0 ? "" : part.slice(i + 1)))];
        })
        .sort((a, b) => ordinal(a[0], b[0]) || ordinal(a[1], b[1]))
        .map(([n, v]) => `${n}=${v}`).join("&");
}

export function signRequest(keys, { method, url, body, timestamp, nonce, idempotencyKey = "" }) {
    const parsed = new URL(url, "http://placeholder");
    const contentSha256 = sha256Hex(body);
    const canonical = ["FAST-HMAC-SHA256", method.toUpperCase(), parsed.pathname.toLowerCase(), canonicalQuery(parsed.search),
        String(timestamp), nonce, idempotencyKey, contentSha256].join("\n");
    const headers = {
        "X-Fast-Timestamp": String(timestamp),
        "X-Fast-Nonce": nonce,
        "X-Fast-Content-SHA256": contentSha256,
        "X-Fast-Signature": hmacHex(keys.request, canonical)
    };
    if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
    return headers;
}

export function verifyResponse(keys, { status, headers, requestNonce, body }) {
    const contentSha256 = sha256Hex(body);
    const canonical = ["FAST-HMAC-SHA256-RESPONSE", String(status), headers["x-request-id"] || "", requestNonce,
        headers["x-fast-timestamp"] || "", contentSha256].join("\n");
    return same(contentSha256, (headers["x-fast-content-sha256"] || "").toLowerCase())
        && same(hmacHex(keys.response, canonical), (headers["x-fast-signature"] || "").trim().toLowerCase());
}

function assertEqual(actual, expected, name) {
    if (actual !== expected) throw new Error(`${name}: ${actual} != ${expected}`);
}

function testVector() {
    const keys = deriveKeys("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
    assertEqual(keys.request.toString("hex"), "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f", "K_request");
    assertEqual(keys.response.toString("hex"), "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd", "K_response");
    const headers = signRequest(keys, {
        method: "POST", url: "/api/v3/sale-invoice?unitCode=CTY&dryRun=false",
        body: "{\"master\":{\"YourID\":\"HD001\"},\"detail\":[{\"ItemID\":\"VT01\",\"Quantity\":2}]}",
        timestamp: 1792574400, nonce: "9b2e4c1f0a7d4e3b", idempotencyKey: "6f1c2d3e-0000-4000-8000-000000000001"
    });
    assertEqual(headers["X-Fast-Content-SHA256"], "534275e0a5da4d457614f2f130e8db3dfe3a21cef90e3f5e13ae77c3d60ed12f", "SHA-256 body");
    assertEqual(headers["X-Fast-Signature"], "6728c67bb7eded95a6b13b56c3bcb13378c521fe55b0f23b97a3581918adfa54", "Chu ky request");
    const response = "{\"code\":\"SUCCESS\"}";
    const ok = verifyResponse(keys, {
        status: 200, requestNonce: "9b2e4c1f0a7d4e3b", body: response,
        headers: {
            "x-request-id": "8c2f0000-0000-4000-8000-000000000002", "x-fast-timestamp": "1792574401", "x-fast-content-sha256": sha256Hex(response),
            "x-fast-signature": "1972c2ae0b53f37dd745becadfd19669a8da4d968fb282891ed43fddec17fd68"
        }
    });
    assertEqual(ok, true, "Chu ky response");
    console.log("Vector thu: dat");
}

async function send(baseUrl, keys, method, path, body, extraHeaders = {}) {
    const { serverTime } = await (await fetch(baseUrl + "/api/v3/_meta/server")).json();
    const nonce = randomBytes(16).toString("hex");
    const headers = { ...signRequest(keys, { method, url: path, body, timestamp: serverTime, nonce }), "Content-Type": "application/json", ...extraHeaders };
    const response = await fetch(baseUrl + path, { method, headers, body });
    const bytes = Buffer.from(await response.arrayBuffer());
    if (!verifyResponse(keys, { status: response.status, headers: Object.fromEntries(response.headers), requestNonce: nonce, body: bytes })) {
        throw new Error("Chu ky response sai: khong tin noi dung nay");
    }
    return JSON.parse(bytes.toString("utf8"));
}

testVector();
const [baseUrl, user, privateKey] = process.argv.slice(2);
if (privateKey) {
    const keys = deriveKeys(privateKey);
    const { accessToken } = await send(baseUrl.replace(/\/$/, ""), keys, "POST", "/api/v3/auth/token", JSON.stringify({ userName: user }), { "X-Fast-User": user });
    console.log(await send(baseUrl.replace(/\/$/, ""), keys, "POST", "/api/v3/_meta/ping", "", { Authorization: "Bearer " + accessToken }));
}

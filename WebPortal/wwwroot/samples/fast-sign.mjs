// Fast API - ký request, kiểm chữ ký response (Node.js 18+, chỉ dùng node:crypto và fetch)
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

const md5Hex = data => createHash("md5").update(data).digest("hex");
const hmacHex = (key, text) => createHmac("sha256", key).update(text, "utf8").digest("hex");
const same = (a, b) => a.length === b.length && timingSafeEqual(Buffer.from(a), Buffer.from(b));

// Chữ ký = HMAC-SHA256(K_request, md5Hex(body) + nonce); nonce = "<unix giây>-<ngẫu nhiên>"
export function signRequest(keys, { body, nonce, idempotencyKey = "" }) {
    const headers = {
        "X-Fast-Nonce": nonce,
        "X-Fast-Signature": hmacHex(keys.request, md5Hex(body) + nonce)
    };
    if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
    return headers;
}

// X-Fast-Signature = HMAC-SHA256(K_response, md5Hex(body) + nonce của request)
export function verifyResponse(keys, { headers, requestNonce, body }) {
    return same(hmacHex(keys.response, md5Hex(body) + requestNonce), (headers["x-fast-signature"] || "").trim().toLowerCase());
}

function assertEqual(actual, expected, name) {
    if (actual !== expected) throw new Error(`${name}: ${actual} != ${expected}`);
}

function testVector() {
    const keys = deriveKeys("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=");
    assertEqual(keys.request.toString("hex"), "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f", "K_request");
    assertEqual(keys.response.toString("hex"), "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd", "K_response");
    const nonce = "1792574400-9b2e4c1f0a7d4e3b";
    const body = "{\"master\":{\"YourID\":\"HD001\"},\"detail\":[{\"ItemID\":\"VT01\",\"Quantity\":2}]}";
    assertEqual(md5Hex(body), "8e0a0e9704e5282159769a91441af571", "MD5 body");
    assertEqual(signRequest(keys, { body, nonce })["X-Fast-Signature"], "9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac", "Chu ky request");
    const response = "{\"code\":\"SUCCESS\"}";
    const ok = verifyResponse(keys, { requestNonce: nonce, body: response, headers: { "x-fast-signature": "0736896e2b8b43d7215b2e4366f133f2a4d1f4ff472fc089f08365fc79bbd50f" } });
    assertEqual(ok, true, "Chu ky response");
    console.log("Vector thu: dat");
}

async function send(baseUrl, keys, method, path, body, extraHeaders = {}) {
    const { serverTime } = await (await fetch(baseUrl + "/api/_meta/server")).json();
    const nonce = `${serverTime}-${randomBytes(8).toString("hex")}`;
    const headers = { ...signRequest(keys, { body, nonce }), "Content-Type": "application/json", ...extraHeaders };
    const response = await fetch(baseUrl + path, { method, headers, body });
    const bytes = Buffer.from(await response.arrayBuffer());
    if (!verifyResponse(keys, { headers: Object.fromEntries(response.headers), requestNonce: nonce, body: bytes })) {
        throw new Error("Chu ky response sai: khong tin noi dung nay");
    }
    return JSON.parse(bytes.toString("utf8"));
}

testVector();
const [baseUrl, user, privateKey] = process.argv.slice(2);
if (privateKey) {
    const keys = deriveKeys(privateKey);
    const { accessToken } = await send(baseUrl.replace(/\/$/, ""), keys, "POST", "/api/auth/token", JSON.stringify({ userName: user }));
    console.log(await send(baseUrl.replace(/\/$/, ""), keys, "POST", "/api/_meta/ping", "", { Authorization: "Bearer " + accessToken }));
}

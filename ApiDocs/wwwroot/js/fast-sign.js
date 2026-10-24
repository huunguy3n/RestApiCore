// Ký và kiểm thông điệp Fast API bằng WebCrypto (KE_HOACH 4.2), cùng thuật toán với Security/MessageSignature.cs.
// Request: X-Fast-Signature = HMAC-SHA256(K_request, md5Hex(body) + nonce), nonce = "<unix giây>-<ngẫu nhiên>".
// Response: X-Fast-Signature = HMAC-SHA256(K_response, md5Hex(body) + nonce của request).
// Dùng được trong trình duyệt và Node 20+ (globalThis.crypto). private_key chỉ nằm trong bộ nhớ, không gửi đi đâu.


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

/** MD5 hex (WebCrypto không có MD5). data là chuỗi UTF-8 hoặc byte */
export function md5Hex(data) {
    const bytes = typeof data === "string" ? utf8.encode(data) : data;
    const K = Array.from({ length: 64 }, (_, i) => Math.floor(Math.abs(Math.sin(i + 1)) * 2 ** 32) >>> 0);
    const S = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];
    const padded = new Uint8Array(((bytes.length + 8) >> 6 << 6) + 64);
    padded.set(bytes);
    padded[bytes.length] = 0x80;
    const view = new DataView(padded.buffer);
    view.setUint32(padded.length - 8, (bytes.length * 8) >>> 0, true);
    view.setUint32(padded.length - 4, Math.floor(bytes.length / 2 ** 29), true);
    let [a0, b0, c0, d0] = [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476];
    for (let offset = 0; offset < padded.length; offset += 64) {
        let [a, b, c, d] = [a0, b0, c0, d0];
        for (let i = 0; i < 64; i++) {
            let f, g;
            if (i < 16) { f = (b & c) | (~b & d); g = i; }
            else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
            else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
            else { f = c ^ (b | ~d); g = (7 * i) % 16; }
            const shift = S[(i >> 4) * 4 + (i % 4)];
            f = (f + a + K[i] + view.getUint32(offset + g * 4, true)) >>> 0;
            a = d; d = c; c = b;
            b = (b + ((f << shift) | (f >>> (32 - shift)))) >>> 0;
        }
        a0 = (a0 + a) >>> 0; b0 = (b0 + b) >>> 0; c0 = (c0 + c) >>> 0; d0 = (d0 + d) >>> 0;
    }
    const out = new DataView(new ArrayBuffer(16));
    [a0, b0, c0, d0].forEach((word, i) => out.setUint32(i * 4, word, true));
    return toHex(out.buffer);
}

/** Nonce "<unix giây>-<16 ký tự hex ngẫu nhiên>"; giây lấy từ serverTime của /api/_meta/server */
export function newNonce(timestamp) {
    return `${timestamp}-${toHex(globalThis.crypto.getRandomValues(new Uint8Array(8)))}`;
}

/**
 * Ký một request: body là chuỗi gửi đi nguyên văn ("" nếu không có), timestamp là giây của server.
 * Trả các header cần gửi kèm message đã ký để hiện khi debug
 */
export async function signRequest(keys, { body = "", timestamp, nonce = newNonce(timestamp), idempotencyKey = "" }) {
    const message = md5Hex(body) + nonce;
    const headers = {
        "X-Fast-Nonce": nonce,
        "X-Fast-Signature": await hmacHex(keys.request, message)
    };
    if (idempotencyKey) {
        headers["Idempotency-Key"] = idempotencyKey;
    }
    return { headers, message, nonce };
}

/** Kiểm chữ ký response; bodyBytes là byte thô nhận được, requestNonce là nonce của request tương ứng */
export async function verifyResponse(keys, { requestNonce, signature, bodyBytes }) {
    const message = md5Hex(bodyBytes) + requestNonce;
    const expected = await hmacHex(keys.response, message);
    return { message, signatureOk: expected === (signature || "").trim().toLowerCase() };
}

/** Vector thử cố định, dùng chung với Tests/MessageSignatureTests.cs và code mẫu */
export const TEST_VECTOR = {
    privateKey: "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
    requestKeyHex: "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f",
    responseKeyHex: "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd",
    body: "{\"master\":{\"YourID\":\"HD001\"},\"detail\":[{\"ItemID\":\"VT01\",\"Quantity\":2}]}",
    bodyMd5: "8e0a0e9704e5282159769a91441af571",
    timestamp: 1792574400,
    nonce: "1792574400-9b2e4c1f0a7d4e3b",
    requestSignature: "9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac",
    responseBody: "{\"code\":\"SUCCESS\"}",
    responseBodyMd5: "12db205e69b01844ef11955d7ca2b740",
    responseSignature: "0736896e2b8b43d7215b2e4366f133f2a4d1f4ff472fc089f08365fc79bbd50f"
};

/** Chạy lại vector thử bằng chính các hàm trên; trả danh sách { name, ok } */
export async function selfTest() {
    const v = TEST_VECTOR;
    const keys = await deriveKeys(v.privateKey);
    const signed = await signRequest(keys, { body: v.body, timestamp: v.timestamp, nonce: v.nonce });
    const verified = await verifyResponse(keys, { requestNonce: v.nonce, signature: v.responseSignature, bodyBytes: utf8.encode(v.responseBody) });
    return [
        { name: "K_request", ok: toHex(keys.request) === v.requestKeyHex },
        { name: "K_response", ok: toHex(keys.response) === v.responseKeyHex },
        { name: "MD5 body", ok: md5Hex(v.body) === v.bodyMd5 },
        { name: "MD5 body rỗng", ok: md5Hex("") === "d41d8cd98f00b204e9800998ecf8427e" },
        { name: "Chữ ký request", ok: signed.headers["X-Fast-Signature"] === v.requestSignature },
        { name: "Chữ ký response", ok: md5Hex(v.responseBody) === v.responseBodyMd5 && verified.signatureOk }
    ];
}

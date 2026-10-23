# Fast API - ký request, kiểm chữ ký response (Python 3.8+, chỉ dùng thư viện chuẩn)
# Chạy: python fast_sign.py            -> kiểm vector thử
#       python fast_sign.py <base_url> <user> <private_key>   -> lấy token rồi gọi _meta/ping
import base64, hashlib, hmac, json, secrets, sys, time, urllib.request
from urllib.parse import quote, unquote_plus, urlsplit


def derive_keys(private_key_b64):
    key = base64.b64decode(private_key_b64)
    return (hmac.new(key, b"FAST-API-V3-REQUEST", hashlib.sha256).digest(),
            hmac.new(key, b"FAST-API-V3-RESPONSE", hashlib.sha256).digest())


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def rfc3986(value: str) -> str:
    return quote(value, safe="-._~")


def canonical_query(raw: str) -> str:
    raw = (raw or "").lstrip("?")
    pairs = []
    for part in filter(None, raw.split("&")):
        name, _, value = part.partition("=")
        pairs.append((rfc3986(unquote_plus(name)), rfc3986(unquote_plus(value))))
    return "&".join(f"{n}={v}" for n, v in sorted(pairs))


def canonical_request(method, path, query, timestamp, nonce, idempotency_key, content_sha256):
    return "\n".join(["FAST-HMAC-SHA256", method.upper(), path.lower(), query, str(timestamp), nonce, idempotency_key or "", content_sha256.lower()])


def canonical_response(status, request_id, request_nonce, timestamp, content_sha256):
    return "\n".join(["FAST-HMAC-SHA256-RESPONSE", str(status), request_id, request_nonce, str(timestamp), content_sha256.lower()])


def sign_request(k_request, method, url, body: bytes, timestamp, nonce, idempotency_key=""):
    parts = urlsplit(url)
    content_sha256 = sha256_hex(body)
    canonical = canonical_request(method, parts.path, canonical_query(parts.query), timestamp, nonce, idempotency_key, content_sha256)
    headers = {
        "X-Fast-Timestamp": str(timestamp),
        "X-Fast-Nonce": nonce,
        "X-Fast-Content-SHA256": content_sha256,
        "X-Fast-Signature": hmac.new(k_request, canonical.encode(), hashlib.sha256).hexdigest(),
    }
    if idempotency_key:
        headers["Idempotency-Key"] = idempotency_key
    return headers


def verify_response(k_response, status, headers, request_nonce, body: bytes) -> bool:
    content_sha256 = sha256_hex(body)
    canonical = canonical_response(status, headers.get("X-Request-ID", ""), request_nonce, headers.get("X-Fast-Timestamp", ""), content_sha256)
    expected = hmac.new(k_response, canonical.encode(), hashlib.sha256).hexdigest()
    return (hmac.compare_digest(content_sha256, headers.get("X-Fast-Content-SHA256", "").lower())
            and hmac.compare_digest(expected, headers.get("X-Fast-Signature", "").strip().lower()))


def test_vector():
    k_req, k_res = derive_keys("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")
    assert k_req.hex() == "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f"
    assert k_res.hex() == "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd"
    body = b'{"master":{"YourID":"HD001"},"detail":[{"ItemID":"VT01","Quantity":2}]}'
    headers = sign_request(k_req, "POST", "/api/sale-invoice?unitCode=CTY&dryRun=false", body,
                           1792574400, "9b2e4c1f0a7d4e3b", "6f1c2d3e-0000-4000-8000-000000000001")
    assert headers["X-Fast-Content-SHA256"] == "534275e0a5da4d457614f2f130e8db3dfe3a21cef90e3f5e13ae77c3d60ed12f"
    assert headers["X-Fast-Signature"] == "4588c91a82467e239600b66fd21e08e18f626037cc91ff4197aa9cf137dc81b2"
    response = b'{"code":"SUCCESS"}'
    assert verify_response(k_res, 200, {
        "X-Request-ID": "8c2f0000-0000-4000-8000-000000000002", "X-Fast-Timestamp": "1792574401",
        "X-Fast-Content-SHA256": sha256_hex(response),
        "X-Fast-Signature": "1972c2ae0b53f37dd745becadfd19669a8da4d968fb282891ed43fddec17fd68"}, "9b2e4c1f0a7d4e3b", response)
    print("Vector thu: dat")


def send(base_url, k_req, k_res, method, path, body: bytes, token=None, extra=None):
    server_time = json.load(urllib.request.urlopen(base_url + "/api/_meta/server"))["serverTime"]
    nonce = secrets.token_hex(16)
    headers = sign_request(k_req, method, path, body, server_time, nonce)
    headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = "Bearer " + token
    headers.update(extra or {})
    request = urllib.request.Request(base_url + path, data=body, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request)
    except urllib.error.HTTPError as error:
        response = error
    data = response.read()
    if not verify_response(k_res, response.status, response.headers, nonce, data):
        raise RuntimeError("Chu ky response sai: khong tin noi dung nay")
    return json.loads(data)


if __name__ == "__main__":
    test_vector()
    if len(sys.argv) == 4:
        base_url, user, private_key = sys.argv[1].rstrip("/"), sys.argv[2], sys.argv[3]
        k_req, k_res = derive_keys(private_key)
        token = send(base_url, k_req, k_res, "POST", "/api/auth/token", json.dumps({"userName": user}).encode(), extra={"X-Fast-User": user})["accessToken"]
        print(send(base_url, k_req, k_res, "POST", "/api/_meta/ping", b"", token))

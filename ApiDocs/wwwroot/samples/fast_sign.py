# Fast API - ký request, kiểm chữ ký response (Python 3.8+, chỉ dùng thư viện chuẩn)
# Chạy: python fast_sign.py            -> kiểm vector thử
#       python fast_sign.py <base_url> <user> <private_key>   -> lấy token rồi gọi _meta/ping
import base64, hashlib, hmac, json, secrets, sys, urllib.request


def derive_keys(private_key_b64):
    key = base64.b64decode(private_key_b64)
    return (hmac.new(key, b"FAST-API-V3-REQUEST", hashlib.sha256).digest(),
            hmac.new(key, b"FAST-API-V3-RESPONSE", hashlib.sha256).digest())


def md5_hex(data: bytes) -> str:
    return hashlib.md5(data).hexdigest()


def sign_request(k_request, body: bytes, nonce, idempotency_key=""):
    """X-Fast-Signature = HMAC-SHA256(K_request, md5Hex(body) + nonce); nonce = "<unix giay>-<ngau nhien>" """
    headers = {
        "X-Fast-Nonce": nonce,
        "X-Fast-Signature": hmac.new(k_request, (md5_hex(body) + nonce).encode(), hashlib.sha256).hexdigest(),
    }
    if idempotency_key:
        headers["Idempotency-Key"] = idempotency_key
    return headers


def verify_response(k_response, headers, request_nonce, body: bytes) -> bool:
    """X-Fast-Signature = HMAC-SHA256(K_response, md5Hex(body) + nonce cua request)"""
    expected = hmac.new(k_response, (md5_hex(body) + request_nonce).encode(), hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, headers.get("X-Fast-Signature", "").strip().lower())


def test_vector():
    k_req, k_res = derive_keys("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=")
    assert k_req.hex() == "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f"
    assert k_res.hex() == "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd"
    body = b'{"master":{"YourID":"HD001"},"detail":[{"ItemID":"VT01","Quantity":2}]}'
    nonce = "1792574400-9b2e4c1f0a7d4e3b"
    assert md5_hex(body) == "8e0a0e9704e5282159769a91441af571"
    assert sign_request(k_req, body, nonce)["X-Fast-Signature"] == "9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac"
    response = b'{"code":"SUCCESS"}'
    assert verify_response(k_res, {"X-Fast-Signature": "0736896e2b8b43d7215b2e4366f133f2a4d1f4ff472fc089f08365fc79bbd50f"}, nonce, response)
    print("Vector thu: dat")


def send(base_url, k_req, k_res, method, path, body: bytes, token=None):
    server_time = json.load(urllib.request.urlopen(base_url + "/api/_meta/server"))["serverTime"]
    nonce = f"{server_time}-{secrets.token_hex(8)}"
    headers = sign_request(k_req, body, nonce)
    headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(base_url + path, data=body, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request)
    except urllib.error.HTTPError as error:
        response = error
    data = response.read()
    if not verify_response(k_res, response.headers, nonce, data):
        raise RuntimeError("Chu ky response sai: khong tin noi dung nay")
    return json.loads(data)


if __name__ == "__main__":
    test_vector()
    if len(sys.argv) == 4:
        base_url, user, private_key = sys.argv[1].rstrip("/"), sys.argv[2], sys.argv[3]
        k_req, k_res = derive_keys(private_key)
        token = send(base_url, k_req, k_res, "POST", "/api/auth/token", json.dumps({"userName": user}).encode())["accessToken"]
        print(send(base_url, k_req, k_res, "POST", "/api/_meta/ping", b"", token))

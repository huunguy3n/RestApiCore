#!/usr/bin/env bash
# Fast API - ký request bằng cURL + OpenSSL (bash, openssl 1.1+, xxd)
# Chạy: ./fast-sign.sh                                   -> kiểm vector thử
#       ./fast-sign.sh <base_url> <user> <private_key>   -> lấy token rồi gọi _meta/ping
# Script không kiểm chữ ký response (xem hàm verify_response); dùng để thử nhanh, code thật nên theo mẫu ngôn ngữ khác.
# Query phải đã ở dạng chuẩn (tên, giá trị encode RFC 3986, sắp theo tên) vì script không tự sắp.
set -euo pipefail

# K = HMAC-SHA256(private_key, label), in hex
derive_key() { printf '%s' "$2" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$(printf '%s' "$1" | base64 -d | xxd -p -c 256)" | awk '{print $NF}'; }
sha256_hex() { openssl dgst -sha256 | awk '{print $NF}'; }
hmac_hex() { printf '%s' "$2" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$1" | awk '{print $NF}'; }

# canonical_request METHOD PATH CANONICAL_QUERY TIMESTAMP NONCE IDEMPOTENCY_KEY CONTENT_SHA256
canonical_request() {
  printf 'FAST-HMAC-SHA256\n%s\n%s\n%s\n%s\n%s\n%s\n%s' "${1^^}" "${2,,}" "$3" "$4" "$5" "$6" "$7"
}

# verify_response K_RESPONSE STATUS REQUEST_ID REQUEST_NONCE TIMESTAMP SIGNATURE BODY_FILE -> in "ok" hoặc "SAI"
verify_response() {
  local sha canonical
  sha=$(sha256_hex < "$7")
  canonical=$(printf 'FAST-HMAC-SHA256-RESPONSE\n%s\n%s\n%s\n%s\n%s' "$2" "$3" "$4" "$5" "$sha")
  [ "$(hmac_hex "$1" "$canonical")" = "${6,,}" ] && echo ok || echo SAI
}

test_vector() {
  local k_req k_res body sha signature tmp
  k_req=$(derive_key "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=" "FAST-API-V3-REQUEST")
  k_res=$(derive_key "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=" "FAST-API-V3-RESPONSE")
  [ "$k_req" = "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f" ] || { echo "Sai K_request"; exit 1; }
  [ "$k_res" = "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd" ] || { echo "Sai K_response"; exit 1; }
  body='{"master":{"YourID":"HD001"},"detail":[{"ItemID":"VT01","Quantity":2}]}'
  sha=$(printf '%s' "$body" | sha256_hex)
  [ "$sha" = "534275e0a5da4d457614f2f130e8db3dfe3a21cef90e3f5e13ae77c3d60ed12f" ] || { echo "Sai SHA-256 body"; exit 1; }
  signature=$(hmac_hex "$k_req" "$(canonical_request POST /api/sale-invoice 'dryRun=false&unitCode=CTY' 1792574400 9b2e4c1f0a7d4e3b 6f1c2d3e-0000-4000-8000-000000000001 "$sha")")
  [ "$signature" = "4588c91a82467e239600b66fd21e08e18f626037cc91ff4197aa9cf137dc81b2" ] || { echo "Sai chu ky request"; exit 1; }
  tmp=$(mktemp); printf '%s' '{"code":"SUCCESS"}' > "$tmp"
  [ "$(verify_response "$k_res" 200 8c2f0000-0000-4000-8000-000000000002 9b2e4c1f0a7d4e3b 1792574401 1972c2ae0b53f37dd745becadfd19669a8da4d968fb282891ed43fddec17fd68 "$tmp")" = ok ] || { echo "Sai chu ky response"; exit 1; }
  rm -f "$tmp"
  echo "Vector thu: dat"
}

# send BASE_URL K_REQ METHOD PATH QUERY BODY [header...]
send() {
  local base=$1 k_req=$2 method=$3 path=$4 query=$5 body=$6; shift 6
  local ts nonce sha signature url
  ts=$(curl -s "$base/api/_meta/server" | sed -E 's/.*"serverTime":([0-9]+).*/\1/')
  nonce=$(openssl rand -hex 16)
  sha=$(printf '%s' "$body" | sha256_hex)
  signature=$(hmac_hex "$k_req" "$(canonical_request "$method" "$path" "$query" "$ts" "$nonce" "" "$sha")")
  url="$base$path${query:+?$query}"
  curl -s -X "$method" "$url" -H "Content-Type: application/json" \
    -H "X-Fast-Timestamp: $ts" -H "X-Fast-Nonce: $nonce" -H "X-Fast-Content-SHA256: $sha" -H "X-Fast-Signature: $signature" \
    "$@" --data-binary "$body"
}

test_vector
if [ $# -eq 3 ]; then
  base=${1%/}; user=$2
  k_req=$(derive_key "$3" "FAST-API-V3-REQUEST")
  token=$(send "$base" "$k_req" POST /api/auth/token "" "{\"userName\":\"$user\"}" -H "X-Fast-User: $user" | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
  send "$base" "$k_req" POST /api/_meta/ping "" "" -H "Authorization: Bearer $token"
  echo
fi

#!/usr/bin/env bash
# Fast API - ký request bằng cURL + OpenSSL (bash, openssl 1.1+, xxd)
# Chạy: ./fast-sign.sh                                   -> kiểm vector thử
#       ./fast-sign.sh <base_url> <user> <private_key>   -> lấy token rồi gọi _meta/ping
# Script không kiểm chữ ký response (xem hàm verify_response); dùng để thử nhanh, code thật nên theo mẫu ngôn ngữ khác.
# Chữ ký = HMAC-SHA256(K_request, md5(body) + nonce); nonce = "<unix giây>-<ngẫu nhiên>".
set -euo pipefail

# K = HMAC-SHA256(private_key, label), in hex
derive_key() { printf '%s' "$2" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$(printf '%s' "$1" | base64 -d | xxd -p -c 256)" | awk '{print $NF}'; }
sha256_hex() { openssl dgst -sha256 | awk '{print $NF}'; }
md5_hex() { openssl dgst -md5 | awk '{print $NF}'; }
hmac_hex() { printf '%s' "$2" | openssl dgst -sha256 -mac HMAC -macopt "hexkey:$1" | awk '{print $NF}'; }

# verify_response K_RESPONSE STATUS REQUEST_ID REQUEST_NONCE TIMESTAMP SIGNATURE BODY_FILE -> in "ok" hoặc "SAI"
verify_response() {
  local sha canonical
  sha=$(sha256_hex < "$7")
  canonical=$(printf 'FAST-HMAC-SHA256-RESPONSE\n%s\n%s\n%s\n%s\n%s' "$2" "$3" "$4" "$5" "$sha")
  [ "$(hmac_hex "$1" "$canonical")" = "${6,,}" ] && echo ok || echo SAI
}

test_vector() {
  local k_req k_res body md5 signature tmp
  k_req=$(derive_key "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=" "FAST-API-V3-REQUEST")
  k_res=$(derive_key "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=" "FAST-API-V3-RESPONSE")
  [ "$k_req" = "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f" ] || { echo "Sai K_request"; exit 1; }
  [ "$k_res" = "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd" ] || { echo "Sai K_response"; exit 1; }
  body='{"master":{"YourID":"HD001"},"detail":[{"ItemID":"VT01","Quantity":2}]}'
  md5=$(printf '%s' "$body" | md5_hex)
  [ "$md5" = "8e0a0e9704e5282159769a91441af571" ] || { echo "Sai MD5 body"; exit 1; }
  signature=$(hmac_hex "$k_req" "${md5}1792574400-9b2e4c1f0a7d4e3b")
  [ "$signature" = "9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac" ] || { echo "Sai chu ky request"; exit 1; }
  tmp=$(mktemp); printf '%s' '{"code":"SUCCESS"}' > "$tmp"
  [ "$(verify_response "$k_res" 200 8c2f0000-0000-4000-8000-000000000002 1792574400-9b2e4c1f0a7d4e3b 1792574401 ad5353907e543ef9429cfe159bdaf1ded0ae2c37a27208110bbc2a47ef902bac "$tmp")" = ok ] || { echo "Sai chu ky response"; exit 1; }
  rm -f "$tmp"
  echo "Vector thu: dat"
}

# send BASE_URL K_REQ METHOD PATH BODY [header...]
send() {
  local base=$1 k_req=$2 method=$3 path=$4 body=$5; shift 5
  local ts nonce signature
  ts=$(curl -s "$base/api/_meta/server" | sed -E 's/.*"serverTime":([0-9]+).*/\1/')
  nonce="$ts-$(openssl rand -hex 8)"
  signature=$(hmac_hex "$k_req" "$(printf '%s' "$body" | md5_hex)$nonce")
  curl -s -X "$method" "$base$path" -H "Content-Type: application/json" \
    -H "X-Fast-Nonce: $nonce" -H "X-Fast-Signature: $signature" \
    "$@" --data-binary "$body"
}

test_vector
if [ $# -eq 3 ]; then
  base=${1%/}; user=$2
  k_req=$(derive_key "$3" "FAST-API-V3-REQUEST")
  token=$(send "$base" "$k_req" POST /api/auth/token "{\"userName\":\"$user\"}" | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
  send "$base" "$k_req" POST /api/_meta/ping "" -H "Authorization: Bearer $token"
  echo
fi

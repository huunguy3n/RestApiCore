# Hướng dẫn sử dụng FastAPI v3

Tutorial thực hành: dựng môi trường, cấp tài khoản đối tác, ký request, gọi nghiệp vụ, dùng portal, và thêm form XML.

Đặc tả đầy đủ: [KE_HOACH.md](KE_HOACH.md). Tóm tắt repo: [../README.md](../README.md).

---

## 1. Kiến trúc ngắn

```
Đối tác                    FastAPI v3                      FBO / SQL
─────────                  ──────────                      ─────────
1. Ký HMAC + lấy JWT  →   /api/auth/token
2. JWT + ký HMAC      →   /api/{resource}[/{verb}]
                          → kiểm JWT, nonce, chữ ký
                          → quyền form / IP / đơn vị
                          → nạp XML <import>
                          → checking + processing SQL  →  bảng / proc nghiệp vụ
3. Kiểm chữ ký response ←  JSON + header X-Fast-*
```

- **Nguồn dữ liệu:** SQL Server (DB app FBO).
- **Định nghĩa API:** file XML trong `Api/App_Data/Form/`.
- **Bí mật đối tác:** `private_key` (32 byte, base64) — chỉ dùng ký HMAC, không dùng đăng nhập portal.
- **Mật khẩu portal:** `restApiUser.password` (AES trên server); trình duyệt mã hóa RSA-OAEP trước khi gửi.

---

## 2. Cài đặt máy phát triển

### 2.1. Clone và khôi phục

```powershell
cd "D:\Fast Source\Development\RestApiCore"
dotnet restore
dotnet build
```

### 2.2. `appsettings.Local.json`

```powershell
copy Api\appsettings.Local.example.json Api\appsettings.Local.json
```

| Khóa | Ý nghĩa |
|---|---|
| `appSettings:secretKey` | Ký JWT (≥ 32 ký tự). **Khác** `passwordKey` |
| `appSettings:passwordKey` | AES mật khẩu user — **đúng 32 ký tự** |
| `connectionStrings:appConnectionString` | Chuỗi kết nối DB app |
| `Security:KeyEncryptionKey` | Base64 32 byte — mã hóa `private_key` trong DB |
| `Security:PortalKeyPath` | (Tuỳ chọn) PFX khóa RSA portal. Không khai → khóa tạm (đổi mỗi lần restart) |
| `appSettings:portalOrigins` | Origin WebPortal được CORS, vd `["http://localhost:5225"]` |

Ví dụ đầy đủ hơn:

```json
{
  "appSettings": {
    "secretKey": "thay-bang-chuoi-ngau-nhien-dai-hon-32",
    "passwordKey": "0123456789abcdef0123456789abcdef",
    "issuerToken": "FastRestApi",
    "maxRequestsPerMinute": "120",
    "isDevState": true,
    "commandTimeout": "300",
    "portalOrigins": [ "http://localhost:5225" ]
  },
  "connectionStrings": {
    "appConnectionString": "Data Source=.;Initial Catalog=FSD_Dev;Integrated Security=True;TrustServerCertificate=True;Application Name=restApi;"
  },
  "Security": {
    "KeyEncryptionKey": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
  }
}
```

> Trên production: tắt `isDevState`, dùng HTTPS, ACL chặt cho file Local, không dùng khóa ephemeral cho portal.

### 2.3. Script SQL

Trên DB app, chạy theo thứ tự:

| File | Việc |
|---|---|
| `sql/001_restApi_CheckUnitRights.sql` | Kiểm quyền đơn vị |
| `sql/002_restApiUser_private_key.sql` | Cột `private_key` |
| `sql/003_restApiRevokedToken.sql` | JWT đã thu hồi |
| `sql/004_restApiIdempotency.sql` | Chống ghi trùng |
| `sql/005_restApi_GetUserRights.sql` | Một lượt lấy user + quyền + khóa |
| `sql/006` … `008` | Log mới (khi đã triển khai đầy đủ pha log) |

Giả định đã có bảng nền `restApiUser`, `restApiAllowForm`, `restApiAllowIp`, … (từ bản v3/dev). Chỉ đụng object `restApi*`.

### 2.4. Chạy

```powershell
dotnet run --project Api --launch-profile http
# http://localhost:5143

dotnet run --project WebPortal --launch-profile http
# http://localhost:5225
```

Kiểm:

```powershell
curl http://localhost:5143/health/ready
curl http://localhost:5143/api/_meta/server
```

---

## 3. Chuẩn bị tài khoản

`restApiUser` **không có cờ admin riêng**. Quyền quản trị đơn vị lấy từ FBO: `name_erp` → `userinfo2` / `vsysuserinfo` có `admin = 1`. Quyền form vẫn phải khai trong `restApiAllowForm` (không có dòng = không gọi được form).

| Khái niệm | Ý nghĩa |
|---|---|
| `restApiUser.name` | Tên đăng nhập API / portal (khóa chính) |
| `restApiUser.password` | Mật khẩu **portal** (AES `passwordKey`) — không dùng ký API |
| `restApiUser.private_key` | Khóa HMAC ký request/response API |
| `restApiUser.name_erp` | Phải khớp `userinfo2.name` (NSD FBO còn `status = '1'`) |

### 3.1. Khởi tạo user quản trị (bảng đang rỗng)

1. Trên FBO phải có sẵn NSD admin (thường `userinfo2.name = 'ADMIN'`, `admin = 1`, `status = '1'`).
2. Tạo tài khoản API nối sang NSD đó (mật khẩu đặt qua biến môi trường, không ghi trên dòng lệnh):

```powershell
# Xem trước (không ghi)
$env:RESTAPI_PASSWORD = 'DoiMatKhauNgay'
dotnet run --project Cli -- create-user `
  --config Api/appsettings.Local.json `
  --user APIADMIN `
  --name-erp ADMIN `
  --password-env RESTAPI_PASSWORD `
  --forms getCustomer,setSaleInvoice,setTemplate `
  --issue-key

# Ghi thật
dotnet run --project Cli -- create-user `
  --config Api/appsettings.Local.json `
  --user APIADMIN `
  --name-erp ADMIN `
  --password-env RESTAPI_PASSWORD `
  --forms getCustomer,setSaleInvoice,setTemplate `
  --issue-key `
  --apply

Remove-Item Env:RESTAPI_PASSWORD
```

- Portal: đăng nhập `APIADMIN` + mật khẩu vừa đặt.
- API: dùng `private_key` in ra **một lần** khi có `--issue-key`.
- `--forms`: tên file XML không đuôi. Bổ sung form sau bằng SQL `INSERT restApiAllowForm`.

### 3.2. Cấp user / mật khẩu cho đối tác thường

NSD FBO thường (`admin = 0`) phải tồn tại trước; quyền đơn vị theo `sysunitrights` của NSD đó.

```powershell
$env:RESTAPI_PASSWORD = 'MatKhauDoiTac'
dotnet run --project Cli -- create-user `
  --config Api/appsettings.Local.json `
  --user DOITAC01 `
  --name-erp NVKD01 `
  --password-env RESTAPI_PASSWORD `
  --forms getCustomer,setSaleInvoice `
  --issue-key `
  --apply
Remove-Item Env:RESTAPI_PASSWORD
```

Giao cho đối tác:

1. `name` + mật khẩu portal (vào WebPortal).
2. `private_key` (base64) — kênh an toàn; dùng ký HMAC / lấy JWT API.

Đổi mật khẩu portal sau này:

```powershell
$env:RESTAPI_PASSWORD = 'MatKhauMoi'
dotnet run --project Cli -- set-password --config Api/appsettings.Local.json --user DOITAC01 --password-env RESTAPI_PASSWORD --apply
Remove-Item Env:RESTAPI_PASSWORD
```

Chỉ cấp / xoay `private_key` (đã có user):

```powershell
dotnet run --project Cli -- issue-key --config Api/appsettings.Local.json --user DOITAC01 --apply
# đã có khóa: thêm --rotate
```

(Tuỳ chọn) `restApiAllowIp` — không khai thì cho mọi IP; có khai thì chỉ các IP đó.

### 3.3. Mã hóa lại mật khẩu (khi tách `passwordKey` khỏi `secretKey` cũ)

```powershell
dotnet run --project Cli -- rekey-passwords --config Api/appsettings.Local.json
dotnet run --project Cli -- rekey-passwords --config Api/appsettings.Local.json --apply
```

---

## 4. Bảo mật thông điệp (đối tác bắt buộc)

Mọi request nghiệp vụ và `/api/auth/token`, `/api/_meta/ping`, … (trừ `GET /api/_meta/server` và health) cần:

| Header | Nội dung |
|---|---|
| `Authorization` | `Bearer <jwt>` (không cần khi lấy token) |
| `Content-Type` | `application/json` |
| `X-Fast-Nonce` | `<unix giây>-<8–48 ký tự A-Za-z0-9_- >` |
| `X-Fast-Signature` | hex chữ thường HMAC-SHA256 |
| `Idempotency-Key` | (Tuỳ chọn) chống ghi trùng |

### 4.1. Dẫn xuất khóa

```
K_request  = HMAC-SHA256(private_key, "FAST-API-V3-REQUEST")
K_response = HMAC-SHA256(private_key, "FAST-API-V3-RESPONSE")
```

### 4.2. Chữ ký request

```
message   = md5Hex(rawBody) + nonce     // body rỗng → md5 = d41d8cd98f00b204e9800998ecf8427e
signature = hex(HMAC-SHA256(K_request, UTF8(message)))
```

Đồng bộ giờ: gọi `GET /api/_meta/server` lấy `serverTime`, dùng làm phần giây của nonce (lệch tối đa `clockSkewSeconds`, mặc định 300).

### 4.3. Kiểm chữ ký response

Header response: `X-Request-ID`, `X-Fast-Signature` (= HMAC-SHA256(K_response, md5Hex(body response) + nonce của request)).

```
canonical =
  "FAST-HMAC-SHA256-RESPONSE" + "\n" +
  STATUS + "\n" + REQUEST_ID + "\n" + REQUEST_NONCE + "\n" +
  TIMESTAMP + "\n" + sha256Hex(responseBody)

signature = hex(HMAC-SHA256(K_response, UTF8(canonical)))
```

Response **không có** chữ ký (thường lỗi JWT/user sớm) → không tin nội dung.

### 4.4. Vector thử cố định

Dùng để kiểm client tự viết (cùng giá trị trong `Tests/MessageSignatureTests.cs` và `WebPortal/wwwroot/samples/`):

| Thành phần | Giá trị |
|---|---|
| `private_key` (base64) | `AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=` |
| Body | `{"master":{"YourID":"HD001"},"detail":[{"ItemID":"VT01","Quantity":2}]}` |
| Nonce | `1792574400-9b2e4c1f0a7d4e3b` |
| `X-Fast-Signature` (request) | `9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac` |

Chạy mẫu C# (file top-level statements; tạo project tạm hoặc chạy theo hướng dẫn trong comment đầu file):

```powershell
# Vector thử (không cần server):
#   mở WebPortal/wwwroot/samples/FastSign.cs — hàm TestVector() chạy khi start
# Gọi thật:
#   dotnet run -- <base_url> <user> <private_key_base64>
```

Có sẵn Python / Java / Node / shell trong cùng thư mục `samples/`.

---

## 5. Luồng gọi API (từng bước)

Giả sử: base `http://localhost:5143`, user `DOITAC01`, đã có `private_key`.

### Bước A — Đồng bộ giờ

```http
GET /api/_meta/server
```

Lấy `serverTime` (Unix giây).

### Bước B — Lấy JWT

```http
POST /api/auth/token
Content-Type: application/json
X-Fast-Nonce: <serverTime>-<ngẫu nhiên>
X-Fast-Signature: <hmac>

{"userName":"DOITAC01"}
```

Không gửi `Authorization`. Server tìm `private_key` theo `userName` để kiểm chữ ký.

Response (đã ký):

```json
{
  "code": "SUCCESS",
  "success": true,
  "accessToken": "<jwt>",
  "tokenType": "Bearer",
  "expiresIn": 3600
}
```

### Bước C — Gọi nghiệp vụ

**Lấy danh mục khách** (`getCustomer.xml`):

```http
POST /api/customer/get
Authorization: Bearer <jwt>
Content-Type: application/json
X-Fast-Nonce: …
X-Fast-Signature: …

{"keyword":"huy","customerId":""}
```

**Ghi hóa đơn** (`setSaleInvoice.xml` — cần đơn vị):

```http
POST /api/sale-invoice?unitCode=CTY
Authorization: Bearer <jwt>
Content-Type: application/json
Idempotency-Key: 6f1c-unique-per-document
X-Fast-Nonce: …
X-Fast-Signature: …

{
  "master": {
    "yourId": "HD001",
    "voucherDate": "21/10/2026",
    "customerId": "KH001",
    "buyer": "Nguyen Van A",
    "note": "Giao hàng"
  },
  "detail": [
    { "itemId": "VT01", "quantity": 2, "price": 100000 },
    { "itemId": "VT02", "quantity": 1, "price": 50000 }
  ]
}
```

Kiểm tra không ghi (`dryRun`):

```http
POST /api/sale-invoice?unitCode=CTY&dryRun=true
```

### Bước D — Thử ping

```http
POST /api/_meta/ping
Authorization: Bearer <jwt>
…
```

### Bước E — Thu hồi token

```http
POST /api/auth/revoke
Authorization: Bearer <jwt>
X-Fast-Nonce: …
X-Fast-Signature: …
```

Body có thể rỗng (vẫn ký với md5 của body rỗng).

---

## 6. Quy ước route và body

### 6.1. Tên file → URL

| File | Route |
|---|---|
| `setSaleInvoice.xml` | `POST /api/sale-invoice` |
| `getCustomer.xml` | `POST /api/customer/get` |
| `cancelInvoice.xml` | `POST /api/invoice/cancel` |
| `approveInvoice.xml` | `POST /api/invoice/approve` |

- Động từ = tiền tố chữ thường đầu tên; resource = phần còn lại PascalCase → kebab-case.
- Nghiệp vụ **chỉ POST**. `GET` vào route form → `405`.

### 6.2. Body JSON

- Khối gốc là **object**; khối con là **mảng**.
- Form nhiều khối: `{ "master": { … }, "detail": [ … ] }`.
- Form một khối (vd `get`): nhận dạng phẳng `{ "keyword": "…" }` hoặc `{ "filter": { … } }`.
- Field không khai trong XML → **bỏ qua**, không lỗi.
- Nhiều chứng từ: mảng các object trên (có trần `maxDocumentsPerRequest`).

### 6.3. Tham số điều khiển

| Tham số | Vị trí | Khi nào |
|---|---|---|
| `unitCode` | query | Form `unitRequired="true"` |
| `dryRun` | query | Chạy checking, không processing |
| `cursor` / `page` | query | Phân trang (form có `<paging>`) |
| `Idempotency-Key` | header | `set` / ghi — cùng key + cùng body → trả kết quả cũ; cùng key khác body → `409` |

### 6.4. Response chuẩn

```json
{
  "code": "SUCCESS",
  "success": true,
  "message": "",
  "requestId": "…",
  "data": { "customers": [ … ] }
}
```

Dataset từ SQL: bảng 0 có `code`, `message`, tuỳ chọn `tables` (vd `master,detail`); các bảng sau là dữ liệu.

Một số mã lỗi thường gặp: `INVALID_SIGNATURE`, `TIMESTAMP_EXPIRED`, `NONCE_REUSED`, `FORBIDDEN`, `UNIT_REQUIRED`, `UNIT_DENIED`, `INVALID_SHAPE`, `IDEMPOTENCY_CONFLICT`, `TOO_MANY_REQUESTS`. Đầy đủ: `GET /api/_meta/errors` hoặc `App_Data/Options/Message.xml`.

---

## 7. Portal đối tác (WebPortal)

1. Mở WebPortal (vd `http://localhost:5225`), cấu hình base URL API nếu trang yêu cầu.
2. Đăng nhập bằng `restApiUser.name` + mật khẩu: trình duyệt lấy `portalKey` từ `/api/_meta/server`, mã hóa RSA-OAEP, gọi `POST /api/auth/portal`.
3. JWT `scope=portal` **chỉ** đọc `/api/_meta/*` — không gọi nghiệp vụ.
4. Xem danh sách tài nguyên, chi tiết field, mẫu JSON, bảng lỗi.
5. **Gọi thử:** dán `private_key` vào ô (chỉ giữ trong bộ nhớ trang) → lấy JWT API → ký HMAC → gửi `dryRun` hoặc gửi thật.

Code mẫu tải từ trang portal hoặc thư mục `WebPortal/wwwroot/samples/`.

---

## 8. Viết / nâng form XML

### 8.1. Cấu trúc tối thiểu

```xml
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE import [
  <!ENTITY And "&#38;#13;">
]>
<import xmlns="urn:schemas-fast-com:fast-api"
        type="List" jsonNaming="camelCase" columnNaming="snake_case"
        description="…">

  <fields name="filter">
    <field name="keyword" fastName="tu_khoa" maxLength="64"/>
  </fields>

  <query action="processing">
    <text><![CDATA[
SELECT 'SUCCESS' AS code, '' AS message, 'items' AS tables
SELECT …
    ]]></text>
  </query>
</import>
```

Gợi ý:

- JSON phía đối tác: `jsonNaming="camelCase"`; cột tạm SQL: `columnNaming="snake_case"`; alias cột trả về nên camelCase để khớp JSON.
- `field@fastName` = tên cột trong `#filter` / `#master` / …
- Voucher: `type="Voucher" voucherCode="HDA" unitRequired="true"`, có `checking` + `processing`.
- Kiểm tồn tại mã: `check` + `errorCode` (kiểu Upload FBO). Không dùng `<reference>` / `Update.xml` / `@@update`.

Mẫu tham khảo: `getCustomer.xml`, `setSaleInvoice.xml`, `setTemplate.xml`.

### 8.2. Nâng form v2 → v3

```powershell
dotnet run --project Cli -- upgrade "D:\path\to\App_Data\FastAPI" --config "Api\appsettings.Local.json" --out "Api\App_Data\Form"
```

- Đọc form FastAPI 2.4.7 (`<import>` phẳng, entity `Include\*.txt`), xuất `<import>` v3, giữ tên file.
- Tên field đổi sang camelCase; `justContent` thành `check`; field `cacheID` bị bỏ.
- `--config` (chỉ đọc DB) để tách `master`/`detail` của chứng từ theo cột thật của `m??$000000` / `d??$000000`. Không có thì mọi field vào `master`.
- **Processing không tự chuyển được**: v2 gọi proc `api_*` đọc `#_data`. Query v2 được giữ làm chú thích, processing trả `ERROR` tới khi bạn viết lại.
- In ghi chú cần xem lại. Không có `--out` → chỉ in ra màn hình.

Sau khi copy XML mới: restart Api (form nạp lúc khởi động). Cấp `restApiAllowForm` cho tên form mới.

---

## 9. Vận hành

| Việc | Cách |
|---|---|
| Health | `GET /health/live` (tiến trình), `GET /health/ready` (SQL + thư mục log) |
| Log file | Mặc định `%ProgramData%\FastRestApi\Logs\api-*.log`; gắn `X-Request-ID` |
| Rate limit | Theo user đã xác thực (hoặc IP trước khi có JWT); vượt → `429 TOO_MANY_REQUESTS` |
| Đổi `private_key` | `issue-key --rotate --apply` + đối tác cập nhật |
| Thu hồi phiên | Client gọi `/api/auth/revoke`; hoặc đợi JWT hết hạn |

Production checklist ngắn:

1. HTTPS, Hosting Bundle .NET 10, App Pool No Managed Code.
2. Bí mật ngoài Git; PFX portal hoặc Certificate Store.
3. SQL script đã áp; quyền DDL không cần trên runtime (chỉ lúc cài).
4. `isDevState=false`; `portalOrigins` đúng origin thật.
5. Backup `appsettings.Local.json` và khóa PFX riêng, ACL hẹp.

---

## 10. Gỡ lỗi nhanh

| Triệu chứng | Hướng xử lý |
|---|---|
| Api không start: thiếu `secretKey` / connection / `passwordKey` | Kiểm `appsettings.Local.json` |
| `401 INVALID_SIGNATURE` | Sai `private_key`, body đã đổi sau khi ký, hoặc hex hoa/thường lệch quy ước |
| `401 TIMESTAMP_EXPIRED` / `NONCE_REUSED` | Đồng bộ `serverTime`; mỗi request một nonce mới |
| `403 FORBIDDEN` | Có khai `restApiAllowForm` nhưng không có form này (admin FBO hoặc không khai thì được mọi form) |
| `400 UNIT_REQUIRED` / `UNIT_DENIED` | Thêm `?unitCode=`; kiểm `sysunitrights` / admin FBO |
| `400 INVALID_SHAPE` | Sai cấu trúc JSON (thiếu khối bắt buộc, detail không phải mảng, …) |
| Portal login fail sau restart | Đang dùng khóa RSA ephemeral — khai `PortalKeyPath` hoặc `PortalKeyThumbprint` |
| Form không thấy route | Tên file không tách được verb/resource, hoặc XML lỗi lúc nạp (xem log khởi động) |

---

## 11. Liên kết nhanh

| Nhu cầu | Đến đâu |
|---|---|
| Đặc tả đầy đủ | [KE_HOACH.md](KE_HOACH.md) |
| README repo | [../README.md](../README.md) |
| Mẫu ký C#/Python/… | `WebPortal/wwwroot/samples/` |
| Vector chữ ký trong test | `Tests/MessageSignatureTests.cs` |
| Client mẫu trong test | `Tests/Sample/FastApiSigner.cs` |
| Script DB | `sql/` |

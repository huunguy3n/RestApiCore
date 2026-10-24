# Hướng dẫn sử dụng FastAPI v3

Tutorial thực hành: dựng môi trường, cấp tài khoản đối tác, ký request, gọi nghiệp vụ, dùng WebPortal / PortalApp, và thêm form XML.

Quyết định kiến trúc portal: [ADR-0001](adr/ADR-0001-portal-cpanel-portalapp.md). Tóm tắt repo: [../README.md](../README.md). Đặc tả giao thức API: [KE_HOACH.md](KE_HOACH.md).

---

## 1. Kiến trúc ngắn

```
Đối tác                    WebApi                          FBO / SQL
─────────                  ──────                          ─────────
1. Ký HMAC + lấy JWT  →   /api/auth/token
2. JWT + ký HMAC      →   /api/{resource}[/{verb}]
                          → kiểm JWT, nonce, chữ ký, iat ≥ token_valid_after
                          → quyền form / IP / đơn vị
                          → nạp XML <import>
                          → checking + processing SQL  →  bảng / proc nghiệp vụ
3. Kiểm chữ ký response ←  JSON + header X-Fast-*
```

Ba cổng thao tác, không còn CLI:

| Môi trường | Công cụ | Ghi chú |
|---|---|---|
| DEV / UAT | **WebPortal** | `/cpanel` Super Admin; `/{maDa}` Admin và user Api. Portal là nguồn sự thật, đồng bộ một chiều xuống DB dự án |
| PROD | **PortalApp** | Nằm trong folder restApi, đọc `appsettings.Local.json` cạnh nó, không đăng nhập, cần license gắn MAC |
| Mọi nơi | **WebApi** | Chỉ API. Không giao diện, không endpoint quản trị |

- **Nguồn dữ liệu nghiệp vụ:** SQL Server (DB app FBO).
- **Định nghĩa API:** file XML trong `WebApi/App_Data/Form/` (folder restApi của từng dự án).
- **Bí mật đối tác:** `private_key` (32 byte, base64) — chỉ dùng ký HMAC.
- **Mật khẩu portal:** băm PBKDF2-HMAC-SHA256 (310.000 vòng), tối thiểu 12 ký tự.

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
copy WebApi\appsettings.Local.example.json WebApi\appsettings.Local.json
copy WebPortal\appsettings.Local.example.json WebPortal\appsettings.Local.json
```

**WebApi**

| Khóa | Ý nghĩa |
|---|---|
| `appSettings:secretKey` | Ký JWT (≥ 32 ký tự) |
| `appSettings:environment` | `DEV` hoặc `PROD`. PROD mới kiểm license |
| `appSettings:projectCode` | Mã dự án (khớp `nbdmda` / license) |
| `appSettings:licenseEnforce` | `true` = WebApi chặn request khi license sai. Mặc định chỉ cảnh báo |
| `appSettings:portalOrigins` | Origin WebPortal được CORS, vd `["http://localhost:5225"]` |
| `connectionStrings:appConnectionString` | Chuỗi kết nối DB app |
| `Security:KeyEncryptionKey` | Base64 32 byte — mã hóa `private_key` trong DB |

Ví dụ:

```json
{
  "appSettings": {
    "secretKey": "thay-bang-chuoi-ngau-nhien-dai-hon-32",
    "environment": "DEV",
    "projectCode": "AQTC",
    "licenseEnforce": false,
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

**WebPortal** — xem `WebPortal/appsettings.Local.example.json`. Cần PortalDb, QLDA (chỉ SELECT `nbdmda`), `Portal:AllowedApiRoots`, và (khi cấp license) PFX ECDSA P-256 ngoài repo.

> Trên production: `environment=PROD`, tắt `isDevState`, dùng HTTPS, ACL chặt cho folder restApi và file Local.

### 2.3. Script SQL

**DB dự án**, chạy theo thứ tự:

| File | Việc |
|---|---|
| `sql/000_restApiBase.sql` | Bảng nền `restApiUser`, `restApiAllowForm`, `restApiAllowIp`, … |
| `sql/001_restApi_CheckUnitRights.sql` | Kiểm quyền đơn vị |
| `sql/002_restApiUser_private_key.sql` | Cột `private_key` |
| `sql/003_restApiRevokedToken.sql` | JWT đã thu hồi (jti) |
| `sql/004_restApiIdempotency.sql` | Chống ghi trùng |
| `sql/005_restApi_GetUserRights.sql` | Một lượt lấy user + quyền + khóa |
| `sql/006` … `008` | Log request / dọn / tìm |
| `sql/009_restApiPortalSupport.sql` | `token_valid_after`, `restApiLicense`, chỉ mục log, bucket thời gian |

Chỉ đụng object `restApi*`.

**PortalDb:** `sql/portal/001_portalDb.sql` rồi `002_portalCustomize.sql`.

### 2.4. Chạy

```powershell
dotnet run --project WebApi --launch-profile http
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

## 3. Chuẩn bị tài khoản (WebPortal)

Không dùng CLI. Mọi user Api và quyền được tạo trên portal rồi **đẩy một chiều** xuống DB dự án.

### 3.1. Super Admin

1. Mở `/cpanel/setup` (chỉ khi chưa có SA).
2. Đặt tên đăng nhập (không trùng bất kỳ Admin / user Api nào sau này) và mật khẩu ≥ 12 ký tự.
3. Đăng nhập `/cpanel/login`. Lần đầu bắt buộc đổi mật khẩu.
4. Tạo dự án: chọn mã từ `nbdmda` (`status = 1`), xác nhận `api_folder` (phải nằm dưới `AllowedApiRoots`, có `appsettings.Local.json` và `App_Data\Form`, kết nối SQL được, đã chạy `sql/009`).
5. Cấp Admin dự án (tài khoản ngoài mapping FBO, không gọi được api).

### 3.2. Admin dự án — cấp user Api

Đăng nhập `/{maDa}/login`. Trong một dự án, tên Admin và tên user Api không trùng nhau. Tên đăng nhập là duy nhất toàn portal.

1. Tạo user: `user_name`, mật khẩu ≥ 12, `name_erp` chọn từ NSD FBO đang hoạt động (`vsysuserinfo`).
2. Cấp form: để trống = **mọi form đang bật** trong `App_Data\Form`. Có khai = đúng danh sách. Lưu danh sách rỗng phải xác nhận trên UI.
3. Cấp IP: cùng quy tắc (rỗng = mọi IP).
4. Cấp hoặc xoay `private_key` — hiện **một lần**. Giao cho đối tác qua kênh an toàn.
5. Thu hồi token: ghi `token_valid_after` trên DB dự án; JWT cũ bị WebApi từ chối.

NSD FBO `admin = 1` luôn được mọi form (hành vi WebApi).

### 3.3. User Api trên portal

Đăng nhập `/{maDa}/login`: xem hướng dẫn, form được cấp, mã lỗi, gọi thử (CORS), thu hồi token của mình, xem key sau khi nhập lại mật khẩu.

### 3.4. Một phiên mỗi tài khoản

Đăng nhập đúng mật khẩu mà đang có phiên hiệu lực:

1. Portal hiện cảnh báo (IP, thời điểm, trình duyệt của phiên cũ) và tạo challenge 2 phút.
2. Phiên cũ tự hỏi `/session/notice` mỗi 30 giây và hiện banner.
3. Đồng ý → phiên cũ bị thu hồi (`replaced`), phiên mới được tạo trong một transaction.
4. Không đồng ý hoặc quá hạn → challenge hủy, phiên cũ giữ nguyên.

Đổi mật khẩu, khóa tài khoản hoặc SA ép thoát: thu hồi mọi phiên của tài khoản đó.

### 3.5. Báo lệch và đồng bộ lại

Nếu ai đó sửa thẳng `restApiUser` / `restApiAllowForm` / `restApiAllowIp` trên DB dự án, lần đẩy sau sẽ ghi đè. Admin bấm **Đồng bộ lại** hoặc xem **Báo lệch** (chỉ đọc). Lỗi đẩy thì portal giữ thay đổi và gắn `sync_error`.

---

## 4. Bảo mật thông điệp (đối tác bắt buộc)

Mọi request nghiệp vụ và `/api/auth/token`, `/api/_meta/ping` (trừ `GET /api/_meta/server` và health) cần:

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

Response **không có** chữ ký (thường lỗi JWT/user sớm) → không tin nội dung.

### 4.4. Vector thử cố định

Dùng để kiểm client tự viết (cùng giá trị trong `Tests/MessageSignatureTests.cs` và `WebPortal/wwwroot/samples/`):

| Thành phần | Giá trị |
|---|---|
| `private_key` (base64) | `AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=` |
| Body | `{"master":{"YourID":"HD001"},"detail":[{"ItemID":"VT01","Quantity":2}]}` |
| Nonce | `1792574400-9b2e4c1f0a7d4e3b` |
| `X-Fast-Signature` (request) | `9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac` |

Có sẵn C# / Python / Java / Node / shell trong `WebPortal/wwwroot/samples/`.

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
```

### Bước E — Thu hồi token

Một JWT hiện tại:

```http
POST /api/auth/revoke
Authorization: Bearer <jwt>
X-Fast-Nonce: …
X-Fast-Signature: …
```

Mọi JWT của user: Admin (hoặc chính user Api) bấm thu hồi trên WebPortal / PortalApp — WebApi chặn theo `iat` và `token_valid_after`.

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

## 7. WebPortal

Hai cổng, chỉ hai ô user/pass:

| URL | Ai |
|---|---|
| `/cpanel/login` | Đúng một Super Admin |
| `/{maDa}/login` | Admin dự án hoặc user Api. `maDa` phải có trong `nbdmda` (status = 1) và đã khai `portalProject`. Sai → 404 chung |

Mã dành riêng, không dùng làm mã dự án: `cpanel`, `login`, `logout`, `css`, `js`, `lib`, `samples`, `error`, `session`, `api`, `health`.

Gọi thử API từ trang user Api: trình duyệt ký HMAC bằng `private_key` (chỉ giữ trong bộ nhớ trang), gửi sang WebApi qua CORS. Không còn `POST /api/auth/portal` hay JWT `scope=portal`.

Code mẫu tải từ trang hoặc `WebPortal/wwwroot/samples/`.

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

Admin sửa form trên WebPortal (revision + publish) hoặc copy file vào `App_Data\Form` rồi restart WebApi. Cấp `restApiAllowForm` (hoặc để rỗng = mọi form) cho tên form mới.

### 8.2. Nâng form v2 → v3

Trên WebPortal (màn Forms) hoặc PortalApp: chọn thư mục FastAPI 2.4.7, xuất `<import>` v3.

- Đọc form FastAPI 2.4.7 (`<import>` phẳng, entity `Include\*.txt`), giữ tên file.
- Tên field đổi sang camelCase; `justContent` thành `check`; field `cacheID` bị bỏ.
- Có kết nối DB thì tách `master`/`detail` của chứng từ theo cột thật của `m??$000000` / `d??$000000`. Không có thì mọi field vào `master`.
- **Processing không tự chuyển được**: v2 gọi proc `api_*` đọc `#_data`. Query v2 được giữ làm chú thích, processing trả `ERROR` tới khi bạn viết lại.

---

## 9. PortalApp trên PROD

Đặt `PortalApp.exe` ngay trong folder restApi (cùng `appsettings.json` / `appsettings.Local.json`). Không có ô nhập connection string, không có đăng nhập.

1. **License:** tạo `license-request.json` → gửi SA/Admin trên WebPortal → nhận `restApi.lic` → nạp vào folder. Chưa hợp lệ thì chỉ còn màn yêu cầu / nạp license.
2. **Setup wizard:** preflight, chạy `sql/000`–`009`, sinh secret, IIS, ghi `projectCode`.
3. User Api, form/IP (rỗng = tất cả), cấp/xoay key, thu hồi token.
4. Log / hiệu suất; áp gói từ portal (nhập SHA-256).

Bảo vệ bằng ACL Windows của folder (Administrators + App Pool). Mọi thao tác ghi `restApiAudit` kèm `DOMAIN\User` và tên máy. Bước cần quyền máy (IIS, ACL) chạy với Administrator (UAC).

Ở PROD, PortalApp ghi thẳng DB dự án — ngoại lệ có chủ đích vì máy khách không gọi được WebPortal.

---

## 10. Vận hành

| Việc | Cách |
|---|---|
| Health | `GET /health/live` (tiến trình), `GET /health/ready` (SQL + thư mục log + license khi PROD) |
| Log file | Mặc định `%ProgramData%\FastRestApi\Logs\api-*.log`; gắn `X-Request-ID` |
| Rate limit | Theo user đã xác thực (hoặc IP trước khi có JWT); vượt → `429 TOO_MANY_REQUESTS` |
| Đổi `private_key` | WebPortal / PortalApp xoay key + đối tác cập nhật |
| Thu hồi một JWT | Client gọi `/api/auth/revoke` |
| Thu hồi mọi JWT của user | Portal / PortalApp → `token_valid_after` |
| Bản phát hành | `build/release.ps1` (Obfuscar + `integrity.json`) |

Production checklist ngắn:

1. HTTPS, Hosting Bundle .NET 10, App Pool No Managed Code.
2. Bí mật ngoài Git; PFX license hoặc Certificate Store (`LocalMachine\My`).
3. SQL `000`–`009` đã áp; quyền DDL không cần trên runtime (chỉ lúc cài).
4. `environment=PROD`; `projectCode` khớp; `restApi.lic` cạnh `appsettings.json`.
5. Folder restApi: ACL hẹp. Backup `appsettings.Local.json` và PFX riêng.

---

## 11. Gỡ lỗi nhanh

| Triệu chứng | Hướng xử lý |
|---|---|
| WebApi không start: thiếu `secretKey` / connection | Kiểm `appsettings.Local.json` |
| `401 INVALID_SIGNATURE` | Sai `private_key`, body đã đổi sau khi ký, hoặc hex hoa/thường lệch quy ước |
| `401 TIMESTAMP_EXPIRED` / `NONCE_REUSED` | Đồng bộ `serverTime`; mỗi request một nonce mới |
| `401` sau khi thu hồi mọi token | JWT có `iat` trước `token_valid_after` — lấy token mới |
| `403 FORBIDDEN` | Có khai form/IP nhưng không khớp (admin FBO hoặc không khai thì được mọi form / mọi IP) |
| `400 UNIT_REQUIRED` / `UNIT_DENIED` | Thêm `?unitCode=`; kiểm `sysunitrights` / admin FBO |
| `400 INVALID_SHAPE` | Sai cấu trúc JSON (thiếu khối bắt buộc, detail không phải mảng, …) |
| `/cpanel` 404 / không vào được dự án | Mã không có trong `nbdmda` status = 1, hoặc trùng đoạn URL dành riêng |
| Đồng bộ lỗi | Xem `sync_error`; kiểm `AllowedApiRoots`, quyền đọc folder, SQL login |
| PortalApp chỉ còn màn license | Sai chữ ký, sai MAC, sai `projectCode`, hết hạn, hoặc đồng hồ bị chỉnh lùi |
| Form không thấy route | Tên file không tách được verb/resource, hoặc XML lỗi lúc nạp (xem log khởi động) |

---

## 12. Liên kết nhanh

| Nhu cầu | Đến đâu |
|---|---|
| ADR kiến trúc portal | [ADR-0001](adr/ADR-0001-portal-cpanel-portalapp.md) |
| Đặc tả giao thức API | [KE_HOACH.md](KE_HOACH.md) |
| README repo | [../README.md](../README.md) |
| Mẫu ký C#/Python/… | `WebPortal/wwwroot/samples/` |
| Vector chữ ký trong test | `Tests/MessageSignatureTests.cs` |
| License / phiên / quyền | `Tests/LicenseTests.cs`, `Tests/SessionServiceTests.cs`, `Tests/PortalAccessTests.cs` |
| Script DB dự án | `sql/` |
| Script PortalDb | `sql/portal/` |

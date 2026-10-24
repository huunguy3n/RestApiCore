# FastAPI v3 (RestApiCore)

REST API tích hợp đối tác với Fast Business Online (FBO), viết bằng **ASP.NET Core** (.NET 10), chạy trên **Windows + IIS + SQL Server**.

Đối tác gọi nghiệp vụ qua `POST /api/...`, body JSON, xác thực **JWT** và chữ ký **HMAC-SHA256 hai chiều**. Định nghĩa tài nguyên nằm trong file XML `<import>` (`App_Data/Form`).

> Đây là **v3** (repo này). FastAPI v2 (VB) đóng băng cho đối tác cũ; đối tác mới dùng v3.

---

## Nội dung chính

| Thành phần | Vai trò |
|---|---|
| `Api` | Host HTTP: auth, chữ ký, form, health, portal meta |
| `Business` / `DataAccess` / `Models` / `Security` / `Provider` | Logic form, SQL, mô hình, mã hóa/ký, ADO.NET |
| `ApiDocs` | Cổng đối tác (đăng nhập RSA, xem tài nguyên, gọi thử, code mẫu) |
| `Cli` | Công cụ quản trị: cấp khóa, mã hóa lại mật khẩu, nâng XML v2 → v3 |
| `CallbackWorker` | Worker callback/notify (ngoài phạm vi giai đoạn hiện tại) |
| `Tests` | Unit / integration / vector chữ ký |
| `sql/` | Script DB `restApi*` (chạy theo thứ tự số) |
| `docs/` | Kế hoạch thiết kế và hướng dẫn sử dụng |

---

## Yêu cầu môi trường

- Windows (dev hoặc server)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (dev) / Hosting Bundle (IIS)
- SQL Server (thấp nhất đã kiểm: **2008 R2**; production nên TLS 1.2)
- DB app FBO có sẵn `userinfo2`, `dmdvcs`, `ff_CheckUnitRight`, …

---

## Chạy nhanh (máy dev)

### 1. Cấu hình bí mật

```powershell
copy Api\appsettings.Local.example.json Api\appsettings.Local.json
```

Điền tối thiểu trong `Api/appsettings.Local.json`:

```json
{
  "appSettings": {
    "secretKey": "<chuỗi ngẫu nhiên ≥ 32 ký tự, ký JWT>",
    "passwordKey": "<đúng 32 ký tự, khác secretKey, mã hóa mật khẩu user>"
  },
  "connectionStrings": {
    "appConnectionString": "Data Source=...;Initial Catalog=...;Uid=...;Pwd=...;TrustServerCertificate=True;Application Name=restApi;"
  },
  "Security": {
    "KeyEncryptionKey": "<base64 32 byte — mã hóa private_key HMAC>"
  }
}
```

`appsettings.Local.json` đã nằm trong `.gitignore` — không commit.

Sinh `KeyEncryptionKey` (PowerShell):

```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
```

Hoặc dùng helper trong code: `Security.PrivateKeyProtector.GenerateKeyEncryptionKey()`.

### 2. Áp script SQL

Chạy lần lượt các file trong `sql/` trên DB app (theo số thứ tự `001` …). Chỉ tạo/sửa object tên `restApi*`.

### 3. Chạy API

```powershell
dotnet run --project Api --launch-profile http
```

Mặc định: `http://localhost:5143`

Kiểm nhanh:

- `GET /` → `REST API`
- `GET /health/live`, `GET /health/ready`
- `GET /api/_meta/server` → `serverTime`, `signatureVersion`, `portalKey`

### 4. Portal (tuỳ chọn)

```powershell
dotnet run --project ApiDocs --launch-profile http
```

Khai `appSettings:portalOrigins` trên Api (vd `http://localhost:5225`) để CORS cho phép trình duyệt gọi `/api`.

### 5. Test

```powershell
dotnet test
```

---

## Tài khoản (tóm tắt)

`restApiUser` nối FBO qua `name_erp` → `userinfo2`. Không có cờ admin riêng: NSD FBO `admin = 1` thì được mọi form và mọi đơn vị. User thường: không khai `restApiAllowForm` (và `restApiAllowIp`) thì được mọi form (mọi IP), có khai thì chỉ kiểm theo danh sách đã khai.

```powershell
# User quản trị (name_erp = ADMIN trên FBO) + private_key
$env:RESTAPI_PASSWORD = '...'
dotnet run --project Cli -- create-user --config Api/appsettings.Local.json `
  --user APIADMIN --name-erp ADMIN --password-env RESTAPI_PASSWORD `
  --forms getCustomer,setSaleInvoice --issue-key --apply

# User đối tác thường
dotnet run --project Cli -- create-user --config Api/appsettings.Local.json `
  --user DOITAC01 --name-erp NVKD01 --password-env RESTAPI_PASSWORD `
  --forms getCustomer --issue-key --apply
```

Chi tiết: **[docs/HUONG_DAN_SU_DUNG.md §3](docs/HUONG_DAN_SU_DUNG.md)**. Gọi API / ký HMAC: cùng file hướng dẫn.
---

## Route chính

| Method | Đường dẫn | Mô tả |
|---|---|---|
| `POST` | `/api/auth/token` | Lấy JWT (ký HMAC, body `{ "userName" }`) |
| `POST` | `/api/auth/revoke` | Thu hồi JWT hiện tại |
| `POST` | `/api/auth/portal` | Đăng nhập cổng (mật khẩu RSA-OAEP) |
| `POST` | `/api/{resource}` | Form `set{Resource}.xml` |
| `POST` | `/api/{resource}/{verb}` | Form `{verb}{Resource}.xml` (vd `getCustomer` → `/api/customer/get`) |
| `GET` | `/api/_meta/server` | Đồng bộ giờ + khóa RSA portal (không cần token) |
| `GET` | `/api/_meta/resources` | Danh sách tài nguyên theo quyền (JWT portal hoặc API) |
| `GET` | `/api/_meta/resources/{form}` | Định nghĩa field + mẫu JSON |
| `GET` | `/api/_meta/errors` | Bảng mã lỗi |
| `POST` | `/api/_meta/ping` | Thử ký hai chiều (cần JWT + HMAC) |
| `GET` | `/health/live`, `/health/ready` | Giám sát |

Tham số điều khiển (query/header): `unitCode`, `dryRun`, `cursor` / `page`, `Idempotency-Key`.

---

## Form XML mẫu

| File | Route |
|---|---|
| `Api/App_Data/Form/getCustomer.xml` | `POST /api/customer/get` |
| `Api/App_Data/Form/setSaleInvoice.xml` | `POST /api/sale-invoice?unitCode=…` |
| `Api/App_Data/Form/setTemplate.xml` | Khung tham khảo |

Quy ước tên file: `{động từ chữ thường}{Resource PascalCase}.xml` → resource kebab-case.  
`set` → `POST /api/{resource}`; động từ khác → `POST /api/{resource}/{verb}`.

---

## CLI quản trị

```text
Fast.Api.Cli rekey-passwords --config <appsettings.Local.json> [--apply]
Fast.Api.Cli issue-key       --config <appsettings.Local.json> --user <name> [--rotate] [--apply]
Fast.Api.Cli upgrade         <file.xml | thư mục FastAPI> [--config <json>] [--out <thư mục>] [--force]
```

```powershell
dotnet run --project Cli -- --help
```

---

## Code mẫu ký HMAC

Trong `ApiDocs/wwwroot/samples/`:

- `FastSign.cs`, `fast_sign.py`, `FastSign.java`, `fast-sign.mjs`, `fast-sign.sh`

Có **vector thử cố định** (khóa / body / chữ ký mong đợi) — dùng để kiểm client tự viết.

---

## Tài liệu thêm

| File | Nội dung |
|---|---|
| [docs/HUONG_DAN_SU_DUNG.md](docs/HUONG_DAN_SU_DUNG.md) | Tutorial: cài đặt, tài khoản, ký, gọi API, portal, viết form |
| [docs/KE_HOACH.md](docs/KE_HOACH.md) | Đặc tả thiết kế v3 (route, bảo mật, XML, DB, roadmap) |
| `docs/architecture/` | Ghi chú kiến trúc / refactor (nội bộ) |

---

## Triển khai production (ý chính)

- Windows + IIS + .NET 10 Hosting Bundle + SQL Server
- Bí mật chỉ trong `appsettings.Local.json` / biến môi trường / Certificate Store
- HTTPS bắt buộc (HMAC không mã hóa body)
- Log vận hành: thư mục ngoài release (mặc định `%ProgramData%\FastRestApi\Logs`)
- Một instance sau IIS là baseline; không phụ thuộc Redis/queue ngoài

Setup một lệnh (`Fast.Api.Cli setup`) nằm trong kế hoạch pha 7 — hiện tại cài SQL + IIS theo checklist thủ công (xem hướng dẫn sử dụng).

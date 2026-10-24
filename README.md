# FastAPI v3 (RestApiCore)

REST API tích hợp đối tác với Fast Business Online (FBO), viết bằng **ASP.NET Core** (.NET 10), chạy trên **Windows + IIS + SQL Server**.

Đối tác gọi nghiệp vụ qua `POST /api/...`, body JSON, xác thực **JWT** và chữ ký **HMAC-SHA256 hai chiều**. Định nghĩa tài nguyên nằm trong file XML `<import>` (`App_Data/Form`).

> Đây là **v3** (repo này). FastAPI v2 (VB) đóng băng cho đối tác cũ; đối tác mới dùng v3.
>
> Kiến trúc portal hiện tại: [ADR-0001](docs/adr/ADR-0001-portal-cpanel-portalapp.md) — WebPortal nội bộ (`/cpanel` + `/{mã dự án}`), không còn CLI, PROD dùng PortalApp có license.

---

## Nội dung chính

| Thành phần | Vai trò |
|---|---|
| `WebApi` | Host HTTP: chỉ API. Auth, chữ ký HMAC, form, health. Không có giao diện, không có endpoint quản trị |
| `WebPortal` | Cổng nội bộ DEV/UAT: Super Admin ở `/cpanel`, Admin dự án và user Api ở `/{maDa}` |
| `PortalApp` | WinForms mini-portal cho PROD, đặt trong folder restApi, không đăng nhập, cần license gắn MAC |
| `WebProject` | Module FBO (log API trên lưới FBO) |
| `Business` / `DataAccess` / `Models` / `Security` / `Provider` | Logic form, SQL, mô hình, mã hóa/ký/license, ADO.NET |
| `Tests` | Unit / integration / vector chữ ký / license / phiên / quyền |
| `sql/` | Script DB dự án `restApi*` (`000` … `009`) |
| `sql/portal/` | Script PortalDb |
| `docs/` | Hướng dẫn sử dụng và ADR |

Không còn project `Cli`. Việc cấp user, khóa, token, setup, nâng form làm trên WebPortal (DEV/UAT) hoặc PortalApp (PROD).

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
copy WebApi\appsettings.Local.example.json WebApi\appsettings.Local.json
copy WebPortal\appsettings.Local.example.json WebPortal\appsettings.Local.json
```

Điền tối thiểu trong `WebApi/appsettings.Local.json`:

```json
{
  "appSettings": {
    "secretKey": "<chuỗi ngẫu tiền ≥ 32 ký tự, ký JWT>",
    "environment": "DEV",
    "projectCode": "AQTC"
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

Hoặc `Security.PrivateKeyProtector.GenerateKeyEncryptionKey()`.

WebPortal cần PortalDb + QLDA trong `WebPortal/appsettings.Local.json`, và `Portal:AllowedApiRoots` trỏ tới gốc chứa folder restApi của các dự án.

### 2. Áp script SQL

- **DB dự án (app FBO):** chạy lần lượt `sql/000` … `sql/009`. Chỉ tạo/sửa object tên `restApi*`. `sql/009` thêm `token_valid_after`, license và chỉ mục log.
- **PortalDb:** `sql/portal/001_portalDb.sql` rồi `002_portalCustomize.sql`.

### 3. Chạy WebApi

```powershell
dotnet run --project WebApi --launch-profile http
```

Mặc định: `http://localhost:5143`

Kiểm nhanh:

- `GET /` → `REST API`
- `GET /health/live`, `GET /health/ready`
- `GET /api/_meta/server` → `serverTime`, `signatureVersion`

### 4. WebPortal (DEV/UAT)

```powershell
dotnet run --project WebPortal --launch-profile http
```

- Lần đầu: `/cpanel/setup` tạo Super Admin duy nhất (mật khẩu ≥ 12 ký tự, đổi ở lần đăng nhập đầu).
- SA vào `/cpanel`: tạo dự án từ `nbdmda` (status = 1), khai `api_folder`, cấp Admin.
- Admin / user Api vào `/{maDa}/login`. Mỗi tài khoản chỉ có **một phiên**; đăng nhập nơi khác phải xác nhận thay phiên.

Khai `appSettings:portalOrigins` trên WebApi (vd `http://localhost:5225`) để CORS cho phép trình duyệt gọi thử `/api`.

### 5. Test

```powershell
dotnet test
```

---

## Tài khoản

Ba loại, quyền cố định (không bảng vai trò):

| Loại | Đăng nhập | Việc được làm |
|---|---|---|
| Super Admin | `/cpanel` | Tạo dự án, cấp Admin, xem log mọi dự án, cấp/thu hồi license, làm được việc Admin ở mọi dự án |
| Admin dự án | `/{maDa}` | Khai folder API, CRUD user Api, cấp form/IP, cấp/xoay key, thu hồi token, xem log. **Không gọi được api** |
| User Api | `/{maDa}` | Xem form được cấp, mã lỗi, gọi thử, thu hồi token mình, xem key (nhập lại mật khẩu) |

`restApiUser` trên DB dự án nối FBO qua `name_erp` → `userinfo2`. NSD FBO `admin = 1` thì được mọi form và mọi đơn vị.

**Quyền form và IP:** có khai thì chỉ đúng danh sách đã khai; **danh sách rỗng = tất cả**. Portal là nguồn sự thật; đồng bộ một chiều portal → `restApiUser` / `restApiAllowForm` / `restApiAllowIp`. Sửa thẳng ở DB dự án sẽ bị ghi đè lần đồng bộ sau.

Chi tiết thao tác: **[docs/HUONG_DAN_SU_DUNG.md](docs/HUONG_DAN_SU_DUNG.md)**.

---

## Route chính (WebApi)

| Method | Đường dẫn | Mô tả |
|---|---|---|
| `POST` | `/api/auth/token` | Lấy JWT (ký HMAC, body `{ "userName" }`) |
| `POST` | `/api/auth/revoke` | Thu hồi JWT hiện tại (jti) |
| `POST` | `/api/{resource}` | Form `set{Resource}.xml` |
| `POST` | `/api/{resource}/{verb}` | Form `{verb}{Resource}.xml` (vd `getCustomer` → `/api/customer/get`) |
| `GET` | `/api/_meta/server` | Đồng bộ giờ (không cần token) |
| `GET` | `/api/_meta/errors` | Bảng mã lỗi |
| `POST` | `/api/_meta/ping` | Thử ký hai chiều (cần JWT + HMAC) |
| `GET` | `/health/live`, `/health/ready` | Giám sát; PROD báo thêm trạng thái license |

Không còn `/api/auth/portal`, `/api/_portal/*`, `/api/_meta/resources*`. Tài nguyên và gọi thử nằm trên WebPortal / PortalApp.

Thu hồi **mọi** JWT của một user: portal/PortalApp ghi `restApiUser.token_valid_after`; WebApi chặn JWT có `iat` cũ hơn (hiệu lực sau tối đa `userCacheSeconds`).

Tham số điều khiển: `unitCode`, `dryRun`, `cursor` / `page`, `Idempotency-Key`.

---

## Form XML mẫu

| File | Route |
|---|---|
| `WebApi/App_Data/Form/getCustomer.xml` | `POST /api/customer/get` |
| `WebApi/App_Data/Form/setSaleInvoice.xml` | `POST /api/sale-invoice?unitCode=…` |
| `WebApi/App_Data/Form/setTemplate.xml` | Khung tham khảo |

Quy ước tên file: `{động từ chữ thường}{Resource PascalCase}.xml` → resource kebab-case.
`set` → `POST /api/{resource}`; động từ khác → `POST /api/{resource}/{verb}`.

---

## License và bản phát hành (PROD)

Một bản restApi chỉ chạy đúng máy đã đăng ký:

1. PortalApp xuất `license-request.json` (mã dự án + MAC card vật lý). Không chứa bí mật.
2. SA/Admin tải lên WebPortal; portal ký ECDSA P-256, trả `restApi.lic`.
3. Chép `restApi.lic` vào folder restApi. PortalApp kiểm chữ ký + MAC + mã dự án rồi mới mở các màn khác.
4. WebApi (environment = `PROD`) kiểm cùng file lúc khởi động. Mặc định chỉ cảnh báo trên `/health/ready`; bật `appSettings:licenseEnforce = true` thì chặn request.

Nhị phân Release: Obfuscar + `integrity.json` (SHA-256) do WebPortal ký thành `integrity.sig`. PortalApp từ chối chạy nếu file bị sửa.

Gói phát hành: `build/release.ps1`.

---

## Code mẫu ký HMAC

Trong `WebPortal/wwwroot/samples/`:

- `FastSign.cs`, `fast_sign.py`, `FastSign.java`, `fast-sign.mjs`, `fast-sign.sh`

Có **vector thử cố định** (khóa / body / chữ ký mong đợi) — dùng để kiểm client tự viết.

---

## Tài liệu thêm

| File | Nội dung |
|---|---|
| [docs/HUONG_DAN_SU_DUNG.md](docs/HUONG_DAN_SU_DUNG.md) | Tutorial: cài đặt, tài khoản, ký, gọi API, portal, PortalApp, viết form |
| [docs/adr/ADR-0001-portal-cpanel-portalapp.md](docs/adr/ADR-0001-portal-cpanel-portalapp.md) | Quyết định kiến trúc portal / bỏ CLI / license |
| [docs/KE_HOACH.md](docs/KE_HOACH.md) | Đặc tả thiết kế v3 (route, bảo mật, XML, DB) — một số tên project/CLI đã cũ, xem ADR |
| `docs/architecture/` | Ghi chú kiến trúc / refactor (nội bộ) |

---

## Triển khai production (ý chính)

- Windows + IIS + .NET 10 Hosting Bundle + SQL Server
- Folder restApi: ACL chỉ Administrators + App Pool identity (PortalApp không có đăng nhập)
- Bí mật chỉ trong `appsettings.Local.json` / biến môi trường / Certificate Store
- HTTPS bắt buộc (HMAC không mã hóa body)
- `appSettings:environment = PROD`, `projectCode` khớp mã đã cấp license
- Log vận hành: thư mục ngoài release (mặc định `%ProgramData%\FastRestApi\Logs`)
- Setup, user, key, token, log, áp gói: **PortalApp.exe** trong cùng folder
- Một instance sau IIS là baseline; không phụ thuộc Redis/queue ngoài

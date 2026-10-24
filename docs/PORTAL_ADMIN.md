# Portal Admin: phân quyền và kế hoạch

Trạng thái: **mới xong Phase 0 (khảo sát) và Phase 3 (nền phân quyền) kèm phần quyền form của Phase 4**. Phần giao diện và các phase còn lại xem "Chưa làm".

## 1. Hiện trạng đã xác nhận trong code

- `ApiDocs` là Razor Pages **tĩnh**: một trang `Index.cshtml` + `portal.js` (SPA định tuyến hash). Không có handler nghiệp vụ, không DB, không thấy bí mật nào. CSP `script-src 'self'`, mật khẩu mã hóa RSA-OAEP bằng WebCrypto.
- Đăng nhập: `POST /api/auth/portal` (restApiUser.name + mật khẩu RSA) → JWT `scope=portal`, hạn `portalTokenMinutes`, giữ trong `sessionStorage`. `SignatureMiddleware.PortalMayCall` chỉ cho portal token gọi `GET /api/_meta/*`.
- Quyền form API: `UserRights.CanAccessForm` = `ErpAdmin || AllowForms.Count == 0 || AllowForms.Contains(form)`. **Không có dòng `restApiAllowForm` nào = mọi form.** (`docs/HUONG_DAN_SU_DUNG.md` dòng ~115 ghi ngược lại, là tài liệu cũ; code và `KE_HOACH.md` dòng 62 là đúng.)
- `ImportFormCatalog`: bất biến, dựng từ `App_Data/Form/*.xml`, `FormReloader` theo dõi file và thay nguyên catalog. Không có revision, không ghi file từ portal.
- "Controller" trong hệ thống này là **form XML sinh route** (`/api/{resource}/{verb}`), không có controller C# động, không có metadata trong DB.
- CLI: `create-user`, `set-password`, `rekey-passwords`, `issue-key` (đổi/cấp khóa, nhạy cảm), `upgrade` (chỉ đọc/ghi file XML). Đều đọc bí mật từ file cấu hình/biến môi trường, mặc định dry-run, `--apply` mới ghi.
- Chưa có audit quản trị, chưa có vai trò portal. `userinfo2.admin` chỉ là quyền nghiệp vụ ERP.

## 2. Đã làm

| Hạng mục | File |
|---|---|
| Migration (SQL 2008, chạy lại được, có rollback trong chú thích) | `sql/008_restApiPortalAuthorization.sql` |
| Danh mục quyền chi tiết (deny-by-default, giá trị lạ bị bỏ) | `Models/PortalPermissions.cs` |
| Tính quyền từ vai trò; quản trị quyền form; danh sách user không lộ bí mật | `Business/PortalAdmin.cs` |
| Truy vấn (ROW_NUMBER, transaction khi thay danh sách form, audit) | `DataAccess/PortalAccessRepository.cs` |
| Endpoint `/api/_portal/*` | `Api/Custom/PortalAdminEndpoints.cs` |
| Cho portal token gọi `/api/_portal` (GET/PUT), CORS thêm PUT | `SignatureMiddleware.cs`, `PortalCors.cs` |
| Test (8 ca) | `Tests/PortalAdminTests.cs` |

Endpoint:

| Route | Quyền | Ghi chú |
|---|---|---|
| `GET /api/_portal/me` | (mọi portal token) | vai trò + quyền hiệu lực; `partner` luôn có |
| `GET /api/_portal/users?search&page&pageSize` | `users.read` | không trả password/private_key, chỉ cờ `hasKey`; ký tự LIKE bị loại |
| `GET /api/_portal/users/{name}/forms` | `forms.permissions.manage` hoặc `users.read` | `mode` = `erpAdmin` / `explicit` / `unrestricted`, kèm cảnh báo quyền rộng |
| `PUT /api/_portal/users/{name}/forms` | `forms.permissions.manage` | body `{forms:[...]}` |

Quy tắc quyền form: mọi tên phải có trong catalog đang chạy (chuẩn hóa theo tên catalog); **danh sách rỗng bị từ chối** (`EMPTY_ALLOW_LIST`) vì xóa nốt form cuối sẽ biến thành "mọi form"; thay trong một transaction; xóa cache khóa/quyền ngay; ghi audit (kể cả bị từ chối).

Quyết định bảo mật: quyền không nằm trong JWT, tra DB mỗi request (thu hồi vai trò có hiệu lực ngay); `userinfo2.admin` không tham gia; token `scope=api` bị từ chối ở `/api/_portal`; chưa chạy sql/008 thì mọi người chỉ là Partner (fail closed).

## 3. Ma trận quyền mặc định (seed trong sql/008)

| Quyền | Partner | Deployment | Administrator |
|---|---|---|---|
| forms.read / controllers.read | (xem form được cấp qua `_meta`) | ✔ | ✔ |
| forms.edit | | ✔ | ✔ |
| forms.publish | | | ✔ |
| forms.permissions.manage | | ✔ | ✔ |
| users.read/create/update/deactivate/delete, users.roles.manage | | | ✔ |
| controllers.create/update/publish | | | ✔ |
| cli.validate | | ✔ | ✔ |
| cli.inspect, audit.read, settings.manage | | | ✔ |
| cli.issue-key, cli.rotate-key, cli.rekey-passwords | | | cấp tay |

Hiện chỉ `forms.permissions.manage` và `users.read` được ép ở endpoint; các quyền còn lại là danh mục sẵn cho phase sau.

## 4. Triển khai

1. Chạy `sql/008_restApiPortalAuthorization.sql` trên DB app.
2. Cấp Administrator đầu tiên bằng câu INSERT (đã comment cuối file). Quyền portal không suy từ ERP admin.
3. Deploy Api. Nếu portal ở origin khác: `portalOrigins` đã có; CORS nay cho thêm PUT.

## 5. Chưa làm (cố ý hoãn, theo phase)

- **Phase 1/2 giao diện**: layout mới, sidebar theo quyền, dark mode, API Explorer, trang Authentication/Code Samples. Chưa động vào `ApiDocs`. `/api/_portal/me` đã sẵn để dựng menu.
- Quản lý user (tạo/sửa/khóa/xóa), gán vai trò, xem audit: cần thêm endpoint trên nền này; tạo user/cấp khóa nên gọi lại logic của `Cli` qua service dùng chung, không gọi tiến trình.
- **Form editor**: cần thiết kế revision + ghi file nguyên tử + khóa lạc quan; hiện `FormReloader` nạp lại khi file đổi nên publish = ghi file an toàn.
- Controller management: chỉ có ý nghĩa với metadata form (route, enable, paging); chưa chốt danh sách thuộc tính được sửa.
- CLI qua portal: `upgrade`/`create-user`/`issue-key` chưa tách thành service; chưa mở lệnh nào.
- Rate limit riêng cho `/api/_portal`, xác nhận lại mật khẩu cho thao tác rủi ro cao.
- Xác minh thủ công trên Windows + IIS chưa chạy; test mới chỉ ở mức tích hợp trong bộ nhớ (không SQL thật), repository SQL chưa chạy trên DB thật.

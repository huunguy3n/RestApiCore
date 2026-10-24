# Portal trung tâm nhiều dự án: thiết kế

> **Trạng thái: Thay thế bởi [ADR-0001](adr/ADR-0001-portal-cpanel-portalapp.md)** (24/10/2026). Giữ lại để tra lịch sử; mô hình agent `/api/_portal`, vai trò staff/customer và CLI bên dưới không còn áp dụng.

Chốt với người dùng: (1) portal **gọi Api của từng dự án**, không giữ connection string; (2) tài khoản portal lưu ở **DB riêng của portal**.

## 1. Hai tầng

| Tầng | Chạy ở đâu | Giữ gì |
|---|---|---|
| **Control plane** (portal trung tâm) | 1 bản duy nhất, DB riêng `PortalDb` | tài khoản portal, loại tài khoản, dự án, thành viên dự án, vai trò, audit tổng |
| **Project agent** (Api của dự án) | mỗi dự án một bản cùng DB dự án | `restApiUser`, `restApiAllowForm`, form XML, audit của dự án |

`/api/_portal/*` đã làm ở phase trước trở thành **project agent API**: nó vẫn là chỗ duy nhất đụng DB dự án, nhưng người gọi là control plane chứ không phải trình duyệt khách.

## 2. Phân biệt nội bộ / khách hàng / dự án

- `portalAccount.kind`: `staff` (nội bộ) hoặc `customer` (khách). Cố định khi tạo, không đổi được qua UI.
- `portalMembership(account, project, role)`: tài khoản thuộc dự án nào, vai trò gì trong dự án đó.
  - `customer`: **bắt buộc** có ≥ 1 membership; chỉ vai trò `partner`; chỉ thấy dự án của mình. Không có quyền toàn cục.
  - `staff`: vai trò `deployment` / `administrator` theo từng dự án; `project_id` NULL = toàn bộ dự án (chỉ cấp cho Administrator trung tâm).
- Mỗi membership của khách ghi `project_user_name` = `restApiUser.name` trong dự án đó: đây là danh tính gọi API nghiệp vụ, tách khỏi tài khoản đăng nhập portal.
- Mọi truy vấn control plane lọc theo membership của người gọi (chống IDOR giữa dự án/khách).

## 3. Control plane gọi dự án thế nào

Dùng lại giao thức đã có, không phát minh giao thức mới:

1. Mỗi dự án cấp một `restApiUser` loại agent (`private_key` HMAC bằng `issue-key`), chỉ được gọi `/api/_portal/*`.
2. Control plane lưu khóa này **mã hóa** (DPAPI/khóa cấu hình của portal), ký request bằng HMAC-SHA256 như đối tác (`X-Fast-Nonce`, `X-Fast-Signature`), kiểm chữ ký response.
3. Quyết định "ai được làm gì ở dự án nào" do control plane tính (tài khoản × membership × quyền). Project agent kiểm thêm: người gọi phải là agent hợp lệ, thao tác nằm trong tập quyền của agent, và ghi `actor` (tên tài khoản portal, nằm trong phần được ký) vào `restApiAudit` của dự án.
4. Agent chỉ nhận thao tác trong danh sách cho phép (quyền form, danh sách user, sau này form/validate). Không lệnh tùy ý, không đường dẫn, không SQL.

Thay đổi cần làm ở code đã có: thêm đường xác thực agent (scope `api` + ký) cho `/api/_portal`, nhận `actor` được ký thay vì lấy từ JWT portal, và bỏ vai trò/quyền lưu ở DB dự án (sql/008 phần role) hoặc giữ làm dự phòng khi dự án chạy độc lập.

## 4. Lược đồ control plane

`sql/portal/001_portalDb.sql` (SQL 2008, chạy lại được): `portalAccount`, `portalProject`, `portalRole`, `portalRolePermission`, `portalMembership`, `portalAudit`. Mật khẩu lưu băm (PBKDF2 qua `PasswordHasher`), không lưu mật khẩu có thể giải ngược. Khóa agent của dự án lưu ở `portalProject.agent_key_enc` (đã mã hóa).

## 5. Vòng đời customize: DEV → PROD (bổ sung theo yêu cầu mới)

Mục đích: Api mỗi dự án có customize riêng (SQL xử lý, params nhận/trả) nhưng vẫn theo quy tắc chung của Api. Portal là nơi **khai báo và kiểm soát** customize ở DB **DEV**; lên PROD thì khai báo đã đầy đủ, không sửa tại PROD.

| Môi trường | Ai làm gì | Công cụ |
|---|---|---|
| DEV | Khai báo/sửa customize (form XML, params, SQL xử lý), validate, xem diff, publish revision | Portal (qua project agent của DEV) |
| Đóng gói | Chốt một **package**: danh sách `form@revision + SHA-256` (+ tên proc/script SQL đi kèm), có manifest băm | Portal |
| PROD | Áp package (chỉ ghi file form/đối tượng đã khai báo, kiểm hash từng file), **không có sửa tay** | `Cli apply-package` (dự kiến) |
| PROD, bảo trì | Cấp tài khoản, đổi mật khẩu, cấp/xoay khóa như nhân viên bảo trì | `Gui` / `Cli` hiện có (`create-user`, `set-password`, `issue-key`) |

Nguyên tắc: portal **không ghi vào PROD**. Thay đổi lên PROD đi qua package đã băm và áp bằng CLI/GUI do người bảo trì chạy, nên PROD không phụ thuộc portal còn sống. Mọi thao tác ghi một dòng nhật ký (mục 6).

Giả định cần xác nhận: package chuyển sang PROD bằng file (người bảo trì mang đi), kết quả áp ở PROD trả về portal bằng file nhật ký mà CLI xuất ra để nhập vào portal. Nếu PROD có thể gọi ngược về portal thì thay bước nhập file bằng gọi trực tiếp.

## 6. Nhật ký đồng bộ theo dự án

`portalSyncLog` (một dòng cho mỗi sự kiện): `EXPORT` (portal chốt package), `APPLY` (CLI áp ở PROD, nhập về portal), `VERIFY` (so hash PROD với package), `ACCOUNT_ISSUE`, `PASSWORD_CHANGE`, `KEY_ISSUE`, `FAILED`. Có `project_id`, `env` (DEV/PROD), `package_id`, `source` (portal/cli/gui), người thực hiện, kết quả, chi tiết **không chứa bí mật** (không mật khẩu, khóa, connection string). Màn hình: lọc theo dự án/môi trường/kết quả/khoảng ngày, mở package xem từng file và hash.

## 7. Dashboard tổng quan (chỉ số có thật)

Lấy từ bảng của portal, không số giả: số dự án và trạng thái; mỗi dự án: revision publish mới nhất ở DEV, package gần nhất, lần áp PROD gần nhất, **lệch** (DEV mới hơn PROD bao nhiêu revision), số lần áp lỗi 30 ngày; số tài khoản staff/customer theo dự án; thao tác bảo trì PROD gần đây; cảnh báo: dự án chưa có agent, package chưa áp quá hạn, hash PROD khác package. Số liệu runtime của Api dự án (request/lỗi) lấy qua agent khi cần, đánh dấu rõ nguồn và thời điểm.

Lược đồ bổ sung: `sql/portal/002_portalCustomize.sql`.

## 7b. Log request/response theo dự án

Log **không sao chép về portal**: mỗi dự án vẫn giữ `restApiLogReq` / `restApiLogBody` của mình (đã có, body nén GZip, dọn theo `logRetentionDays`). Portal đọc theo yêu cầu qua project agent, nên không có bản log thứ hai để rò rỉ hay lệch.

Đã làm ở phía Api dự án (`/api/_portal/logs`, test trong `PortalAdminTests`):

| Route | Quyền | Trả |
|---|---|---|
| `GET /api/_portal/logs?from&to&user&form&status&reqId&page&pageSize` | `logs.read` | metadata: thời gian, user, IP, method, path, form, status, mã kết quả, thời lượng, byte, kết quả kiểm chữ ký. Mặc định 24 giờ gần nhất, tối đa 31 ngày, tối đa 100 dòng/trang |
| `GET /api/_portal/logs/{reqId}` | `logs.read`; body cần thêm `logs.body.read` | chi tiết + query string; body request/response nếu có quyền body |

Quy tắc an toàn: body là dữ liệu nghiệp vụ của khách nên quyền xem body tách riêng, **không gán mặc định cho vai trò nào** và mỗi lần mở body ghi `restApiAudit`; body chỉ hiện khi là JSON, các khóa `password/secret/token/accessToken/credential/privateKey/signature/...` bị che `***` ở mọi cấp, body giải nén tối đa 256 KB; tham số lọc đi bằng tham số SQL, tên user/form chỉ giữ ký tự hợp lệ. Log của `auth/token`, `auth/portal` và response chứa JWT vốn đã không được lưu body (RequestLogMiddleware).

Chưa làm: màn hình trên portal (bảng lọc, chi tiết, so request/response), đọc log PROD (portal chỉ-đọc qua agent PROD, cần quyết định riêng vì khác nguyên tắc "portal không ghi PROD" ở chỗ chỉ đọc), log chỉ của chính khách trên cổng đối tác, và gộp số liệu `restApiLogAgg` vào dashboard.

## 8. Thứ tự làm tiếp

1. Host control plane: biến `WebPortal` thành server-side có DB riêng, đăng nhập bằng cookie HttpOnly + CSRF (bỏ JWT trong `sessionStorage` cho phần quản trị), rate limit đăng nhập.
2. Dịch vụ phân quyền theo membership + test (khách không thấy dự án khác, staff chỉ trong dự án được gán).
3. Client gọi project agent (ký HMAC) + đăng ký dự án + kiểm kết nối.
4. Quản lý tài khoản (tạo staff/khách, gán dự án) và giao diện theo quyền.
5. Form editor, quản lý cấu hình cài đặt từng dự án, CLI cho phép.

## 9. Trạng thái xây dựng (cập nhật)

**Đã có (bước 1, 2 và phần CLI của bước 4)**, kèm test (`Tests/ControlPlaneTests.cs`):

| Hạng mục | Nơi |
|---|---|
| Băm mật khẩu PBKDF2-HMAC-SHA256 310.000 vòng, salt riêng, so thời gian hằng | `Security/PortalPasswordHasher.cs` |
| Quy tắc vai trò theo loại tài khoản (dùng chung portal + CLI) | `Models/PortalMembershipRules.cs` |
| Quyền theo dự án từ tài khoản + membership (deny mặc định, dòng sai quy tắc bị bỏ qua) | `WebPortal/ControlPlane/ControlPlane.cs` (`ProjectAccessService`) |
| Đăng nhập: một thông báo lỗi chung, khóa 5 lần / 15 phút (kể cả tên không tồn tại), băm giả cho tên lạ | `AccountLoginService` |
| Kho SQL cho DB portal | `WebPortal/ControlPlane/SqlControlPlaneStore.cs` |
| Trang `/Admin` (đăng nhập, tổng quan theo dự án, đăng xuất), cookie `HttpOnly` + `SameSite=Strict` + `Secure` (trừ Development), CSRF qua antiforgery của Razor Pages, chống open redirect, `Cache-Control: no-store`, CSP cho phép `form-action 'self'` riêng `/Admin` | `WebPortal/Pages/Admin/*`, `Program.cs` |
| Quyền KHÔNG nằm trong cookie: tính lại từ DB mỗi request (khóa tài khoản hoặc thu hồi membership có hiệu lực ngay) | `Pages/Admin/Index.cshtml.cs` |
| CLI `create-portal-account` (kiểm thử mặc định, `--apply` mới ghi, mật khẩu qua biến môi trường) | `Cli/CreatePortalAccountCommand.cs` |
| Seed quyền theo vai trò | `sql/portal/003_portalSeed.sql` |

**Quản trị tài khoản / membership / dự án trên `/Admin`** (`WebPortal/ControlPlane/AccountAdminService.cs`, trang `Pages/Admin/Accounts/*`, `Projects/*`, `Password`), có test `Tests/AccountAdminTests.cs` và luồng trang trong `ControlPlaneTests`:

| Ai | Được làm | Không được |
|---|---|---|
| Administrator trung tâm (membership "mọi dự án") | tạo/khóa/mở mọi tài khoản, gán/gỡ mọi membership, thêm/sửa/ngừng dự án | tự khóa mình; khóa hoặc gỡ administrator trung tâm cuối cùng |
| Người có `users.*` ở một dự án (vd administrator gắn dự án) | tạo khách trong dự án đó, khóa/mở và gán/gỡ vai trò partner cho khách NẰM GỌN trong các dự án mình có quyền | động tới staff, khách có membership ở dự án khác, cấp vai trò staff hoặc quyền mọi dự án, quản lý dự án |
| Deployment, khách hàng | chỉ đổi mật khẩu của chính mình | mọi thao tác quản trị (menu không hiện và server từ chối) |

Ràng buộc: tên đăng nhập `[A-Za-z0-9._@-]{3,64}`, mật khẩu ban đầu ≥ 12 ký tự (băm PBKDF2, không ghi audit); khách luôn là `partner` gắn một dự án còn hoạt động; ngoài phạm vi thì chi tiết trả "không tìm thấy" (không dò được ID). Địa chỉ Api dự án chỉ nhận https (http chỉ cho localhost), không user:pass/query/fragment. Mọi thao tác, kể cả bị từ chối, ghi `portalAudit` (người thao tác, dự án, đối tượng, kết quả, không có bí mật). Khóa tài khoản hoặc gỡ membership có hiệu lực ngay ở request kế tiếp vì quyền tính lại mỗi lần.

**Nhật ký và dashboard** (`WebPortal/ControlPlane/AuditViewService.cs`, trang `Pages/Admin/Audit/*` và `Pages/Admin/Index`, test `Tests/AuditViewTests.cs`): xem `portalAudit` lọc theo khoảng ngày (tối đa 92 ngày), dự án, người/đối tượng, tiền tố hành động, kết quả; 50 dòng/trang. Phạm vi theo `audit.read`: administrator trung tâm thấy mọi dòng (kể cả đăng nhập, vốn không thuộc dự án); người chỉ có quyền ở vài dự án chỉ thấy dòng của các dự án đó và không chọn được dự án khác; deployment, khách hàng không thấy gì. Chuỗi lọc chỉ giữ chữ-số và vài ký tự an toàn, truy vấn hoàn toàn bằng tham số. Dashboard `/Admin` lấy số liệu thật từ bảng portal: tài khoản nội bộ/khách (hoạt động, bị khóa), từng dự án (khách, nhân viên gắn dự án, số lần bị từ chối 7 ngày), tổng số thao tác bị từ chối 7 ngày, đăng nhập sai 24 giờ (chỉ người xem được mọi dòng audit), 8 hoạt động gần nhất; mỗi khối chỉ hiện khi người xem có `users.read` / `audit.read` tương ứng. Chưa có chỉ số đồng bộ DEV→PROD hay request API vì chưa có dữ liệu thật cho chúng. Mốc thời gian so với `GETDATE()` của máy SQL, nên giờ máy ứng dụng và máy SQL nên cùng múi giờ.

**Form editor, gói DEV→PROD, nhật ký đồng bộ** (`Business/FormDefinitionValidator.cs`, `FormPackage.cs`, `FormPackageApplier.cs`; `WebPortal/ControlPlane/FormManagementService.cs`; trang `Admin/Forms/*`, `Packages`, `Sync`; `Cli apply-package`; test `FormDeliveryTests`, `FormManagementTests`):

- **Soạn và kiểm.** Mỗi lần lưu là một revision bất biến trong `portalFormRevision` (không sửa tại chỗ). Lưu hợp lệ → `validated`; còn lỗi → `draft` (lưu để soạn tiếp, không publish được). Bộ kiểm dùng chung portal và CLI: tên file `{động từ}{Resource}` ≤ 32 ký tự; XML tối đa 256 KB, **cấm DTD/entity ngoài** (không SYSTEM/PUBLIC, `XmlResolver` null, giới hạn bung entity 64 KB) nhưng vẫn dùng được entity nội bộ như `&And;`; luật nghiệp vụ của `ImportFormLoader` (khối, field, check, paging...); rà SQL trong `<query>` chặn lệnh cấp máy chủ (`xp_*`, `sp_OA*`, `OPENROWSET`, `BULK INSERT`, `GRANT`, `DROP/TRUNCATE` bảng thật, tạo/sửa đối tượng CSDL...) và cảnh báo SQL động. Đây là lưới an toàn, không thay thế việc người publish đọc SQL: SQL động và hàm hệ thống không chặn được hết; vẫn phải để tài khoản DB của Api ít quyền.
- **Xung đột.** Khóa lạc quan: lưu phải kèm revision gốc đúng bằng revision lớn nhất hiện có (transaction `SERIALIZABLE`, khóa `UPDLOCK`), lệch thì `CONFLICT` và không ghi gì; giao diện giữ nguyên chữ người dùng đã gõ. Hai tên form chỉ khác chữ hoa/thường bị từ chối (cùng một file trên Windows).
- **Quyền.** Chỉ **nhân viên** xem được nội dung form, kể cả khi vai trò partner có `forms.read`. `forms.read` xem, `forms.edit` lưu/kiểm, `forms.publish` publish + tạo/tải gói + nhập kết quả áp. Seed hiện chỉ Administrator có `forms.publish` (người soạn Deployment không tự publish được).
- **Publish.** Chỉ revision `validated`; kiểm lại ngay lúc publish và bắt trùng route với form đang hiệu lực khác. Form đang hiệu lực = revision publish lớn nhất; quay về bản cũ = lưu nội dung cũ thành revision mới rồi publish (lịch sử còn nguyên).
- **Gói.** Tạo từ mọi form đang hiệu lực của dự án: `manifest.json` (SHA-256 từng form) + `forms/{tên}.xml` trong một file zip. Manifest lưu nguyên văn trong DB; khi tải, gói dựng lại và kiểm toàn vẹn trước khi giao. Màn hình Gói hiện SHA-256 manifest để người mang gói đối chiếu **ngoài băng tần**. Gói chỉ có băm, không có chữ ký số: SHA-256 chỉ bảo vệ khi người áp nhập giá trị lấy từ portal chứ không từ chính file gói.
- **Áp gói** (không qua portal): `Fast.Api.Cli apply-package --package <zip> --target <App_Data\Form> --manifest-sha256 <hex> --environment DEV|PROD --operator <tên> [--apply]`. Mặc định chỉ kiểm thử. Đọc gói rất chặt (tên mục, kích thước, zip bomb, thừa/thiếu mục), kiểm lại từng form, **một form hỏng thì không ghi gì**. Mỗi file ghi qua file tạm `.tmp` rồi thay nguyên tử (FormReloader không bao giờ thấy file ghi dở); bản cũ sao lưu vào `_backup\<thời điểm>`. Lỗi giữa chừng được báo rõ file nào đã áp. Luôn xuất file kết quả JSON (không chứa nội dung form).
- **Nhật ký đồng bộ.** `EXPORT` tự ghi khi tạo gói. `VERIFY` (chạy thử) và `APPLY` ghi khi nhập file kết quả ở màn hình Đồng bộ; portal chỉ nhận khi gói thuộc dự án và SHA-256 manifest khớp bản đã lưu. Tên người thao tác trong file do CLI tự khai nên chỉ là ghi nhận; người nhập kết quả mới là người chịu trách nhiệm trong `portalAudit`.
- **Chưa làm:** tô màu cú pháp XML, gộp (merge) tự động khi xung đột, tách người soạn / người duyệt bắt buộc khác nhau, chữ ký số cho gói, gói chọn từng form, tự động đẩy gói sang môi trường (hiện luôn thủ công qua file), phân biệt môi trường đích khi ghi nhận ngoài việc người dùng khai `--environment`.

**Rà soát bảo mật (portal /Admin, form editor, gói, `Cli apply-package`)** — các lỗi tìm thấy và đã sửa, mỗi lỗi có test hồi quy ở `Tests/PortalSecurityReviewTests.cs` (trừ khi ghi khác):

| # | Lỗi | Sửa |
|---|---|---|
| 1 | Một người sai mật khẩu 5 lần là khóa được tài khoản của **người khác** (DoS đăng nhập, nhất là administrator trung tâm) | Khóa theo cặp (tên, IP); khóa toàn cục theo tên chỉ khi tổng lần sai từ mọi IP vượt 30 |
| 2 | Mỗi lần đăng nhập tốn một lần băm PBKDF2, dùng làm đòn bẩy ăn CPU | Rate limit 20 request/phút/IP cho trang đăng nhập |
| 3 | Đổi mật khẩu hoặc khóa tài khoản không cắt phiên đang mở (cookie bị đánh cắp sống tới 8 giờ) | Dấu vết mật khẩu trong cookie + kiểm tài khoản ở MỌI request; đổi mật khẩu thì đăng xuất |
| 4 | Phiên bị đánh cắp có thể dò mật khẩu hiện tại ở trang đổi mật khẩu | Khóa sau 5 lần sai (15 phút), chặn trước khi băm |
| 5 | Kiểm "còn administrator trung tâm cuối cùng" rồi mới ghi: hai người cùng khóa / gỡ nhau cùng lúc có thể vượt qua và để hệ thống không còn ai quản trị | Kiểm và ghi trong CÙNG một transaction `SERIALIZABLE` có khóa (chưa kiểm được bằng test vì cần SQL Server thật) |
| 6 | Người quản trị một dự án gắn khách này vào tài khoản API của khách khác trong dự án | Mỗi `project_user_name` chỉ thuộc một tài khoản portal (chỉ mục lọc `sql/portal/004`) |
| 7 | Tên đăng nhập có ký tự xuống dòng/điều khiển ghi thẳng vào `portalAudit`; chuỗi từ file kết quả CLI ghi thẳng vào nhật ký đồng bộ | Loại ký tự điều khiển ở cả service lẫn lớp ghi SQL |
| 8 | `Cli apply-package --apply` gõ nhầm `--target` thì âm thầm tạo thư mục mới và "áp thành công" | Từ chối (`TARGET_NOT_FOUND`); chạy thử vẫn dùng được |
| 9 | Đọc gói tin vào kích thước khai trong header zip; header nói dối có thể phình bộ nhớ | Cộng theo số byte thực đã đọc (kiểm bằng test kích thước nhiều mục) |
| 10 | Cookie phiên / antiforgery / tempdata chưa ràng buộc chặt | Ngoài Development: `Secure`, `HttpOnly`, `SameSite=Strict`, tiền tố `__Host-`; thêm `Permissions-Policy` |
| 11 | Thân request không giới hạn ở các form POST (nhất là nhập file kết quả) | `RequestSizeLimit` 1 MB mặc định, 128 KB ở màn hình Đồng bộ (**chưa kiểm bằng test** vì host thử không áp giới hạn này; kiểm khi chạy trên IIS/Kestrel thật) |

Đã xem xét và **không phải lỗi**: truy vấn đều bằng tham số (danh sách `IN` chỉ ghép số nguyên do server tính); XSS (Razor mã hóa mọi giá trị, CSP `script-src 'self'`); CSRF (antiforgery của Razor Pages cho mọi POST, GET không đổi dữ liệu); IDOR (mọi truy cập theo `projectId` đều kiểm quyền và gói/revision lọc theo dự án); open redirect (`returnUrl` chỉ nhận đường dẫn nội bộ dưới `/Admin`); zip slip (tên mục khớp mẫu cố định, tên file đích dựng từ tên form đã kiểm); XXE và entity bomb (test riêng ở `FormDeliveryTests`).

Rủi ro còn lại, cần biết: (a) rà SQL trong form chỉ là lưới an toàn, SQL động và hàm hệ thống không chặn hết, người publish vẫn phải đọc và tài khoản DB của Api phải ít quyền; (b) bộ đếm đăng nhập nằm trong bộ nhớ một tiến trình (chạy nhiều tiến trình/khởi động lại thì đếm lại, rate limit IP cũng theo từng tiến trình); (c) gói chỉ có băm, không có chữ ký số — an toàn chỉ khi người áp nhập SHA-256 lấy từ portal; (d) tên người thao tác trong file kết quả CLI do chính CLI khai, không xác thực; (e) nhập lại cùng một file kết quả sẽ sinh thêm một dòng nhật ký (chưa chống lặp); (f) địa chỉ Api dự án mới chỉ được kiểm khi lưu (https, không user:pass), khi sau này portal gọi tới đó phải chặn thêm IP nội bộ / loopback; (g) chưa chạy thử trên SQL Server thật nên các truy vấn SQL mới chưa được kiểm ngoài việc tránh lỗi cú pháp ở mức biên dịch.

**Kiểm trên SQL Server thật** (đã chạy; kết quả ghi lại để tái lập):

- Môi trường: SQL Server **2019 LocalDB** (`(localdb)\MSSQLLocalDB`), database dùng một lần `FastPortalTest` đặt `COMPATIBILITY_LEVEL = 100` (chế độ T-SQL của 2008). Đây KHÔNG phải SQL Server 2008 R2 thật: mức tương thích chỉ giúp từ chối một số cú pháp mới, chưa chứng minh chạy được trên 2008 R2.
- `sql/portal/001-004` chạy qua `sqlcmd -b` hai lần liên tiếp, đều thoát 0 (chạy lại được); seed đúng 26 dòng quyền (administrator 19, deployment 6, partner 1).
- **Lỗi tìm thấy khi chạy thật** (không thấy được bằng test với kho giả): (1) `004` thất bại khi chạy bằng `sqlcmd` vì chỉ mục lọc cần `QUOTED_IDENTIFIER ON` mà `sqlcmd` mặc định OFF → script tự `SET QUOTED_IDENTIFIER ON` (ghi tay vào `portalMembership` bằng `sqlcmd` thì phải thêm `-I`); (2) các dòng audit khóa/mở tài khoản không gắn dự án nên người chỉ có `audit.read` ở dự án không thấy → nay ghi một dòng cho mỗi dự án của tài khoản đích.
- Test tích hợp (`Tests/SqlServerStoreTests.cs`, `SqlServerPortalSmokeTests.cs`) tự bỏ qua trừ khi đặt biến môi trường `FAST_PORTAL_SQL` trỏ tới DB có tên bắt đầu bằng `FastPortalTest` (chặn chạy nhầm lên DB thật). Chạy: `FAST_PORTAL_SQL="Server=(localdb)\MSSQLLocalDB;Database=FastPortalTest;Integrated Security=true;TrustServerCertificate=true" dotnet test Tests -c Release`. Khi có DB: 513/513 qua; không có DB: 503 qua, 10 bỏ qua.
- Đã kiểm: seed và quyền; dự án (trùng mã không phân biệt hoa thường); tạo tài khoản + membership trong một transaction (trùng tên, vai trò sai, trùng tài khoản API đều hoàn tác, không để tài khoản mồ côi); tìm kiếm với `%`, `_`, `[`, chuỗi giống SQL injection; phân trang và lọc theo phạm vi dự án; audit (lọc, phạm vi, bỏ ký tự điều khiển, đếm); revision form (10 người cùng lưu từ revision gốc: đúng một người thắng); publish có điều kiện; gói (build lỗi thì hoàn tác, không để dòng gói); nhật ký đồng bộ.
- **Khóa nguyên tử administrator trung tâm cuối cùng:** 25 vòng x 3 lần chạy, mỗi vòng hai luồng cùng khóa nhau (và cùng gỡ membership của nhau); mỗi vòng đúng một luồng thành công, luôn còn ít nhất một administrator, không có deadlock. Chưa thử tải cao hơn hay nhiều tiến trình Api.
- Chạy thật cả vòng đời portal trên DB thật (Razor Pages + cookie + antiforgery + store SQL): đăng nhập, dashboard, dự án, tạo / khóa khách, soạn - publish form, tạo - tải gói, áp gói và nhập kết quả, xem audit.
- Chạy binary `Fast.Api.Cli` thật: `create-portal-account` (kiểm thử không ghi; tạo staff và khách; trùng tên; sai quy tắc; mật khẩu ngắn; không để tài khoản mồ côi; băm `v1.310000...`) và `apply-package` (sai SHA-256, thư mục đích không tồn tại, chạy thử, áp thật, áp lại thấy `SAME`, file kết quả JSON).
- **Kiểm lại trên SQL Server 2008 R2 thật** (10.50.1600.1 RTM, Developer, instance `SQL2008`, database `FSD_API`, đăng nhập `db_owner`; database trống khi bắt đầu): `001-004` chạy hai lần đều thoát 0 và 10/10 test tích hợp qua (kể cả khóa nguyên tử 25 vòng, không deadlock, và vòng đời portal đầy đủ). Test ghi dữ liệu nên chạy với `FAST_PORTAL_SQL_DISPOSABLE=1` (DB tên khác `FastPortalTest`), sau đó dữ liệu do test tạo đã được xóa và reseed identity: `FSD_API` hiện chỉ có cấu trúc + seed (3 vai trò, 26 quyền), chưa có dự án hay tài khoản nào. Kết nối tới instance này đàm phán **TLS 1.0** (SQL 2008 R2 chưa vá không hỗ trợ TLS 1.2): dùng trong mạng nội bộ đáng tin, nên vá hoặc nâng cấp trước khi đưa ra môi trường thật.
- Dọn dữ liệu thủ công bằng `sqlcmd` phải thêm `-I` (chỉ mục lọc trên `portalMembership` cần `QUOTED_IDENTIFIER ON`).
- Chưa kiểm: nhiều tiến trình Api cùng trỏ một DB, tải cao hơn, IIS (giới hạn kích thước request).

**Cài đặt DB portal:** tạo database riêng, chạy lần lượt `sql/portal/001_portalDb.sql`, `002_portalCustomize.sql`, `003_portalSeed.sql`, `004_portalMembershipApiUser.sql`. Khai `ControlPlane:ConnectionString` (user-secrets hoặc biến môi trường `ControlPlane__ConnectionString`, không ghi vào file cấu hình trong repo). Tạo Administrator đầu tiên và dự án:

```
INSERT portalProject (code, name, api_base_url) VALUES ('FSD_DEV', N'FSD Dev', 'https://...')
Fast.Api.Cli create-portal-account --config <json> --login admin --name "Quản trị" --kind staff --role administrator --password-env PORTAL_PWD --apply
```

Môi trường thử có thể dùng `--allow-weak-password` (mật khẩu tối thiểu 6 ký tự thay vì 12): CLI in cảnh báo và ghi "cho phép mật khẩu ngắn" vào `portalAudit`; trang Đổi mật khẩu của portal luôn đòi ≥ 12 ký tự.

**Hạn chế đã biết:** khóa đăng nhập giữ trong bộ nhớ một tiến trình (khởi động lại hoặc chạy nhiều tiến trình thì đếm lại); chưa có quản lý tài khoản/membership trên giao diện (chỉ CLI); chưa có đổi mật khẩu, thoát phiên từ xa, rate limit theo IP; thời hạn cookie cố định 8 giờ không gia hạn; chưa chạy thử trên SQL Server thật và chưa có sidebar theo quyền ngoài mục Tổng quan.

**Chưa làm:** client gọi Api dự án (bước 3, cần quyết định lại cách xác thực sau khi đã bỏ agent HMAC cho FBO), đặt lại mật khẩu cho người khác (hiện chỉ người dùng tự đổi; quên mật khẩu thì administrator tạo lại tài khoản hoặc chạy CLI), xuất audit ra file, form editor + package DEV→PROD, nhật ký đồng bộ, dashboard tổng quan (mục 5–7).

# ADR-0001: Portal nội bộ /cpanel + /{mã dự án}, bỏ CLI, PortalApp có license cho PROD

- Trạng thái: Chấp nhận
- Ngày: 2026-10-24
- Khách/Phạm vi: toàn bộ (RestApiCore)
- Thay thế: quyết định "portal gọi Api từng dự án, không giữ connection string" trong `docs/PORTAL_MULTI_PROJECT.md`

## Bối cảnh

Bản trước của portal giữ DB riêng và chỉ nói chuyện với dự án qua `/api/_portal/*` (agent HMAC). Phần agent chưa làm được, nên portal không cấp được tài khoản cho dự án, không xem được log và không sửa trực tiếp được. Portal và SQL các dự án DEV/UAT đều nằm trong mạng nội bộ; danh sách dự án đã có sẵn ở QLDA (`nbdmda`). PROD đặt tại máy khách, không kết nối về portal. Người vận hành gồm cả BA, nên CLI là rào cản.

## Quyết định

Chúng tôi sẽ:

- **Đổi tên project:** `Api` → `WebApi` (chỉ còn API), `ApiDocs` → `WebPortal`, `FboWeb` → `WebProject`, `Gui` → `PortalApp`. Bỏ hẳn project `Cli`; logic chuyển vào `Business`.
- **WebPortal (DEV/UAT):**
  - `/cpanel` dành cho đúng một Super Admin (SA). `/{mã dự án}` dành cho Admin của dự án và user Api, với mã dự án phải có `nbdmda.status = 1`.
  - Admin dự án nằm ngoài mapping FBO và không có quyền gọi api.
  - User Api và quyền form/IP lưu ở các bảng gọn trong PortalDb, rồi đồng bộ một chiều portal → dự án.
  - Portal đọc connection string từ `appsettings.Local.json` trong folder restApi của dự án (`portalProject.api_folder` do SA/Admin khai). Chỉ chạm DB dự án khi đồng bộ hoặc khi xem form, mã lỗi, token, private key, log.
- **Quyền form và IP:** có khai báo thì chỉ đúng danh sách đã khai; danh sách rỗng là được tất cả.
- **Phiên đăng nhập:** mỗi tài khoản portal chỉ có một phiên hiệu lực. Đăng nhập nơi khác phải xác nhận; phiên cũ bị thu hồi và được báo lý do.
- **PortalApp (PROD):**
  - Đặt ngay trong folder restApi, đọc connection string local, không có đăng nhập; bảo vệ bằng ACL Windows của folder.
  - Chỉ chạy khi có license ECDSA P-256 gắn mã dự án + MAC, do WebPortal ký. WebApi kiểm cùng license, mặc định chỉ cảnh báo.
  - Nhị phân phát hành được làm rối bằng Obfuscar (miễn phí) và kèm manifest SHA-256 có ký để tự kiểm toàn vẹn.

## Hệ quả

- **Được:**
  - Portal làm đủ việc cấp tài khoản, cấp quyền, xem log mà không cần agent.
  - PROD có công cụ giao diện cho người không biết code.
  - Một bản restApi không còn chép tùy ý sang máy khác được.
- **Mất:**
  - Portal nắm đường vào DB của mọi dự án DEV/UAT, nên máy portal và PortalDb phải được bảo vệ như tài sản nhạy cảm.
  - Sửa thẳng quyền ở DB dự án sẽ bị ghi đè ở lần đồng bộ sau.
  - License và obfuscation chỉ nâng rào, không chống được người đủ trình.
- **Ràng buộc:**
  - App Pool của WebPortal cần quyền đọc các folder restApi.
  - Private key license phải nằm trong Certificate Store hoặc PFX ngoài repo.
  - Ở PROD, PortalApp ghi thẳng DB dự án: đây là ngoại lệ có chủ đích với nguyên tắc "portal là nguồn sự thật".
- `/api/_portal/*`, `POST /api/auth/portal`, `/_meta/resources*`, JWT `scope=portal` và `sql/008` (phần vai trò) bị gỡ.

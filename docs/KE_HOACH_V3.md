# Kế hoạch FastAPI v3 (RestApiCore) — đề xuất, chờ duyệt

**Cập nhật:** 23/10/2026 · v3 = repo này (`huunguyenit/RestApiCore`), phát triển tiếp, không dựng mới. v2 = FastAPI 2.4.7 (VB, `D:\Fast Source\API\FastAPI 2.4.7`).

## 0. Quyết định đã chốt

| Hạng mục | Quyết định |
|---|---|
| Chiến lược | Hai luồng tách hẳn. v2 đóng băng, chỉ sửa cho đối tác cũ. Đối tác mới làm trên v3. Bỏ dần v2 |
| Nền | **ASP.NET Core Web API trên IIS**, C#. **Nâng từ .NET 8 lên .NET 10 LTS**: .NET 8 hết hỗ trợ ngày 10/11/2026 |
| Nguồn dữ liệu | **SQL Server là nguồn dữ liệu chính** (bản thấp nhất ở khách: 2008 R2) |
| Triển khai | **Windows-first, zero-dependency**: IIS + Hosting Bundle + SQL Server. Không cài thêm dịch vụ cache/queue/log bên ngoài |
| Redis / Garnet | **Không dùng** (không bắt buộc, không tùy chọn, không pha sau). Mọi trạng thái: `HybridCache` / Rate Limiting in-process, hoặc bảng SQL, hoặc file trên ổ Windows |
| Cache | `Microsoft.Extensions.Caching.Hybrid` (memory-only) |
| Rate limit | ASP.NET Core Rate Limiting (in-process), partition theo `u_id` đã xác thực |
| Logger | Serilog rolling file (thư mục dữ liệu độc lập, vd `ProgramData\...\Logs`), không ELK/Grafana bắt buộc |
| Chia tải | **Một instance** sau IIS là baseline. N instance / ARR **ngoài phạm vi**; nếu sau này cần thì dùng SQL làm kho dùng chung, không Redis |
| DB | Object `restApi*` đang có giữ nguyên, **trừ** thêm cột `restApiUser.private_key` (3.1). Object viết mới cũng đặt tên `restApi*` (bảng, view) và `restApi_*` (proc, function). Không dùng chung bảng với v2 |
| Người dùng | `restApiUser.name` là khóa chính, `restApiUser.id` là identity (= `u_id`). Nối FBO qua **`restApiUser.name_erp = userinfo2.name`**. `restApiUser.name` **khác** `userinfo2.name`; `restApiUser.id` **khác** `userinfo2.id`. `user_id` (kiểm quyền FBO) = `sysdb..userinfo2.id` |
| XML | Gốc là `<import>` của v2, mở rộng cho v3. Không có `<response>`: dữ liệu trả về do backend quyết định qua dataset. Không có chế độ chạy file v2, không có thuộc tính `version` |
| JSON | **Chỉ dạng object**: `{ "master": { … }, "detail": [ … ] }`, khối gốc là **một object**, khối con là mảng. Bỏ dạng flat |
| Tên file | **Giữ cách đặt tên v2**: `{động từ}{Resource}.xml` (`getCustomer.xml`, `setCustomer.xml`, `cancelInvoice.xml`) |
| Method | **Nghiệp vụ chỉ dùng `POST`.** `set` → `POST /api/v3/{resource}`. Động từ khác, kể cả `get` → `POST /api/v3/{resource}/{động từ}`. Không dùng HTTP `GET` cho `get{Resource}.xml` |
| Kiểm tra field | `check`/`condition`/`tag`/`errorCode`/`category` **giống Upload FBO**. Bỏ `<reference>`, `Update.xml`, `@@update(n)` |
| Body | `application/json`, là **JSON object dữ liệu**. Tham số điều khiển (`unitCode`, `dryRun`, `cursor`, `page`, `Idempotency-Key`) đi bằng query string / header |
| Bảo mật thông điệp | **SHA-256** cho checksum body, **HMAC-SHA256** xác thực **hai chiều** (request do client ký, response do server ký), khóa bí mật lưu ở `restApiUser.private_key`. Bắt buộc với mọi request, cả HTTP lẫn HTTPS. Đăng nhập trả **JWT** (mục 4.2) |
| Bí mật nội dung | SHA-256/HMAC **không mã hóa**. **HTTPS/TLS lo phần mã hóa body**. Không có lớp mã hóa body riêng (bỏ `encryptData`), API không dùng khóa bất đối xứng |
| `unitCode` | Query string `?unitCode=` (được ký). Form khai `unitRequired="true"` thì bắt buộc. Kiểm quyền đơn vị bằng hàm FBO có sẵn |
| Truyền tệp | **Ngoài giai đoạn này** (Phụ lục A) |
| Cổng đối tác | Phát triển từ project `ApiDocs`. **Dùng luôn tài khoản `restApiUser`** (không bảng/cờ riêng): đăng nhập bằng `name` + `password`. Mật khẩu **mã hóa tại client** bằng khóa công khai RSA của server, server giải bằng khóa riêng rồi kiểm (5.3). `private_key` chỉ lo phần API |
| Cấu trúc code | Giữ `Api` / `Business` / `DataAccess` / `Models` / `Security` / `Provider`. Provider ADO.NET giữ, không bắt buộc Dapper |

---

## 1. Hiện trạng đã kiểm

### 1.1. Code RestApiCore

| Thành phần | Hiện trạng | Hướng xử lý |
|---|---|---|
| `Api` | `AuthController`, `QueryController` (`POST api/query/{name}`), middleware log và rate limit, xác thực JWT tự viết | Giữ khung, đổi route (mục 4). Bổ sung Serilog, HybridCache, RateLimiter tích hợp, Health Checks |
| `Business` | `FormManager`, `FormMethodBase`/`FormMethodCache`, `ResponseManager` (phân trang), `AuthManager`, `LogManager` | Giữ cách chia lớp. Viết lại phân trang và log |
| `DataAccess` | `FormRepository` (đọc XML `<form>`, dựng `#struct`, sinh script reference/check, chạy Checking rồi Processing), `UserRepository`, `CacheRepository` (file JSON + `restApiCache`), `LogRepository` | Đổi bộ đọc XML sang `<import>`. User theo `name_erp` → `userinfo2`. Cache danh mục/quyền sang HybridCache; revoke/idempotency trên SQL |
| `Models` | `Form`, `Struct`, `Field`, `FieldReference`, `FieldCheck`, `Message`, `Update` | Giữ, bổ sung `Paging`, `UnitRequired`, `Verb`. `FieldCheck` đổi sang 4 danh sách song song kiểu Upload. Bỏ `FieldReference`, `Update` |
| `App_Data/Options` | `Message.xml` (mã lỗi ↔ HTTP status), `Update.xml` (script cho `reference`), `Irregular.txt` | Giữ `Message.xml`, `Irregular.txt`. `Update.xml` không dùng nữa |
| `Security` | JWT HS256 (audience = IP), `GenerateAuthKey` (MD5 + `positions` như v2), AES cho mật khẩu | Giữ JWT. Thay MD5 bằng SHA-256 checksum + HMAC-SHA256 hai chiều (4.2). AES dùng để mã hóa `private_key` khi lưu |
| `ApiDocs` | Khung Razor Pages, dữ liệu giả | Thành cổng đối tác |
| `CallbackWorker` | Worker mẫu | Callback/notify, ngoài giai đoạn này |
| `Provider` | ADO.NET tĩnh, đồng bộ, `CommandTimeout` static dùng chung | Chuyển async, timeout theo cấu hình |

### 1.2. DB dev (đã truy vấn thật)

| Kiểm | Kết quả | Ảnh hưởng tới thiết kế |
|---|---|---|
| Phiên bản | **SQL Server 2008 R2 RTM** (10.50.1600), Developer Edition | Xem 1.3 |
| `FSD_Dev_SP229_S..userinfo2` | `id int`, `name char(30)`, `admin tinyint`, `status char(1)`, `password varchar(256)`, cột audit… | `user_id` = `id`. `@@userName` = `RTRIM(name)` |
| Quyền đơn vị | `sysunitrights(user_id, ma_dvcs char(8), r_new, r_edit, r_del, r_access)`. Có sẵn **`dbo.ff_CheckUnitRight(@nUserID, @cMa_dvcs, @cAction)`**, `@cAction` ∈ `New`/`Edit`/`Del`/`Acc` | `restApi_CheckUnitRights` gọi lại hàm này |
| Danh mục đơn vị | `dmdvcs.ma_dvcs char(8)` | `unitCode` tối đa 8 ký tự, phải có trong `dmdvcs` |
| Object `restApi*` đang có | Bảng `restApiUser`, `restApiAllowForm(u_id, form)`, `restApiAllowIp(u_id, ip_address, description)`, `restApiBlackListIp`, `restApiCache`, `restApiLogData`, `restApiLogError`, `restApiLogActivity`, `restApiLogTransfer`, `restApiNotify`, `restApiTransfer`, `restApiUpdate`. Proc `restApi_CheckFormRights`, `restApi_CheckIPAddress`, `restApi_InsertLogData`, `restApi_InsertLogError`, `restApi_RemoveCache`, `restApi_TableDebug` | Cách v3 dùng ở mục 3.1 |

Hai điểm trong logic quyền hiện tại cần đổi ở v3:
- `restApi_CheckFormRights` và `restApi_CheckIPAddress`: user **không có dòng nào** trong `restApiAllowForm`/`restApiAllowIp` thì được **mọi form / mọi IP**. v3 đổi sang **mặc định từ chối** với form. Riêng IP vẫn giữ cách "không khai thì cho mọi IP", vì nhiều đối tác không có IP tĩnh.
- `fsd_GetUnitRight` có nhánh `@Admin = 0` khi truyền `@Unit` thì trả nguyên danh sách đơn vị mà **không kiểm quyền**. v3 không dùng hàm này.

### 1.3. SQL Server 2008 R2 — giới hạn phải tuân theo
Mọi SQL `restApi*` và câu lệnh v3 tự sinh phải chạy được trên 2008 R2: **không dùng** `OFFSET/FETCH`, `THROW`, `IIF`, `CONCAT`, `FORMAT`, `TRY_CONVERT`, `STRING_SPLIT`, hàm JSON, `SEQUENCE`. Phân trang dùng `ROW_NUMBER()`, tách chuỗi dùng hàm sẵn có của FBO (vd `fsd_StringToTable`).

Đã thử (pha 0, 21/10/2026): .NET 10 + `Microsoft.Data.SqlClient` 6.1.7 kết nối **được** tới 10.50.1600, cả `Encrypt=True` lẫn `False`, nhưng **phải hạ xuống TLS 1.0**. Checklist deploy cho SQL 2008/2008 R2: cài bản vá TLS 1.2 cho SQL Server (2008 R2 SP3 + bản cập nhật TLS 1.2), hoặc máy chạy API còn bật TLS 1.0 phía client.

### 1.4. Lỗi cần sửa trước khi đưa cho đối tác

| # | Mức độ | Vấn đề | Vị trí | Sửa ở pha |
|---|---|---|---|---|
| 1 | **Nghiêm trọng** | Connection string và `secretKey` JWT đã commit lên GitHub | `Api/appsettings.json` | 0 (xong phần code; còn đổi mật khẩu) |
| 2 | **Nghiêm trọng** | SQL injection: header `unit` ghép thẳng vào SQL | `FormRepository.ReplaceDefaultVaraible` | 3 |
| 3 | **Nghiêm trọng** | SQL injection: `path`, `ip`, `userName` ghép chuỗi vào `#logData`/`#logError` | `LogRepository` | 1 (xong) |
| 4 | Cao | Request sửa `form.Queries`, `field.MaxLength`/`Type` của form đang cache dùng chung → request đồng thời ghi đè nhau | `FormRepository`, `FormMethodBase` | 3 |
| 5 | Cao | `@@update({i})` thay theo số thứ tự vòng lặp, không theo `index`. v3 bỏ `@@update`; `@@checking(n)` phải tránh lại lỗi này | `FormRepository` | 3 |
| 6 | Trung bình | Log ghi SQL đồng bộ trong middleware, đệm cả response | `LogMiddleware` | 5 |
| 7 | Trung bình | Rate limit, chống replay đăng nhập trong RAM, đọc rồi ghi không nguyên tử; mỗi request gọi proc kiểm IP | `RateLimitManager`, `AuthController` | 1, 2 |
| 8 | Thấp | `Api/Log/*.json` đang được commit | `.gitignore` | 0 (xong) |
| 9 | Thấp | Checksum body là MD5 không khóa | `QueryController` | 2 |

---

## 2. Định nghĩa API bằng XML (`<import>` mở rộng)

### 2.1. Ví dụ `App_Data/Form/setSaleInvoice.xml`
```xml
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE import [
  <!ENTITY And "&#38;#13;">
  <!ENTITY IrregularValue SYSTEM "..\Options\Irregular.txt">
]>

<import xmlns="urn:schemas-fast-com:fast-api"
        type="Voucher" voucherCode="HDA" unitRequired="true"
        description="Hóa đơn bán hàng">

  <fields name="master" table="m81$000000" key="YourID">
    <field name="YourID"      fastName="id_doitac" type="string" maxLength="36" allowNull="false" isPrimaryKey="true"/>
    <field name="VoucherDate" fastName="ngay_ct"   type="datetime" format="dd/MM/yyyy" allowNull="false"/>
    <field name="CustomerID"  fastName="ma_kh" allowNull="false" upperCase="true" category="1"
           check="ma_kh not in (select ma_kh from dmkh where status = '1')"
           errorCode="CUSTOMER_NOT_FOUND"/>
    <field name="SaleEmployee" fastName="ma_nvbh" checkIrregular="true"/>
  </fields>

  <fields name="detail" table="d81$000000">
    <field name="ItemID"    fastName="ma_vt" allowNull="false" upperCase="true" category="2"
           check="ma_vt not in (select ma_vt from dmvt where status = '1')"
           errorCode="ITEM_NOT_FOUND"/>
    <field name="Promotion" fastName="km_yn" type="int" defaultValue="0" category="2"
           check="km_yn not in (0, 1, 2, 3)" errorCode="INVALID_FIELD"/>
    <field name="Quantity"  fastName="so_luong" type="numeric"/>
  </fields>

  <query action="checking"><text><![CDATA[ @@checking(1) @@checking(2) ]]></text></query>
  <query action="processing"><text><![CDATA[ exec restApi_ProcessDataFor_m81 @@voucherCode, @@unit, @@userId ]]></text></query>
</import>
```
Entity `And` **phải** khai `"&#38;#13;"`. Khai `"&#13;"` thì ký tự CR bị chuẩn hóa thành dấu cách trong giá trị thuộc tính (quy tắc chuẩn hóa thuộc tính của XML), danh sách `check` không tách được (đã thử 23/10/2026). Entity `SYSTEM` (vd `IrregularValue`) chỉ dùng được trong nội dung phần tử, XML cấm dùng trong thuộc tính.

Khối `detail` không cần khai `YourID`: khóa `key` của khối gốc được tự thêm vào mọi khối con, để query nối `#master` với `#detail`.

Đối tác gửi **mã FBO** (`ma_kh`, `ma_vt`) như khi Upload Excel; API chỉ kiểm mã tồn tại. Đối tác bắt buộc gửi mã của họ (`id_doitac`) thì khai cột riêng và tự tra ra mã FBO trong query `checking`, trước `@@checking(n)`.

### 2.2. Thuộc tính

| Phần tử | Thuộc tính | Ý nghĩa |
|---|---|---|
| `import` | `type` | `List` / `Voucher` / `Report` / `Transfer` |
| | `voucherCode` | Mã chứng từ |
| | `unitRequired` | `true` → bắt buộc `?unitCode=`, thiếu thì `UNIT_REQUIRED`. Mặc định `false`. Có gửi `unitCode` thì luôn bị kiểm quyền |
| | `unitAction` | Quyền đơn vị cần kiểm: `Acc`/`New`/`Edit`/`Del`. Không khai thì suy từ động từ (3.2) |
| | `enable`, `description` | Bật/tắt, mô tả trên portal |
| `fields` | `name` | Tên khối = tên thuộc tính JSON = bảng tạm `#<name>`. Khối đầu tiên là **khối gốc** |
| | `table` | Bảng FBO thật. Có khai thì kiểu và độ rộng lấy từ cột thật (2.3) |
| | `key` | Chỉ khai ở khối gốc. Khóa nối khối gốc với khối con |
| `field` | `name`, `fastName`, `type`, `maxLength`, `allowNull`, `isPrimaryKey`, `upperCase`, `hidden`, `defaultValue`, `justContent`, `checkIrregular` | Giữ nguyên tên thuộc tính của v2 |
| | `type` | `string`, `numeric`, `datetime`, `bool`, `int`, `unixtime` |
| | `format` | Định dạng ngày nhận vào. Luôn nhận thêm ISO 8601 |
| | `description`, `example` | Hiện trên portal |
| | `check`, `condition`, `tag`, `errorCode` | **Như Upload FBO**: 4 danh sách song song ngăn bằng `&And;`. `check` là mệnh đề SQL trên `#<khối>`, **đúng = lỗi**. `condition`/`tag` là SQL mở/đóng bọc quanh mệnh đề cùng vị trí. `errorCode` là mã trong `Message.xml`. Số phần tử 4 danh sách phải bằng nhau, lệch thì lỗi lúc nạp XML |
| | `category` | `@@checking(N)` chỉ chạy field có `category="N"`; `@@checking` không số chạy mọi field. Đã dùng `@@checking(N)` mà field không khai `category` thì nạp XML cảnh báo |
| `query` | `action` | `checking` (có lỗi thì dừng) rồi `processing`. Không khai `action` = `processing` |
| `paging` | `pageSize`, `key` | Mục 4.4 |

### 2.3. Kiểu dữ liệu và độ rộng

| Kiểu XML | Cột bảng tạm khi khối có `table` | Khi khối không có `table` |
|---|---|---|
| `string` | Đúng kiểu cột thật | `NVARCHAR(maxLength)`, không có thì `NVARCHAR(MAX)` |
| `numeric` | Đúng `numeric(p,s)` của cột | `NUMERIC(30,12)` |
| `int` | `INT` | `INT` |
| `datetime`, `unixtime` | Đúng `datetime`/`smalldatetime` của cột | `DATETIME` |
| `bool` | `BIT` | `BIT` |

`maxLength` lớn hơn cột thật → cảnh báo khi nạp XML, kiểm theo cột thật. Cấu trúc cột đọc **một lần khi nạp XML**, file đổi thì nạp lại.

### 2.4. `@@checking` sinh ra gì
Mỗi bảng tạm `#<khối>` có thêm cột ẩn `_row` và `_parent`. Mỗi mệnh đề `check` thứ *n* được biên dịch **một lần lúc nạp XML** thành:
```sql
<condition[n]>
insert into #errors (block, row_no, parent_no, field, code)
  select 'detail', _row, _parent, 'ItemID', 'ITEM_NOT_FOUND' from #detail where <check[n]>
<tag[n]>
```
Hết query `checking` mà `#errors` có dòng thì dừng, trả **đủ** lỗi kèm vị trí `[0].detail[2].ItemID`. Mệnh đề tự loại giá trị rỗng như Upload (`ma_kh <> '' and ma_kh not in (…)`).

### 2.5. File XML v2
Không có chế độ chạy file v2. Tool `Fast.Api.Cli upgrade` hỗ trợ chuyển: giữ tên file, gợi ý tách khối gốc/khối con, đổi `<reference>` thành `check` tồn tại mã. Người xem lại rồi mới dùng.

### 2.6. Biến thay thế trong query
`@@form` (= tên file), `@@resource`, `@@verb`, `@@type`, `@@voucherCode`, `@@userId` (= `userinfo2.id`), `@@userName` (= `RTRIM(userinfo2.name)`), `@@admin`, `@@unit`, `@@appDatabaseName`, `@@sysDatabaseName`, `@@primaryKeyList`, `@@primaryKeyListAlias`, `@@checking`, `@@checking(n)`. Trong `check`, `@@table` = bảng tạm của khối chứa field.

- `@@unit` chỉ được thay **sau khi** `unitCode` đã khớp `dmdvcs` và qua kiểm quyền. Lớp phòng thứ hai: escape `'`.
- Form biên dịch **một lần lúc nạp XML** thành bản không đổi (sửa lỗi 1.4 #4, #5).

---

## 3. Cơ sở dữ liệu `restApi*`

**Quy tắc:** object `restApi*` đang có giữ nguyên cấu trúc, **trừ** thêm cột `restApiUser.private_key`. Object viết mới đặt tên `restApi*` / `restApi_*`. Không gọi hay sửa proc `api_*` của v2. Mọi script tương thích SQL Server 2008, để ở `sql/`, chạy lại không lỗi.

### 3.1. Object có sẵn — v3 dùng thế nào

| Object | v3 dùng |
|---|---|
| `restApiUser` | Tài khoản API. **`name` khóa chính** (tên đăng nhập API), **`id` identity** (= `u_id` trong các bảng `restApi*`). **`name_erp = userinfo2.name`** nối sang user FBO. `name` khác `userinfo2.name`, `id` khác `userinfo2.id`: hai bộ định danh độc lập, chỉ nối qua `name_erp`. Lúc đăng nhập tra ra `sysdb..userinfo2.id` làm `user_id` cho mọi kiểm quyền FBO. Script kiểm và sửa khóa chính/identity nếu DB khách khác |
| `restApiUser.private_key` (**cột mới**) | Khóa bí mật dùng chung giữa đối tác và server cho HMAC-SHA256 (4.2). 256 bit ngẫu nhiên do server sinh. **Lưu dạng mã hóa** (AES-256-GCM, khóa chủ `Security:KeyEncryptionKey` ở biến môi trường / `appsettings.Local.json`), không lưu rõ, không băm (HMAC cần khóa gốc). `nvarchar(512) null`; user chưa có khóa thì không gọi được v3 |
| `restApiUser.password` | **Mật khẩu đăng nhập portal** (5.3) và đăng nhập kiểu cũ của RestApiCore (`GenerateAuthKey`) tới khi pha 2 thay. Lưu AES giải ngược được (`EncryptionHelper.EncryptString`), từ 23/10/2026 bằng khóa riêng **`appSettings:passwordKey`** (32 ký tự, khác `secretKey`). FastAPI 2.4.7 không đọc bảng này (đã grep). Không dùng cho API v3 (API dùng `private_key`) |
| `restApiAllowForm(u_id, form varchar(32))` | Quyền theo tên file XML không đuôi, như v2: `getCustomer`, `setSaleInvoice`, `cancelInvoice`. Mỗi động từ là một quyền riêng |
| `restApiAllowIp`, `restApiBlackListIp` | Giữ nguyên ý nghĩa: `255` = wildcard; không khai IP thì cho mọi IP |
| `restApiLogError` + `restApi_InsertLogError` | Giữ, ghi qua tham số (sửa lỗi 1.4 #3) |
| `restApiLogData` + `restApi_InsertLogData` | Dùng tới pha 5, sau đó thay bằng bảng log mới |
| `restApiCache` + `restApi_RemoveCache` | v3 không dùng. Giữ nguyên, không xoá |
| `restApiLogActivity`, `restApiLogTransfer`, `restApiNotify`, `restApiTransfer`, `restApiUpdate` | Giữ nguyên, dùng khi tới chức năng tương ứng |
| `restApi_CheckFormRights`, `restApi_CheckIPAddress` | Giữ nguyên; v3 dùng `restApi_GetUserRights` thay thế (mặc định từ chối form, một lượt gọi DB) |

### 3.2. Object viết mới

| Object | Pha | Nội dung |
|---|---|---|
| `restApi_CheckUnitRights` (proc) | 0 (xong) | Kiểm `unitCode` theo quyền FBO (bên dưới) |
| `ALTER restApiUser ADD private_key` (script) | 1 (xong) | Thêm cột nếu chưa có; kiểm `name` là khóa chính, `id` identity |
| `restApi_GetUserRights` (proc) | 1 (xong) | Một lượt trả: thông tin user, `user_id`/`admin` từ `userinfo2` qua `name_erp`, `private_key` (mã hóa), form được phép (**không có dòng = không quyền**), IP cho phép/chặn |
| `restApiRevokedToken` (bảng) | 1 (xong) | JWT đã thu hồi: `jti`, `u_id`, `expires_at`. Dọn theo `expires_at` |
| `restApiIdempotency` (bảng) | 1 (xong) | `Idempotency-Key` + `u_id` + SHA256(bản rõ + query) + response, TTL 24 giờ |
| `restApiLogReq`, `restApiLogBody`, `restApiLogAgg`, `restApiLogAttack`, `restApiLogAttackAgg` | 5 | Log mới (5.2) |
| `restApi_LogSearch`, `restApi_LogCleanup` | 5 | Xem log; dọn theo lô |
| `restApiSetupLog` (bảng) | 7 | Script setup đã áp: `version`, `script`, `sha256`, `applied_at`, `applied_by`, `duration_ms`. Script đầu tiên của setup tạo bảng này; nhờ nó chạy lại hay nâng cấp chỉ áp script chưa có |

**`restApi_CheckUnitRights @user_id, @unit_code, @action`**
1. `@unit_code` phải có trong `dmdvcs` với `status = '1'`, không thì `UNIT_NOT_FOUND`.
2. `userinfo2.admin = 1` → được mọi đơn vị.
3. Còn lại → `dbo.ff_CheckUnitRight`. `@action` lấy từ `unitAction`; không khai thì theo động từ: `get` → `Acc`, `set` → `New`, `delete` → `Del`, động từ khác → `Edit`.

---

## 4. Thiết kế REST v3

### 4.1. Route

| Method | Đường dẫn | Việc |
|---|---|---|
| `POST` | `/api/v3/auth/token` | Lấy JWT |
| `POST` | `/api/v3/auth/revoke` | Thu hồi JWT |
| `POST` | `/api/v3/{resource}` | `set{Resource}.xml` |
| `POST` | `/api/v3/{resource}/{verb}` | `{verb}{Resource}.xml`: `getCustomer` → `/customer/get`, `deleteInvoice` → `/invoice/delete`, `cancelInvoice` → `/invoice/cancel` |
| `GET` | `/api/v3/_meta/server` | Giờ server (để client đồng bộ timestamp), phiên bản chữ ký, **khóa công khai RSA dùng cho đăng nhập portal** (JWK + `kid`). Không cần token |
| `POST` | `/api/v3/auth/portal` | Đăng nhập portal bằng `name` + mật khẩu đã mã hóa (5.3). Trả JWT phạm vi `portal`, chỉ gọi được `_meta` |
| `GET` | `/api/v3/_meta/resources` | Tài nguyên và động từ user được cấp quyền |
| `GET` | `/api/v3/_meta/resources/{form}` | Định nghĩa khối, field, mẫu JSON |
| `GET` | `/health/live`, `/health/ready` | Giám sát. `ready` kiểm SQL + thư mục log |

Tham số điều khiển:

| Tham số | Đi bằng | Ý nghĩa |
|---|---|---|
| `unitCode` | query | Đơn vị |
| `dryRun` | query | Chạy hết `checking`, không chạy `processing`, rollback |
| `cursor`, `page` | query | Phân trang (4.4) |
| `Idempotency-Key` | header | Chống ghi trùng (4.3) |

**Tách tên file thành động từ + resource** (lúc nạp XML):
- Động từ = đoạn chữ thường đầu tên file; **chấp nhận mọi tiền tố chữ thường** (`get`, `set`, `delete`, `cancel`, `post`, `approve`…), không có danh sách cố định. Resource = phần còn lại, PascalCase → kebab-case: `setSaleInvoice` → `sale-invoice`. Không tự bỏ số nhiều.
- `set` → `POST /{resource}`, động từ khác → `POST /{resource}/{verb}`. Không dùng `GET`/`DELETE` cho nghiệp vụ vì mọi động từ đều cần body.
- Hai file ra cùng route hoặc tên không tách được → lỗi lúc nạp, bỏ qua file, ghi log.
- URL không phân biệt hoa thường, chỉ nhận `[a-z0-9-]`. Sai method → `405` (`Allow: POST`). Không có resource/động từ → `404`.

### 4.2. Bảo mật thông điệp: SHA-256 + HMAC-SHA256 hai chiều

#### 4.2.1. Mục tiêu và giới hạn

| Yêu cầu | Cơ chế |
|---|---|
| **Toàn vẹn** request: body, query, tham số header không bị sửa | SHA-256 checksum body + HMAC-SHA256 trên chuỗi chuẩn hóa |
| **Xác thực** client: request đúng do đối tác gửi | HMAC-SHA256 với khóa chỉ đối tác và server biết (`private_key`) |
| **Xác thực** server: response đúng do server tạo, không bị sửa | HMAC-SHA256 trên response (chiều ngược lại) |
| **Chống phát lại** | Timestamp + nonce, nonce lưu HybridCache |

**Giới hạn phải ghi rõ với khách:**
- SHA-256 và HMAC **không mã hóa**. Ai bắt được gói tin trên HTTP vẫn đọc được dữ liệu. Bí mật nội dung do HTTPS/TLS đảm nhận; triển khai cho khách khuyến nghị bắt buộc HTTPS.
- HMAC dựa trên khóa dùng chung: cả client lẫn server đều tạo được HMAC hợp lệ, nên **không chống chối bỏ** như chữ ký số bất đối xứng. Log chứng minh được "request khớp khóa của đối tác", không chứng minh được với bên thứ ba rằng chỉ đối tác mới tạo được.

#### 4.2.2. Bốn loại checksum/chữ ký

| Thành phần | Công thức | Mục đích |
|---|---|---|
| Request checksum | `SHA256(rawBody)` | Tóm tắt chính xác byte body |
| Request signature | `HMAC-SHA256(K_request, canonicalRequest)` | Xác thực client và toàn vẹn request |
| Query/cache fingerprint | `SHA256(canonicalQuery)` | Cache key ổn định (cursor, idempotency) |
| Response signature | `HMAC-SHA256(K_response, canonicalResponse)` | Xác minh response do server tạo |

**Khóa.** Hai khóa dẫn xuất từ cùng `private_key`, tách mục đích để chữ ký request không dùng lại được làm chữ ký response:
```
K_request  = HMAC-SHA256(private_key, "FAST-API-V3-REQUEST")
K_response = HMAC-SHA256(private_key, "FAST-API-V3-RESPONSE")
```
- `private_key`: 32 byte ngẫu nhiên (`RandomNumberGenerator`), giao cho đối tác dạng base64 **một lần** qua kênh an toàn (không qua email rõ, không ghi log).
- Đổi khóa: sinh khóa mới, ghi đè cột, đối tác cập nhật cùng lúc (có cắt ngang ngắn). Đổi khóa không gián đoạn (hai khóa song song) **ngoài phạm vi đợt này**.

#### 4.2.3. Request

```
POST /api/v3/sale-invoice?unitCode=CTY&dryRun=false
Authorization: Bearer <jwt>
Content-Type: application/json
Idempotency-Key: 6f1c…
X-Fast-Timestamp: 1792574400
X-Fast-Nonce: 9b2e4c…              (16–64 ký tự ngẫu nhiên)
X-Fast-Content-SHA256: 3a7bd3e2…   (hex SHA256 byte thô của body)
X-Fast-Signature: 5d41402a…        (hex HMAC-SHA256)

{ "master": { … }, "detail": [ … ] }
```

```
canonicalRequest =
  "FAST-HMAC-SHA256"  + "\n" +
  METHOD              + "\n" +   // POST
  PATH                + "\n" +   // /api/v3/sale-invoice, viết thường
  canonicalQuery      + "\n" +   // tham số sắp theo tên (rồi theo giá trị), URL-encode RFC 3986, nối bằng &; không có thì rỗng
  TIMESTAMP           + "\n" +
  NONCE               + "\n" +
  IDEMPOTENCY_KEY     + "\n" +   // không gửi thì rỗng
  CONTENT_SHA256                 // hex chữ thường; body rỗng = SHA256 của chuỗi rỗng
```

**Server kiểm theo thứ tự, sai bước nào dừng ở bước đó:**
1. JWT hợp lệ, `jti` không nằm trong `restApiRevokedToken`. Lấy `u_id` → `private_key` (cache HybridCache, giải mã trong bộ nhớ).
2. `|now − TIMESTAMP| ≤ ClockSkewSeconds` (mặc định 300). Nonce chưa gặp (HybridCache, TTL 2 × `ClockSkewSeconds`).
3. Tự tính `SHA256(rawBody)`, so với `X-Fast-Content-SHA256` → sai: `400 CHECKSUM_MISMATCH`.
4. Tự dựng `canonicalRequest`, tính HMAC bằng `K_request`, so với `X-Fast-Signature` bằng `CryptographicOperations.FixedTimeEquals` → sai: `401 INVALID_SIGNATURE`.
5. Parse JSON object (4.3) → sai dạng: `400 INVALID_SHAPE`.
6. Phân quyền: user còn hiệu lực → IP → `restApiAllowForm` → `unitRequired`/`unitCode` → `restApi_CheckUnitRights`. Kết quả cache vài phút; đổi quyền thì xoá cache.

Body đọc **một lần** (buffer có giới hạn `MaxRequestBodySize`), vừa băm vừa giữ để parse. Thay header `checksum` MD5 hiện tại (lỗi 1.4 #9).

#### 4.2.4. Response

```
HTTP/1.1 200
Content-Type: application/json
X-Request-ID: 8c2f…
X-Fast-Timestamp: 1792574401
X-Fast-Content-SHA256: …
X-Fast-Signature: …

{ "code": "SUCCESS", … }
```
```
canonicalResponse =
  "FAST-HMAC-SHA256-RESPONSE" + "\n" +
  STATUS            + "\n" +   // 200
  REQUEST_ID        + "\n" +
  REQUEST_NONCE     + "\n" +   // nonce của request tương ứng
  TIMESTAMP         + "\n" +
  CONTENT_SHA256
```
- Gắn `REQUEST_NONCE` để response cũ không phát lại được cho request khác.
- Lỗi **trước bước 1** (thiếu/sai JWT, user không tồn tại): chưa biết khóa của ai nên **không ký**, chỉ trả `code`, `message` chung, `request_id`. Client coi response không ký là lỗi xác thực, không tin nội dung.

#### 4.2.5. Lấy token

`POST /api/v3/auth/token`, không có `Authorization`, có thêm header `X-Fast-User: <restApiUser.name>`. Ký y như 4.2.3, body `{ "userName": "<restApiUser.name>" }` phải khớp `X-Fast-User`. Server kiểm `restApiUser.status`, `name_erp = userinfo2.name` khớp một dòng `status = '1'`, IP, khoá đăng nhập sai nhiều lần (HybridCache). Response ký như 4.2.4, chứa `{ "accessToken": "<jwt>", "expiresIn": 3600 }`. JWT: `sub` = `restApiUser.name`, claim `u_id` = `restApiUser.id`, `user_id` = `sysdb..userinfo2.id`, `name_erp`, `jti`. JWT ký bằng `secretKey` của server (khác `private_key`).

#### 4.2.6. Rate limit
Middleware ASP.NET Core, partition theo `u_id` (sau xác thực) và theo IP (trước xác thực, cho `auth/token`). In-process, phù hợp một instance.

### 4.3. Body request (chỉ dạng object)

```json
{ "master": { "YourID": "HD001", "VoucherDate": "21/10/2026", "CustomerID": "KH01" },
  "detail": [ { "ItemID": "VT01", "Quantity": 2 },
              { "ItemID": "VT02", "Quantity": 1 } ] }
```
- Nhiều chứng từ: gửi **mảng** các object trên, tối đa `MaxDocumentsPerRequest`.
- `get`: body là object tham số lọc theo field của `get{Resource}.xml`, vd `{ "FromDate": "01/10/2026", "ToDate": "31/10/2026" }`. Form chỉ có một khối thì nhận cả dạng phẳng này lẫn dạng `{ "<khối>": { … } }`; form nhiều khối bắt buộc dạng có tên khối.
- `delete`/`cancel`: thường chỉ khối gốc với field khóa.
- Dòng con gửi kèm field khóa thì phải trùng khối gốc. Sai dạng → `400 INVALID_SHAPE`.
- Thuộc tính không khai trong XML → **bỏ qua**, không báo lỗi (đối tác thêm field không làm hỏng tích hợp).
- Kiểm tra gom đủ lỗi, có vị trí (`[0].detail[2].Quantity`).
- `Idempotency-Key`: `restApiIdempotency` giữ response 24 giờ. Gửi lại đúng key → nhận kết quả cũ (ký lại cho request mới); cùng key mà body/query khác → `409`.

### 4.4. Response và phân trang dataset nhiều bảng
Quy ước dataset do query `processing` trả về:
- Bảng 0: `code`, `message` (`field`, `condition` khi lỗi), tùy chọn `tables` = danh sách tên bảng, vd `'master,detail,tax'`.
- Bảng 1..N: dữ liệu. Không khai `tables` thì tên là `data` hoặc `data1..dataN`.

```json
{ "code": "SUCCESS", "success": true, "message": "", "request_id": "8c2f…",
  "data": { "master": [ … ], "detail": [ … ], "tax": [ … ] },
  "paging": { "cursor": "a1b2…", "page_index": 0, "page_count": 12, "page_size": 100, "total_row_count": 1180 } }
```

**Phân trang N bảng** — `<paging pageSize="100" key="stt_rec"/>`:
1. Cắt theo giá trị `key` của bảng dữ liệu đầu tiên; mỗi trang kèm mọi dòng cùng key ở các bảng khác. Một chứng từ không bị tách sang hai trang.
2. Bảng không có cột `key` chỉ trả ở trang 0.
3. Kết quả nén lưu HybridCache theo trang, cache key có `u_id` + `form` + `unitCode` + `SHA256(canonicalQuery)` + SHA256(body). TTL `CursorTtlMinutes`. Recycle → `CURSOR_EXPIRED`.
4. Không khai `<paging>` thì trả hết, có trần `MaxRowsWithoutPaging`.
5. `Transfer`: chỉ đánh dấu đã chuyển các dòng của trang đã trả.

Mã mới trong `Message.xml`: `METHOD_NOT_ALLOWED` (405), `NOT_FOUND` (404), `UNIT_REQUIRED` (400), `UNIT_NOT_FOUND` (400), `UNIT_DENIED` (403), `INVALID_SHAPE` (400), `CHECKSUM_MISMATCH` (400), `INVALID_SIGNATURE` (401), `TIMESTAMP_EXPIRED` (401), `NONCE_REUSED` (401), `IDEMPOTENCY_CONFLICT` (409), `CURSOR_EXPIRED` (404), cùng các mã nghiệp vụ dùng trong `errorCode` của `check`.

---

## 5. Trạng thái in-process, log, portal

### 5.1. Phân bổ trạng thái (không Redis)

| Dữ liệu | Nơi lưu |
|---|---|
| Quyền user, IP, `private_key` đã giải mã | `HybridCache` memory-only, TTL ngắn |
| Rate limit | ASP.NET Core Rate Limiting (in-process) |
| JWT đã thu hồi (`jti`) | SQL `restApiRevokedToken` |
| Nonce / khoá đăng nhập sai | HybridCache + TTL |
| `Idempotency-Key` | SQL `restApiIdempotency` |
| Cursor phân trang | HybridCache + TTL |
| Hàng đợi log | `Channel` → `BackgroundService` → SQL; lỗi → spool file |
| `private_key` | SQL, mã hóa bằng `Security:KeyEncryptionKey` |
| Cặp khóa RSA đăng nhập portal | Khóa riêng: tệp PFX ngoài thư mục release (mật khẩu ở biến môi trường) hoặc Windows Certificate Store. Khóa công khai: công bố qua `/_meta/server` |

Quy tắc cache cho ERP: cache key chứa đơn vị và tham số ảnh hưởng kết quả; đổi quyền/dữ liệu thì xoá hoặc tăng version key; không cache công nợ, tồn kho, phân quyền theo cách có thể trả nhầm giữa hai công ty / hai user.

### 5.2. Log mới
Middleware đẩy vào `Channel`, `LogWorker` ghi theo lô vào bảng log mới qua tham số / bulk copy; SQL lỗi thì spool `.jsonl.gz`. Body nén GZip, loại trùng theo SHA256. Dọn theo `LogRetentionDays` bằng `restApi_LogCleanup`. Log ghi kèm `X-Fast-Content-SHA256`, nonce, timestamp, kết quả kiểm chữ ký; **không** ghi `private_key`, chữ ký HMAC, JWT, connection string.

**Serilog** ghi file chẩn đoán vận hành: rolling theo ngày, giới hạn dung lượng, path ngoài thư mục release. `X-Request-ID` = `req_id` để tra log file và SQL.

### 5.3. Cổng đối tác (`ApiDocs`)

**Ai vào được:** mọi tài khoản `restApiUser` còn hiệu lực (`status = '1'`), đăng nhập bằng `name` + `password`. Không có bảng hay cờ riêng.

**Đăng nhập (mật khẩu mã hóa bất đối xứng tại client):**
1. Trình duyệt lấy khóa công khai RSA của server (`/_meta/server`, ≥ 2048 bit).
2. Mã hóa bằng WebCrypto `RSA-OAEP` (SHA-256) gói `{ "password": "…", "timestamp": 1792574400, "nonce": "…" }`. Mật khẩu rõ không bao giờ rời trình duyệt, không nằm trong log.
3. Gửi `POST /api/v3/auth/portal` với `{ "userName": "<restApiUser.name>", "kid": "…", "credential": "<base64 bản mã>" }`.
4. Server giải bằng khóa riêng theo `kid`, kiểm `timestamp` trong `ClockSkewSeconds` và `nonce` chưa dùng (chống phát lại gói đã bắt được), giải `restApiUser.password` (AES) rồi so bằng `CryptographicOperations.FixedTimeEquals`. Kiểm `status`, IP, khoá sau nhiều lần sai (HybridCache), như `auth/token`.
5. Trả JWT phạm vi `portal` (claim `scope = portal`), thời hạn ngắn. JWT này **chỉ** gọi được `/_meta/*`; route nghiệp vụ từ chối dù chữ ký đúng.

Server chỉ giữ một cặp khóa RSA cho cả instance, không cần đối tác đăng ký khóa gì. Mã hóa RSA ở đây là lớp thứ hai: portal vẫn phải chạy HTTPS, vì trang portal tải qua HTTP thì kẻ đứng giữa có thể thay cả khóa công khai lẫn script mã hóa.

**Nội dung:**
- **Danh sách tài nguyên** theo quyền; **chi tiết** khối, field, mẫu JSON, có cần `unitCode`, có phân trang.
- **Gọi thử:** đối tác dán `private_key` vào ô riêng, chỉ giữ trong bộ nhớ trình duyệt; HMAC tính bằng WebCrypto, khóa không gửi lên server. Có `unitCode`, tham số lọc, "Kiểm tra" (`dryRun`), "Gửi thật" (hỏi xác nhận), "Trang tiếp"; hiện `canonicalRequest` để đối tác so khi debug chữ ký.
- **Code mẫu:** cURL, C#, JavaScript, Python, Java: tính checksum, ký request, kiểm chữ ký response. Kèm **vector thử cố định** (khóa, body, kết quả mong đợi).
- **Bảng mã lỗi** sinh từ `Message.xml`.

### 5.4. Cài đặt: `Fast.Api.Cli setup`

Cài và nâng cấp chỉ qua **một CLI**. NSD chỉ khai tham số, CLI làm hết phần còn lại: copy toàn bộ dll, exe, XML từ **gói gốc** vào thư mục dự án; tạo table, view, proc trên DB khách; dựng site IIS. Không còn bộ `Install.ps1`/`Update.ps1` riêng.

**Gói gốc** (`--source`): thư mục release do build tạo ra, gồm `bin/` (Api, ApiDocs, Cli đã `dotnet publish`), `sql/` (script đánh số thứ tự `NNN_ten.sql`), `App_Data/` (Form, Options) và `setup.manifest.json` (phiên bản, danh sách tệp + SHA-256, thứ tự script).

**NSD khai** (hỏi tương tác, hoặc `--config setup.json` để chạy lại không hỏi):

| Nhóm | Tham số |
|---|---|
| Thư mục | Thư mục cài (dự án), thư mục log, thư mục dữ liệu (khóa PFX, spool) |
| SQL | Server, DB app, DB sys, kiểu đăng nhập (Windows / SQL + tài khoản) |
| IIS | Tên site, App Pool, hostname, port, chứng chỉ HTTPS (thumbprint) |
| Phạm vi | Danh sách XML form cài (mặc định: tất cả trong gói); có cài portal `ApiDocs` không |
| Tùy chọn | Tạo tài khoản `restApiUser` đầu tiên (`name`, `name_erp`) |

**CLI tự làm**, theo thứ tự:
1. **Preflight:** chạy quyền Administrator, Windows, IIS, Hosting Bundle .NET 10, port trống, dung lượng ổ, kết nối SQL (cảnh báo TLS cho 2008 R2, mục 1.3), quyền DDL của tài khoản SQL, `userinfo2`/`dmdvcs`/`ff_CheckUnitRight` có tồn tại. Kiểm SHA-256 mọi tệp trong gói theo manifest. Hỏng bước nào thì dừng, chưa ghi gì.
2. **Sinh bí mật** (chỉ lần cài đầu): `secretKey`, `passwordKey`, `KeyEncryptionKey`, cặp khóa RSA portal (PFX + mật khẩu). Ghi vào `appsettings.Local.json` và thư mục dữ liệu, ACL chỉ cho App Pool identity + Administrators. Không in ra màn hình, không ghi log.
3. **SQL:** chạy các script chưa áp theo thứ tự manifest, mỗi script một transaction, ghi vào `restApiSetupLog` (3.2). Script nào đã áp mà checksum khác → dừng, báo. Chỉ tạo/sửa object `restApi*`; không đụng object FBO chuẩn hay `api_*` của v2.
4. **Tệp:** copy `bin/` và `App_Data/`. XML khách đã sửa (khác checksum bản gốc lần cài trước) thì **giữ nguyên**, ghi bản mới cạnh nó dạng `.new` và báo.
5. **IIS:** tạo/cập nhật App Pool (No Managed Code), site, binding HTTPS, ACL thư mục log/dữ liệu.
6. **Verify:** `/health/ready`, gọi `/_meta/server`, đăng nhập thử bằng tài khoản đầu tiên (nếu có tạo). Tạo tài khoản thì in `private_key` **đúng một lần**.
7. **Báo cáo:** phiên bản, script đã áp, tệp giữ lại, cảnh báo; ghi file log setup (không có bí mật).

**Chế độ:** mặc định **chạy thử** (in kế hoạch, không ghi), `--apply` mới làm thật (như `rekey-passwords`). Chạy lại cùng phiên bản không đổi gì. Gói mới hơn = **nâng cấp**: backup thư mục cài trước khi copy; verify hỏng thì khôi phục thư mục. Script SQL không tự rollback, nên chỉ được thêm, không xoá dữ liệu, chạy lại không lỗi. `setup verify` chỉ chạy bước 1 và 6.

---

## 6. Kiểm thử

| Bộ | Nội dung |
|---|---|
| Unit | Đọc XML, tách tên file → động từ + resource, JSON object, gán khóa khối con, kiểu/độ rộng, 4 danh sách `check`, `@@checking(n)`, cắt trang N bảng, `canonicalQuery` (sắp xếp, encode, tham số trùng tên), `canonicalRequest`/`canonicalResponse`, dẫn xuất `K_request`/`K_response`, HMAC với **vector cố định** dùng chung cho code mẫu portal |
| Chuyển đổi v2 | Mỗi XML v2 qua `Fast.Api.Cli upgrade` + JSON mẫu: dữ liệu đúng thì processing nhận cùng bảng tạm như v2; dữ liệu sai thì cùng bị từ chối. Transaction rồi rollback |
| Integration | DB dev: `POST /{resource}` và `/{verb}` (kể cả `/get`), `GET` vào route nghiệp vụ → `405`, `unitRequired`, `unitCode` sai quyền / không tồn tại, phân trang 3 bảng, idempotency, revoke JWT, recycle → cursor hết hạn, thuộc tính JSON lạ bị bỏ qua, file có động từ tự đặt (vd `approveInvoice` → `/invoice/approve`) |
| Bảo mật | SQL injection qua `unitCode`, tham số lọc, tên resource; thiếu/sai checksum; sai chữ ký; sửa 1 byte body; đổi `unitCode`/`dryRun` trên query; đổi `Idempotency-Key`; timestamp quá hạn; nonce dùng lại; chữ ký request dùng làm chữ ký response; response cũ phát lại cho request mới; JWT đã thu hồi; user không có `restApiAllowForm`; user chưa có `private_key`; so chữ ký không theo thời gian hằng; portal: gửi mật khẩu rõ, bản mã sai `kid`, phát lại bản mã cũ (`nonce`/`timestamp`), JWT `portal` gọi route nghiệp vụ → bị từ chối, sai mật khẩu nhiều lần → khoá |
| Setup | Máy sạch: chạy thử rồi `--apply` → API chạy, `/health/ready` đạt. Chạy lại cùng gói → không đổi gì. Nâng cấp gói → chỉ áp script mới, giữ XML khách đã sửa, giữ `appsettings.Local.json`. Gói bị sửa 1 tệp → dừng ở preflight. Verify hỏng → khôi phục thư mục. Log setup không chứa bí mật. Cài trên DB đang chạy v2 → v2 không bị ảnh hưởng |
| Tải | Một instance IIS; độ trễ khi bật ký, log, phân trang |

---

## 7. Roadmap triển khai

### 7.1. Tổng quan

```
Pha 0  Dọn nền ─────────────► (xong, còn đổi mật khẩu SQL + secretKey)
Pha 1  Nền vận hành + DB ───► M1: health, log file, script DB chạy trên DB dev
Pha 2  Bảo mật HMAC ────────► M2: đăng nhập + gọi API mẫu có ký 2 chiều
Pha 3  XML engine + route ──► M3: setSaleInvoice / getCustomer chạy thật trên DB dev
Pha 4  Phân trang N bảng ───┐
Pha 5  Log mới ─────────────┤  (4, 5, 6 song song sau M3)
Pha 6  Portal ──────────────┘
Pha 7  Setup CLI ───────────► M4: cài sạch + nâng cấp bằng một lệnh   (khung bắt đầu sau M1, chạy song song)
Pha 8  Phát hành + pilot ───► M5: chạy song song v2 trên bản sao DB khách
```
Phụ thuộc: 0 → 1 → 2 → 3. Sau 3 thì 4, 5, 6 song song. Pha 7 dựng khung ngay sau M1 và chạy song song; mỗi pha sau đưa script SQL / XML / tệp của mình vào gói gốc, nên đến lúc xong pha 6 thì setup đã cài được đủ. 8 cuối cùng, chỉ cài bằng setup. Pha 2 làm trước pha 3 để mọi endpoint nghiệp vụ sinh ra đã nằm sau lớp ký, không có giai đoạn chạy không ký.

Quy tắc cho mọi pha từ pha 1: hiện vật mới (script SQL, XML mẫu, cấu hình) phải nằm trong gói gốc và cài được bằng `setup`. Không cài tay trên DB khách.

### 7.2. Chi tiết từng pha

**Pha 0 — Dọn nền** · *Xong 21/10/2026* (chi tiết 7.3)
- Còn lại (người phụ trách), **đúng thứ tự**:
  1. Thêm `passwordKey` (32 ký tự ngẫu nhiên) vào `appsettings.Local.json`, giữ nguyên `secretKey` cũ.
  2. `Fast.Api.Cli rekey-passwords --config Api/appsettings.Local.json` (xem trước) rồi thêm `--apply`.
  3. Đổi `secretKey` JWT (mọi token cũ mất hiệu lực) và mật khẩu SQL.

**Pha 1 — Nền vận hành zero-dependency + DB**
| Việc | Đầu ra |
|---|---|
| Serilog rolling file, correlation `X-Request-ID` | Log ra `ProgramData\…\Logs` |
| HybridCache memory-only, RateLimiter ASP.NET Core (bỏ `RateLimitManager` tự viết) | 429 khi vượt ngưỡng |
| Health Checks `/health/live`, `/health/ready` | `ready` kiểm SQL + thư mục log |
| `Provider` chuyển async, timeout theo cấu hình; `LogRepository` ghi qua tham số (lỗi 1.4 #3) | Không còn ghép chuỗi trong log |
| Script SQL: `restApiUser` + `private_key` (kiểm PK `name`, identity `id`), `restApi_GetUserRights`, `restApiRevokedToken`, `restApiIdempotency` | Chạy lại không lỗi trên DB dev (2008 R2) |
| Lớp `UserRepository` mới: tra `restApiUser` → `name_erp` → `userinfo2` | Lấy đúng `user_id`, `admin` |
| Đánh số script SQL theo thứ tự `NNN_ten.sql` trong `sql/` (chuẩn bị cho setup, pha 7) | Thứ tự áp script cố định |

*Nghiệm thu M1:* `/health/ready` đạt; log ghi file; rate limit trả 429; script DB chạy 2 lần liên tiếp không lỗi.

**Pha 2 — Bảo mật thông điệp HMAC hai chiều**
| Việc | Đầu ra |
|---|---|
| Sinh / mã hóa / giải mã `private_key` (AES-256-GCM, `KeyEncryptionKey`); lệnh quản trị cấp khóa cho user | Khóa lưu mã hóa, hiện cho người cấp đúng một lần |
| Middleware đọc body một lần, `SHA256(rawBody)`, dựng `canonicalQuery`/`canonicalRequest`, HMAC `K_request`, so thời gian hằng | 400/401 đúng mã lỗi |
| Timestamp + nonce (HybridCache) | Phát lại → 401 |
| Ký response `K_response` (mọi response sau khi xác định được user) | Header ký trên mọi response |
| `auth/token` ký theo `X-Fast-User`, trả JWT có `u_id`, `user_id`, `name_erp`, `jti`; `auth/revoke` | Đăng nhập không còn MD5 |
| `restApi_GetUserRights` + HybridCache; thứ tự kiểm quyền 4.2.3 bước 6 | Một lượt gọi DB cho quyền |
| `GET /_meta/server` | Đối tác lấy giờ server |
| Vector thử cố định + client mẫu C# trong `Tests` | Test ký/kiểm hai chiều xanh |

*Nghiệm thu M2:* đăng nhập và gọi một endpoint mẫu có ký hai chiều, cả HTTP lẫn HTTPS; toàn bộ ca "Bảo mật" ở mục 6 (phần ký) đạt.

**Pha 3 — XML engine + route**
| Việc | Đầu ra |
|---|---|
| Bộ đọc `<import>`, biên dịch form một lần (lỗi 1.4 #4, #5) | Form bất biến trong cache |
| Tách tên file → động từ + resource, route `POST /{resource}`, `/{resource}/{verb}`, 405/404 | Bảng route lúc khởi động |
| Kiểu/độ rộng từ cột thật, JSON object → `#<khối>` (`_row`, `_parent`, khóa khối con) | Bảng tạm đúng cấu trúc |
| `check`/`condition`/`tag`/`errorCode`/`category`, `@@checking(n)`, `#errors` có vị trí | Lỗi trả đủ, đúng vị trí |
| `unitCode` + `restApi_CheckUnitRights`, `@@unit` sau kiểm quyền (lỗi 1.4 #2) | Không còn ghép header vào SQL |
| `dryRun`, `Idempotency-Key` | Rollback / trả lại kết quả cũ |
| `Fast.Api.Cli upgrade` | Chuyển được XML v2 mẫu |

*Nghiệm thu M3:* `setSaleInvoice.xml` và `getCustomer.xml` chạy thật trên DB dev qua lớp ký; bộ "Chuyển đổi v2" đạt.

**Pha 4 — Phân trang N bảng**: quy ước bảng 0 (`tables`), cắt theo key, cursor HybridCache (cache key có fingerprint query), `Transfer` theo trang. *Nghiệm thu:* 3 bảng × 10.000 dòng lấy đủ qua cursor, không tách chứng từ.

**Pha 5 — Log mới**: Middleware → Channel → worker → bảng log mới, spool file, `restApi_LogCleanup`, `restApi_LogSearch`. *Nghiệm thu:* bật log không làm tăng đáng kể độ trễ; SQL tắt thì spool rồi đẩy lại được.

**Pha 6 — Portal**: cặp khóa RSA server (PFX / Certificate Store) + công bố qua `/_meta/server`, `POST /auth/portal` (giải RSA-OAEP, so `password`, chống phát lại), JWT phạm vi `portal`; `ApiDocs` theo 5.3, code mẫu 5 ngôn ngữ dùng chung vector thử. *Nghiệm thu:* một người ngoài nhóm tự tích hợp được một endpoint chỉ bằng portal.

**Pha 7 — Setup CLI** (`Fast.Api.Cli setup`, thiết kế ở 5.4)
| Việc | Đầu ra |
|---|---|
| Lệnh `pack`: `dotnet publish` Api, ApiDocs, Cli + gom `sql/`, `App_Data/` + sinh `setup.manifest.json` (phiên bản, SHA-256 từng tệp, thứ tự script) | Gói gốc tái tạo được từ một commit |
| Khai tham số: hỏi tương tác hoặc `--config setup.json`; kiểm giá trị (đường dẫn, port, kết nối SQL) trước khi chạy | NSD chỉ trả lời câu hỏi |
| Preflight (5.4 bước 1) | Báo đủ lỗi môi trường một lần, chưa ghi gì |
| Sinh bí mật + `appsettings.Local.json` + PFX RSA, ACL | Không bí mật nào nằm trong gói hay log |
| Trình chạy SQL: `restApiSetupLog`, mỗi script một transaction, kiểm checksum script đã áp | Chạy lại không lỗi, nâng cấp chỉ áp script mới |
| Copy tệp có backup; giữ XML khách đã sửa (`.new`) | Nâng cấp không mất tùy biến của khách |
| IIS: App Pool, site, binding HTTPS, ACL (`Microsoft.Web.Administration`) | Site chạy sau một lệnh |
| Verify + khôi phục thư mục khi hỏng; `setup verify`; báo cáo + log setup | Lỗi thì quay về bản trước |
| Tạo tài khoản `restApiUser` đầu tiên, in `private_key` một lần | Có thể gọi API ngay sau cài |
| Gộp `rekey-passwords` và `upgrade` (XML v2) thành lệnh con của cùng CLI | Một công cụ duy nhất cho NSD |

*Nghiệm thu M4:* máy Windows sạch có IIS + SQL: một lệnh `setup --apply` ra API chạy được, `/health/ready` đạt; chạy lại không đổi gì; nâng cấp lên gói mới giữ nguyên cấu hình và XML khách đã sửa; toàn bộ ca "Setup" ở mục 6 đạt.

**Pha 8 — Phát hành + pilot**: gói gốc phát hành (từ `pack`), tài liệu đối tác, hướng dẫn NSD chạy setup, hướng dẫn chuyển từ v2, quy trình cấp/đổi `private_key`. *Nghiệm thu M5:* chạy `setup` trên bản sao DB khách đang chạy v2: v2 vẫn chạy, v3 chạy song song, một đối tác pilot gọi được.

### 7.3. Tiến độ pha 0 (21/10/2026, nhánh `v3/phase0`)

| Việc | Trạng thái |
|---|---|
| Gỡ bí mật khỏi git: giá trị thật sang `Api/appsettings.Local.json` (gitignore), có `appsettings.Local.example.json`; `Program.cs` nạp Local + biến môi trường | Xong. **Còn:** đổi mật khẩu SQL và `secretKey` |
| Bỏ theo dõi `Api/Log/*.json` | Xong |
| Nâng .NET 8 → 10, cập nhật gói, ghim `System.Security.Cryptography.Pkcs` 10.0.13 | Xong, build 0 lỗi, không cảnh báo lỗ hổng |
| Thử kết nối SQL 2008 R2 | Xong (mục 1.3) |
| Project `Tests` (xUnit), 3 test `EncryptionHelper` | Xong, 3/3 đạt |
| `sql/restApi_CheckUnitRights.sql` | Xong, thử 10 trường hợp đều đúng |
| Garnet cho máy dev | **Hủy** (23/10/2026), đã gỡ `tools/garnet` |
| Tách khóa mật khẩu (8.1): `appSettings:passwordKey`, Api từ chối khởi động nếu thiếu/trùng `secretKey`; `Fast.Api.Cli rekey-passwords` mã hóa lại `restApiUser.password` (mặc định chạy thử, `--apply` ghi trong một transaction, chạy lại không lỗi); 7 test mới | Xong (23/10/2026). Chạy thử trên DB dev: 1 tài khoản cần mã hóa lại, 2 trống. **Chưa `--apply`** |
| Kiểm lại 23/10/2026 | Build 0 lỗi (63 cảnh báo nullable có sẵn), test 3/3 đạt, `dotnet list package --vulnerable` sạch, file cấu hình đã commit không còn bí mật |

### 7.3b. Tiến độ pha 1 (nhánh `v3/phase1`)

| Việc | Trạng thái |
|---|---|
| Serilog rolling file (`appSettings:logDirectory`, trống = `ProgramData\FastRestApi\Logs`; cuộn theo ngày + 50 MB, giữ 31 tệp), `X-Request-ID` sinh ở `RequestIdMiddleware`, gắn vào mọi dòng log file và dùng chung với `LogMiddleware` | Xong 23/10/2026, chạy thử: log ghi đúng `RequestId` |
| `/health/live` (không kiểm gì), `/health/ready` (SQL `SELECT 1` + ghi thử thư mục log); bỏ qua `LogMiddleware`/rate limit | Xong 23/10/2026, cả hai trả 200 trên DB dev |
| HybridCache memory-only (`AddHybridCache`, không đăng ký `IDistributedCache`) | Xong 23/10/2026 |
| RateLimiter ASP.NET Core thay `RateLimitManager`: fixed window `maxRequestsPerMinute`/phút, partition theo user đã xác thực (claim `sub`), chưa xác thực thì theo IP; 429 `TOO_MANY_REQUESTS` + `Retry-After`. Chạy sau `UseAuthentication` | Xong 23/10/2026, chạy thử: request thứ 6 → 429, `/health` không bị tính. Pha 2 đổi partition sang `u_id` |
| Kiểm IP tách thành `IpAccessManager`/`IpAccessMiddleware`, kết quả `restApi_CheckIPAddress` cache HybridCache `ipCheckCacheSeconds` (mặc định 60), xoá theo tag `ip-access` (lỗi 1.4 #7) | Xong 23/10/2026. **Đổi hành vi:** trước đây kiểm IP chạy trước xác thực nên luôn gửi `anonymous`; nay gửi đúng user nên `restApiAllowIp` theo user có hiệu lực |
| `Provider`: thêm `OpenConnAsync`/`ExecuteAsync`/`RetrieveAsync`/`BulkAsync` có `CancellationToken`; `CommandTimeout` đặt **một lần** lúc khởi động (`Provider.Configure(appSettings:commandTimeout)`), repository không gán lại biến static nữa. Hàm đồng bộ giữ cho `FormRepository` tới pha 3 | Xong 23/10/2026 |
| `LogRepository` ghi `#logData`/`#logError` hoàn toàn qua tham số có kiểu + độ dài, tự cắt theo cột (lỗi 1.4 #3); ghi log và kiểm IP chạy async | Xong 23/10/2026, chạy thử: path `/x'y';--` lưu nguyên văn vào `restApiLogData` |
| Script SQL đánh số trong `sql/`: `001_restApi_CheckUnitRights`, `002_restApiUser_private_key` (thêm cột; kiểm PK `name`, identity `id`: thiếu PK thì tạo, sai PK / thiếu identity thì báo lỗi để sửa tay), `003_restApiRevokedToken`, `004_restApiIdempotency`, `005_restApi_GetUserRights` (4 result set cố định: user + userinfo2 qua `vsysuserinfo`, form, IP cho phép, IP chặn) | Xong 23/10/2026, chạy 2 lần liên tiếp trên DB dev không lỗi |
| `UserRepository.GetUserRightsAsync` → `UserRights` (`UId` ≠ `UserId`, `IsActive`, `CanAccessForm` mặc định từ chối, `IsIpAllowed` như `restApi_CheckIPAddress`); 14 test mới. Chưa nối vào xác thực (pha 2) | Xong 23/10/2026. DB dev: `ADMIN` → u_id 2, user_id 1, admin; `NGUYENTDH` không có user FBO tương ứng nên không hiệu lực |

*M1:* `/health/ready` đạt, log ghi file, rate limit trả 429, script DB chạy 2 lần không lỗi → **đạt** (23/10/2026).

### 7.3c. Tiến độ pha 2 (nhánh `v3/phase2`)

| Việc | Trạng thái |
|---|---|
| `Security/MessageSignature`: `K_request`/`K_response`, `SHA256` hex, `canonicalQuery` (giải mã rồi encode RFC 3986, `+` = dấu cách, sắp ordinal theo tên rồi giá trị, thiếu giá trị → `ten=`), `canonicalRequest`/`canonicalResponse`, so chữ ký thời gian hằng | Xong 23/10/2026 |
| `Security/PrivateKeyProtector`: AES-256-GCM, khóa chủ `Security:KeyEncryptionKey` (base64 32 byte), AAD gắn `u_id`, định dạng `v1:` + base64(nonce, tag, bản mã) | Xong 23/10/2026 |
| `Fast.Api.Cli issue-key --user <name> [--rotate] [--apply]`: cấp/thay `private_key`, in khóa một lần | Xong 23/10/2026, chạy thử (không ghi) trên DB dev |
| Vector thử cố định (`Tests/MessageSignatureTests.cs`, kết quả mong đợi tính độc lập bằng Python): khóa dẫn xuất, checksum, chữ ký request/response, sửa 1 byte body, đổi query, chữ ký request không dùng làm chữ ký response; 26 test mới | Xong 23/10/2026, 50/50 đạt |
| `SignatureMiddleware` cho `/api/v3/*` (trừ `GET /_meta/server`): bước 1–4 của 4.2.3. Khóa ký lấy qua `SignerKeyStore` (`restApi_GetUserRights` → giải `private_key` → chỉ giữ `K_request`/`K_response`, HybridCache `userCacheSeconds`). Body đọc một lần, giới hạn `maxRequestBodyBytes` (mặc định 10 MB), giữ ở `HttpContext.Items["rawBody"]`. Nonce `[A-Za-z0-9_-]{16,64}`, **ghi sau khi chữ ký đúng** (request giả không đốt được nonce), kiểm-và-ghi nguyên tử bằng `IMemoryCache` + lock (HybridCache không có thao tác nguyên tử) | Xong 23/10/2026 |
| Ký response (4.2.4) cho mọi response sau khi biết khóa, kể cả lỗi và 500 (middleware đặt ngoài `UseExceptionHandler`). Chưa xác thực / tài khoản không hiệu lực / chưa cấp khóa → không ký | Xong 23/10/2026 |
| Mã lỗi mới trong `Message.xml`: `MISSING_SIGNATURE` (401), `TIMESTAMP_EXPIRED`, `NONCE_REUSED`, `CHECKSUM_MISMATCH`, `INVALID_SIGNATURE`, **`KEY_NOT_ISSUED` (403)**, **`PAYLOAD_TOO_LARGE` (413)** (hai mã cuối không có trong 4.4, thêm vì cần) | Xong 23/10/2026 |
| `GET /api/v3/_meta/server` (giờ server, phiên bản chữ ký; khóa RSA portal để pha 6), `POST /api/v3/_meta/ping` (cần JWT + chữ ký, trả `u_id`, `name`) | Xong 23/10/2026 |
| Client mẫu C# tự chứa `Tests/Sample/FastApiV3Signer.cs` (chỉ dùng `System.Security.Cryptography`); 17 test TestServer: ký đúng, sửa body, đổi query, đổi `Idempotency-Key`, sai khóa, timestamp ±301 s, nonce dùng lại, chữ ký sai không đốt nonce, nonce sai dạng, thiếu header, body quá lớn, chưa xác thực, chưa cấp khóa, response cũ không khớp request khác | Xong 23/10/2026, 67/67 đạt |
| `CustomJwtAuthenticationHandler` trả `Fail` thay vì tự ghi response (trước đây token sai bị ghi response hai lần) | Xong 23/10/2026 |
| Api bắt buộc `Security:KeyEncryptionKey` (base64 32 byte) lúc khởi động | Xong 23/10/2026 |
| `POST /api/v3/auth/token`: user lấy từ `X-Fast-User`, request ký bằng khóa của user đó; body `{ "userName" }` phải khớp (`USER_MISMATCH`); IP theo `restApiAllowIp` của user; khóa đăng nhập sau `loginMaxFailures` (5) lần **chữ ký sai** trong `loginLockMinutes` (15) → `ACCOUNT_LOCKED` (403). Trả `accessToken`, `tokenType`, `expiresIn` (`v3TokenMinutes`, mặc định 60) | Xong 23/10/2026 |
| JWT v3: `sub`, `u_id`, `user_id`, `name_erp`, `scope = api`, `jti`, audience `fast-api-v3` (**không gắn IP**, khác token cũ). Route v3 đòi claim `u_id` khớp tài khoản và `jti` chưa thu hồi; token cũ (audience = IP) không dùng được cho v3 | Xong 23/10/2026 |
| `POST /api/v3/auth/revoke`: thu hồi `jti` đang dùng → `restApiRevokedToken` (đồng thời dọn dòng hết hạn), tra qua HybridCache, thu hồi ghi đè cache nên có hiệu lực ngay | Xong 23/10/2026, thử repository trên DB dev |
| Mã lỗi mới: `ACCOUNT_LOCKED` (403), `INVALID_SHAPE` (400), `USER_MISMATCH` (400); `IIpAccessManager` tách interface | Xong 23/10/2026 |
| 10 test TestServer mới: cấp token rồi dùng, claim JWT, `USER_MISMATCH`, IP bị chặn, user không tồn tại / ngừng / rỗng, khóa sau 3 lần sai, thu hồi rồi bị từ chối, token cũ, `u_id` không khớp | Xong 23/10/2026, 77/77 đạt |
| Kiểm quyền bước 6 (form `restApiAllowForm`, `unitRequired`/`unitCode`) | Chuyển sang pha 3: cần bảng route và `unitRequired` của form |
| Chạy end-to-end trên DB dev (cần `KeyEncryptionKey` trong `appsettings.Local.json` + `issue-key --apply`) | Chưa |

### 7.3d. Tiến độ pha 3 (nhánh `v3/phase3`)

| Việc | Trạng thái |
|---|---|
| Model form v3 bất biến `Models/V3/ImportForm` (khối, field, 4 danh sách check, paging); engine v2 (`Models/Form`, `FormRepository`) giữ nguyên tới khi thay xong | Xong 23/10/2026 |
| `Business/V3/ImportFormLoader`: đọc `<import xmlns="urn:schemas-fast-com:fast-api">`, kiểm thuộc tính, tên khối/field (định danh SQL), trùng field, `key` chỉ ở khối gốc và phải là field của khối gốc, khối con tự thêm field khóa, 4 danh sách cùng số phần tử, `unitAction` suy từ động từ, `paging`. Lỗi → `ImportFormException`, bỏ qua file | Xong 23/10/2026 |
| Tách tên file → động từ + resource kebab-case (`getVATInvoice` → `vat-invoice/get`), route `set` → `/{resource}`, khác → `/{resource}/{verb}`; resource `auth` dành riêng | Xong 23/10/2026 |
| `Business/V3/CheckingCompiler`: `@@checking(n)` theo giá trị `category`, `@@checking` mọi field; `@@table` → `#khối`; tự loại giá trị rỗng; `condition`/`tag` bọc quanh; cảnh báo field có check mà thiếu/không được gọi category | Xong 23/10/2026 |
| `Business/V3/ImportFormCatalog`: nạp `App_Data/Form/*.xml` (không đệ quy), file lỗi / trùng route bỏ qua và ghi log (trùng thì bỏ cả hai), phân giải route không phân biệt hoa thường, chỉ `[a-z0-9-]`, method khác POST → 405, form `enable="false"` → 404 | Xong 23/10/2026 |
| 71 test mới (`Tests/ImportFormTests.cs`) | Xong 23/10/2026, 138/138 đạt |
| Kiểu/độ rộng cột thật (2.3): `ColumnSchemaRepository` đọc `sys.columns` (kiểu người dùng định nghĩa quy về kiểu gốc, độ dài `nchar`/`nvarchar` tính theo ký tự), `ColumnTypeResolver` gắn `SqlType` + độ dài kiểm. Cột chuỗi thêm `COLLATE DATABASE_DEFAULT` để so được với bảng thật khi tempdb khác collation. Field không có cột trong `table` → dùng type XML + cảnh báo; bảng không tồn tại → lỗi nạp | Xong 23/10/2026 |
| `JsonBodyMapper`: một chứng từ hoặc mảng (tối đa `MaxDocumentsPerRequest`), tên thuộc tính không phân biệt hoa thường, thuộc tính lạ bỏ qua. **Form một khối nhận thêm dạng phẳng** (body tham số của `get`, 4.3). `_row` = chỉ số trong mảng cha, `_parent` = chỉ số chứng từ; khối con nhận khóa khối gốc, gửi khóa khác → `INVALID_SHAPE`. Field: `allowNull` (thiếu / null / chuỗi trắng → `FIELD_REQUIRED`), `defaultValue`, `upperCase`, độ dài (`VALUE_TOO_LONG`), `checkIrregular` (`INVALID_FIELD_IRREGULAR`), kiểu/format (`INVALID_VALUE`; ngày theo `format` hoặc ISO 8601, có múi giờ thì đổi về giờ máy chủ; `unixtime` = giây), `hidden` không nhận từ JSON. Lỗi giá trị gom đủ với vị trí `[0].detail[2].Quantity`. `justContent` chưa xử lý (chưa rõ nghĩa ở v2) | Xong 23/10/2026 |
| `CREATE TABLE #khối` theo `SqlType`; 38 test mới (176/176). Thử trên DB dev: resolver theo `m81$000000`/`d81$000000`, bulk copy vào `#master`/`#detail`, chạy `@@checking(1) @@checking(2)` ra đúng 4 lỗi có vị trí | Xong 23/10/2026 |
| `#errors` → response lỗi, nối route vào pipeline, kiểm quyền form + `unitCode`, `dryRun`, `Idempotency-Key`, `Fast.Api.Cli upgrade`, mẫu `setSaleInvoice`/`getCustomer` | Chưa |

### 7.4. Ngoài giai đoạn này
- **Truyền tệp** (pdf, png, jpg…): thiết kế sơ bộ ở Phụ lục A, làm sau M5.
- Callback/notify (`CallbackWorker`).
- Nhiều instance / ARR.
- Đổi `private_key` không gián đoạn (hai khóa song song, `private_key_next`).
- Mã hóa body ở tầng ứng dụng, chữ ký số / khóa bất đối xứng cho API: **không làm**, HTTPS lo phần mã hóa. (RSA chỉ dùng cho đăng nhập portal, 5.3.)

---

## 8. Đã chốt và còn cần chốt

Đã chốt (21/10/2026): tài khoản API nối với user FBO qua `restApiUser.name_erp`; admin `userinfo2.admin = 1` được mọi đơn vị; SQL Server 2008 là bản thấp nhất.

Đã chốt (23/10/2026): zero-dependency (không Redis/Garnet); một instance IIS; giữ tên file `{động từ}{Resource}.xml`; JSON chỉ dạng object; bỏ `version` và chế độ chạy file v2; `check` kiểu Upload FBO thay `<reference>`; nghiệp vụ chỉ dùng `POST` (`get` → `POST /{resource}/get`); body là object dữ liệu, tham số khác qua query/header; đăng nhập trả JWT.

Đã chốt (23/10/2026, lần cuối): **SHA-256** checksum body, **HMAC-SHA256** xác thực hai chiều, ASP.NET Core Web API trên IIS, SQL Server là nguồn dữ liệu chính; thêm cột `restApiUser.private_key`; `restApiUser.name_erp = userinfo2.name`, `restApiUser.name ≠ userinfo2.name`, `restApiUser.id ≠ userinfo2.id`; truyền tệp ngoài giai đoạn này. Thay thế phương án JWE/JWS trước đó.

Đã chốt (23/10/2026, bổ sung): bỏ khóa bất đối xứng và mã hóa body tầng ứng dụng (`encryptData`), HTTPS lo phần mã hóa body; đổi khóa không gián đoạn ngoài phạm vi đợt này; động từ chấp nhận mọi tiền tố chữ thường; thuộc tính JSON không khai trong XML thì bỏ qua.

Đã chốt (23/10/2026, portal): dùng luôn `restApiUser` cho portal, đăng nhập `name` + `password`; mật khẩu mã hóa tại client, server kiểm bằng cặp khóa bất đối xứng (RSA-OAEP); `private_key` chỉ lo phần API.

Đã chốt (23/10/2026, 8.1): tách khóa mã hóa `password` khỏi `secretKey` JWT. Mật khẩu dùng `appSettings:passwordKey`, mã hóa lại bằng `Fast.Api.Cli rekey-passwords` trước khi đổi `secretKey`. Giữ định dạng AES hiện tại.

Đã chốt (23/10/2026, setup): thêm pha Setup CLI. Chạy CLI thì hệ thống tự sinh toàn bộ dll, exe, table, view, proc, XML từ gói gốc vào dự án; NSD chỉ khai tham số (5.4, pha 7).

Còn cần chốt:
1. **"Source gốc" là gì**: đề xuất là **gói release đã build** (lệnh `pack` tạo, có manifest + SHA-256), để máy khách không cần .NET SDK hay mã nguồn. Phương án khác: CLI tự `dotnet publish` từ repo, khi đó máy cài phải có SDK. Chốt trước pha 7.
2. **Gỡ cài đặt** (`setup uninstall`): có cần trong đợt này không. Nếu có: chỉ gỡ site, App Pool, thư mục; **không** xoá object `restApi*` hay dữ liệu trên DB khách trừ khi NSD xác nhận riêng.

---

## Phụ lục A. Truyền tệp (ngoài giai đoạn này)

Thiết kế sơ bộ, chưa làm:
- Field `type="file"` với `accept`, `maxSize`, `maxCount`. Tệp đi base64 trong JSON: `"Attachment": { "fileName": "hd001.pdf", "content": "JVBERi0x…" }`, nên vẫn nằm trong checksum và chữ ký HMAC.
- Kiểm loại tệp theo byte đầu (PDF `%PDF`, PNG `89 50 4E 47`, JPG `FF D8 FF`), không tin tên tệp; giới hạn dung lượng; tên tệp chỉ giữ phần tên.
- Vào SQL qua bảng tạm `#files(block, _row, _parent, field, file_no, file_name, content_type, file_size, sha256, content varbinary(max))` nạp bằng `SqlBulkCopy`; proc nghiệp vụ tự lưu.
- Động từ `get` khai `responseType="file"` trả `"files": [ { fileName, contentType, sha256, content } ]`.
- Cần tra trước: FBO lưu đính kèm ở bảng/thư mục nào.

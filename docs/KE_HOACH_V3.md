# Kế hoạch FastAPI v3 (RestApiCore) — đề xuất, chờ duyệt

**Cập nhật:** 21/10/2026 · v3 = repo này (`huunguyenit/RestApiCore`), phát triển tiếp, không dựng mới. v2 = FastAPI 2.4.7 (VB, `D:\Fast Source\API\FastAPI 2.4.7`).

## 0. Quyết định đã chốt

| Hạng mục | Quyết định |
|---|---|
| Chiến lược | Hai luồng tách hẳn. v2 đóng băng, chỉ sửa cho đối tác cũ. Đối tác mới làm trên v3. Bỏ dần v2 |
| Nền | C# / ASP.NET Core. **Nâng từ .NET 8 lên .NET 10 LTS**: .NET 8 hết hỗ trợ ngày 10/11/2026 |
| Redis | Bắt buộc. Server là Garnet 2.2.3 (chạy native trên Windows), client `StackExchange.Redis`. Garnet **không có Redis Streams** và **mặc định tắt Lua** → hàng đợi log dùng List, server phải chạy với `--lua true` (mục 5.1) |
| Chia tải | Không dùng WCF Distribute. Chạy N instance sau IIS ARR/nginx, trạng thái chung ở Redis |
| DB | Object `restApi*` đang có **giữ nguyên**. Object viết mới cũng đặt tên `restApi*` (bảng, view) và `restApi_*` (proc, function). Không dùng chung bảng với v2. `apiToken[_partner]` của v2 chưa đụng tới. DB dev là DB khai báo trong `Api/appsettings.json` |
| Người dùng | `user_id` = `sysdb..userinfo2.id` |
| XML | Gốc là `<import>` của v2, mở rộng cho v3. Không có `<response>`: dữ liệu trả về do backend quyết định qua dataset |
| JSON object | `{ "master": { … }, "detail": [ … ], "tax": [ … ] }`: khối gốc là **một object**, các khối con là mảng |
| Method | `GET` để lấy, `POST` để ghi. `setCustomers` → `POST /api/v3/customer`. File: `customer.get.xml`, `customer.post.xml` |
| `unitCode` | Query string `?unitCode=`. Form khai `unitRequired="true"` thì **bắt buộc** nhập. Kiểm quyền đơn vị bằng hàm FBO có sẵn |
| Cổng đối tác | Phát triển từ project `ApiDocs` |

---

## 1. Hiện trạng đã kiểm

### 1.1. Code RestApiCore

| Thành phần | Hiện trạng | Hướng xử lý |
|---|---|---|
| `Api` | `AuthController`, `QueryController` (`POST api/query/{name}`), middleware log và rate limit, xác thực JWT tự viết | Giữ khung, đổi route (mục 4) |
| `Business` | `FormManager`, `FormMethodBase`/`FormMethodCache`, `ResponseManager` (phân trang), `AuthManager`, `LogManager` | Giữ cách chia lớp. Viết lại phân trang và log |
| `DataAccess` | `FormRepository` (đọc XML `<form>`, dựng `#struct`, sinh script reference/check, chạy Checking rồi Processing), `UserRepository`, `CacheRepository` (file JSON + `restApiCache`), `LogRepository` | Đổi bộ đọc XML sang `<import>`. User theo `userinfo2`. Cache sang Redis |
| `Models` | `Form`, `Struct`, `Field`, `FieldReference`, `FieldCheck`, `Message`, `Update` | Giữ, bổ sung `Shape`, `Paging`, `UnitRequired`, `Method` |
| `App_Data/Options` | `Message.xml` (mã lỗi ↔ HTTP status), `Update.xml` (script cho `reference`), `Irregular.txt` | Giữ nguyên cách làm |
| `Security` | JWT HS256 (audience = IP), `GenerateAuthKey` (MD5 + `positions` như v2), AES cho mật khẩu | Giữ JWT. Chữ ký đăng nhập đổi sang HMAC-SHA256 |
| `ApiDocs` | Khung Razor Pages, dữ liệu giả | Thành cổng đối tác |
| `CallbackWorker` | Worker mẫu | Dành cho callback/notify, ngoài phạm vi đợt này |
| `Provider` | ADO.NET tĩnh, đồng bộ, `CommandTimeout` static dùng chung | Chuyển async, timeout theo cấu hình |

### 1.2. DB dev (đã truy vấn thật)

| Kiểm | Kết quả | Ảnh hưởng tới thiết kế |
|---|---|---|
| Phiên bản | **SQL Server 2008 R2 RTM** (10.50.1600), Developer Edition | Xem 1.3 |
| `FSD_Dev_SP229_S..userinfo2` | `id int`, `name char(30)`, `admin tinyint`, `status char(1)`, `password varchar(256)`, cột audit `datetime0/datetime2/user_id0/user_id2`… | `user_id` = `id`. `@@userName` = `RTRIM(name)` |
| Quyền đơn vị | `sysunitrights(user_id, ma_dvcs char(8), r_new, r_edit, r_del, r_access)` ở DB app. Có sẵn hàm **`dbo.ff_CheckUnitRight(@nUserID, @cMa_dvcs, @cAction)`**, `@cAction` ∈ `New`/`Edit`/`Del`/`Acc` | `restApi_CheckUnitRights` gọi lại hàm này, không tự đặt quy tắc mới |
| Danh mục đơn vị | `dmdvcs.ma_dvcs char(8)` | `unitCode` tối đa 8 ký tự, phải có trong `dmdvcs` |
| Object `restApi*` đang có | Bảng `restApiUser`, `restApiAllowForm(u_id, form)`, `restApiAllowIp(u_id, ip_address, description)`, `restApiBlackListIp`, `restApiCache`, `restApiLogData`, `restApiLogError`, `restApiLogActivity`, `restApiLogTransfer`, `restApiNotify`, `restApiTransfer`, `restApiUpdate`. Proc `restApi_CheckFormRights`, `restApi_CheckIPAddress`, `restApi_InsertLogData`, `restApi_InsertLogError`, `restApi_RemoveCache`, `restApi_TableDebug` | Giữ nguyên cấu trúc, cách v3 dùng ở mục 3.1 |

Hai điểm trong logic quyền hiện tại cần đổi ở v3:
- `restApi_CheckFormRights` và `restApi_CheckIPAddress`: user **không có dòng nào** trong `restApiAllowForm`/`restApiAllowIp` thì được **mọi form / mọi IP**. v3 đổi sang **mặc định từ chối** với form. Riêng IP vẫn giữ cách "không khai thì cho mọi IP", vì nhiều đối tác không có IP tĩnh.
- `fsd_GetUnitRight` (có trong DB) có nhánh `@Admin = 0` khi truyền `@Unit` thì trả lại nguyên danh sách đơn vị mà **không kiểm quyền**. v3 không dùng hàm này.

### 1.3. SQL Server 2008 R2 — giới hạn phải tuân theo
Mọi SQL `restApi*` và câu lệnh do v3 tự sinh phải chạy được trên 2008 R2. Nghĩa là **không dùng**: `OFFSET/FETCH`, `THROW`, `IIF`, `CONCAT`, `FORMAT`, `TRY_CONVERT`, `STRING_SPLIT`, các hàm JSON, `SEQUENCE`. Phân trang SQL dùng `ROW_NUMBER()`, tách chuỗi dùng hàm sẵn có của FBO (vd `fsd_StringToTable`).

Rủi ro kết nối: 2008 R2 bản RTM chưa hỗ trợ TLS 1.2. `Microsoft.Data.SqlClient` mới trên Windows Server đã tắt TLS cũ có thể không kết nối được. **SQL Server 2008 là bản thấp nhất ở khách**, nên giới hạn trên áp dụng cố định.

Đã thử (pha 0, 21/10/2026): .NET 10 + `Microsoft.Data.SqlClient` 6.1.7 kết nối **được** tới DB dev 10.50.1600, cả `Encrypt=True` lẫn `False`, nhưng **phải hạ xuống TLS 1.0** (SqlClient in cảnh báo). Checklist deploy cho khách dùng SQL 2008/2008 R2: hoặc cài bản vá TLS 1.2 cho SQL Server (2008 R2 SP3 + bản cập nhật TLS 1.2), hoặc máy chạy API phải còn bật TLS 1.0 phía client. Máy đã tắt TLS 1.0 mà SQL chưa vá thì sẽ không kết nối được.

### 1.4. Lỗi cần sửa trước khi đưa cho đối tác

| # | Mức độ | Vấn đề | Vị trí |
|---|---|---|---|
| 1 | **Nghiêm trọng** | `Api/appsettings.json` chứa connection string (gồm mật khẩu SQL) và `secretKey` ký JWT, **đã commit và push lên GitHub** | `Api/appsettings.json` |
| 2 | **Nghiêm trọng** | SQL injection: header `unit` ghép thẳng vào SQL (`'{request.unit}'`) | `FormRepository.ReplaceDefaultVaraible` |
| 3 | **Nghiêm trọng** | SQL injection: `path`, `ip`, `userName` ghép chuỗi vào `insert #logData`/`#logError` | `LogRepository` |
| 4 | Cao | `GetScriptUpdate`/`GetScriptCheck` sửa `form.Queries` của form đang cache dùng chung, `GetTypeByName` sửa `field.MaxLength`/`Type` trên mỗi request → các request đồng thời ghi đè lên nhau | `FormRepository`, `FormMethodBase` |
| 5 | Cao | `@@update({i})` thay theo số thứ tự vòng lặp, không theo `index` khai trong XML | `FormRepository` |
| 6 | Trung bình | Log ghi SQL đồng bộ trong middleware, đệm cả response vào `MemoryStream` | `LogMiddleware` |
| 7 | Trung bình | Rate limit và chống replay đăng nhập nằm trong RAM từng instance, đọc rồi ghi không nguyên tử. Mỗi request đều gọi proc kiểm IP | `RateLimitManager`, `AuthController` |
| 8 | Thấp | `Api/Log/*.json` (request/response thật) đang được commit | `.gitignore` |
| 9 | Thấp | Checksum body là MD5 không khóa → không chống được sửa nội dung | `QueryController` |

Lỗi #1 xử lý ngay: đổi mật khẩu SQL và `secretKey`, đưa bí mật ra biến môi trường/user-secrets, gỡ file khỏi git. Lịch sử git vẫn giữ giá trị cũ, nên bước đổi mật khẩu là bắt buộc.

---

## 2. Định nghĩa API bằng XML (`<import>` mở rộng)

### 2.1. Ví dụ `App_Data/Form/sale-invoice.post.xml`
```xml
<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE import [ <!ENTITY IrregularValue SYSTEM "..\Options\Irregular.txt"> ]>

<import xmlns="urn:schemas-fast-com:fast-api" version="3"
        type="Voucher" voucherCode="HDA" shape="both" unitRequired="true"
        description="Hóa đơn bán hàng">

  <fields name="master" table="m81$000000" key="YourID">
    <field name="YourID"      fastName="id_doitac" type="string" maxLength="36" allowNull="false" isPrimaryKey="true"/>
    <field name="VoucherDate" fastName="ngay_ct"   type="datetime" format="dd/MM/yyyy" allowNull="false"/>
    <field name="CustomerID"  fastName="customer_id" type="string" maxLength="36" allowNull="false">
      <reference controller="Customer" fastName="ma_kh" index="0"/>
    </field>
    <field name="SaleEmployee" fastName="ma_nvbh" checkIrregular="true"/>
    <field name="ma_kh" hidden="true"/>
  </fields>

  <fields name="detail" table="d81$000000">
    <field name="ItemID"    fastName="item_id" type="string" maxLength="36" allowNull="false">
      <reference controller="Item" fastName="ma_vt" index="1"/>
    </field>
    <field name="Promotion" fastName="km_yn" type="int" defaultValue="0">
      <check condition="km_yn not in (0, 1, 2, 3)" errorCode="INVALID_FIELD" index="0"/>
    </field>
    <field name="Quantity"  fastName="so_luong" type="numeric"/>
  </fields>

  <query action="checking"><text><![CDATA[ @@update(0) @@checking(0) @@update(1) ]]></text></query>
  <query action="processing"><text><![CDATA[ exec restApi_ProcessDataFor_m81 @@voucherCode, @@unit, @@userId ]]></text></query>
</import>
```
Khối `detail` không cần khai `YourID`: khóa `key` của khối gốc được tự thêm vào mọi khối con (cột `id_doitac` trong `#detail`), để query nối `#master` với `#detail`.

### 2.2. Thuộc tính

| Phần tử | Thuộc tính | Ý nghĩa |
|---|---|---|
| `import` | `version` | `3` = định dạng v3. Không khai = file v2, chạy chế độ tương thích (2.4) |
| | `type` | `List` / `Voucher` / `Report` / `Transfer` |
| | `voucherCode` | Mã chứng từ |
| | `shape` | `object` / `flat` / `both` (mặc định `both`), chỉ áp dụng cho POST |
| | `unitRequired` | `true` → bắt buộc `?unitCode=`, thiếu thì trả `UNIT_REQUIRED`. Mặc định `false`. Có gửi `unitCode` thì luôn bị kiểm quyền, dù `unitRequired` là gì |
| | `enable`, `description` | Bật/tắt, mô tả trên portal |
| `fields` | `name` | Tên khối = tên thuộc tính trong JSON object = bảng tạm `#<name>`. Khối đầu tiên là **khối gốc**. Không khai thì là `_data` → `#_data` như v2 |
| | `table` | Bảng FBO thật mà khối đổ vào. Có khai thì kiểu và độ rộng lấy từ cột thật (2.3) |
| | `key` | Chỉ khai ở khối gốc. Khóa nối khối gốc với khối con, và dùng để gộp dòng khi nhận JSON phẳng |
| `field` | `name`, `fastName`, `type`, `maxLength`, `allowNull`, `isPrimaryKey`, `upperCase`, `hidden`, `defaultValue`, `justContent`, `checkIrregular` | Giữ nguyên tên thuộc tính của v2 |
| | `type` | v2: `string`, `numeric`, `datetime`, `bool`. Thêm: `int`, `unixtime` |
| | `format` | Định dạng ngày nhận vào. Luôn nhận thêm ISO 8601 |
| | `description`, `example` | Hiện trên portal |
| `reference` | `controller`, `fastName`, `index` | Tra mã FBO từ mã đối tác theo `Options/Update.xml`, chèn vào `@@update(index)` |
| `check` | `condition`, `errorCode`, `index` | Điều kiện SQL trên `#<khối>`, chèn vào `@@checking(index)`, lỗi lấy từ `Message.xml` |
| `query` | `action` | `checking` (trả bảng lỗi thì dừng) rồi `processing`. `<query>` không khai `action` = `processing` (dạng v2) |
| `paging` | `pageSize`, `key` | Mục 4.4 |

### 2.3. Kiểu dữ liệu và độ rộng

| Kiểu XML | Cột bảng tạm khi khối có `table` | Khi khối không có `table` |
|---|---|---|
| `string` | Đúng kiểu cột thật (`char(8)`, `nvarchar(128)`…) | `NVARCHAR(maxLength)`, không có thì `NVARCHAR(MAX)` |
| `numeric` | Đúng `numeric(p,s)` của cột | `NUMERIC(30,12)` (như v2) |
| `int` | `INT` | `INT` |
| `datetime`, `unixtime` | Đúng `datetime`/`smalldatetime` của cột | `DATETIME` |
| `bool` | `BIT` | `BIT` |

- `maxLength` khai lớn hơn cột thật → cảnh báo khi nạp XML, dữ liệu được kiểm theo độ rộng cột thật.
- Cấu trúc cột đọc **một lần khi nạp XML**, file đổi thì nạp lại.

### 2.4. Chạy file XML v2
File không có `version="3"`: một khối `_data`, một query. Kiểu dữ liệu và thứ tự kiểm tra (`Hidden`, `justContent`, `MaxLength`, numeric, bool) y như v2. JSON đi qua bản chuyển sang C# của `JsonFlattener` v2. Bộ Parity (mục 6) bảo đảm cùng XML + JSON thì `#_data` giống v2.

Tool `Fast.Api.Cli upgrade` chuyển file v2 sang v3: đổi tên theo `resource.method.xml`, gợi ý tách khối gốc và khối con. Người xem lại rồi mới dùng.

### 2.5. Biến thay thế trong query
`@@form`, `@@resource`, `@@method`, `@@type`, `@@voucherCode`, `@@userId` (= `userinfo2.id`), `@@userName` (= `RTRIM(userinfo2.name)`), `@@unit`, `@@appDatabaseName`, `@@sysDatabaseName`, `@@primaryKeyList`, `@@primaryKeyListAlias`, `@@update(n)`, `@@checking(n)`, cộng các biến v2 (`@@table`, `@@primaryKey`, `@@key`, `@@checkIrregular`, `$controller`) cho file v2.

- `@@unit` chỉ được thay **sau khi** `unitCode` đã khớp một mã có trong `dmdvcs` và qua kiểm quyền. Lớp phòng thứ hai: escape `'`.
- Form được biên dịch **một lần lúc nạp XML** thành bản không đổi. Request không sửa form trong cache nữa (sửa lỗi 1.4 #4, #5).

---

## 3. Cơ sở dữ liệu `restApi*`

**Quy tắc:** object `restApi*` đang có **giữ nguyên cấu trúc**. Object viết mới cũng đặt tên `restApi*` (bảng, view) và `restApi_*` (proc, function). Không gọi hay sửa proc `api_*` của v2. Mọi script tương thích SQL Server 2008, để ở `sql/` của repo, chạy lại không lỗi.

### 3.1. Object có sẵn — v3 dùng thế nào

| Object | v3 dùng |
|---|---|
| `restApiUser` | Tài khoản API: `name` là tên đăng nhập, `password` (mã hóa) là khóa ký. **Nối với user FBO qua `name_erp = RTRIM(userinfo2.name)`** (đã chốt). Lúc đăng nhập v3 tra ra `userinfo2.id`; đó là `user_id` dùng cho mọi kiểm quyền FBO (`restApi_CheckUnitRights`, `@@userId`). `restApiUser.id` chỉ là khóa nội bộ của các bảng `restApi*` (`u_id`) |
| `restApiAllowForm(u_id, form varchar(32))` | Quyền theo tài nguyên **và** method: `form` = tên file XML không đuôi, vd `customer.get`, `sale-invoice.post`. Vì `form` rộng 32 nên tên resource tối đa 27 ký tự |
| `restApiAllowIp`, `restApiBlackListIp` | Giữ nguyên ý nghĩa: `255` = wildcard; user không khai IP thì cho mọi IP |
| `restApiLogError` + `restApi_InsertLogError` | Giữ, ghi qua tham số (sửa lỗi 1.4 #3 ở phía C#) |
| `restApiLogData` + `restApi_InsertLogData` | Dùng tới pha 4, sau đó thay bằng bảng log mới (3.2) |
| `restApiCache` + `restApi_RemoveCache` | v3 không dùng nữa (cache phân trang chuyển sang Redis). Giữ nguyên, không xoá |
| `restApiLogActivity`, `restApiLogTransfer`, `restApiNotify`, `restApiTransfer`, `restApiUpdate` | Giữ nguyên, dùng khi tới chức năng tương ứng |
| `restApi_CheckFormRights`, `restApi_CheckIPAddress` | Giữ nguyên, nhưng v3 chuyển sang `restApi_GetUserRights` (3.2). Lý do: proc cũ cho user **không khai dòng nào** được gọi **mọi form**, còn v3 cần mặc định từ chối; và mỗi request đang tốn hai lượt gọi DB |

### 3.2. Object viết mới

| Object | Pha | Nội dung |
|---|---|---|
| `restApi_CheckUnitRights` (proc) | 0 | Kiểm `unitCode` theo quyền FBO (bên dưới) |
| `restApi_GetUserRights` (proc) | 2 | Một lần gọi trả về: thông tin user, `admin` (từ `userinfo2`), danh sách `form` được phép (**không có dòng = không quyền**), danh sách IP cho phép / chặn. Kết quả cache ở Redis |
| `restApiLogReq`, `restApiLogBody`, `restApiLogAgg`, `restApiLogAttack`, `restApiLogAttackAgg` | 4 | Log mới (mục 5.2): dòng chỉ mục mỏng, body nén loại trùng (`IGNORE_DUP_KEY`), bảng gộp theo phút |
| `restApi_LogSearch`, `restApi_LogCleanup` | 4 | Xem log từ FBO/portal; dọn log theo lô (SQL 2008 không có `TRUNCATE ... PARTITIONS`) |

**`restApi_CheckUnitRights @user_id, @unit_code, @action`**
1. `@unit_code` phải có trong `dmdvcs` với `status = '1'`, nếu không trả `UNIT_NOT_FOUND`.
2. `userinfo2.admin = 1` → **được mọi đơn vị** (admin là quyền lớn nhất).
3. Còn lại → `dbo.ff_CheckUnitRight(@user_id, @unit_code, @action)` của FBO, với action theo method: `GET` → `Acc`, `POST` → `New`, `DELETE` → `Del`. POST cập nhật bản ghi có sẵn thì proc nghiệp vụ tự kiểm thêm `Edit`.

---

## 4. Thiết kế REST v3

### 4.1. Route

| Method | Đường dẫn | File XML / việc |
|---|---|---|
| `POST` | `/api/v3/auth/token` | Lấy JWT |
| `POST` | `/api/v3/auth/revoke` | Thu hồi JWT (đưa `jti` vào Redis tới khi hết hạn) |
| `GET` | `/api/v3/{resource}?unitCode=&…` | `{resource}.get.xml`, tham số lọc lấy từ query string |
| `GET` | `/api/v3/{resource}?cursor=…&page=n` | Trang `n` của kết quả đã truy vấn |
| `POST` | `/api/v3/{resource}?unitCode=` | `{resource}.post.xml`, body JSON |
| `POST` | `/api/v3/{resource}?unitCode=&dryRun=true` | Chạy hết `checking`, không chạy `processing`, rollback |
| `GET` | `/api/v3/_meta/resources` | Tài nguyên và method user được cấp quyền |
| `GET` | `/api/v3/_meta/resources/{resource}/{method}` | Định nghĩa khối, field, mẫu JSON |
| `GET` | `/health/live`, `/health/ready` | Cho load balancer. `ready` kiểm SQL + Redis |

- `resource` viết thường, nối bằng gạch ngang, chỉ nhận `[a-z0-9-]`, không phân biệt hoa thường.
- Có file nhưng sai method → `405` kèm `Allow`. Không có file → `404`.
- Đối chiếu v2: `setCustomers` → `customer.post.xml`, `getPayableReport` → `payable-report.get.xml`.

### 4.2. Xác thực và phân quyền
- **Lấy token:** `{ "userName": "NGUYENTDH", "time": 1792574400, "signature": hex(HMAC-SHA256(pri_key, "NGUYENTDH.1792574400")) }`, `userName` = `restApiUser.name`. Lệch giờ tối đa `ClockSkewSeconds`. Kiểm `restApiUser.status`; `name_erp` phải khớp một dòng `userinfo2` có `status = '1'`, không khớp thì từ chối; kiểm IP; khoá đăng nhập sai nhiều lần qua Redis. Trả JWT: `sub` = `restApiUser.name`, claim `u_id` = `restApiUser.id`, `user_id` = `userinfo2.id`, `name_erp`, `jti`.
- **Gọi API:** `Authorization: Bearer <jwt>`.
- **HMAC theo request** (user được bật ký theo request; cột cờ đặt ở đâu chốt cùng mục 8): `X-Fast-Timestamp`, `X-Fast-Nonce`, `X-Fast-Signature = hex(HMAC-SHA256(pri_key, METHOD\nPATH?QUERY\nTIMESTAMP\nNONCE\nhex(SHA256(body))))`. Nonce lưu Redis `SET NX EX`. Thay header `checksum` MD5. `unitCode` nằm trong query string nên được ký cùng.
- **Thứ tự kiểm:** user còn hiệu lực → IP (`restApiBlackListIp`, `restApiAllowIp`) → `restApiAllowForm` (`form` = `resource.method`) → `unitRequired`/`unitCode` → `restApi_CheckUnitRights`. Kết quả cache Redis vài phút. Admin đổi quyền thì publish lệnh xoá cache.

### 4.3. Request POST: object / flat / both

**object** — **một chứng từ**: khối gốc là object, khối con là mảng. Không cần lặp `YourID` ở dòng con:
```json
{ "master": { "YourID": "HD001", "VoucherDate": "21/10/2026", "CustomerID": "KH01" },
  "detail": [ { "ItemID": "VT01", "Quantity": 2 },
              { "ItemID": "VT02", "Quantity": 1 } ] }
```
Nhiều chứng từ một lần: gửi **mảng** các object trên. Dòng con có gửi kèm field khóa thì giá trị phải trùng với khối gốc, khác thì báo lỗi.

**flat** — mỗi dòng chứa field của mọi khối. v3 tách field về đúng khối, gộp dòng khối gốc theo `key`:
```json
[ { "YourID": "HD001", "VoucherDate": "21/10/2026", "CustomerID": "KH01", "ItemID": "VT01", "Quantity": 2 },
  { "YourID": "HD001", "VoucherDate": "21/10/2026", "CustomerID": "KH01", "ItemID": "VT02", "Quantity": 1 } ]
```
- `both`: phần tử có thuộc tính trùng tên khối gốc (`master`) → object; ngược lại → flat.
- Flat: hai dòng cùng `key` mà field khối gốc khác nhau → `INVALID_FLAT_CONFLICT`, chỉ rõ dòng.
- Kiểm tra gom đủ lỗi, có vị trí (`detail[2].Quantity`), mã lấy từ `Message.xml`.
- `Idempotency-Key` cho POST: Redis giữ response 24 giờ. Gửi lại đúng key thì nhận lại kết quả cũ; cùng key mà body khác → `409`.

### 4.4. Response và phân trang dataset nhiều bảng
Quy ước dataset do query `processing` trả về:
- Bảng 0: `code`, `message` (`field`, `condition` khi lỗi), tùy chọn `tables` = danh sách tên bảng dữ liệu, vd `'master,detail,tax'`.
- Bảng 1..N: dữ liệu. Không khai `tables` thì tên là `data` (1 bảng) hoặc `data1..dataN`.

```json
{ "code": "SUCCESS", "success": true, "message": "", "request_id": "8c2f…",
  "data": { "master": [ … ], "detail": [ … ], "tax": [ … ] },
  "paging": { "cursor": "a1b2…", "page_index": 0, "page_count": 12, "page_size": 100, "total_row_count": 1180 } }
```
Một bảng dữ liệu thì `data` là mảng, như RestApiCore hiện tại.

**Phân trang N bảng** — `<paging pageSize="100" key="stt_rec"/>`:
1. Cắt theo giá trị `key` của bảng dữ liệu đầu tiên: mỗi trang tối đa `pageSize` giá trị key, kèm mọi dòng cùng key ở các bảng khác. Một chứng từ không bị tách sang hai trang.
2. Bảng không có cột `key` chỉ trả ở trang 0.
3. Kết quả nén lưu Redis theo trang (TTL `CursorTtlMinutes`), instance nào cũng trả được. Bỏ cache file JSON và `restApiCache`.
4. Không khai `<paging>` thì trả hết một lần, có trần `MaxRowsWithoutPaging`.
5. `Transfer`: chỉ đánh dấu đã chuyển các dòng của trang đã trả.

Mã mới thêm vào `Message.xml`: `METHOD_NOT_ALLOWED` (405), `NOT_FOUND` (404), `UNIT_REQUIRED` (400), `UNIT_NOT_FOUND` (400), `UNIT_DENIED` (403), `INVALID_FLAT_CONFLICT` (400), `IDEMPOTENCY_CONFLICT` (409), `CURSOR_EXPIRED` (404), `INVALID_SIGNATURE` (401).

---

## 5. Redis, log, portal

### 5.1. Redis (Garnet)

| Khoá | Dùng cho |
|---|---|
| `fa3:{env}:user:{id}` | `restApiUser`, quyền form, IP, quyền đơn vị |
| `fa3:{env}:jti:{jti}` | JWT đã thu hồi |
| `fa3:{env}:nonce:{user}:{nonce}` | Chống replay HMAC |
| `fa3:{env}:rl:{ip}`, `:rlu:{user}` | Rate limit (Lua, nguyên tử) |
| `fa3:{env}:lock:{user}` | Khoá đăng nhập sai nhiều lần |
| `fa3:{env}:cur:{cursor}:{page}` | Trang kết quả đã nén |
| `fa3:{env}:idem:{user}:{key}` | Response theo `Idempotency-Key` |
| List `fa3:{env}:log` + `fa3:{env}:log:proc:{instance}` | Hàng đợi log tin cậy: `RPUSH` vào `log`; worker `LMOVE` sang `proc:{instance}`, ghi SQL xong mới `DEL`. Khởi động lại thì xử lý tiếp phần còn trong `proc:{instance}`. `LTRIM` giữ trần `LogQueueMax` |
| `fa3:{env}:job:{name}` | Khoá chạy job trên 1 instance |

Redis lỗi: `/health/ready` báo không sẵn sàng; tra user/quyền rơi về SQL; rate limit, nonce, idempotency từ chối an toàn (`503`); log ghi tạm ra file. Garnet bật checkpoint (`-c <thư mục> -r --checkpoint-freq 60`).

**Đã thử trên Garnet 2.2.3 từ .NET (pha 0):** chạy được `PING`, `INFO`, `SET NX EX`, `EVAL` Lua (khi bật `--lua true`), Hash nhị phân, `RPUSH`/`LMOVE`/`BLMOVE`/`LPOP count`/`LREM`/`LTRIM`, `MULTI/EXEC`, `SCAN`, `PUBLISH/SUBSCRIBE`. **Không chạy được**: `XADD`, `XGROUP`, `XREADGROUP`, `XACK` (`ERR unknown command`).

### 5.2. Log mới
Middleware gom thông tin rồi `RPUSH` vào List `fa3:{env}:log`, không chờ SQL. `LogWorker` (BackgroundService, mỗi instance một worker) `LMOVE` từng lô sang `log:proc:{instance}`, `SqlBulkCopy` vào các bảng log mới (3.2). SQL lỗi thì ghi spool `.jsonl.gz`, đẩy lại sau. Body nén GZip, loại trùng theo hash. Dọn theo `LogRetentionDays` bằng `restApi_LogCleanup` (xoá theo lô vì SQL 2008 R2 không có `TRUNCATE ... PARTITIONS`). `X-Request-ID` (đã có) = `req_id` để tra log.

### 5.3. Cổng đối tác (`ApiDocs`)
Lấy dữ liệu từ `/api/v3/_meta`:
- **Đăng nhập:** `userId` + `pri_key`. Chữ ký tính trên trình duyệt (WebCrypto), khóa không gửi lên server.
- **Danh sách tài nguyên** theo quyền, kèm method.
- **Chi tiết:** các khối và field (tên · kiểu · độ rộng thật · bắt buộc · giá trị cho phép · mô tả), mẫu JSON object và flat, có cần `unitCode` không, có phân trang không.
- **Gọi thử:** `unitCode`, tham số GET, nút "Kiểm tra" (`dryRun`) và "Gửi thật" (hỏi xác nhận), nút "Trang tiếp".
- **Code mẫu:** cURL, C#, JavaScript, Python, có cách ký HMAC.
- **Bảng mã lỗi:** sinh từ `Message.xml`.

---

## 6. Kiểm thử

| Bộ | Nội dung |
|---|---|
| Unit | Đọc XML v2/v3, tách object/flat, tự gán khóa cho khối con, kiểm kiểu/độ rộng, dựng `@@update`/`@@checking`, cắt trang N bảng, HMAC |
| Parity với v2 | Mỗi XML v2 + JSON mẫu: `#_data` của v3 phải giống `api_<control>_tmp` mà v2 ghi ra khi `dev=1`; cùng dữ liệu sai thì cùng bị từ chối. Chạy trong transaction rồi rollback |
| Integration | DB dev + Garnet: GET/POST, `unitRequired`, `unitCode` sai quyền / không tồn tại, phân trang 3 bảng, idempotency, 2 instance dùng chung Redis |
| Bảo mật | SQL injection qua `unitCode`, tham số GET, tên resource; replay nonce; JWT đã thu hồi; user không có dòng `restApiAllowForm` |
| Tải | N instance sau ARR, độ trễ khi bật log và phân trang |

---

## 7. Lộ trình

| Pha | Việc | Kết quả kiểm được |
|---|---|---|
| **0. Dọn nền** | Lỗi 1.4 #1, #8. Nâng .NET 10 và **thử kết nối tới SQL 2008 R2**. Cài Garnet dev. Script `restApi_CheckUnitRights`. Thêm project test | Build sạch .NET 10; đăng nhập bằng user `userinfo2` |
| **1. XML `<import>`** | Bộ đọc v2 + v3, form biên dịch một lần, kiểu/độ rộng từ DB, `reference`/`check`, object/flat/both, chế độ v2 + Parity | Parity khớp XML v2; `sale-invoice.post.xml` chạy cả hai dạng JSON |
| **2. Route + bảo mật** | GET/POST theo resource, `unitRequired`/`unitCode` + `restApi_CheckUnitRights`, `restApi_GetUserRights`, HMAC, Redis cho rate limit/nonce/revoke/quyền, sửa lỗi 1.4 #2–#7, `dryRun`, `Idempotency-Key` | Bộ test bảo mật đạt; 2 instance dùng chung trạng thái |
| **3. Phân trang N bảng** | Quy ước bảng 0 (`tables`), cắt theo key, Redis, `Transfer` theo trang | Báo cáo 3 bảng × 10.000 dòng lấy đủ qua cursor, không tách chứng từ |
| **4. Log mới** | Middleware → List Redis → worker → `restApiLogReq`…, spool, dọn dữ liệu, `restApi_LogSearch` | Log không làm tăng độ trễ request |
| **5. Portal** | `ApiDocs` theo 5.3 | Đối tác thử đủ luồng |
| **6. Phát hành** | Gói deploy (Hosting Bundle, site IIS, dịch vụ Garnet, appsettings mẫu không có bí mật, script SQL), tài liệu đối tác, hướng dẫn chuyển từ v2 | Cài trên bản sao DB khách đang chạy v2: v2 vẫn chạy, v3 chạy song song |

Phụ thuộc: 0 → 1 → 2. Sau 2 thì 3, 4, 5 song song. Cuối cùng là 6.

---

## 7.1. Tiến độ pha 0 (21/10/2026, nhánh `v3/phase0`)

| Việc | Trạng thái |
|---|---|
| Gỡ bí mật khỏi git: giá trị thật chuyển sang `Api/appsettings.Local.json` (gitignore), `appsettings.json` của `Api` và `CallbackWorker` để trống, có `appsettings.Local.example.json`. `Program.cs` nạp file Local + biến môi trường, báo lỗi rõ nếu thiếu | Xong. **Còn việc của người phụ trách:** đổi mật khẩu SQL và `secretKey` (giá trị cũ vẫn còn trong lịch sử git) |
| Bỏ theo dõi `Api/Log/*.json` | Xong |
| Nâng .NET 8 → 10, cập nhật gói (SqlClient 6.1.7, Hosting/Caching 10.0.13, Jwt 8.23.0, Newtonsoft 13.0.4), ghim `System.Security.Cryptography.Pkcs` 10.0.13 để bỏ 2 lỗ hổng | Xong, build 0 lỗi, không còn cảnh báo lỗ hổng |
| Thử kết nối SQL 2008 R2 | Xong, kết quả ở mục 1.3 |
| Project `Tests` (xUnit), 3 test chốt hành vi `EncryptionHelper` | Xong, 3/3 đạt |
| `sql/restApi_CheckUnitRights.sql`: đọc `admin` qua view chuẩn `vsysuserinfo`, user thường theo `ff_CheckUnitRight` | Xong, đã tạo trên DB dev, thử 10 trường hợp (admin, đơn vị không tồn tại, action sai, user không tồn tại, chuỗi injection, user thường có/không quyền) đều đúng |
| Cài Garnet cho máy dev | Xong: `dotnet tool install -g garnet-server --version 2.2.3`, chạy bằng `tools/garnet/start-garnet-dev.ps1` (chỉ nghe `127.0.0.1:6379`, bật Lua, checkpoint ở `%LOCALAPPDATA%\Garnet\dev`). Đã thử các lệnh ở 5.1 |

## 8. Đã chốt và còn cần chốt

Đã chốt (21/10/2026): tài khoản API nối với user FBO qua `restApiUser.name_erp`; admin `userinfo2.admin = 1` là quyền lớn nhất, được mọi đơn vị; SQL Server 2008 là bản thấp nhất; object `restApi*` giữ nguyên, object mới cũng tên `restApi*`.

Còn cần chốt:
1. Cờ bật **ký HMAC theo request** và cờ **cho vào portal** theo user: `restApiUser` giữ nguyên cấu trúc nên cần chỗ khác. Đề xuất bảng mới `restApiUserOption(u_id, hmac_yn, portal_yn)`; cần chốt trước pha 2.

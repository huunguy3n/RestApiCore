namespace Models
{
    public class AppConfig
    {
        public string secretKey
        {
            get; set;
        }

        /// <summary>
        /// Khóa AES mã hóa restApiUser.password, tách riêng khỏi secretKey ký JWT
        /// </summary>
        public string passwordKey
        {
            get; set;
        }

        /// <summary>
        /// Khóa chủ AES-256-GCM mã hóa restApiUser.private_key (base64 32 byte), đọc từ Security:KeyEncryptionKey
        /// </summary>
        public string keyEncryptionKey
        {
            get; set;
        }

        /// <summary>
        /// Độ lệch tối đa (giây) giữa giây trong X-Fast-Nonce và giờ server; nonce giữ 2 lần khoảng này
        /// </summary>
        public int clockSkewSeconds
        {
            get; set;
        }

        /// <summary>
        /// Giới hạn byte body request v3 (đọc một lần vào bộ nhớ để băm và parse)
        /// </summary>
        public long maxRequestBodyBytes
        {
            get; set;
        }

        /// <summary>
        /// Số giây cache quyền và khóa ký của user (restApi_GetUserRights)
        /// </summary>
        public int userCacheSeconds
        {
            get; set;
        }

        /// <summary>
        /// Thời hạn (phút) JWT v3 cấp ở /api/auth/token
        /// </summary>
        public int tokenMinutes
        {
            get; set;
        }

        /// <summary>
        /// Số lần chữ ký sai liên tiếp ở auth/token trước khi khóa đăng nhập
        /// </summary>
        public int loginMaxFailures
        {
            get; set;
        }

        /// <summary>
        /// DEV | PROD. PROD: kiểm license (restApi.lic) và manifest toàn vẹn lúc khởi động
        /// </summary>
        public string environment
        {
            get; set;
        }

        public bool IsProduction => string.Equals(environment, "PROD", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Mã dự án (nbdmda / portalProject.ma_da). License cấp theo mã này
        /// </summary>
        public string projectCode
        {
            get; set;
        }

        /// <summary>
        /// true: license không hợp lệ thì /api trả 503 LICENSE_INVALID. false (mặc định): chỉ ghi log và báo ở /health/ready
        /// </summary>
        public bool licenseEnforce
        {
            get; set;
        }

        /// <summary>
        /// Origin của WebPortal được gọi /api từ trình duyệt (CORS, màn "Gọi thử"), vd "https://portal.noibo". Rỗng: không mở CORS
        /// </summary>
        public string[] portalOrigins
        {
            get; set;
        }

        /// <summary>
        /// Số phút khóa đăng nhập (đếm lại từ lần sai cuối)
        /// </summary>
        public int loginLockMinutes
        {
            get; set;
        }

        /// <summary>
        /// Số chứng từ tối đa trong một body v3 (mảng)
        /// </summary>
        public int maxDocumentsPerRequest
        {
            get; set;
        }

        /// <summary>
        /// Số giờ giữ response theo Idempotency-Key
        /// </summary>
        public int idempotencyHours
        {
            get; set;
        }

        /// <summary>
        /// Số phút giữ các trang của một cursor phân trang
        /// </summary>
        public int cursorTtlMinutes
        {
            get; set;
        }

        /// <summary>
        /// Form không khai &lt;paging&gt;: tổng số dòng tối đa được trả trong một response
        /// </summary>
        public int maxRowsWithoutPaging
        {
            get; set;
        }

        /// <summary>
        /// Log v3: số mục tối đa chờ ghi trong bộ nhớ (đầy thì bỏ mục mới, không làm chậm request)
        /// </summary>
        public int logQueueCapacity
        {
            get; set;
        }

        /// <summary>
        /// Log v3: số request ghi SQL mỗi lô
        /// </summary>
        public int logBatchSize
        {
            get; set;
        }

        /// <summary>
        /// Log v3: body lớn hơn thì chỉ lưu SHA256, không lưu nội dung
        /// </summary>
        public int logMaxBodyBytes
        {
            get; set;
        }

        /// <summary>
        /// Số ngày giữ log chi tiết (restApiLogReq, restApiLogBody); quá hạn thì đổ vào restApiLogAgg rồi xoá
        /// </summary>
        public int logRetentionDays
        {
            get; set;
        }

        /// <summary>
        /// Số ngày giữ restApiLogAttack
        /// </summary>
        public int attackLogRetentionDays
        {
            get; set;
        }

        public string issuerToken
        {
            get; set;
        }

        public int maxRequestsPerMinute
        {
            get; set;
        }

        /// <summary>
        /// Số giây cache kết quả kiểm IP (restApi_CheckIPAddress)
        /// </summary>
        public int ipCheckCacheSeconds
        {
            get; set;
        }

        public int commandTimeout
        {
            get; set;
        }

        public bool isDevState
        {
            get; set;
        }

        public string appDatabaseName
        {
            get; set;
        }

        public string sysDatabaseName
        {
            get; set;
        }

        public string appConnectionString
        {
            get; set;
        }

        public string pathDirectory
        {
            get; set;
        }

        /// <summary>
        /// Thư mục log file (Serilog), nằm ngoài thư mục release. Trống thì dùng ProgramData\FastRestApi\Logs
        /// </summary>
        public string logDirectory
        {
            get; set;
        }

        public AppConfig()
        {
            secretKey = string.Empty;
            passwordKey = string.Empty;
            keyEncryptionKey = string.Empty;
            clockSkewSeconds = 300;
            maxRequestBodyBytes = 10 * 1024 * 1024;
            userCacheSeconds = 60;
            tokenMinutes = 60;
            maxDocumentsPerRequest = 100;
            idempotencyHours = 24;
            cursorTtlMinutes = 30;
            maxRowsWithoutPaging = 10000;
            logQueueCapacity = 10000;
            logBatchSize = 200;
            logMaxBodyBytes = 1024 * 1024;
            logRetentionDays = 30;
            attackLogRetentionDays = 90;
            loginMaxFailures = 5;
            environment = "DEV";
            projectCode = string.Empty;
            licenseEnforce = false;
            portalOrigins = [];
            loginLockMinutes = 15;
            issuerToken = string.Empty;
            maxRequestsPerMinute = 0;
            ipCheckCacheSeconds = 60;
            commandTimeout = 360;
            isDevState = false;
            appDatabaseName = string.Empty;
            sysDatabaseName = string.Empty;
            appConnectionString = string.Empty;
            pathDirectory = string.Empty;
            logDirectory = string.Empty;
        }
    }
}

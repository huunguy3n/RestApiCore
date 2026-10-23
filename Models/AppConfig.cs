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
        /// Độ lệch tối đa (giây) giữa X-Fast-Timestamp và giờ server; nonce giữ 2 lần khoảng này
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

        public int expiryToken
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

        public int maxRowInResponse
        {
            get; set;
        }

        public int checkTypeResponse
        {
            get; set;
        }

        public int commandTimeout
        {
            get; set;
        }

        public int cacheDurationInMinutes
        {
            get; set;
        }

        public bool isDevState
        {
            get; set;
        }

        public string positions
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
            expiryToken = 0;
            issuerToken = string.Empty;
            maxRequestsPerMinute = 0;
            ipCheckCacheSeconds = 60;
            maxRowInResponse = 0;
            checkTypeResponse = 1;
            commandTimeout = 360;
            cacheDurationInMinutes = 10;
            isDevState = false;
            positions = string.Empty;
            appDatabaseName = string.Empty;
            sysDatabaseName = string.Empty;
            appConnectionString = string.Empty;
            pathDirectory = string.Empty;
            logDirectory = string.Empty;
        }
    }
}

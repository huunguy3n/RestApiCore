namespace Models
{
    public class AppConfig
    {
        public string secretKey
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

        public AppConfig()
        {
            secretKey = string.Empty;
            expiryToken = 0;
            issuerToken = string.Empty;
            maxRequestsPerMinute = 0;
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
        }
    }
}

-- Log v3 (KE_HOACH_V3 5.2): request, body nén loại trùng theo SHA256, tổng hợp ngày, sự kiện tấn công
-- Tương thích SQL Server 2008, chạy lại được. Thay restApiLogData (giữ nguyên, không xoá)
--C--NGUYENTDH 23/10/2026

-- Mỗi request một dòng. Không lưu private_key, chữ ký HMAC, JWT, connection string
IF OBJECT_ID('dbo.restApiLogReq', 'U') IS NULL
	CREATE TABLE dbo.restApiLogReq
	(
		id BIGINT IDENTITY(1, 1) NOT NULL,
		req_id UNIQUEIDENTIFIER NOT NULL,
		created_at DATETIME NOT NULL,
		u_id INT NULL,
		user_name VARCHAR(32) NULL,
		ip_address VARCHAR(45) NULL,
		method VARCHAR(10) NOT NULL,
		path NVARCHAR(256) NOT NULL,
		query_string NVARCHAR(1024) NULL,
		form VARCHAR(32) NULL,
		status_code INT NOT NULL,
		result_code VARCHAR(64) NULL,
		duration_ms INT NOT NULL,
		request_bytes INT NOT NULL,
		response_bytes INT NOT NULL,
		request_sha256 CHAR(64) NULL, -- khóa restApiLogBody
		response_sha256 CHAR(64) NULL,
		content_sha256 VARCHAR(64) NULL, -- X-Fast-Content-SHA256 client gửi
		nonce VARCHAR(64) NULL,
		client_timestamp BIGINT NULL,
		signature_result VARCHAR(32) NULL, -- VALID hoặc mã lỗi kiểm chữ ký; NULL = route không cần ký
		idempotency_key VARCHAR(128) NULL,
		CONSTRAINT PK_restApiLogReq PRIMARY KEY (id)
	)
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiLogReq') AND name = 'IX_restApiLogReq_created_at')
	CREATE INDEX IX_restApiLogReq_created_at ON dbo.restApiLogReq (created_at)
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiLogReq') AND name = 'IX_restApiLogReq_u_id')
	CREATE INDEX IX_restApiLogReq_u_id ON dbo.restApiLogReq (u_id, created_at)
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiLogReq') AND name = 'IX_restApiLogReq_req_id')
	CREATE INDEX IX_restApiLogReq_req_id ON dbo.restApiLogReq (req_id)
GO

-- Body nén GZip, một bản cho mỗi SHA256 (request lặp lại cùng body chỉ lưu một lần)
IF OBJECT_ID('dbo.restApiLogBody', 'U') IS NULL
	CREATE TABLE dbo.restApiLogBody
	(
		sha256 CHAR(64) NOT NULL,
		created_at DATETIME NOT NULL,
		size INT NOT NULL, -- byte trước khi nén
		content VARBINARY(MAX) NOT NULL,
		CONSTRAINT PK_restApiLogBody PRIMARY KEY (sha256)
	)
GO

-- Tổng hợp theo ngày, giữ lâu hơn log chi tiết (restApi_LogCleanup đổ vào trước khi xoá)
IF OBJECT_ID('dbo.restApiLogAgg', 'U') IS NULL
	CREATE TABLE dbo.restApiLogAgg
	(
		log_date DATETIME NOT NULL,
		u_id INT NOT NULL, -- 0 = chưa xác thực
		form VARCHAR(32) NOT NULL, -- '' = route hệ thống
		status_code INT NOT NULL,
		request_count INT NOT NULL,
		total_ms BIGINT NOT NULL,
		max_ms INT NOT NULL,
		CONSTRAINT PK_restApiLogAgg PRIMARY KEY (log_date, u_id, form, status_code)
	)
GO

-- Sự kiện nghi tấn công: chữ ký sai, nonce dùng lại, khóa đăng nhập, IP bị chặn, vượt rate limit…
IF OBJECT_ID('dbo.restApiLogAttack', 'U') IS NULL
	CREATE TABLE dbo.restApiLogAttack
	(
		id BIGINT IDENTITY(1, 1) NOT NULL,
		req_id UNIQUEIDENTIFIER NOT NULL,
		created_at DATETIME NOT NULL,
		ip_address VARCHAR(45) NULL,
		user_name VARCHAR(32) NULL,
		path NVARCHAR(256) NOT NULL,
		code VARCHAR(64) NOT NULL,
		CONSTRAINT PK_restApiLogAttack PRIMARY KEY (id)
	)
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiLogAttack') AND name = 'IX_restApiLogAttack_created_at')
	CREATE INDEX IX_restApiLogAttack_created_at ON dbo.restApiLogAttack (created_at)
GO

IF OBJECT_ID('dbo.restApiLogAttackAgg', 'U') IS NULL
	CREATE TABLE dbo.restApiLogAttackAgg
	(
		log_date DATETIME NOT NULL,
		ip_address VARCHAR(45) NOT NULL,
		code VARCHAR(64) NOT NULL,
		event_count INT NOT NULL,
		CONSTRAINT PK_restApiLogAttackAgg PRIMARY KEY (log_date, ip_address, code)
	)
GO

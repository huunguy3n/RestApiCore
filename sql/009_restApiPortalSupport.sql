-- Hỗ trợ WebPortal / PortalApp trên DB dự án (ADR-0001). SQL Server 2008, chạy lại được, chạy SAU 001-008.
--  1. restApiUser: token_valid_after (thu hồi mọi JWT phát trước mốc này), key_issued_at, synced_at
--  2. restApiAudit (nhật ký thao tác của PortalApp / WebPortal trên dự án), restApiLicense (license đã kích hoạt)
--  3. Bỏ bảng vai trò portal cũ (sql/008 cũ): restApiPortalUserRole, restApiPortalRolePermission, restApiPortalRole
--  4. restApi_GetUserRights trả thêm token_valid_after
--  5. restApiLogAgg thêm 4 bucket thời gian xử lý; restApi_LogCleanup đổ thêm các bucket này
--  6. Index log cho màn hiệu suất
-- Rollback: DROP TABLE restApiLicense; bỏ 3 cột mới của restApiUser; chạy lại 005 và 007 để về proc cũ

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiUser') AND name = 'token_valid_after')
	ALTER TABLE dbo.restApiUser ADD token_valid_after DATETIME NULL -- giờ UTC (GETUTCDATE)

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiUser') AND name = 'key_issued_at')
	ALTER TABLE dbo.restApiUser ADD key_issued_at DATETIME NULL

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiUser') AND name = 'synced_at')
	ALTER TABLE dbo.restApiUser ADD synced_at DATETIME NULL -- lần cuối WebPortal đẩy xuống
GO

-- Nhật ký thao tác: chỉ ghi thêm. Không lưu mật khẩu, khóa, JWT, connection string
IF OBJECT_ID('dbo.restApiAudit', 'U') IS NULL
	CREATE TABLE dbo.restApiAudit
	(
		id BIGINT IDENTITY(1, 1) NOT NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_restApiAudit_created_at DEFAULT (GETDATE()),
		actor_u_id INT NULL,
		actor_name VARCHAR(32) NULL,
		action VARCHAR(64) NOT NULL,
		target_type VARCHAR(32) NULL,
		target VARCHAR(128) NULL,
		outcome VARCHAR(16) NOT NULL, -- SUCCESS | DENIED | REJECTED
		req_id VARCHAR(64) NULL,
		detail NVARCHAR(1024) NULL,
		CONSTRAINT PK_restApiAudit PRIMARY KEY (id)
	)
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiAudit') AND name = 'source')
	ALTER TABLE dbo.restApiAudit ADD source VARCHAR(16) NULL -- PORTAL | PORTALAPP

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiAudit') AND name = 'machine_name')
	ALTER TABLE dbo.restApiAudit ADD machine_name NVARCHAR(64) NULL

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiAudit') AND name = 'windows_user')
	ALTER TABLE dbo.restApiAudit ADD windows_user NVARCHAR(128) NULL -- PortalApp không có đăng nhập
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiAudit') AND name = 'IX_restApiAudit_created_at')
	CREATE INDEX IX_restApiAudit_created_at ON dbo.restApiAudit (created_at)
GO

-- License đã kích hoạt trên máy này (tra soát; file .lic mới là thứ được kiểm)
IF OBJECT_ID('dbo.restApiLicense', 'U') IS NULL
	CREATE TABLE dbo.restApiLicense
	(
		license_id UNIQUEIDENTIFIER NOT NULL,
		ma_da VARCHAR(32) NOT NULL,
		mac VARCHAR(512) NOT NULL,
		machine_name NVARCHAR(64) NULL,
		expires_at DATETIME NULL,
		activated_at DATETIME NOT NULL CONSTRAINT DF_restApiLicense_activated_at DEFAULT (GETDATE()),
		activated_by_windows_user NVARCHAR(128) NULL,
		CONSTRAINT PK_restApiLicense PRIMARY KEY (license_id)
	)
GO

-- Vai trò portal cũ: thay bằng ba loại tài khoản cố định trong WebPortal
IF OBJECT_ID('dbo.restApiPortalUserRole', 'U') IS NOT NULL DROP TABLE dbo.restApiPortalUserRole
IF OBJECT_ID('dbo.restApiPortalRolePermission', 'U') IS NOT NULL DROP TABLE dbo.restApiPortalRolePermission
IF OBJECT_ID('dbo.restApiPortalRole', 'U') IS NOT NULL DROP TABLE dbo.restApiPortalRole
GO

-- Bucket thời gian xử lý để màn hiệu suất không phải quét log chi tiết
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiLogAgg') AND name = 'count_lt100')
	ALTER TABLE dbo.restApiLogAgg ADD count_lt100 INT NOT NULL CONSTRAINT DF_restApiLogAgg_lt100 DEFAULT (0)

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiLogAgg') AND name = 'count_lt500')
	ALTER TABLE dbo.restApiLogAgg ADD count_lt500 INT NOT NULL CONSTRAINT DF_restApiLogAgg_lt500 DEFAULT (0)

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiLogAgg') AND name = 'count_lt2000')
	ALTER TABLE dbo.restApiLogAgg ADD count_lt2000 INT NOT NULL CONSTRAINT DF_restApiLogAgg_lt2000 DEFAULT (0)

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.restApiLogAgg') AND name = 'count_ge2000')
	ALTER TABLE dbo.restApiLogAgg ADD count_ge2000 INT NOT NULL CONSTRAINT DF_restApiLogAgg_ge2000 DEFAULT (0)
GO

-- Màn hiệu suất đọc log gần đây theo khoảng ngày (form, status, thời gian) mà không chạm dòng dữ liệu
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiLogReq') AND name = 'IX_restApiLogReq_perf')
	CREATE INDEX IX_restApiLogReq_perf ON dbo.restApiLogReq (created_at) INCLUDE (form, status_code, duration_ms, u_id)
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiLogReq') AND name = 'IX_restApiLogReq_form')
	CREATE INDEX IX_restApiLogReq_form ON dbo.restApiLogReq (form, created_at) INCLUDE (status_code, duration_ms)
GO

IF OBJECT_ID('dbo.restApi_GetUserRights', 'P') IS NOT NULL
	DROP PROCEDURE dbo.restApi_GetUserRights
GO

CREATE PROCEDURE dbo.restApi_GetUserRights
	@UserName VARCHAR(32) -- restApiUser.name
AS
--C--NGUYENTDH 24/10/2026: như 005, result set 1 thêm token_valid_after (UTC)
--  1. user: restApiUser + userinfo2 (qua name_erp = userinfo2.name)
--  2. form được phép (không có dòng = mọi form)
--  3. IP cho phép của user (không có dòng = mọi IP)
--  4. IP bị chặn (toàn hệ thống)
BEGIN
	SET NOCOUNT ON

	DECLARE @UID INT

	SELECT @UID = a.id FROM dbo.restApiUser a WHERE a.[name] = @UserName

	SELECT a.id AS u_id, a.[name], a.name_erp, a.[status], a.private_key, a.token_valid_after
		 , b.id AS user_id, b.admin, b.[status] AS erp_status
		FROM dbo.restApiUser a
			LEFT JOIN dbo.vsysuserinfo b ON b.[name] = a.name_erp
		WHERE a.id = @UID

	SELECT a.form FROM dbo.restApiAllowForm a WHERE a.u_id = @UID

	SELECT a.ip_address FROM dbo.restApiAllowIp a WHERE a.u_id = @UID

	SELECT a.ip_address FROM dbo.restApiBlackListIp a WHERE @UID IS NOT NULL

	SET NOCOUNT OFF
END
GO

IF OBJECT_ID('dbo.restApi_LogCleanup', 'P') IS NOT NULL
	DROP PROCEDURE dbo.restApi_LogCleanup
GO

CREATE PROCEDURE dbo.restApi_LogCleanup
	@RetentionDays INT, -- giữ log chi tiết (restApiLogReq, restApiLogBody)
	@AttackRetentionDays INT, -- giữ restApiLogAttack
	@BatchSize INT = 5000
AS
--C--NGUYENTDH 24/10/2026: như 007, đổ thêm 4 bucket thời gian xử lý vào restApiLogAgg
BEGIN
	SET NOCOUNT ON

	DECLARE @Cut DATETIME, @AttackCut DATETIME, @Rows INT, @Requests INT, @Bodies INT, @Attacks INT

	SELECT @Cut = DATEADD(DAY, -@RetentionDays, DATEADD(DAY, DATEDIFF(DAY, 0, GETDATE()), 0))
	SELECT @AttackCut = DATEADD(DAY, -@AttackRetentionDays, DATEADD(DAY, DATEDIFF(DAY, 0, GETDATE()), 0))
	SELECT @Requests = 0, @Bodies = 0, @Attacks = 0

	CREATE TABLE #batch (id BIGINT PRIMARY KEY)

	-- 1. Request quá hạn: cộng vào restApiLogAgg rồi xoá
	SELECT @Rows = 1
	WHILE @Rows > 0 BEGIN
		TRUNCATE TABLE #batch

		INSERT INTO #batch (id) SELECT TOP (@BatchSize) a.id FROM dbo.restApiLogReq a WHERE a.created_at < @Cut ORDER BY a.id

		SELECT @Rows = @@ROWCOUNT

		IF @Rows > 0 BEGIN
			BEGIN TRANSACTION

			SELECT DATEADD(DAY, DATEDIFF(DAY, 0, a.created_at), 0) AS log_date, ISNULL(a.u_id, 0) AS u_id
				 , ISNULL(a.form, '') AS form, a.status_code, COUNT(1) AS request_count
				 , SUM(CAST(a.duration_ms AS BIGINT)) AS total_ms, MAX(a.duration_ms) AS max_ms
				 , SUM(CASE WHEN a.duration_ms < 100 THEN 1 ELSE 0 END) AS count_lt100
				 , SUM(CASE WHEN a.duration_ms >= 100 AND a.duration_ms < 500 THEN 1 ELSE 0 END) AS count_lt500
				 , SUM(CASE WHEN a.duration_ms >= 500 AND a.duration_ms < 2000 THEN 1 ELSE 0 END) AS count_lt2000
				 , SUM(CASE WHEN a.duration_ms >= 2000 THEN 1 ELSE 0 END) AS count_ge2000
				INTO #agg
				FROM dbo.restApiLogReq a
					JOIN #batch b ON b.id = a.id
				GROUP BY DATEADD(DAY, DATEDIFF(DAY, 0, a.created_at), 0), ISNULL(a.u_id, 0), ISNULL(a.form, ''), a.status_code

			UPDATE a SET a.request_count = a.request_count + b.request_count, a.total_ms = a.total_ms + b.total_ms
				 , a.max_ms = CASE WHEN b.max_ms > a.max_ms THEN b.max_ms ELSE a.max_ms END
				 , a.count_lt100 = a.count_lt100 + b.count_lt100, a.count_lt500 = a.count_lt500 + b.count_lt500
				 , a.count_lt2000 = a.count_lt2000 + b.count_lt2000, a.count_ge2000 = a.count_ge2000 + b.count_ge2000
				FROM dbo.restApiLogAgg a
					JOIN #agg b ON b.log_date = a.log_date AND b.u_id = a.u_id AND b.form = a.form AND b.status_code = a.status_code

			INSERT INTO dbo.restApiLogAgg (log_date, u_id, form, status_code, request_count, total_ms, max_ms
				 , count_lt100, count_lt500, count_lt2000, count_ge2000)
				SELECT a.log_date, a.u_id, a.form, a.status_code, a.request_count, a.total_ms, a.max_ms
					 , a.count_lt100, a.count_lt500, a.count_lt2000, a.count_ge2000
					FROM #agg a
					WHERE NOT EXISTS (SELECT 1 FROM dbo.restApiLogAgg b
						WHERE b.log_date = a.log_date AND b.u_id = a.u_id AND b.form = a.form AND b.status_code = a.status_code)

			DELETE a FROM dbo.restApiLogReq a JOIN #batch b ON b.id = a.id

			DROP TABLE #agg

			COMMIT TRANSACTION

			SELECT @Requests = @Requests + @Rows
		END
	END

	-- 2. Body quá hạn không còn request nào trỏ tới
	SELECT @Rows = 1
	WHILE @Rows > 0 BEGIN
		DELETE TOP (@BatchSize) a
			FROM dbo.restApiLogBody a
			WHERE a.created_at < @Cut
				AND NOT EXISTS (SELECT 1 FROM dbo.restApiLogReq b WHERE b.request_sha256 = a.sha256)
				AND NOT EXISTS (SELECT 1 FROM dbo.restApiLogReq b WHERE b.response_sha256 = a.sha256)

		SELECT @Rows = @@ROWCOUNT, @Bodies = @Bodies + @@ROWCOUNT
	END

	-- 3. Sự kiện tấn công quá hạn: cộng vào restApiLogAttackAgg rồi xoá
	SELECT @Rows = 1
	WHILE @Rows > 0 BEGIN
		TRUNCATE TABLE #batch

		INSERT INTO #batch (id) SELECT TOP (@BatchSize) a.id FROM dbo.restApiLogAttack a WHERE a.created_at < @AttackCut ORDER BY a.id

		SELECT @Rows = @@ROWCOUNT

		IF @Rows > 0 BEGIN
			BEGIN TRANSACTION

			SELECT DATEADD(DAY, DATEDIFF(DAY, 0, a.created_at), 0) AS log_date, ISNULL(a.ip_address, '') AS ip_address, a.code
				 , COUNT(1) AS event_count
				INTO #attack
				FROM dbo.restApiLogAttack a
					JOIN #batch b ON b.id = a.id
				GROUP BY DATEADD(DAY, DATEDIFF(DAY, 0, a.created_at), 0), ISNULL(a.ip_address, ''), a.code

			UPDATE a SET a.event_count = a.event_count + b.event_count
				FROM dbo.restApiLogAttackAgg a
					JOIN #attack b ON b.log_date = a.log_date AND b.ip_address = a.ip_address AND b.code = a.code

			INSERT INTO dbo.restApiLogAttackAgg (log_date, ip_address, code, event_count)
				SELECT a.log_date, a.ip_address, a.code, a.event_count
					FROM #attack a
					WHERE NOT EXISTS (SELECT 1 FROM dbo.restApiLogAttackAgg b
						WHERE b.log_date = a.log_date AND b.ip_address = a.ip_address AND b.code = a.code)

			DELETE a FROM dbo.restApiLogAttack a JOIN #batch b ON b.id = a.id

			DROP TABLE #attack

			COMMIT TRANSACTION

			SELECT @Attacks = @Attacks + @Rows
		END
	END

	SELECT @Requests AS requests, @Bodies AS bodies, @Attacks AS attacks

	SET NOCOUNT OFF
END
GO

-- Đối tượng nền restApi* cho DB chưa từng cài FastAPI v2 (PortalApp setup trên DB mới). SQL Server 2008, chạy lại được.
-- Chỉ tạo khi CHƯA có: DB đã có v2 thì giữ nguyên bảng/proc cũ, không ghi đè.
-- Rollback (chỉ DB mới, chưa có dữ liệu): DROP PROCEDURE restApi_InsertLogError, restApi_CheckIPAddress;
--   DROP TABLE restApiLogError, restApiBlackListIp, restApiAllowIp, restApiAllowForm, restApiUser

IF OBJECT_ID('dbo.restApiUser', 'U') IS NULL
	CREATE TABLE dbo.restApiUser
	(
		id INT IDENTITY(1, 1) NOT NULL,
		[name] VARCHAR(32) NOT NULL, -- tên đăng nhập API
		[password] VARCHAR(256) NULL, -- không còn dùng (đăng nhập portal kiểm ở PortalDb)
		name_erp VARCHAR(30) NOT NULL, -- = userinfo2.name
		[status] CHAR(1) NOT NULL CONSTRAINT DF_restApiUser_status DEFAULT ('1'),
		CONSTRAINT PK_restApiUser PRIMARY KEY ([name])
	)
GO

IF OBJECT_ID('dbo.restApiAllowForm', 'U') IS NULL
	CREATE TABLE dbo.restApiAllowForm
	(
		u_id INT NOT NULL,
		form VARCHAR(32) NOT NULL -- tên file XML không đuôi
	)
GO

IF OBJECT_ID('dbo.restApiAllowIp', 'U') IS NULL
	CREATE TABLE dbo.restApiAllowIp
	(
		u_id INT NOT NULL,
		ip_address VARCHAR(45) NOT NULL, -- "255" là wildcard
		[description] NVARCHAR(100) NULL
	)
GO

IF OBJECT_ID('dbo.restApiBlackListIp', 'U') IS NULL
	CREATE TABLE dbo.restApiBlackListIp
	(
		ip_address VARCHAR(45) NOT NULL
	)
GO

IF OBJECT_ID('dbo.restApiLogError', 'U') IS NULL
	CREATE TABLE dbo.restApiLogError
	(
		id BIGINT IDENTITY(1, 1) NOT NULL,
		time_stamp DATETIME NOT NULL,
		[path] NVARCHAR(128) NULL,
		[message] NVARCHAR(4000) NULL,
		stacktrace NVARCHAR(4000) NULL,
		CONSTRAINT PK_restApiLogError PRIMARY KEY (id)
	)
GO

-- Như v2: IP bị chặn = 0; user không khai IP = 1; có khai thì khớp mẫu ("255" = %)
IF OBJECT_ID('dbo.restApi_CheckIPAddress', 'P') IS NULL
	EXEC sp_executesql N'CREATE PROCEDURE dbo.restApi_CheckIPAddress
	@IpAddress VARCHAR(45),
	@UserName VARCHAR(32)
AS
--C--NGUYENTDH 24/10/2026: bản tối thiểu cho DB mới, cùng ý nghĩa với v2
BEGIN
	SET NOCOUNT ON

	DECLARE @UID INT

	SELECT @UID = a.id FROM dbo.restApiUser a WHERE a.[name] = @UserName

	IF EXISTS (SELECT 1 FROM dbo.restApiBlackListIp a WHERE @IpAddress LIKE REPLACE(a.ip_address, ''255'', ''%'')) BEGIN
		SELECT ''0'' AS value

		RETURN
	END

	IF @UID IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.restApiAllowIp a WHERE a.u_id = @UID) BEGIN
		SELECT ''1'' AS value

		RETURN
	END

	IF EXISTS (SELECT 1 FROM dbo.restApiAllowIp a WHERE a.u_id = @UID AND @IpAddress LIKE REPLACE(a.ip_address, ''255'', ''%''))
		SELECT ''1'' AS value
	ELSE
		SELECT ''0'' AS value
END'
GO

-- Nhận tên bảng tạm do LogRepository dựng (#logError: timeStamp, path, message, stacktrace)
IF OBJECT_ID('dbo.restApi_InsertLogError', 'P') IS NULL
	EXEC sp_executesql N'CREATE PROCEDURE dbo.restApi_InsertLogError
	@Table SYSNAME
AS
--C--NGUYENTDH 24/10/2026: bản tối thiểu cho DB mới
BEGIN
	SET NOCOUNT ON

	DECLARE @q NVARCHAR(MAX)

	IF LEFT(@Table, 1) <> ''#'' RETURN

	SET @q = ''INSERT INTO dbo.restApiLogError (time_stamp, [path], [message], stacktrace)''
		+ '' SELECT a.timeStamp, a.[path], a.[message], a.stacktrace''
		+ '' FROM '' + QUOTENAME(@Table) + '' a''

	EXEC sp_executesql @q
END'
GO

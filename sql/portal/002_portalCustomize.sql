-- DB portal: revision customize (nguồn sự thật ở DEV), package đưa lên PROD, nhật ký đồng bộ. SQL Server 2008, chạy lại được.
-- Phụ thuộc sql/portal/001. Thiết kế: docs/PORTAL_MULTI_PROJECT.md mục 5-7
-- Rollback: DROP TABLE portalSyncLog, portalPackageItem, portalPackage, portalFormRevision (theo thứ tự này)

-- Mỗi lần sửa form customize là một revision bất biến. status: draft -> validated -> published
IF OBJECT_ID('dbo.portalFormRevision', 'U') IS NULL
BEGIN
	CREATE TABLE dbo.portalFormRevision
	(
		id INT IDENTITY(1, 1) NOT NULL,
		project_id INT NOT NULL,
		form_name VARCHAR(32) NOT NULL, -- tên file XML không đuôi, như restApiAllowForm.form
		revision INT NOT NULL,
		base_revision INT NULL, -- revision đã dùng làm gốc khi sửa: khóa lạc quan (lệch gốc thì từ chối lưu)
		status VARCHAR(10) NOT NULL,
		content NVARCHAR(MAX) NOT NULL,
		content_sha256 CHAR(64) NOT NULL,
		created_by INT NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_portalFormRevision_created_at DEFAULT (GETDATE()),
		published_by INT NULL,
		published_at DATETIME NULL,
		CONSTRAINT PK_portalFormRevision PRIMARY KEY (id),
		CONSTRAINT FK_portalFormRevision_project FOREIGN KEY (project_id) REFERENCES dbo.portalProject (id),
		CONSTRAINT CK_portalFormRevision_status CHECK (status IN ('draft', 'validated', 'published'))
	)
	CREATE UNIQUE INDEX UX_portalFormRevision ON dbo.portalFormRevision (project_id, form_name, revision)
END
GO

-- Package: tập revision chốt để đưa lên PROD. manifest_sha256 = băm của manifest để PROD kiểm trước khi áp
IF OBJECT_ID('dbo.portalPackage', 'U') IS NULL
	CREATE TABLE dbo.portalPackage
	(
		id INT IDENTITY(1, 1) NOT NULL,
		project_id INT NOT NULL,
		created_by INT NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_portalPackage_created_at DEFAULT (GETDATE()),
		manifest NVARCHAR(MAX) NOT NULL, -- JSON: form@revision, sha256, tên proc/script SQL đi kèm
		manifest_sha256 CHAR(64) NOT NULL,
		CONSTRAINT PK_portalPackage PRIMARY KEY (id),
		CONSTRAINT FK_portalPackage_project FOREIGN KEY (project_id) REFERENCES dbo.portalProject (id)
	)
GO

IF OBJECT_ID('dbo.portalPackageItem', 'U') IS NULL
	CREATE TABLE dbo.portalPackageItem
	(
		package_id INT NOT NULL,
		revision_id INT NOT NULL,
		CONSTRAINT PK_portalPackageItem PRIMARY KEY (package_id, revision_id),
		CONSTRAINT FK_portalPackageItem_package FOREIGN KEY (package_id) REFERENCES dbo.portalPackage (id),
		CONSTRAINT FK_portalPackageItem_revision FOREIGN KEY (revision_id) REFERENCES dbo.portalFormRevision (id)
	)
GO

-- Nhật ký đồng bộ theo dự án. Chỉ ghi thêm; detail không chứa mật khẩu, khóa, connection string
IF OBJECT_ID('dbo.portalSyncLog', 'U') IS NULL
BEGIN
	CREATE TABLE dbo.portalSyncLog
	(
		id BIGINT IDENTITY(1, 1) NOT NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_portalSyncLog_created_at DEFAULT (GETDATE()),
		project_id INT NOT NULL,
		env VARCHAR(4) NOT NULL, -- DEV | PROD
		event VARCHAR(32) NOT NULL, -- EXPORT | APPLY | VERIFY | ACCOUNT_ISSUE | PASSWORD_CHANGE | KEY_ISSUE | FAILED
		package_id INT NULL,
		source VARCHAR(8) NOT NULL, -- portal | cli | gui
		actor VARCHAR(64) NULL, -- tài khoản portal hoặc tên người bảo trì ghi nhận ở CLI/GUI
		outcome VARCHAR(8) NOT NULL, -- OK | FAILED
		detail NVARCHAR(1024) NULL,
		CONSTRAINT PK_portalSyncLog PRIMARY KEY (id),
		CONSTRAINT FK_portalSyncLog_project FOREIGN KEY (project_id) REFERENCES dbo.portalProject (id),
		CONSTRAINT CK_portalSyncLog_env CHECK (env IN ('DEV', 'PROD'))
	)
	CREATE INDEX IX_portalSyncLog_project ON dbo.portalSyncLog (project_id, created_at)
END
GO

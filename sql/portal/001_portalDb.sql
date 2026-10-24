-- DB riêng của WebPortal (KHÔNG chạy trên DB của dự án). SQL Server 2008, chạy lại được.
-- Thiết kế: docs/adr/ADR-0001-portal-cpanel-portalapp.md
-- Ba loại tài khoản: SA (duy nhất, /cpanel), ADMIN (quản trị dự án, /{ma_da}), user Api (portalApiUser, /{ma_da}).
-- Nâng từ lược đồ cũ (staff/customer/membership): bỏ portalRole, portalRolePermission, portalMembership; đổi portalProject.code -> ma_da.
-- Rollback: DROP TABLE portalLicense, portalLoginChallenge, portalSession, portalApiUserIp, portalApiUserForm, portalApiUser,
--   portalProjectAdmin (theo thứ tự này); các bảng portalAccount, portalProject, portalAudit giữ lại.

-- Chỉ mục lọc bắt buộc QUOTED_IDENTIFIER ON (sqlcmd mặc định OFF: chạy bằng sqlcmd thì thêm -I)
SET QUOTED_IDENTIFIER ON
SET ANSI_NULLS ON
GO

-- Lược đồ cũ còn tài khoản staff/customer: dừng, không tự đoán chuyển sang SA/ADMIN
IF OBJECT_ID('dbo.portalAccount', 'U') IS NOT NULL
	AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAccount') AND name = 'kind')
	AND EXISTS (SELECT 1 FROM dbo.portalAccount WHERE kind NOT IN ('SA', 'ADMIN'))
BEGIN
	RAISERROR(N'portalAccount còn tài khoản kiểu cũ (staff/customer). Xóa hoặc chuyển tay sang SA/ADMIN rồi chạy lại.', 16, 1)
	SET NOEXEC ON
END
GO

-- ---------- Bỏ bảng vai trò động của lược đồ cũ ----------
IF OBJECT_ID('dbo.portalMembership', 'U') IS NOT NULL DROP TABLE dbo.portalMembership
IF OBJECT_ID('dbo.portalRolePermission', 'U') IS NOT NULL DROP TABLE dbo.portalRolePermission
IF OBJECT_ID('dbo.portalRole', 'U') IS NOT NULL DROP TABLE dbo.portalRole
GO

-- ---------- Tài khoản SA / ADMIN ----------
IF OBJECT_ID('dbo.portalAccount', 'U') IS NULL
	CREATE TABLE dbo.portalAccount
	(
		id INT IDENTITY(1, 1) NOT NULL,
		login VARCHAR(64) NOT NULL,
		display_name NVARCHAR(100) NOT NULL,
		kind VARCHAR(10) NOT NULL, -- SA = quản trị portal (duy nhất), ADMIN = quản trị dự án
		status CHAR(1) NOT NULL CONSTRAINT DF_portalAccount_status DEFAULT ('1'),
		password_hash VARCHAR(256) NOT NULL, -- PBKDF2, không giải ngược được
		must_change_password BIT NOT NULL CONSTRAINT DF_portalAccount_must_change DEFAULT (1),
		password_changed_at DATETIME NULL,
		last_login_at DATETIME NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_portalAccount_created_at DEFAULT (GETDATE()),
		CONSTRAINT PK_portalAccount PRIMARY KEY (id),
		CONSTRAINT UQ_portalAccount_login UNIQUE (login)
	)
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.portalAccount') AND name = 'CK_portalAccount_kind'
	AND definition NOT LIKE '%SA%')
	ALTER TABLE dbo.portalAccount DROP CONSTRAINT CK_portalAccount_kind
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.portalAccount') AND name = 'CK_portalAccount_kind')
	ALTER TABLE dbo.portalAccount ADD CONSTRAINT CK_portalAccount_kind CHECK (kind IN ('SA', 'ADMIN'))
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAccount') AND name = 'must_change_password')
	ALTER TABLE dbo.portalAccount ADD must_change_password BIT NOT NULL CONSTRAINT DF_portalAccount_must_change DEFAULT (1)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAccount') AND name = 'password_changed_at')
	ALTER TABLE dbo.portalAccount ADD password_changed_at DATETIME NULL
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAccount') AND name = 'last_login_at')
	ALTER TABLE dbo.portalAccount ADD last_login_at DATETIME NULL
GO

-- Chỉ một SA
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.portalAccount') AND name = 'UX_portalAccount_single_sa')
	CREATE UNIQUE INDEX UX_portalAccount_single_sa ON dbo.portalAccount (kind) WHERE kind = 'SA'
GO

-- ---------- Dự án ----------
IF OBJECT_ID('dbo.portalProject', 'U') IS NULL
	CREATE TABLE dbo.portalProject
	(
		id INT IDENTITY(1, 1) NOT NULL,
		ma_da VARCHAR(32) NOT NULL, -- mã dự án trong QLDA (nbdmda), cũng là đoạn URL /{ma_da}
		api_folder NVARCHAR(260) NOT NULL, -- folder restApi của dự án (chứa appsettings.Local.json, App_Data\Form)
		api_base_url VARCHAR(256) NOT NULL, -- origin WebApi của dự án, cho "Gọi thử" trên trình duyệt
		status CHAR(1) NOT NULL CONSTRAINT DF_portalProject_status DEFAULT ('1'),
		last_check_at DATETIME NULL,
		last_check_result NVARCHAR(512) NULL, -- không chứa connection string
		created_by INT NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_portalProject_created_at DEFAULT (GETDATE()),
		CONSTRAINT PK_portalProject PRIMARY KEY (id),
		CONSTRAINT UQ_portalProject_ma_da UNIQUE (ma_da)
	)
GO

-- Lược đồ cũ: code -> ma_da, bỏ khóa agent
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'code')
	AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'ma_da')
	EXEC sp_rename 'dbo.portalProject.code', 'ma_da', 'COLUMN'
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'api_folder')
	ALTER TABLE dbo.portalProject ADD api_folder NVARCHAR(260) NOT NULL CONSTRAINT DF_portalProject_api_folder DEFAULT (N'')
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'last_check_at')
	ALTER TABLE dbo.portalProject ADD last_check_at DATETIME NULL
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'last_check_result')
	ALTER TABLE dbo.portalProject ADD last_check_result NVARCHAR(512) NULL
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'created_by')
	ALTER TABLE dbo.portalProject ADD created_by INT NULL
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'created_at')
	ALTER TABLE dbo.portalProject ADD created_at DATETIME NOT NULL CONSTRAINT DF_portalProject_created_at DEFAULT (GETDATE())
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'agent_key_enc')
	ALTER TABLE dbo.portalProject DROP COLUMN agent_key_enc
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'agent_user')
	ALTER TABLE dbo.portalProject DROP COLUMN agent_user
-- Tên dự án đọc từ nbdmda, cột name cũ (nếu có) chỉ còn để tham khảo
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalProject') AND name = 'name' AND is_nullable = 0)
	ALTER TABLE dbo.portalProject ALTER COLUMN name NVARCHAR(100) NULL
GO

-- ---------- Admin của từng dự án ----------
IF OBJECT_ID('dbo.portalProjectAdmin', 'U') IS NULL
BEGIN
	CREATE TABLE dbo.portalProjectAdmin
	(
		project_id INT NOT NULL,
		account_id INT NOT NULL,
		granted_by INT NULL,
		granted_at DATETIME NOT NULL CONSTRAINT DF_portalProjectAdmin_granted_at DEFAULT (GETDATE()),
		CONSTRAINT PK_portalProjectAdmin PRIMARY KEY (project_id, account_id),
		CONSTRAINT FK_portalProjectAdmin_project FOREIGN KEY (project_id) REFERENCES dbo.portalProject (id),
		CONSTRAINT FK_portalProjectAdmin_account FOREIGN KEY (account_id) REFERENCES dbo.portalAccount (id)
	)
	CREATE INDEX IX_portalProjectAdmin_account ON dbo.portalProjectAdmin (account_id)
END
GO

-- ---------- User Api: bảng ánh xạ gọn, nguồn sự thật đồng bộ một chiều xuống restApiUser của dự án ----------
IF OBJECT_ID('dbo.portalApiUser', 'U') IS NULL
	CREATE TABLE dbo.portalApiUser
	(
		id INT IDENTITY(1, 1) NOT NULL,
		project_id INT NOT NULL,
		user_name VARCHAR(32) NOT NULL, -- = restApiUser.name của dự án
		name_erp VARCHAR(30) NOT NULL, -- = userinfo2.name (NSD FBO)
		display_name NVARCHAR(100) NULL,
		password_hash VARCHAR(256) NOT NULL, -- PBKDF2: đăng nhập portal chỉ kiểm ở đây
		status CHAR(1) NOT NULL CONSTRAINT DF_portalApiUser_status DEFAULT ('1'),
		sync_version INT NOT NULL CONSTRAINT DF_portalApiUser_sync_version DEFAULT (1), -- tăng mỗi lần đổi
		synced_version INT NOT NULL CONSTRAINT DF_portalApiUser_synced_version DEFAULT (0), -- = sync_version khi đã đẩy xuống dự án
		synced_at DATETIME NULL,
		sync_error NVARCHAR(512) NULL,
		password_changed_at DATETIME NULL,
		created_by INT NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_portalApiUser_created_at DEFAULT (GETDATE()),
		CONSTRAINT PK_portalApiUser PRIMARY KEY (id),
		CONSTRAINT UQ_portalApiUser_project_user UNIQUE (project_id, user_name),
		CONSTRAINT FK_portalApiUser_project FOREIGN KEY (project_id) REFERENCES dbo.portalProject (id)
	)
GO

-- Quyền form: có dòng = chỉ các form này; không dòng nào = mọi form
IF OBJECT_ID('dbo.portalApiUserForm', 'U') IS NULL
	CREATE TABLE dbo.portalApiUserForm
	(
		api_user_id INT NOT NULL,
		form VARCHAR(32) NOT NULL,
		CONSTRAINT PK_portalApiUserForm PRIMARY KEY (api_user_id, form),
		CONSTRAINT FK_portalApiUserForm_user FOREIGN KEY (api_user_id) REFERENCES dbo.portalApiUser (id)
	)
GO

-- IP cho phép: có dòng = chỉ các IP này ("255" là wildcard như restApiAllowIp); không dòng nào = mọi IP
IF OBJECT_ID('dbo.portalApiUserIp', 'U') IS NULL
	CREATE TABLE dbo.portalApiUserIp
	(
		api_user_id INT NOT NULL,
		ip_address VARCHAR(45) NOT NULL,
		description NVARCHAR(100) NULL,
		CONSTRAINT PK_portalApiUserIp PRIMARY KEY (api_user_id, ip_address),
		CONSTRAINT FK_portalApiUserIp_user FOREIGN KEY (api_user_id) REFERENCES dbo.portalApiUser (id)
	)
GO

-- ---------- Phiên đăng nhập: mỗi tài khoản một phiên hiệu lực ----------
IF OBJECT_ID('dbo.portalSession', 'U') IS NULL
	CREATE TABLE dbo.portalSession
	(
		id UNIQUEIDENTIFIER NOT NULL,
		account_type VARCHAR(8) NOT NULL, -- SA | ADMIN | API
		account_id INT NOT NULL, -- portalAccount.id (SA, ADMIN) hoặc portalApiUser.id (API)
		project_id INT NULL,
		created_at DATETIME NOT NULL,
		last_seen_at DATETIME NOT NULL,
		ip_address VARCHAR(45) NULL,
		user_agent NVARCHAR(256) NULL,
		revoked_at DATETIME NULL,
		revoked_reason VARCHAR(32) NULL, -- logout | replaced | expired | idle | password | locked | forced
		replaced_by UNIQUEIDENTIFIER NULL,
		CONSTRAINT PK_portalSession PRIMARY KEY (id),
		CONSTRAINT CK_portalSession_type CHECK (account_type IN ('SA', 'ADMIN', 'API'))
	)
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.portalSession') AND name = 'UX_portalSession_active')
	CREATE UNIQUE INDEX UX_portalSession_active ON dbo.portalSession (account_type, account_id) WHERE revoked_at IS NULL
GO

-- Đăng nhập đúng mật khẩu khi đang có phiên khác: chờ người đăng nhập mới xác nhận thay phiên (hạn 2 phút)
IF OBJECT_ID('dbo.portalLoginChallenge', 'U') IS NULL
BEGIN
	CREATE TABLE dbo.portalLoginChallenge
	(
		id UNIQUEIDENTIFIER NOT NULL,
		account_type VARCHAR(8) NOT NULL,
		account_id INT NOT NULL,
		project_id INT NULL,
		old_session_id UNIQUEIDENTIFIER NOT NULL,
		ip_address VARCHAR(45) NULL,
		user_agent NVARCHAR(256) NULL,
		created_at DATETIME NOT NULL,
		expires_at DATETIME NOT NULL,
		closed_at DATETIME NULL,
		outcome VARCHAR(16) NULL, -- accepted | cancelled | expired
		CONSTRAINT PK_portalLoginChallenge PRIMARY KEY (id)
	)
	CREATE INDEX IX_portalLoginChallenge_old_session ON dbo.portalLoginChallenge (old_session_id, closed_at)
END
GO

-- ---------- License PortalApp / WebApi ----------
IF OBJECT_ID('dbo.portalLicense', 'U') IS NULL
BEGIN
	CREATE TABLE dbo.portalLicense
	(
		id UNIQUEIDENTIFIER NOT NULL, -- license_id ghi trong file .lic
		project_id INT NOT NULL,
		kid VARCHAR(64) NOT NULL, -- khóa đã ký
		mac_list VARCHAR(512) NOT NULL,
		machine_name NVARCHAR(64) NULL,
		request_sha256 CHAR(64) NOT NULL,
		issued_by INT NOT NULL,
		issued_at DATETIME NOT NULL CONSTRAINT DF_portalLicense_issued_at DEFAULT (GETDATE()),
		expires_at DATETIME NULL, -- NULL = không hạn
		revoked_at DATETIME NULL,
		revoked_reason NVARCHAR(256) NULL,
		CONSTRAINT PK_portalLicense PRIMARY KEY (id),
		CONSTRAINT FK_portalLicense_project FOREIGN KEY (project_id) REFERENCES dbo.portalProject (id)
	)
	CREATE INDEX IX_portalLicense_project ON dbo.portalLicense (project_id, issued_at)
END
GO

-- ---------- Nhật ký thao tác ----------
IF OBJECT_ID('dbo.portalAudit', 'U') IS NULL
	CREATE TABLE dbo.portalAudit
	(
		id BIGINT IDENTITY(1, 1) NOT NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_portalAudit_created_at DEFAULT (GETDATE()),
		actor_account_id INT NULL,
		project_id INT NULL,
		action VARCHAR(64) NOT NULL,
		target VARCHAR(128) NULL,
		outcome VARCHAR(16) NOT NULL,
		req_id VARCHAR(64) NULL,
		detail NVARCHAR(1024) NULL, -- không chứa bí mật
		CONSTRAINT PK_portalAudit PRIMARY KEY (id)
	)
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAudit') AND name = 'actor_type')
	ALTER TABLE dbo.portalAudit ADD actor_type VARCHAR(8) NULL -- SA | ADMIN | API
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAudit') AND name = 'actor_login')
	ALTER TABLE dbo.portalAudit ADD actor_login VARCHAR(64) NULL
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAudit') AND name = 'ip_address')
	ALTER TABLE dbo.portalAudit ADD ip_address VARCHAR(45) NULL
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAudit') AND name = 'acting_as')
	ALTER TABLE dbo.portalAudit ADD acting_as VARCHAR(8) NULL -- SA thao tác thay Admin dự án
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.portalAudit') AND name = 'session_id')
	ALTER TABLE dbo.portalAudit ADD session_id UNIQUEIDENTIFIER NULL
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.portalAudit') AND name = 'IX_portalAudit_project')
	CREATE INDEX IX_portalAudit_project ON dbo.portalAudit (project_id, created_at)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.portalAudit') AND name = 'IX_portalAudit_created_at')
	CREATE INDEX IX_portalAudit_created_at ON dbo.portalAudit (created_at)
GO

SET NOEXEC OFF
GO

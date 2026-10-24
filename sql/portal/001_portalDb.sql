-- DB riêng của portal trung tâm (KHÔNG chạy trên DB của dự án). SQL Server 2008, chạy lại được.
-- Thiết kế: docs/PORTAL_MULTI_PROJECT.md
-- Rollback: DROP TABLE portalMembership, portalRolePermission, portalAudit, portalRole, portalProject, portalAccount (theo thứ tự này)

IF OBJECT_ID('dbo.portalAccount', 'U') IS NULL
	CREATE TABLE dbo.portalAccount
	(
		id INT IDENTITY(1, 1) NOT NULL,
		login VARCHAR(64) NOT NULL,
		display_name NVARCHAR(100) NOT NULL,
		kind VARCHAR(10) NOT NULL, -- staff = nội bộ, customer = khách hàng; không đổi sau khi tạo
		status CHAR(1) NOT NULL CONSTRAINT DF_portalAccount_status DEFAULT ('1'), -- 1 hoạt động, 0 khóa
		password_hash VARCHAR(256) NOT NULL, -- băm PBKDF2, không phải mật khẩu mã hóa
		created_at DATETIME NOT NULL CONSTRAINT DF_portalAccount_created_at DEFAULT (GETDATE()),
		CONSTRAINT PK_portalAccount PRIMARY KEY (id),
		CONSTRAINT UQ_portalAccount_login UNIQUE (login),
		CONSTRAINT CK_portalAccount_kind CHECK (kind IN ('staff', 'customer'))
	)
GO

IF OBJECT_ID('dbo.portalProject', 'U') IS NULL
	CREATE TABLE dbo.portalProject
	(
		id INT IDENTITY(1, 1) NOT NULL,
		code VARCHAR(32) NOT NULL,
		name NVARCHAR(100) NOT NULL,
		api_base_url VARCHAR(256) NOT NULL, -- origin Api của dự án, chỉ https (kiểm ở code), chống SSRF bằng danh sách này
		agent_user VARCHAR(32) NULL, -- restApiUser.name của agent trong dự án
		agent_key_enc NVARCHAR(512) NULL, -- private_key của agent, đã mã hóa; không bao giờ trả ra UI/log
		status CHAR(1) NOT NULL CONSTRAINT DF_portalProject_status DEFAULT ('1'),
		CONSTRAINT PK_portalProject PRIMARY KEY (id),
		CONSTRAINT UQ_portalProject_code UNIQUE (code)
	)
GO

IF OBJECT_ID('dbo.portalRole', 'U') IS NULL
	CREATE TABLE dbo.portalRole
	(
		id INT IDENTITY(1, 1) NOT NULL,
		code VARCHAR(32) NOT NULL, -- partner | deployment | administrator
		scope VARCHAR(10) NOT NULL, -- customer: chỉ dùng cho tài khoản customer; staff: chỉ cho staff
		name NVARCHAR(100) NOT NULL,
		CONSTRAINT PK_portalRole PRIMARY KEY (id),
		CONSTRAINT UQ_portalRole_code UNIQUE (code),
		CONSTRAINT CK_portalRole_scope CHECK (scope IN ('staff', 'customer'))
	)
GO

IF OBJECT_ID('dbo.portalRolePermission', 'U') IS NULL
	CREATE TABLE dbo.portalRolePermission
	(
		role_id INT NOT NULL,
		permission VARCHAR(64) NOT NULL, -- định danh trong Models.PortalPermissions
		CONSTRAINT PK_portalRolePermission PRIMARY KEY (role_id, permission),
		CONSTRAINT FK_portalRolePermission_role FOREIGN KEY (role_id) REFERENCES dbo.portalRole (id)
	)
GO

-- Tài khoản thuộc dự án nào, vai trò gì. project_id NULL = mọi dự án (chỉ cho staff, kiểm ở code và ràng buộc dưới)
IF OBJECT_ID('dbo.portalMembership', 'U') IS NULL
BEGIN
	CREATE TABLE dbo.portalMembership
	(
		id INT IDENTITY(1, 1) NOT NULL,
		account_id INT NOT NULL,
		project_id INT NULL,
		role_id INT NOT NULL,
		project_user_name VARCHAR(32) NULL, -- restApiUser.name của khách trong dự án (danh tính gọi API nghiệp vụ)
		granted_by INT NULL,
		granted_at DATETIME NOT NULL CONSTRAINT DF_portalMembership_granted_at DEFAULT (GETDATE()),
		CONSTRAINT PK_portalMembership PRIMARY KEY (id),
		CONSTRAINT FK_portalMembership_account FOREIGN KEY (account_id) REFERENCES dbo.portalAccount (id),
		CONSTRAINT FK_portalMembership_project FOREIGN KEY (project_id) REFERENCES dbo.portalProject (id),
		CONSTRAINT FK_portalMembership_role FOREIGN KEY (role_id) REFERENCES dbo.portalRole (id)
	)
	-- Một tài khoản một vai trò trên một dự án (NULL project tính là một giá trị riêng)
	CREATE UNIQUE INDEX UX_portalMembership ON dbo.portalMembership (account_id, role_id, project_id)
END
GO

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

IF NOT EXISTS (SELECT 1 FROM dbo.portalRole WHERE code = 'partner')
	INSERT dbo.portalRole (code, scope, name) VALUES ('partner', 'customer', N'Khách hàng (Partner)')
IF NOT EXISTS (SELECT 1 FROM dbo.portalRole WHERE code = 'deployment')
	INSERT dbo.portalRole (code, scope, name) VALUES ('deployment', 'staff', N'Deployment Staff')
IF NOT EXISTS (SELECT 1 FROM dbo.portalRole WHERE code = 'administrator')
	INSERT dbo.portalRole (code, scope, name) VALUES ('administrator', 'staff', N'Administrator')
GO
-- Quyền theo vai trò: seed giống sql/008 (xem docs/PORTAL_ADMIN.md mục 3), thêm khi dựng host control plane

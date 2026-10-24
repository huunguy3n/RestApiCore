-- Phân quyền portal (vai trò + quyền chi tiết) và nhật ký quản trị. Tách khỏi userinfo2.admin của FBO.
-- Tương thích SQL Server 2008 (không OFFSET/FETCH, THROW, IIF, CONCAT...), chạy lại được. Không đổi bảng có sẵn.
-- Mặc định deny: user không có dòng restApiPortalUserRole chỉ là Partner (xem tài nguyên của mình, không có quyền quản trị)
--
-- Khôi phục (rollback): DROP TABLE restApiPortalUserRole, restApiPortalRolePermission, restApiPortalRole, restApiAudit
-- (theo thứ tự này). Không bảng v3 nào khác phụ thuộc. Mất các bảng này = mọi user quay về Partner, API nghiệp vụ không đổi.

IF OBJECT_ID('dbo.restApiPortalRole', 'U') IS NULL
	CREATE TABLE dbo.restApiPortalRole
	(
		id INT IDENTITY(1, 1) NOT NULL,
		code VARCHAR(32) NOT NULL,
		name NVARCHAR(100) NOT NULL,
		is_system BIT NOT NULL CONSTRAINT DF_restApiPortalRole_is_system DEFAULT (0),
		CONSTRAINT PK_restApiPortalRole PRIMARY KEY (id),
		CONSTRAINT UQ_restApiPortalRole_code UNIQUE (code)
	)
GO

IF OBJECT_ID('dbo.restApiPortalRolePermission', 'U') IS NULL
	CREATE TABLE dbo.restApiPortalRolePermission
	(
		role_id INT NOT NULL,
		permission VARCHAR(64) NOT NULL, -- định danh trong Models.PortalPermissions; giá trị lạ bị code bỏ qua (deny)
		CONSTRAINT PK_restApiPortalRolePermission PRIMARY KEY (role_id, permission),
		CONSTRAINT FK_restApiPortalRolePermission_role FOREIGN KEY (role_id) REFERENCES dbo.restApiPortalRole (id)
	)
GO

-- u_id = restApiUser.id. Không FK tới restApiUser để xóa user không mất dòng này (thu hồi quyền làm bằng DELETE ở đây)
IF OBJECT_ID('dbo.restApiPortalUserRole', 'U') IS NULL
	CREATE TABLE dbo.restApiPortalUserRole
	(
		u_id INT NOT NULL,
		role_id INT NOT NULL,
		granted_by INT NULL,
		granted_at DATETIME NOT NULL CONSTRAINT DF_restApiPortalUserRole_granted_at DEFAULT (GETDATE()),
		CONSTRAINT PK_restApiPortalUserRole PRIMARY KEY (u_id, role_id),
		CONSTRAINT FK_restApiPortalUserRole_role FOREIGN KEY (role_id) REFERENCES dbo.restApiPortalRole (id)
	)
GO

-- Nhật ký quản trị: chỉ ghi thêm. Không lưu mật khẩu, khóa, JWT, payload nhạy cảm
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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiAudit') AND name = 'IX_restApiAudit_created_at')
	CREATE INDEX IX_restApiAudit_created_at ON dbo.restApiAudit (created_at)
GO

-- Vai trò hệ thống (Partner không cần dòng: là mặc định của mọi tài khoản portal)
IF NOT EXISTS (SELECT 1 FROM dbo.restApiPortalRole WHERE code = 'deployment')
	INSERT dbo.restApiPortalRole (code, name, is_system) VALUES ('deployment', N'Deployment Staff', 1)
IF NOT EXISTS (SELECT 1 FROM dbo.restApiPortalRole WHERE code = 'administrator')
	INSERT dbo.restApiPortalRole (code, name, is_system) VALUES ('administrator', N'Administrator', 1)
GO

-- Quyền mặc định của vai trò. Deployment KHÔNG có users.*, cli.issue-key/rotate-key/rekey-passwords, settings.manage
DECLARE @dep INT, @adm INT
SELECT @dep = id FROM dbo.restApiPortalRole WHERE code = 'deployment'
SELECT @adm = id FROM dbo.restApiPortalRole WHERE code = 'administrator'

DECLARE @p TABLE (role_id INT, permission VARCHAR(64))
-- SQL 2008 có row constructor VALUES (...), (...)
INSERT @p VALUES (@dep, 'forms.read'), (@dep, 'forms.edit'), (@dep, 'forms.permissions.manage'), (@dep, 'controllers.read'), (@dep, 'cli.validate'), (@dep, 'logs.read')
INSERT @p VALUES (@adm, 'users.read'), (@adm, 'users.create'), (@adm, 'users.update'), (@adm, 'users.deactivate'), (@adm, 'users.delete'), (@adm, 'users.roles.manage'),
	(@adm, 'forms.read'), (@adm, 'forms.edit'), (@adm, 'forms.publish'), (@adm, 'forms.permissions.manage'),
	(@adm, 'controllers.read'), (@adm, 'controllers.create'), (@adm, 'controllers.update'), (@adm, 'controllers.publish'),
	(@adm, 'cli.inspect'), (@adm, 'cli.validate'), (@adm, 'audit.read'), (@adm, 'logs.read'), (@adm, 'settings.manage')
-- logs.body.read (xem body request/response = dữ liệu nghiệp vụ của khách), cli.issue-key, cli.rotate-key, cli.rekey-passwords: không gán mặc định cho vai trò nào, cấp tay từng vai trò khi cần

INSERT dbo.restApiPortalRolePermission (role_id, permission)
SELECT a.role_id, a.permission FROM @p a
WHERE NOT EXISTS (SELECT 1 FROM dbo.restApiPortalRolePermission b WHERE b.role_id = a.role_id AND b.permission = a.permission)
GO

-- Cấp Administrator đầu tiên (chạy tay, sửa tên đăng nhập): quyền portal KHÔNG suy từ userinfo2.admin
-- INSERT dbo.restApiPortalUserRole (u_id, role_id)
-- SELECT a.id, r.id FROM dbo.restApiUser a, dbo.restApiPortalRole r WHERE a.[name] = 'TENDANGNHAP' AND r.code = 'administrator'

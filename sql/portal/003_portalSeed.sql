-- DB portal: quyền mặc định theo vai trò. Chạy sau sql/portal/001. SQL Server 2008, chạy lại được.
-- Khớp ma trận ở docs/PORTAL_ADMIN.md mục 3. Quyền không có trong Models.PortalPermissions.All bị code bỏ qua (deny)
-- Rollback: DELETE FROM dbo.portalRolePermission

DECLARE @partner INT, @dep INT, @adm INT
SELECT @partner = id FROM dbo.portalRole WHERE code = 'partner'
SELECT @dep = id FROM dbo.portalRole WHERE code = 'deployment'
SELECT @adm = id FROM dbo.portalRole WHERE code = 'administrator'

DECLARE @p TABLE (role_id INT, permission VARCHAR(64))
-- Partner (khách hàng): chỉ xem tài nguyên của dự án mình thuộc về
INSERT @p VALUES (@partner, 'forms.read')
-- Deployment Staff: không có users.*, cli.issue-key/rotate-key/rekey-passwords, settings.manage
INSERT @p VALUES (@dep, 'forms.read'), (@dep, 'forms.edit'), (@dep, 'forms.permissions.manage'), (@dep, 'controllers.read'), (@dep, 'cli.validate'), (@dep, 'logs.read')
-- Administrator
INSERT @p VALUES (@adm, 'users.read'), (@adm, 'users.create'), (@adm, 'users.update'), (@adm, 'users.deactivate'), (@adm, 'users.delete'), (@adm, 'users.roles.manage'),
	(@adm, 'forms.read'), (@adm, 'forms.edit'), (@adm, 'forms.publish'), (@adm, 'forms.permissions.manage'),
	(@adm, 'controllers.read'), (@adm, 'controllers.create'), (@adm, 'controllers.update'), (@adm, 'controllers.publish'),
	(@adm, 'cli.inspect'), (@adm, 'cli.validate'), (@adm, 'audit.read'), (@adm, 'logs.read'), (@adm, 'settings.manage')
-- logs.body.read, cli.issue-key, cli.rotate-key, cli.rekey-passwords: không gán mặc định, cấp tay từng vai trò khi cần

INSERT dbo.portalRolePermission (role_id, permission)
SELECT a.role_id, a.permission FROM @p a
WHERE a.role_id IS NOT NULL
	AND NOT EXISTS (SELECT 1 FROM dbo.portalRolePermission b WHERE b.role_id = a.role_id AND b.permission = a.permission)
GO

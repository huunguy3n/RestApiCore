-- DB portal: mỗi tài khoản API (restApiUser) của một dự án chỉ gắn cho MỘT tài khoản portal, để người quản trị một dự án
-- không gắn khách này vào danh tính API của khách khác trong cùng dự án. SQL Server 2008 (chỉ mục lọc), chạy lại được.
-- Phụ thuộc sql/portal/001. Rollback: DROP INDEX UX_portalMembership_api_user ON dbo.portalMembership
-- Nếu CREATE báo trùng: dữ liệu cũ đã có hai khách cùng project_user_name, phải xử lý tay trước.

-- Chỉ mục lọc bắt buộc QUOTED_IDENTIFIER ON; sqlcmd / osql mặc định OFF (chạy bằng sqlcmd thì thêm -I cho các lệnh ghi vào portalMembership)
SET QUOTED_IDENTIFIER ON
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.portalMembership') AND name = 'UX_portalMembership_api_user')
	CREATE UNIQUE INDEX UX_portalMembership_api_user ON dbo.portalMembership (project_id, project_user_name) WHERE project_user_name IS NOT NULL
GO

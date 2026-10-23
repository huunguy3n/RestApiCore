-- Một lượt trả đủ thông tin kiểm quyền của tài khoản API v3, thay restApi_CheckFormRights + restApi_CheckIPAddress
-- Tương thích SQL Server 2008, chạy lại được
IF OBJECT_ID('dbo.restApi_GetUserRights', 'P') IS NOT NULL
	DROP PROCEDURE dbo.restApi_GetUserRights
GO

CREATE PROCEDURE dbo.restApi_GetUserRights
	@UserName VARCHAR(32) -- restApiUser.name
AS
--C--NGUYENTDH 23/10/2026: 4 result set cố định, không có user thì cả 4 rỗng
--  1. user: restApiUser + userinfo2 (qua name_erp = userinfo2.name)
--  2. form được phép (không có dòng = không quyền form nào)
--  3. IP cho phép của user (không có dòng = mọi IP)
--  4. IP bị chặn (toàn hệ thống)
BEGIN
	SET NOCOUNT ON

	DECLARE @UID INT

	SELECT @UID = a.id FROM dbo.restApiUser a WHERE a.[name] = @UserName

	SELECT a.id AS u_id, a.[name], a.name_erp, a.[status], a.private_key,
			b.id AS user_id, b.admin, b.[status] AS erp_status
		FROM dbo.restApiUser a
		LEFT JOIN dbo.vsysuserinfo b ON b.[name] = a.name_erp
		WHERE a.id = @UID

	SELECT a.form FROM dbo.restApiAllowForm a WHERE a.u_id = @UID

	SELECT a.ip_address FROM dbo.restApiAllowIp a WHERE a.u_id = @UID

	SELECT a.ip_address FROM dbo.restApiBlackListIp a WHERE @UID IS NOT NULL

	SET NOCOUNT OFF
END
GO

-- Kiểm quyền user (userinfo2.id) trên đơn vị (dmdvcs.ma_dvcs) cho REST API v3
-- Tương thích SQL Server 2008, chạy lại được
IF OBJECT_ID('dbo.restApi_CheckUnitRights', 'P') IS NOT NULL
	DROP PROCEDURE dbo.restApi_CheckUnitRights
GO

CREATE PROCEDURE dbo.restApi_CheckUnitRights
	@UserID INT,
	@UnitCode VARCHAR(33),
	@Action VARCHAR(5) -- Acc: GET, New: POST, Edit, Del: DELETE (theo dbo.ff_CheckUnitRight)
AS
--C--NGUYENTDH 21/10/2026: admin (userinfo2.admin = 1) được mọi đơn vị, user thường theo sysunitrights qua ff_CheckUnitRight
BEGIN
	SET NOCOUNT ON

	DECLARE @Code VARCHAR(32), @Admin TINYINT

	SELECT @Code = 'SUCCESS', @UnitCode = RTRIM(LTRIM(ISNULL(@UnitCode, '')))

	IF @Action NOT IN ('Acc', 'New', 'Edit', 'Del')
		SELECT @Code = 'INVALID_ACTION'
	ELSE IF NOT EXISTS (SELECT 1 FROM dmdvcs a WITH (NOLOCK) WHERE a.ma_dvcs = @UnitCode AND a.status = '1')
		SELECT @Code = 'UNIT_NOT_FOUND'
	ELSE BEGIN
		SELECT @Admin = a.admin FROM vsysuserinfo a WHERE a.id = @UserID AND a.status = '1'

		IF @Admin IS NULL
			SELECT @Code = 'UNIT_DENIED'
		ELSE IF @Admin <> 1 AND dbo.ff_CheckUnitRight(@UserID, @UnitCode, @Action) <> 1
			SELECT @Code = 'UNIT_DENIED'
	END

	SELECT @Code AS code

	SET NOCOUNT OFF
END
GO

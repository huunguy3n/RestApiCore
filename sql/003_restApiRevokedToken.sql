-- JWT đã thu hồi (POST /api/auth/revoke). Dọn theo expires_at: hết hạn thì token tự vô hiệu, không cần giữ
-- Tương thích SQL Server 2008, chạy lại được
--C--NGUYENTDH 23/10/2026

IF OBJECT_ID('dbo.restApiRevokedToken', 'U') IS NULL
	CREATE TABLE dbo.restApiRevokedToken
	(
		jti VARCHAR(36) NOT NULL,
		u_id INT NOT NULL,
		expires_at DATETIME NOT NULL,
		revoked_at DATETIME NOT NULL CONSTRAINT DF_restApiRevokedToken_revoked_at DEFAULT (GETDATE()),
		CONSTRAINT PK_restApiRevokedToken PRIMARY KEY (jti)
	)
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiRevokedToken') AND name = 'IX_restApiRevokedToken_expires_at')
	CREATE INDEX IX_restApiRevokedToken_expires_at ON dbo.restApiRevokedToken (expires_at)
GO

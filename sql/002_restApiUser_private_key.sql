-- restApiUser cho REST API v3: thêm cột private_key, kiểm khóa chính name và identity id
-- Tương thích SQL Server 2008, chạy lại được. Không xoá, không đổi dữ liệu có sẵn
--C--NGUYENTDH 23/10/2026

IF OBJECT_ID('dbo.restApiUser', 'U') IS NULL
BEGIN
	RAISERROR('Không có bảng dbo.restApiUser', 16, 1)
	RETURN
END

-- private_key: khóa HMAC 256 bit, lưu đã mã hóa AES-256-GCM (base64), null = chưa cấp khóa
IF COL_LENGTH('dbo.restApiUser', 'private_key') IS NULL
	ALTER TABLE dbo.restApiUser ADD private_key NVARCHAR(512) NULL

-- id phải là identity (= u_id trong các bảng restApi*). Thêm identity cần dựng lại bảng nên không tự sửa
IF ISNULL(COLUMNPROPERTY(OBJECT_ID('dbo.restApiUser'), 'id', 'IsIdentity'), 0) <> 1
	RAISERROR('dbo.restApiUser.id không phải identity, cần sửa tay trước khi dùng v3', 16, 1)

-- name phải là khóa chính
IF NOT EXISTS (SELECT 1 FROM sys.indexes a
		JOIN sys.index_columns b ON b.object_id = a.object_id AND b.index_id = a.index_id
		WHERE a.object_id = OBJECT_ID('dbo.restApiUser') AND a.is_primary_key = 1)
BEGIN
	IF EXISTS (SELECT [name] FROM dbo.restApiUser GROUP BY [name] HAVING COUNT(1) > 1)
		RAISERROR('dbo.restApiUser có name trùng, không tạo được khóa chính', 16, 1)
	ELSE BEGIN
		ALTER TABLE dbo.restApiUser ALTER COLUMN [name] VARCHAR(32) NOT NULL
		ALTER TABLE dbo.restApiUser ADD CONSTRAINT PK_restApiUser PRIMARY KEY ([name])
	END
END
ELSE IF EXISTS (SELECT 1 FROM sys.indexes a
		JOIN sys.index_columns b ON b.object_id = a.object_id AND b.index_id = a.index_id
		WHERE a.object_id = OBJECT_ID('dbo.restApiUser') AND a.is_primary_key = 1
			AND COL_NAME(b.object_id, b.column_id) <> 'name')
	RAISERROR('Khóa chính dbo.restApiUser không phải (name), cần sửa tay trước khi dùng v3', 16, 1)
GO

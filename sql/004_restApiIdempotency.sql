-- Idempotency-Key: giữ response 24 giờ. Gửi lại đúng key + cùng request_sha256 thì trả kết quả cũ, khác thì 409
-- Tương thích SQL Server 2008, chạy lại được
--C--NGUYENTDH 23/10/2026

IF OBJECT_ID('dbo.restApiIdempotency', 'U') IS NULL
	CREATE TABLE dbo.restApiIdempotency
	(
		u_id INT NOT NULL,
		idempotency_key VARCHAR(128) NOT NULL,
		form VARCHAR(32) NOT NULL,
		request_sha256 CHAR(64) NOT NULL, -- SHA256(body + canonicalQuery), hex chữ thường
		status_code INT NULL, -- null: request đầu đang xử lý
		response_body NVARCHAR(MAX) NULL,
		created_at DATETIME NOT NULL CONSTRAINT DF_restApiIdempotency_created_at DEFAULT (GETDATE()),
		expires_at DATETIME NOT NULL,
		CONSTRAINT PK_restApiIdempotency PRIMARY KEY (u_id, idempotency_key)
	)
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.restApiIdempotency') AND name = 'IX_restApiIdempotency_expires_at')
	CREATE INDEX IX_restApiIdempotency_expires_at ON dbo.restApiIdempotency (expires_at)
GO

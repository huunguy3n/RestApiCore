-- Dọn log v3 theo lô: đổ vào bảng tổng hợp ngày rồi xoá log chi tiết quá hạn, xoá body không còn ai dùng
-- Tương thích SQL Server 2008, chạy lại được
IF OBJECT_ID('dbo.restApi_LogCleanup', 'P') IS NOT NULL
	DROP PROCEDURE dbo.restApi_LogCleanup
GO

CREATE PROCEDURE dbo.restApi_LogCleanup
	@RetentionDays INT, -- giữ log chi tiết (restApiLogReq, restApiLogBody)
	@AttackRetentionDays INT, -- giữ restApiLogAttack
	@BatchSize INT = 5000
AS
--C--NGUYENTDH 23/10/2026: mỗi lô một transaction ngắn để không khóa bảng log lâu
BEGIN
	SET NOCOUNT ON

	DECLARE @Cut DATETIME, @AttackCut DATETIME, @Rows INT, @Requests INT, @Bodies INT, @Attacks INT
	SELECT @Cut = DATEADD(DAY, -@RetentionDays, DATEADD(DAY, DATEDIFF(DAY, 0, GETDATE()), 0)),
		@AttackCut = DATEADD(DAY, -@AttackRetentionDays, DATEADD(DAY, DATEDIFF(DAY, 0, GETDATE()), 0)),
		@Requests = 0, @Bodies = 0, @Attacks = 0

	CREATE TABLE #batch (id BIGINT PRIMARY KEY)

	-- 1. Request quá hạn: cộng vào restApiLogAgg rồi xoá
	SELECT @Rows = 1
	WHILE @Rows > 0
	BEGIN
		TRUNCATE TABLE #batch
		INSERT INTO #batch (id) SELECT TOP (@BatchSize) a.id FROM dbo.restApiLogReq a WHERE a.created_at < @Cut ORDER BY a.id
		SELECT @Rows = @@ROWCOUNT

		IF @Rows > 0
		BEGIN
			BEGIN TRANSACTION

			SELECT DATEADD(DAY, DATEDIFF(DAY, 0, a.created_at), 0) AS log_date, ISNULL(a.u_id, 0) AS u_id, ISNULL(a.form, '') AS form,
					a.status_code, COUNT(1) AS request_count, SUM(CAST(a.duration_ms AS BIGINT)) AS total_ms, MAX(a.duration_ms) AS max_ms
				INTO #agg
				FROM dbo.restApiLogReq a JOIN #batch b ON b.id = a.id
				GROUP BY DATEADD(DAY, DATEDIFF(DAY, 0, a.created_at), 0), ISNULL(a.u_id, 0), ISNULL(a.form, ''), a.status_code

			UPDATE a SET a.request_count = a.request_count + b.request_count, a.total_ms = a.total_ms + b.total_ms,
					a.max_ms = CASE WHEN b.max_ms > a.max_ms THEN b.max_ms ELSE a.max_ms END
				FROM dbo.restApiLogAgg a
				JOIN #agg b ON b.log_date = a.log_date AND b.u_id = a.u_id AND b.form = a.form AND b.status_code = a.status_code

			INSERT INTO dbo.restApiLogAgg (log_date, u_id, form, status_code, request_count, total_ms, max_ms)
				SELECT a.log_date, a.u_id, a.form, a.status_code, a.request_count, a.total_ms, a.max_ms
					FROM #agg a
					WHERE NOT EXISTS (SELECT 1 FROM dbo.restApiLogAgg b
						WHERE b.log_date = a.log_date AND b.u_id = a.u_id AND b.form = a.form AND b.status_code = a.status_code)

			DELETE a FROM dbo.restApiLogReq a JOIN #batch b ON b.id = a.id
			DROP TABLE #agg

			COMMIT TRANSACTION
			SELECT @Requests = @Requests + @Rows
		END
	END

	-- 2. Body quá hạn không còn request nào trỏ tới
	SELECT @Rows = 1
	WHILE @Rows > 0
	BEGIN
		DELETE TOP (@BatchSize) a
			FROM dbo.restApiLogBody a
			WHERE a.created_at < @Cut
				AND NOT EXISTS (SELECT 1 FROM dbo.restApiLogReq b WHERE b.request_sha256 = a.sha256)
				AND NOT EXISTS (SELECT 1 FROM dbo.restApiLogReq b WHERE b.response_sha256 = a.sha256)
		SELECT @Rows = @@ROWCOUNT, @Bodies = @Bodies + @@ROWCOUNT
	END

	-- 3. Sự kiện tấn công quá hạn: cộng vào restApiLogAttackAgg rồi xoá
	SELECT @Rows = 1
	WHILE @Rows > 0
	BEGIN
		TRUNCATE TABLE #batch
		INSERT INTO #batch (id) SELECT TOP (@BatchSize) a.id FROM dbo.restApiLogAttack a WHERE a.created_at < @AttackCut ORDER BY a.id
		SELECT @Rows = @@ROWCOUNT

		IF @Rows > 0
		BEGIN
			BEGIN TRANSACTION

			SELECT DATEADD(DAY, DATEDIFF(DAY, 0, a.created_at), 0) AS log_date, ISNULL(a.ip_address, '') AS ip_address, a.code, COUNT(1) AS event_count
				INTO #attack
				FROM dbo.restApiLogAttack a JOIN #batch b ON b.id = a.id
				GROUP BY DATEADD(DAY, DATEDIFF(DAY, 0, a.created_at), 0), ISNULL(a.ip_address, ''), a.code

			UPDATE a SET a.event_count = a.event_count + b.event_count
				FROM dbo.restApiLogAttackAgg a
				JOIN #attack b ON b.log_date = a.log_date AND b.ip_address = a.ip_address AND b.code = a.code

			INSERT INTO dbo.restApiLogAttackAgg (log_date, ip_address, code, event_count)
				SELECT a.log_date, a.ip_address, a.code, a.event_count
					FROM #attack a
					WHERE NOT EXISTS (SELECT 1 FROM dbo.restApiLogAttackAgg b WHERE b.log_date = a.log_date AND b.ip_address = a.ip_address AND b.code = a.code)

			DELETE a FROM dbo.restApiLogAttack a JOIN #batch b ON b.id = a.id
			DROP TABLE #attack

			COMMIT TRANSACTION
			SELECT @Attacks = @Attacks + @Rows
		END
	END

	SELECT @Requests AS requests, @Bodies AS bodies, @Attacks AS attacks

	SET NOCOUNT OFF
END
GO

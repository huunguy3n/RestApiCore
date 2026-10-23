-- Tra log v3 cho quản trị: lọc theo thời gian, user, đường dẫn, status, req_id (= X-Request-ID)
-- Tương thích SQL Server 2008, chạy lại được
IF OBJECT_ID('dbo.restApi_LogSearch', 'P') IS NOT NULL
	DROP PROCEDURE dbo.restApi_LogSearch
GO

CREATE PROCEDURE dbo.restApi_LogSearch
	@FromDate DATETIME,
	@ToDate DATETIME,
	@UserName VARCHAR(32) = NULL,
	@Path NVARCHAR(256) = NULL, -- tiền tố, vd /api/v3/sale-invoice
	@StatusCode INT = NULL,
	@ReqId UNIQUEIDENTIFIER = NULL,
	@WithBody BIT = 0, -- 1: kèm body nén GZip (giải nén phía client)
	@Top INT = 100
AS
--C--NGUYENTDH 23/10/2026: @ReqId có thì bỏ qua khoảng thời gian
BEGIN
	SET NOCOUNT ON

	SELECT TOP (@Top) a.req_id, a.created_at, a.u_id, a.user_name, a.ip_address, a.method, a.path, a.query_string, a.form,
			a.status_code, a.result_code, a.duration_ms, a.request_bytes, a.response_bytes, a.content_sha256, a.nonce,
			a.client_timestamp, a.signature_result, a.idempotency_key, a.request_sha256, a.response_sha256,
			CASE WHEN @WithBody = 1 THEN b.content END AS request_body,
			CASE WHEN @WithBody = 1 THEN c.content END AS response_body
		FROM dbo.restApiLogReq a
		LEFT JOIN dbo.restApiLogBody b ON @WithBody = 1 AND b.sha256 = a.request_sha256
		LEFT JOIN dbo.restApiLogBody c ON @WithBody = 1 AND c.sha256 = a.response_sha256
		WHERE (@ReqId IS NOT NULL AND a.req_id = @ReqId
				OR @ReqId IS NULL AND a.created_at >= @FromDate AND a.created_at < @ToDate)
			AND (@UserName IS NULL OR a.user_name = @UserName)
			AND (@Path IS NULL OR a.path LIKE REPLACE(REPLACE(REPLACE(@Path, '[', '[[]'), '%', '[%]'), '_', '[_]') + N'%')
			AND (@StatusCode IS NULL OR a.status_code = @StatusCode)
		ORDER BY a.created_at DESC, a.id DESC

	SET NOCOUNT OFF
END
GO

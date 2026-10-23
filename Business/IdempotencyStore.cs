using System.Data;
using Microsoft.Data.SqlClient;
using Models;

namespace Business
{
    public enum IdempotencyState
    {
        /// <summary>Lần đầu: đã giữ chỗ, chạy request rồi gọi CompleteAsync (hoặc ReleaseAsync nếu lỗi)</summary>
        Reserved,
        /// <summary>Đã có kết quả: trả lại response cũ</summary>
        Replay,
        /// <summary>Cùng key nhưng body/query/form khác → 409</summary>
        Conflict,
        /// <summary>Request đầu với key này vẫn đang chạy → 409</summary>
        InProgress
    }

    public sealed record IdempotencyResult(IdempotencyState State, int StatusCode = 0, string ResponseBody = "");

    public interface IIdempotencyStore
    {
        Task<IdempotencyResult> ReserveAsync(int uId, string key, string form, string requestSha256, CancellationToken cancellationToken = default);
        Task CompleteAsync(int uId, string key, int statusCode, string responseBody, CancellationToken cancellationToken = default);
        Task ReleaseAsync(int uId, string key);
    }

    /// <summary>
    /// Idempotency-Key trên restApiIdempotency (KE_HOACH 4.3): giữ response idempotencyHours giờ.
    /// Giữ chỗ bằng INSERT có điều kiện dưới khóa UPDLOCK + HOLDLOCK nên hai request cùng key chỉ một chạy
    /// </summary>
    public class IdempotencyStore(AppConfig appConfig) : IIdempotencyStore
    {
        public async Task<IdempotencyResult> ReserveAsync(int uId, string key, string form, string requestSha256, CancellationToken cancellationToken = default)
        {
            var ds = await Provider.Provider.RetrieveAsync(appConfig.appConnectionString, """
                SET NOCOUNT ON
                BEGIN TRANSACTION
                    DELETE FROM restApiIdempotency WHERE u_id = @u_id AND idempotency_key = @key AND expires_at < GETDATE()

                    -- HOLDLOCK = serializable cho riêng câu này (không SET ISOLATION LEVEL: 2008 R2 không reset khi trả connection về pool)
                    IF NOT EXISTS (SELECT 1 FROM restApiIdempotency WITH (UPDLOCK, HOLDLOCK) WHERE u_id = @u_id AND idempotency_key = @key)
                    BEGIN
                        INSERT INTO restApiIdempotency (u_id, idempotency_key, form, request_sha256, expires_at)
                            VALUES (@u_id, @key, @form, @sha, DATEADD(HOUR, @hours, GETDATE()))
                        SELECT 1 AS reserved, NULL AS form, NULL AS request_sha256, NULL AS status_code, NULL AS response_body
                    END
                    ELSE
                        SELECT 0 AS reserved, form, request_sha256, status_code, response_body
                            FROM restApiIdempotency WHERE u_id = @u_id AND idempotency_key = @key
                COMMIT TRANSACTION
                """, cancellationToken,
                new SqlParameter("@u_id", SqlDbType.Int) { Value = uId },
                new SqlParameter("@key", SqlDbType.VarChar, 128) { Value = key },
                new SqlParameter("@form", SqlDbType.VarChar, 32) { Value = form },
                new SqlParameter("@sha", SqlDbType.Char, 64) { Value = requestSha256 },
                new SqlParameter("@hours", SqlDbType.Int) { Value = appConfig.idempotencyHours });

            DataRow row = ds.Tables[0].Rows[0];
            if (Convert.ToInt32(row["reserved"]) == 1)
            {
                return new IdempotencyResult(IdempotencyState.Reserved);
            }
            if (row["form"].ToString() != form || row["request_sha256"].ToString() != requestSha256)
            {
                return new IdempotencyResult(IdempotencyState.Conflict);
            }
            if (row["status_code"] == DBNull.Value)
            {
                return new IdempotencyResult(IdempotencyState.InProgress);
            }
            return new IdempotencyResult(IdempotencyState.Replay, Convert.ToInt32(row["status_code"]), row["response_body"]?.ToString() ?? "");
        }

        public async Task CompleteAsync(int uId, string key, int statusCode, string responseBody, CancellationToken cancellationToken = default)
        {
            await Provider.Provider.ExecuteAsync(appConfig.appConnectionString,
                "UPDATE restApiIdempotency SET status_code = @status, response_body = @body WHERE u_id = @u_id AND idempotency_key = @key",
                cancellationToken,
                new SqlParameter("@status", SqlDbType.Int) { Value = statusCode },
                new SqlParameter("@body", SqlDbType.NVarChar, -1) { Value = responseBody },
                new SqlParameter("@u_id", SqlDbType.Int) { Value = uId },
                new SqlParameter("@key", SqlDbType.VarChar, 128) { Value = key });
        }

        /// <summary>
        /// Lỗi không lường trước (500, mất kết nối): bỏ giữ chỗ để client gửi lại được với cùng key
        /// </summary>
        public async Task ReleaseAsync(int uId, string key)
        {
            await Provider.Provider.ExecuteAsync(appConfig.appConnectionString,
                "DELETE FROM restApiIdempotency WHERE u_id = @u_id AND idempotency_key = @key AND status_code IS NULL",
                default,
                new SqlParameter("@u_id", SqlDbType.Int) { Value = uId },
                new SqlParameter("@key", SqlDbType.VarChar, 128) { Value = key });
        }
    }
}

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

    /// <summary>
    /// Idempotency-Key (KE_HOACH 4.3): giữ chỗ nguyên tử theo (u_id, key), lưu status + body response để trả lại.
    /// Cài đặt SQL: DataAccess.IdempotencyRepository (restApiIdempotency)
    /// </summary>
    public interface IIdempotencyStore
    {
        Task<IdempotencyResult> ReserveAsync(int uId, string key, string form, string requestSha256, CancellationToken cancellationToken = default);
        Task CompleteAsync(int uId, string key, int statusCode, string responseBody, CancellationToken cancellationToken = default);
        Task ReleaseAsync(int uId, string key);
    }
}

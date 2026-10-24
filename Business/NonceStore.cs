using Microsoft.Extensions.Caching.Memory;

namespace Business
{
    /// <summary>
    /// Nonce đã dùng theo u_id (chữ ký request và gói đăng nhập portal). Kiểm và ghi nguyên tử (một instance, in-process):
    /// recycle app pool thì mất, nhưng nonce cũ cũng đã quá clockSkewSeconds nên không phát lại được
    /// </summary>
    public class NonceStore(IMemoryCache cache)
    {
        private readonly object _lock = new();

        /// <summary>Thời gian giữ nonce: 2 × clockSkewSeconds, đủ phủ mọi nonce còn trong độ lệch giờ cho phép</summary>
        public static TimeSpan ReplayWindow(int clockSkewSeconds) => TimeSpan.FromSeconds(2 * clockSkewSeconds);

        /// <returns>false nếu nonce đã dùng</returns>
        public bool TryUse(int uId, string nonce, TimeSpan ttl)
        {
            string key = $"nonce:{uId}:{nonce}";
            lock (_lock)
            {
                if (cache.TryGetValue(key, out _))
                {
                    return false;
                }
                cache.Set(key, true, ttl);
                return true;
            }
        }
    }
}

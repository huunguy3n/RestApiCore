namespace WebPortal.Portal
{
    /// <summary>
    /// Khóa đăng nhập sau nhiều lần sai. Hai lớp để kẻ tấn công KHÔNG khóa được tài khoản của người khác từ một IP:
    /// khóa theo cặp (khóa, IP) sau MaxFailures lần, khóa theo khóa chỉ khi tổng lần sai (mọi IP) vượt GlobalMaxFailures.
    /// Bộ đếm trong bộ nhớ một tiến trình (khởi động lại thì đếm lại)
    /// </summary>
    public sealed class LoginThrottle(TimeProvider time)
    {
        public const int MaxFailures = 5;
        public const int GlobalMaxFailures = 30;
        public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
        private const int MaxTracked = 20000;

        private sealed record Attempt(int Failures, DateTimeOffset First, DateTimeOffset LockedUntil);

        private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public bool IsLocked(string key, string? ip) => Locked("p:" + key + "|" + ip) || Locked("g:" + key);

        public void RecordFailure(string key, string? ip)
        {
            Record("p:" + key + "|" + ip, MaxFailures);
            Record("g:" + key, GlobalMaxFailures);
        }

        public void Reset(string key, string? ip)
        {
            lock (_gate)
            {
                _attempts.Remove("p:" + key + "|" + ip);
            }
        }

        private bool Locked(string key)
        {
            lock (_gate)
            {
                return _attempts.TryGetValue(key, out Attempt? state) && state.LockedUntil > time.GetUtcNow();
            }
        }

        private void Record(string key, int threshold)
        {
            lock (_gate)
            {
                DateTimeOffset now = time.GetUtcNow();
                if (_attempts.Count >= MaxTracked)
                {
                    foreach (string stale in _attempts.Where(a => a.Value.LockedUntil <= now && now - a.Value.First > LockFor).Select(a => a.Key).ToList())
                    {
                        _attempts.Remove(stale);
                    }
                }
                bool fresh = !_attempts.TryGetValue(key, out Attempt? state) || now - state.First > LockFor && state.LockedUntil <= now;
                int failures = fresh ? 1 : state!.Failures + 1;
                _attempts[key] = new Attempt(failures, fresh ? now : state!.First, failures >= threshold ? now + LockFor : fresh ? default : state!.LockedUntil);
            }
        }
    }
}

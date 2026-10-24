namespace WebPortal.Portal
{
    /// <summary>Tài khoản đã xác thực mật khẩu, chờ mở phiên</summary>
    public sealed record VerifiedAccount(string Kind, int AccountId, string Login, string DisplayName, int? ProjectId, string? MaDa);

    /// <summary>Kết quả mở phiên: có phiên mới, hoặc phải xác nhận thay phiên đang mở ở nơi khác</summary>
    public sealed record SessionStart(Guid? SessionId, ChallengeRecord? Challenge, SessionRecord? ExistingSession)
    {
        public bool NeedsConfirmation => Challenge != null;
    }

    /// <summary>Phiên cũ thấy có người đang chờ thay phiên</summary>
    public sealed record SessionNotice(string? IpAddress, string? UserAgent, DateTime At);

    /// <summary>
    /// Mỗi tài khoản chỉ có một phiên hiệu lực (chỉ mục duy nhất UX_portalSession_active).
    /// Đăng nhập nơi khác khi đang có phiên: tạo challenge 2 phút, phiên cũ nhận cảnh báo, người đăng nhập mới phải xác nhận thì phiên cũ mới bị thu hồi.
    /// Phiên hết hạn khi rảnh IdleTimeout hoặc quá AbsoluteTimeout
    /// </summary>
    public sealed class SessionService(IPortalStore store, TimeProvider time)
    {
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(8);
        public static readonly TimeSpan ChallengeTimeout = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);

        private DateTime Now => time.GetLocalNow().DateTime;

        public bool IsExpired(SessionRecord session) => Now - session.LastSeenAt > IdleTimeout || Now - session.CreatedAt > AbsoluteTimeout;

        public async Task<SessionStart> BeginAsync(VerifiedAccount account, string? ip, string? userAgent, CancellationToken cancellationToken = default)
        {
            SessionRecord? existing = await store.FindActiveSessionAsync(account.Kind, account.AccountId, cancellationToken);
            if (existing != null && IsExpired(existing))
            {
                await store.RevokeSessionAsync(existing.Id, Now - existing.CreatedAt > AbsoluteTimeout ? "expired" : "idle", cancellationToken);
                existing = null;
            }

            if (existing == null)
            {
                SessionRecord session = NewSession(account, ip, userAgent);
                if (await store.CreateSessionAsync(session, cancellationToken))
                {
                    return new SessionStart(session.Id, null, null);
                }
                // Vừa có phiên khác mở đồng thời: coi như đang có phiên, đi đường xác nhận
                existing = await store.FindActiveSessionAsync(account.Kind, account.AccountId, cancellationToken);
                if (existing == null)
                {
                    throw new InvalidOperationException("Không mở được phiên");
                }
            }

            ChallengeRecord challenge = new(Guid.NewGuid(), account.Kind, account.AccountId, account.ProjectId, existing.Id, ip, userAgent, Now, Now + ChallengeTimeout, null, null);
            await store.CreateChallengeAsync(challenge, cancellationToken);
            return new SessionStart(null, challenge, existing);
        }

        /// <summary>
        /// Người đăng nhập mới đồng ý thay phiên. Chỉ nhận challenge còn hạn, chưa đóng, đúng tài khoản; trả id phiên mới hoặc null
        /// </summary>
        public async Task<Guid?> ConfirmAsync(Guid challengeId, VerifiedAccount account, CancellationToken cancellationToken = default)
        {
            ChallengeRecord? challenge = await store.FindChallengeAsync(challengeId, cancellationToken);
            if (challenge == null || challenge.ClosedAt != null || challenge.AccountType != account.Kind || challenge.AccountId != account.AccountId)
            {
                return null;
            }
            if (challenge.ExpiresAt <= Now)
            {
                await store.CloseChallengeAsync(challengeId, "expired", cancellationToken);
                return null;
            }
            if (!await store.CloseChallengeAsync(challengeId, "accepted", cancellationToken))
            {
                return null;
            }
            SessionRecord session = NewSession(account, challenge.IpAddress, challenge.UserAgent);
            return await store.ReplaceSessionAsync(challenge.OldSessionId, session, "replaced", cancellationToken) ? session.Id : null;
        }

        public Task CancelAsync(Guid challengeId, CancellationToken cancellationToken = default) => store.CloseChallengeAsync(challengeId, "cancelled", cancellationToken);

        /// <summary>Phiên còn hiệu lực? Hết hạn thì thu hồi luôn. Cập nhật last_seen_at tối đa mỗi TouchInterval</summary>
        public async Task<SessionRecord?> ValidateAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            SessionRecord? session = await store.FindSessionAsync(sessionId, cancellationToken);
            if (session == null || session.RevokedAt != null)
            {
                return null;
            }
            if (IsExpired(session))
            {
                await store.RevokeSessionAsync(session.Id, Now - session.CreatedAt > AbsoluteTimeout ? "expired" : "idle", cancellationToken);
                return null;
            }
            if (Now - session.LastSeenAt > TouchInterval)
            {
                await store.TouchSessionAsync(session.Id, cancellationToken);
            }
            return session;
        }

        /// <summary>Có người đăng nhập đúng mật khẩu ở nơi khác và đang chờ thay phiên này không</summary>
        public async Task<SessionNotice?> NoticeAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            ChallengeRecord? challenge = await store.FindOpenChallengeForSessionAsync(sessionId, Now, cancellationToken);
            return challenge == null ? null : new SessionNotice(challenge.IpAddress, challenge.UserAgent, challenge.CreatedAt);
        }

        /// <summary>Lý do phiên bị thu hồi (hiện ở trang đăng nhập cho người bị thay phiên)</summary>
        public async Task<string?> RevokedReasonAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            SessionRecord? session = await store.FindSessionAsync(sessionId, cancellationToken);
            if (session?.RevokedReason != "replaced")
            {
                return session?.RevokedReason;
            }
            SessionRecord? replacement = await store.FindActiveSessionAsync(session.AccountType, session.AccountId, cancellationToken);
            return replacement == null ? "replaced" : $"replaced|{replacement.IpAddress}|{replacement.CreatedAt:yyyy-MM-dd HH:mm}";
        }

        public Task EndAsync(Guid sessionId, CancellationToken cancellationToken = default) => store.RevokeSessionAsync(sessionId, "logout", cancellationToken);

        public Task EndAllAsync(string kind, int accountId, string reason, CancellationToken cancellationToken = default) =>
            store.RevokeAccountSessionsAsync(kind, accountId, reason, cancellationToken);

        private SessionRecord NewSession(VerifiedAccount account, string? ip, string? userAgent) =>
            new(Guid.NewGuid(), account.Kind, account.AccountId, account.ProjectId, Now, Now, ip, userAgent, null, null);
    }
}

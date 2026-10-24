using Tests.Support;
using WebPortal.Portal;

namespace Tests
{
    /// <summary>Đồng hồ dịch được, múi UTC để GetLocalNow = GetUtcNow</summary>
    internal sealed class ShiftTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Add(TimeSpan span) => Now += span;
    }

    public class SessionServiceTests
    {
        private static readonly VerifiedAccount Account = new(AccountKind.Admin, 7, "admin.aqtc", "Admin AQTC", 3, "AQTC");

        private static (SessionService Service, MemoryPortalStore Store, ShiftTime Time) Create()
        {
            ShiftTime time = new();
            MemoryPortalStore store = new();
            return (new SessionService(store, time), store, time);
        }

        [Fact]
        public async Task DangNhapLanDau_MoPhienKhongCanXacNhan()
        {
            var (service, store, _) = Create();

            SessionStart start = await service.BeginAsync(Account, "10.0.0.8", "Chrome");

            Assert.False(start.NeedsConfirmation);
            Assert.NotNull(start.SessionId);
            Assert.Null(start.Challenge);
            Assert.NotNull(await service.ValidateAsync(start.SessionId.Value));
            Assert.Single(store.Sessions.Values, s => s.RevokedAt == null);
        }

        [Fact]
        public async Task DangNhapNoiKhac_TaoChallenge_PhienCuConHieuLuc()
        {
            var (service, store, _) = Create();
            SessionStart first = await service.BeginAsync(Account, "10.0.0.8", "Chrome");
            SessionStart second = await service.BeginAsync(Account, "10.0.0.9", "Edge");

            Assert.True(second.NeedsConfirmation);
            Assert.Null(second.SessionId);
            Assert.Equal(first.SessionId, second.ExistingSession!.Id);
            Assert.Equal("10.0.0.8", second.ExistingSession.IpAddress);
            Assert.Equal(first.SessionId, store.Challenges[second.Challenge!.Id].OldSessionId);
            Assert.NotNull(await service.ValidateAsync(first.SessionId!.Value));

            SessionNotice? notice = await service.NoticeAsync(first.SessionId.Value);
            Assert.NotNull(notice);
            Assert.Equal("10.0.0.9", notice.IpAddress);
        }

        [Fact]
        public async Task DongYThayPhien_ThuHoiPhienCu_MoPhienMoi()
        {
            var (service, store, _) = Create();
            SessionStart first = await service.BeginAsync(Account, "10.0.0.8", "Chrome");
            SessionStart second = await service.BeginAsync(Account, "10.0.0.9", "Edge");

            Guid? next = await service.ConfirmAsync(second.Challenge!.Id, Account);

            Assert.NotNull(next);
            Assert.Null(await service.ValidateAsync(first.SessionId!.Value));
            Assert.NotNull(await service.ValidateAsync(next.Value));
            Assert.Equal("replaced", store.Sessions[first.SessionId.Value].RevokedReason);
            Assert.Equal("10.0.0.9", store.Sessions[next.Value].IpAddress);

            string? reason = await service.RevokedReasonAsync(first.SessionId.Value);
            Assert.StartsWith("replaced|10.0.0.9|", reason);
        }

        [Fact]
        public async Task KhongDongY_ChallengeHuy_PhienCuGiuNguyen()
        {
            var (service, _, _) = Create();
            SessionStart first = await service.BeginAsync(Account, "10.0.0.8", "Chrome");
            SessionStart second = await service.BeginAsync(Account, "10.0.0.9", "Edge");

            await service.CancelAsync(second.Challenge!.Id);

            Assert.Null(await service.ConfirmAsync(second.Challenge.Id, Account));
            Assert.NotNull(await service.ValidateAsync(first.SessionId!.Value));
            Assert.Null(await service.NoticeAsync(first.SessionId.Value));
        }

        [Fact]
        public async Task ChallengeQuaHan_KhongThayPhien()
        {
            var (service, _, time) = Create();
            SessionStart first = await service.BeginAsync(Account, "10.0.0.8", "Chrome");
            SessionStart second = await service.BeginAsync(Account, "10.0.0.9", "Edge");
            time.Add(SessionService.ChallengeTimeout.Add(TimeSpan.FromSeconds(1)));

            Assert.Null(await service.ConfirmAsync(second.Challenge!.Id, Account));
            Assert.NotNull(await service.ValidateAsync(first.SessionId!.Value));
        }

        [Fact]
        public async Task ConfirmSaiTaiKhoan_TuChoi()
        {
            var (service, _, _) = Create();
            await service.BeginAsync(Account, "10.0.0.8", "Chrome");
            SessionStart second = await service.BeginAsync(Account, "10.0.0.9", "Edge");
            VerifiedAccount other = Account with { AccountId = 99, Login = "khac" };

            Assert.Null(await service.ConfirmAsync(second.Challenge!.Id, other));
        }

        [Fact]
        public async Task PhienRan_IdleTimeout_BiThuHoi()
        {
            var (service, store, time) = Create();
            SessionStart first = await service.BeginAsync(Account, "10.0.0.8", "Chrome");
            time.Add(SessionService.IdleTimeout.Add(TimeSpan.FromSeconds(1)));

            Assert.Null(await service.ValidateAsync(first.SessionId!.Value));
            Assert.Equal("idle", store.Sessions[first.SessionId.Value].RevokedReason);
        }

        [Fact]
        public async Task PhienQua8Gio_ExpiredDuConDung()
        {
            var (service, store, time) = Create();
            SessionStart first = await service.BeginAsync(Account, "10.0.0.8", "Chrome");
            // Chạm last_seen trước khi hết hạn tuyệt đối: Validate cập nhật last_seen, nhưng CreatedAt vẫn cũ
            time.Add(TimeSpan.FromMinutes(20));
            Assert.NotNull(await service.ValidateAsync(first.SessionId!.Value));
            time.Add(SessionService.AbsoluteTimeout);

            Assert.Null(await service.ValidateAsync(first.SessionId.Value));
            Assert.Equal("expired", store.Sessions[first.SessionId.Value].RevokedReason);
        }

        [Fact]
        public async Task DangNhapLaiKhiPhienCuHetHan_MoPhienMoiKhongCanXacNhan()
        {
            var (service, _, time) = Create();
            SessionStart first = await service.BeginAsync(Account, "10.0.0.8", "Chrome");
            time.Add(SessionService.IdleTimeout.Add(TimeSpan.FromMinutes(1)));

            SessionStart again = await service.BeginAsync(Account, "10.0.0.9", "Edge");

            Assert.False(again.NeedsConfirmation);
            Assert.NotEqual(first.SessionId, again.SessionId);
        }

        [Fact]
        public async Task EndAll_ThuHoiMoiPhienCuaTaiKhoan()
        {
            var (service, _, _) = Create();
            SessionStart first = await service.BeginAsync(Account, "10.0.0.8", "Chrome");

            await service.EndAllAsync(Account.Kind, Account.AccountId, "password-changed");

            Assert.Null(await service.ValidateAsync(first.SessionId!.Value));
            Assert.Equal("password-changed", await service.RevokedReasonAsync(first.SessionId.Value));
        }
    }
}

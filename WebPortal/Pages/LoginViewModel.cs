using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using WebPortal.Portal;

namespace WebPortal.Pages
{
    public sealed class LoginViewModel
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string? Login { get; set; }
        public string? Message { get; set; }
        public string? Info { get; set; }
        public string? PendingToken { get; set; }
        public string? ExistingIp { get; set; }
        public string? ExistingSince { get; set; }
    }

    /// <summary>
    /// Luá»“ng Ä‘Äƒng nháº­p dÃ¹ng chung /cpanel vÃ  /{ma_da}: kiá»ƒm máº­t kháº©u â†’ má»Ÿ phiÃªn; Ä‘ang cÃ³ phiÃªn á»Ÿ nÆ¡i khÃ¡c thÃ¬ hiá»‡n xÃ¡c nháº­n thay phiÃªn
    /// (phiÃªn cÅ© nháº­n cáº£nh bÃ¡o), Ä‘á»“ng Ã½ má»›i thu há»“i phiÃªn cÅ©
    /// </summary>
    public sealed class LoginHandler(PortalPageModel page, SessionService sessions, IDataProtectionProvider protection, TimeProvider time, ILogger logger)
    {
        public async Task<IActionResult?> StartAsync(Func<Task<LoginOutcome>> verify, LoginViewModel view, string scheme,
            Func<VerifiedAccount, Guid, string, Task> signIn, string successUrl)
        {
            LoginOutcome outcome;
            try
            {
                outcome = await verify();
            }
            catch (InvalidOperationException)
            {
                view.Message = "Portal chÆ°a Ä‘Æ°á»£c cáº¥u hÃ¬nh Ä‘áº§y Ä‘á»§. LiÃªn há»‡ quáº£n trá»‹.";
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "ÄÄƒng nháº­p portal lá»—i");
                view.Message = "KhÃ´ng Ä‘Äƒng nháº­p Ä‘Æ°á»£c lÃºc nÃ y. Thá»­ láº¡i sau.";
                return null;
            }
            if (!outcome.Success)
            {
                view.Message = outcome.Code == "ACCOUNT_LOCKED" ? "Táº¡m khÃ³a do nháº­p sai nhiá»u láº§n. Thá»­ láº¡i sau Ã­t phÃºt." : "TÃ i khoáº£n hoáº·c máº­t kháº©u khÃ´ng Ä‘Ãºng.";
                return null;
            }

            SessionStart start = await sessions.BeginAsync(outcome.Account!, page.HttpContext.Connection.RemoteIpAddress?.ToString(),
                page.HttpContext.Request.Headers.UserAgent.ToString(), page.HttpContext.RequestAborted);
            if (start.SessionId is Guid sessionId)
            {
                await signIn(outcome.Account!, sessionId, scheme);
                return new RedirectResult(successUrl);
            }

            view.PendingToken = LoginFlow.Protect(protection, new PendingLogin(start.Challenge!.Id, outcome.Account!, time.GetUtcNow() + SessionService.ChallengeTimeout));
            view.ExistingIp = start.ExistingSession?.IpAddress;
            view.ExistingSince = start.ExistingSession?.CreatedAt.ToString("yyyy-MM-dd HH:mm");
            return null;
        }

        public async Task<IActionResult?> ConfirmAsync(string? token, LoginViewModel view, string scheme, Func<VerifiedAccount, Guid, string, Task> signIn, string successUrl)
        {
            PendingLogin? pending = LoginFlow.Unprotect(protection, token, time);
            Guid? sessionId = pending == null ? null : await sessions.ConfirmAsync(pending.ChallengeId, pending.Account, page.HttpContext.RequestAborted);
            if (sessionId == null)
            {
                view.Message = "YÃªu cáº§u xÃ¡c nháº­n Ä‘Ã£ háº¿t háº¡n hoáº·c khÃ´ng cÃ²n há»£p lá»‡. ÄÄƒng nháº­p láº¡i.";
                return null;
            }
            await signIn(pending!.Account, sessionId.Value, scheme);
            return new RedirectResult(successUrl);
        }

        public async Task CancelAsync(string? token, LoginViewModel view)
        {
            PendingLogin? pending = LoginFlow.Unprotect(protection, token, time);
            if (pending != null)
            {
                await sessions.CancelAsync(pending.ChallengeId, page.HttpContext.RequestAborted);
            }
            view.Info = "ÄÃ£ giá»¯ phiÃªn Ä‘ang má»Ÿ á»Ÿ nÆ¡i khÃ¡c.";
        }
    }
}

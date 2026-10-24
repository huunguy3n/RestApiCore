using Business;
using Microsoft.Extensions.Caching.Memory;

namespace WebPortal.Portal
{
    public sealed record ProjectPerformance(PortalProject Project, string? Error, IReadOnlyList<FormPerformance> Forms, DateTime At)
    {
        public int Requests => Forms.Sum(f => f.Requests);
        public int Errors => Forms.Sum(f => f.Errors);
        public double AverageMs => Requests == 0 ? 0 : (double)Forms.Sum(f => f.TotalMs) / Requests;
        public int MaxMs => Forms.Count == 0 ? 0 : Forms.Max(f => f.MaxMs);
        public int Slow => Forms.Sum(f => f.Ge2000);
    }

    public sealed record LogDetailView(LogRequestSummary Summary, string? QueryString, string? RequestBody, string? ResponseBody);

    /// <summary>
    /// Log request và hiệu suất của dự án, đọc thẳng restApiLogReq / restApiLogAgg trên DB dự án (log không chép về portal).
    /// Admin xem dự án của mình, SA xem mọi dự án; mở body (dữ liệu nghiệp vụ) luôn ghi nhật ký và che khóa bí mật
    /// </summary>
    public sealed class LogService(IPortalStore store, IProjectConnectionResolver resolver, IMemoryCache cache, TimeProvider time)
    {
        public const int PageSize = 50;
        public static readonly TimeSpan ProjectTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan DashboardCache = TimeSpan.FromSeconds(60);

        public async Task<OpResult> ListAsync(PortalIdentity actor, PortalProject project, DateTime? from, DateTime? to, string? user, string? form, int? status, string? reqId,
            int page, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            DateTime end = to ?? time.GetLocalNow().DateTime.AddMinutes(1);
            DateTime start = from ?? end.AddDays(-1);
            if (start >= end || end - start > LogRedaction.MaxRange)
            {
                return OpResult.Fail("INVALID_RANGE");
            }
            Guid? request = null;
            if (!string.IsNullOrWhiteSpace(reqId))
            {
                if (!Guid.TryParse(reqId.Trim(), out Guid parsed))
                {
                    return OpResult.Fail("INVALID_INPUT");
                }
                request = parsed;
            }
            try
            {
                LogPage result = await resolver.Resolve(project).Logs.ListAsync(new LogFilter(start, end, LogRedaction.Clean(user, 32), LogRedaction.Clean(form, 32), status, request),
                    Math.Max(page, 1), PageSize, cancellationToken);
                return new OpResult("SUCCESS", result);
            }
            catch (Exception ex) when (ApiUserService.IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ApiUserService.ProjectMessage(ex));
            }
        }

        public async Task<OpResult> DetailAsync(PortalIdentity actor, PortalProject project, string? reqId, string? ip, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            if (!Guid.TryParse(reqId, out Guid id))
            {
                return OpResult.Fail("INVALID_INPUT");
            }
            try
            {
                LogRequestDetail? detail = await resolver.Resolve(project).Logs.GetAsync(id, LogRedaction.MaxBodyBytes, cancellationToken);
                if (detail == null)
                {
                    return OpResult.Fail("NOT_FOUND");
                }
                // Body là dữ liệu nghiệp vụ của khách: mỗi lần mở đều ghi vết
                await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "logs.body.read", id.ToString(), "SUCCESS", null, ip), cancellationToken);
                return new OpResult("SUCCESS", new LogDetailView(detail.Summary, detail.QueryString, LogRedaction.Redact(detail.RequestBody), LogRedaction.Redact(detail.ResponseBody)));
            }
            catch (Exception ex) when (ApiUserService.IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ApiUserService.ProjectMessage(ex));
            }
        }

        public async Task<OpResult> PerformanceAsync(PortalIdentity actor, PortalProject project, int days, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            return new OpResult("SUCCESS", await LoadAsync(project, Math.Clamp(days, 1, 31), cancellationToken));
        }

        /// <summary>
        /// Tổng quan SA: hiệu suất mọi dự án đang hoạt động, đọc song song, mỗi dự án tối đa ProjectTimeout; cache DashboardCache.
        /// Dự án lỗi chỉ đánh dấu "không truy cập được", không làm chậm dự án khác
        /// </summary>
        public async Task<OpResult> DashboardAsync(PortalIdentity actor, int days, CancellationToken cancellationToken = default)
        {
            if (!actor.IsSuperAdmin)
            {
                return OpResult.Fail("FORBIDDEN");
            }
            days = Math.Clamp(days, 1, 31);
            string key = "dashboard:" + days;
            if (cache.TryGetValue(key, out IReadOnlyList<ProjectPerformance>? cached) && cached != null)
            {
                return new OpResult("SUCCESS", cached);
            }
            IReadOnlyList<PortalProject> projects = (await store.ListProjectsAsync(cancellationToken)).Where(p => p.Active).ToList();
            ProjectPerformance[] results = await Task.WhenAll(projects.Select(p => LoadAsync(p, days, cancellationToken)));
            cache.Set(key, (IReadOnlyList<ProjectPerformance>)results, DashboardCache);
            return new OpResult("SUCCESS", results);
        }

        private async Task<ProjectPerformance> LoadAsync(PortalProject project, int days, CancellationToken cancellationToken)
        {
            DateTime now = time.GetLocalNow().DateTime;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProjectTimeout);
            try
            {
                IReadOnlyList<FormPerformance> forms = await resolver.Resolve(project).Logs.PerformanceAsync(now.AddDays(-days), now.AddMinutes(1), timeout.Token);
                return new ProjectPerformance(project, null, forms, now);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ProjectPerformance(project, "Quá thời gian chờ", [], now);
            }
            catch (Exception ex) when (ApiUserService.IsProjectError(ex) || ex is InvalidOperationException)
            {
                return new ProjectPerformance(project, ApiUserService.ProjectMessage(ex), [], now);
            }
        }
    }
}

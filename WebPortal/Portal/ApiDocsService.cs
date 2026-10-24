using Business;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Options;

namespace WebPortal.Portal
{
    public sealed record ApiFormList(IReadOnlyList<ImportForm> Forms, bool AllForms, IReadOnlyList<string> MissingForms);

    /// <summary>
    /// Tài liệu cho user Api: form được cấp (đọc XML trong App_Data\Form của folder dự án), bảng mã lỗi (Message.xml của dự án).
    /// Quyền form đọc từ portal: có khai = đúng danh sách, rỗng = mọi form đang bật. Không trả SQL của form
    /// </summary>
    public sealed class ApiDocsService(IPortalStore store, IProjectConnectionResolver resolver, IMemoryCache cache)
    {
        public async Task<OpResult> FormsAsync(PortalIdentity actor, PortalProject project, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.IsApiOf(actor, project.Id) && !PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            IReadOnlyList<string> allowed = actor.IsApi ? (await store.FindApiUserAsync(actor.AccountId, cancellationToken))?.Forms ?? [] : [];
            ImportFormCatalog catalog;
            try
            {
                catalog = Catalog(project);
            }
            catch (Exception ex) when (ApiUserService.IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ApiUserService.ProjectMessage(ex));
            }
            List<ImportForm> enabled = catalog.Forms.Where(f => f.Enable).OrderBy(f => f.Resource, StringComparer.Ordinal).ThenBy(f => f.Verb, StringComparer.Ordinal).ToList();
            if (allowed.Count == 0)
            {
                return new OpResult("SUCCESS", new ApiFormList(enabled, true, []));
            }
            HashSet<string> set = new(allowed, StringComparer.OrdinalIgnoreCase);
            return new OpResult("SUCCESS", new ApiFormList(enabled.Where(f => set.Contains(f.Name)).ToList(), false,
                allowed.Where(a => !enabled.Any(f => string.Equals(f.Name, a, StringComparison.OrdinalIgnoreCase))).ToList()));
        }

        public async Task<OpResult> FormAsync(PortalIdentity actor, PortalProject project, string? form, CancellationToken cancellationToken = default)
        {
            OpResult list = await FormsAsync(actor, project, cancellationToken);
            if (!list.Success)
            {
                return list;
            }
            ImportForm? found = ((ApiFormList)list.Data!).Forms.FirstOrDefault(f => string.Equals(f.Name, form, StringComparison.OrdinalIgnoreCase));
            return found == null ? OpResult.Fail("NOT_FOUND") : new OpResult("SUCCESS", found);
        }

        public OpResult Messages(PortalIdentity actor, PortalProject project)
        {
            if (!PortalGuard.IsApiOf(actor, project.Id) && !PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            try
            {
                MessageList messages = resolver.Resolve(project).LoadMessages();
                return new OpResult("SUCCESS", messages.OrderBy(m => m.code, StringComparer.Ordinal).ToList());
            }
            catch (Exception ex) when (ApiUserService.IsProjectError(ex))
            {
                return new OpResult("PROJECT_UNREACHABLE", ApiUserService.ProjectMessage(ex));
            }
        }

        /// <summary>Catalog của folder dự án, cache theo thời điểm sửa mới nhất của các file XML</summary>
        public ImportFormCatalog Catalog(PortalProject project)
        {
            ProjectConnection connection = resolver.Resolve(project);
            DateTime stamp = Directory.Exists(connection.FormDirectory)
                ? Directory.EnumerateFiles(connection.FormDirectory, "*.xml").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max()
                : DateTime.MinValue;
            string key = $"forms:{project.ApiFolder}:{stamp.Ticks}";
            return cache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                return connection.LoadForms();
            })!;
        }
    }
}

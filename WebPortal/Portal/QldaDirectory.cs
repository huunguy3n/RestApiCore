using System.Data;
using Microsoft.Extensions.Caching.Memory;
using Provider;

namespace WebPortal.Portal
{
    /// <summary>Một dự án trong QLDA (nbdmda)</summary>
    public sealed record QldaProject(string MaDa, string Name, bool Active, string SourceDir);

    public interface IQldaDirectory
    {
        /// <summary>Null nếu không có mã này trong QLDA</summary>
        Task<QldaProject?> FindAsync(string maDa, CancellationToken cancellationToken = default);

        /// <summary>Dự án đang hoạt động (status = 1), để SA chọn khi tạo dự án trên portal</summary>
        Task<IReadOnlyList<QldaProject>> ListActiveAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Đọc nbdmda (chỉ SELECT). Kết quả cache 5 phút: QLDA đổi trạng thái thì tối đa 5 phút sau portal mới thấy.
    /// Chưa khai Qlda:ConnectionString thì ném InvalidOperationException (trang báo "chưa cấu hình QLDA")
    /// </summary>
    public sealed class SqlQldaDirectory(QldaSettings settings, IMemoryCache cache) : IQldaDirectory
    {
        public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

        public async Task<QldaProject?> FindAsync(string maDa, CancellationToken cancellationToken = default) =>
            (await LoadAsync(cancellationToken)).GetValueOrDefault(maDa);

        public async Task<IReadOnlyList<QldaProject>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            (await LoadAsync(cancellationToken)).Values.Where(p => p.Active).OrderBy(p => p.MaDa, StringComparer.OrdinalIgnoreCase).ToList();

        private async Task<Dictionary<string, QldaProject>> LoadAsync(CancellationToken cancellationToken)
        {
            if (cache.TryGetValue("qlda:projects", out Dictionary<string, QldaProject>? cached) && cached != null)
            {
                return cached;
            }
            if (settings.ConnectionString.Length == 0)
            {
                throw new InvalidOperationException("Chưa khai Portal:Qlda:ConnectionString");
            }
            if (!settings.IsValid)
            {
                throw new InvalidOperationException("Portal:Qlda: tên bảng/cột không hợp lệ");
            }

            SqlDatabase database = new(settings.ConnectionString, 15);
            DataSet ds = await database.RetrieveAsync($"""
                SELECT RTRIM(a.[{settings.CodeColumn}]) AS ma_da, a.[{settings.NameColumn}] AS ten_da, RTRIM(CAST(a.[{settings.StatusColumn}] AS VARCHAR(8))) AS status
                	 , a.[{settings.SourceDirColumn}] AS dir_src_web
                	FROM dbo.[{settings.Table}] a
                """, cancellationToken);
            Dictionary<string, QldaProject> projects = new(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow r in ds.Tables[0].Rows)
            {
                string code = r["ma_da"]?.ToString()?.Trim() ?? "";
                if (code.Length > 0)
                {
                    projects[code] = new QldaProject(code, r["ten_da"]?.ToString()?.Trim() ?? code, r["status"]?.ToString()?.Trim() == settings.ActiveStatus,
                        r["dir_src_web"]?.ToString()?.Trim() ?? "");
                }
            }
            cache.Set("qlda:projects", projects, CacheFor);
            return projects;
        }
    }
}

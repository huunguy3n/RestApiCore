using System.ComponentModel;
using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Models;
using Newtonsoft.Json;

namespace Business.V3
{
    /// <summary>Thông tin cursor: thuộc form và đơn vị nào, bao nhiêu trang</summary>
    [ImmutableObject(true)]
    public sealed record CursorInfo(string Form, string Unit, int PageCount, int PageSize, int TotalRowCount, int KeyCount);

    public interface ICursorStore
    {
        /// <summary>Lưu mọi trang, trả cursor</summary>
        Task<string> SaveAsync(int uId, string form, string unit, PagedResult paged, CancellationToken cancellationToken = default);
        /// <summary>null nếu cursor hết hạn, không tồn tại hoặc thuộc user khác</summary>
        Task<CursorInfo?> GetInfoAsync(int uId, string cursor, CancellationToken cancellationToken = default);
        /// <summary>JSON object { tên bảng: [dòng…] } của trang; null nếu hết hạn</summary>
        Task<string?> GetPageJsonAsync(int uId, string cursor, int page, CancellationToken cancellationToken = default);
        /// <summary>Giá trị key của trang (đánh dấu Transfer); null nếu hết hạn</summary>
        Task<IReadOnlyList<string>?> GetPageKeysAsync(int uId, string cursor, int page, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Cursor phân trang trên HybridCache (memory-only), TTL cursorTtlMinutes. Mỗi trang lưu JSON nén GZip.
    /// Khóa cache có u_id nên cursor của user này không đọc được bằng user khác. Recycle app pool → mất cursor (CURSOR_EXPIRED)
    /// </summary>
    public class CursorStore(AppConfig appConfig, HybridCache cache) : ICursorStore
    {
        public const string CacheTag = "cursor";

        /// <summary>Không ghi null vào cache khi đọc trượt</summary>
        private static readonly HybridCacheEntryOptions ReadOnly = new()
        {
            Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite
        };

        public async Task<string> SaveAsync(int uId, string form, string unit, PagedResult paged, CancellationToken cancellationToken = default)
        {
            string cursor = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var options = new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(appConfig.cursorTtlMinutes), LocalCacheExpiration = TimeSpan.FromMinutes(appConfig.cursorTtlMinutes) };

            for (int page = 0; page < paged.Pages.Count; page++)
            {
                await cache.SetAsync(PageKey(uId, cursor, page), Compress(ToJson(paged.Pages[page])), options, [CacheTag], cancellationToken);
                await cache.SetAsync(PageKey(uId, cursor, page) + ":keys", Compress(JsonConvert.SerializeObject(paged.Pages[page].Keys)), options, [CacheTag], cancellationToken);
            }
            // Ghi info sau cùng: có info là đủ trang
            await cache.SetAsync(InfoKey(uId, cursor), new CursorInfo(form, unit, paged.Pages.Count, paged.PageSize, paged.TotalRowCount, paged.KeyCount), options, [CacheTag], cancellationToken);
            return cursor;
        }

        public async Task<CursorInfo?> GetInfoAsync(int uId, string cursor, CancellationToken cancellationToken = default)
        {
            return await cache.GetOrCreateAsync<CursorInfo?>(InfoKey(uId, cursor), _ => ValueTask.FromResult<CursorInfo?>(null), ReadOnly, cancellationToken: cancellationToken);
        }

        public async Task<string?> GetPageJsonAsync(int uId, string cursor, int page, CancellationToken cancellationToken = default)
        {
            byte[]? data = await cache.GetOrCreateAsync<byte[]?>(PageKey(uId, cursor, page), _ => ValueTask.FromResult<byte[]?>(null), ReadOnly, cancellationToken: cancellationToken);
            // Đọc trượt HybridCache trả mảng rỗng thay vì null
            return data is null or { Length: 0 } ? null : Decompress(data);
        }

        public async Task<IReadOnlyList<string>?> GetPageKeysAsync(int uId, string cursor, int page, CancellationToken cancellationToken = default)
        {
            byte[]? data = await cache.GetOrCreateAsync<byte[]?>(PageKey(uId, cursor, page) + ":keys", _ => ValueTask.FromResult<byte[]?>(null), ReadOnly, cancellationToken: cancellationToken);
            return data is null or { Length: 0 } ? null : JsonConvert.DeserializeObject<List<string>>(Decompress(data));
        }

        /// <summary>{ tên bảng: [dòng…] }, cùng dạng với data của response</summary>
        public static string ToJson(DataPage page) => JsonConvert.SerializeObject(page.Tables);

        private static string InfoKey(int uId, string cursor) => $"cursor:{uId}:{cursor}";

        private static string PageKey(int uId, string cursor, int page) => $"cursor:{uId}:{cursor}:{page}";

        private static byte[] Compress(string json)
        {
            using MemoryStream output = new();
            using (GZipStream gzip = new(output, CompressionLevel.Fastest))
            {
                gzip.Write(Encoding.UTF8.GetBytes(json));
            }
            return output.ToArray();
        }

        private static string Decompress(byte[] data)
        {
            using GZipStream gzip = new(new MemoryStream(data), CompressionMode.Decompress);
            using StreamReader reader = new(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}

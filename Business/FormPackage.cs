using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Business
{
    public sealed class FormPackageException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    public sealed record PackageFormEntry(string Name, int Revision, string Sha256, int Size);

    public sealed record PackageManifest(int Format, string Project, int PackageId, string CreatedAt, IReadOnlyList<PackageFormEntry> Forms);

    public sealed record PackageContents(PackageManifest Manifest, string ManifestSha256, IReadOnlyDictionary<string, string> Files);

    /// <summary>
    /// Gói form đưa từ DEV sang môi trường khác: manifest.json + forms/{tên}.xml trong một file zip.
    /// Manifest ghi SHA-256 từng form; SHA-256 của chính manifest hiển thị ở portal để người mang gói đối chiếu ngoài băng tần.
    /// Đọc gói rất chặt (số mục, kích thước, tên mục) để gói hỏng hoặc cố ý độc hại không ghi ra ngoài thư mục đích hay làm tràn bộ nhớ
    /// </summary>
    public static partial class FormPackage
    {
        public const int CurrentFormat = 1;
        public const int MaxEntries = 500;
        public const long MaxTotalBytes = 32L * 1024 * 1024;
        public const string ManifestName = "manifest.json";

        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        [GeneratedRegex(@"^forms/([a-z]+[A-Z][A-Za-z0-9]{0,30})\.xml$")]
        private static partial Regex FileEntry();

        public static string FileOf(string formName) => $"forms/{formName}.xml";

        /// <summary>Tạo manifest JSON (thứ tự form cố định theo tên). Lưu nguyên văn chuỗi này để dựng lại gói y hệt về sau</summary>
        /// <param name="forms">Nội dung đã chuẩn hóa (FormDefinitionValidator.Normalize)</param>
        public static (string ManifestJson, string ManifestSha256) CreateManifest(string project, int packageId, DateTime createdAtUtc, IEnumerable<(string Name, int Revision, string Content)> forms)
        {
            List<(string Name, int Revision, string Content)> ordered = forms.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            if (ordered.Count == 0 || ordered.Count > MaxEntries)
            {
                throw new FormPackageException("INVALID_PACKAGE", "gói cần từ 1 đến 500 form");
            }
            if (ordered.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Count)
            {
                throw new FormPackageException("INVALID_PACKAGE", "có hai form khác nhau chỉ ở chữ hoa/thường (trên Windows là cùng một file)");
            }

            PackageManifest manifest = new(CurrentFormat, project, packageId, createdAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ordered.Select(f => new PackageFormEntry(f.Name, f.Revision, FormDefinitionValidator.Sha256(f.Content), Encoding.UTF8.GetByteCount(f.Content))).ToList());
            string json = JsonSerializer.Serialize(manifest, Json);
            return (json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
        }

        /// <summary>Đóng gói zip từ manifest đã tạo và nội dung form (khớp tên trong manifest)</summary>
        public static byte[] Assemble(string manifestJson, IEnumerable<(string Name, string Content)> forms)
        {
            using MemoryStream stream = new();
            using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, ManifestName, Encoding.UTF8.GetBytes(manifestJson));
                foreach (var (name, content) in forms.OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    Write(zip, FileOf(name), Encoding.UTF8.GetBytes(content));
                }
            }
            return stream.ToArray();
        }

        public static (byte[] Zip, string ManifestSha256) Build(string project, int packageId, DateTime createdAtUtc, IEnumerable<(string Name, int Revision, string Content)> forms)
        {
            List<(string Name, int Revision, string Content)> list = forms.ToList();
            var (json, sha) = CreateManifest(project, packageId, createdAtUtc, list);
            return (Assemble(json, list.Select(f => (f.Name, f.Content))), sha);
        }

        private static void Write(ZipArchive zip, string name, byte[] data)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream output = entry.Open();
            output.Write(data);
        }

        /// <summary>Đọc và kiểm toàn vẹn: tên mục, kích thước, SHA-256 từng form khớp manifest, không thừa không thiếu</summary>
        public static PackageContents Read(byte[] zipBytes)
        {
            try
            {
                using ZipArchive zip = new(new MemoryStream(zipBytes), ZipArchiveMode.Read);
                if (zip.Entries.Count is 0 or > MaxEntries + 1)
                {
                    throw new FormPackageException("INVALID_PACKAGE", "số mục trong gói không hợp lệ");
                }

                Dictionary<string, byte[]> raw = new(StringComparer.Ordinal);
                long total = 0, actual = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    bool ok = entry.FullName == ManifestName || FileEntry().IsMatch(entry.FullName);
                    if (!ok || entry.FullName.Contains("..") || raw.ContainsKey(entry.FullName))
                    {
                        throw new FormPackageException("INVALID_PACKAGE", $"mục không được phép trong gói: {Printable(entry.FullName)}");
                    }
                    total += entry.Length;
                    if (entry.Length > FormDefinitionValidator.MaxBytes || total > MaxTotalBytes)
                    {
                        throw new FormPackageException("INVALID_PACKAGE", "gói vượt giới hạn kích thước");
                    }
                    raw[entry.FullName] = ReadBounded(entry);
                    // Header zip có thể khai kích thước thấp hơn thật: cộng theo số byte ĐÃ ĐỌC để tổng bộ nhớ luôn bị chặn
                    actual += raw[entry.FullName].Length;
                    if (actual > MaxTotalBytes)
                    {
                        throw new FormPackageException("INVALID_PACKAGE", "gói vượt giới hạn kích thước");
                    }
                }

                if (!raw.TryGetValue(ManifestName, out byte[]? manifestBytes))
                {
                    throw new FormPackageException("INVALID_PACKAGE", "gói thiếu manifest.json");
                }

                PackageManifest manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<PackageManifest>(manifestBytes, Json) ?? throw new JsonException();
                }
                catch (JsonException)
                {
                    throw new FormPackageException("INVALID_PACKAGE", "manifest.json không hợp lệ");
                }
                if (manifest.Format != CurrentFormat || manifest.Forms is not { Count: > 0 })
                {
                    throw new FormPackageException("INVALID_PACKAGE", "định dạng gói không được hỗ trợ");
                }

                if (manifest.Forms.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Forms.Count)
                {
                    throw new FormPackageException("INVALID_PACKAGE", "manifest có hai form khác nhau chỉ ở chữ hoa/thường");
                }

                Dictionary<string, string> files = new(StringComparer.Ordinal);
                foreach (PackageFormEntry form in manifest.Forms)
                {
                    if (!FormDefinitionValidator.IsValidName(form.Name) || !raw.TryGetValue(FileOf(form.Name), out byte[]? data) || files.ContainsKey(form.Name))
                    {
                        throw new FormPackageException("INVALID_PACKAGE", $"manifest và nội dung gói không khớp ở form {Printable(form.Name)}");
                    }
                    string content = Encoding.UTF8.GetString(data);
                    if (data.Length != form.Size || !string.Equals(FormDefinitionValidator.Sha256(content), form.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new FormPackageException("HASH_MISMATCH", $"form {form.Name} bị thay đổi so với manifest");
                    }
                    files[form.Name] = content;
                }
                if (raw.Count != manifest.Forms.Count + 1)
                {
                    throw new FormPackageException("INVALID_PACKAGE", "gói có mục thừa không khai trong manifest");
                }

                return new PackageContents(manifest, Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), files);
            }
            catch (InvalidDataException)
            {
                throw new FormPackageException("INVALID_PACKAGE", "file không phải gói zip hợp lệ");
            }
        }

        private static byte[] ReadBounded(ZipArchiveEntry entry)
        {
            using Stream input = entry.Open();
            using MemoryStream output = new();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Không tin entry.Length trong header: đọc có giới hạn thật để chống zip bomb
                if (output.Length + read > FormDefinitionValidator.MaxBytes)
                {
                    throw new FormPackageException("INVALID_PACKAGE", "một mục trong gói vượt giới hạn kích thước");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }

        private static string Printable(string value) => new(value.Where(c => !char.IsControl(c)).Take(60).ToArray());
    }
}

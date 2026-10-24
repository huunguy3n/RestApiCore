using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Security.Licensing
{
    public sealed record IntegrityEntry([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("sha256")] string Sha256);

    public sealed record IntegrityDocument(
        [property: JsonPropertyName("format")] int Format,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("files")] IReadOnlyList<IntegrityEntry> Files);

    public sealed record IntegrityResult(string Code, IReadOnlyList<string> Problems)
    {
        public bool IsValid => Code == IntegrityManifest.Valid;
    }

    /// <summary>
    /// Manifest toàn vẹn của bản phát hành: integrity.json (SHA-256 mọi .dll/.exe) + integrity.sig (ký bằng khóa license ở WebPortal).
    /// Thay cho chữ ký Authenticode (không cần mua chứng chỉ). Sửa, thay hay xóa một file nhị phân thì kiểm thất bại
    /// </summary>
    public static class IntegrityManifest
    {
        public const string ManifestFile = "integrity.json";
        public const string SignatureFile = "integrity.sig";
        public const string Valid = "VALID";
        public const string Missing = "INTEGRITY_MISSING";
        public const string BadSignature = "INTEGRITY_BAD_SIGNATURE";
        public const string Changed = "INTEGRITY_CHANGED";

        private static readonly string[] Patterns = ["*.dll", "*.exe"];

        public static IntegrityDocument Create(string directory, string product, string version, DateTimeOffset now)
        {
            List<IntegrityEntry> files = [];
            foreach (string file in Patterns.SelectMany(p => Directory.EnumerateFiles(directory, p, SearchOption.AllDirectories)).Order(StringComparer.OrdinalIgnoreCase))
            {
                files.Add(new IntegrityEntry(Path.GetRelativePath(directory, file).Replace('\\', '/'), HashFile(file)));
            }
            return new IntegrityDocument(1, product, version, now, files);
        }

        public static byte[] Serialize(IntegrityDocument document) =>
            JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });

        /// <summary>Kiểm integrity.sig rồi so hash từng file; Problems liệt kê file sai/thiếu (tối đa 20)</summary>
        public static IntegrityResult Verify(string directory, LicenseVerifier verifier)
        {
            string manifestPath = Path.Combine(directory, ManifestFile), signaturePath = Path.Combine(directory, SignatureFile);
            if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
            {
                return new IntegrityResult(Missing, []);
            }

            byte[] manifest = File.ReadAllBytes(manifestPath);
            if (!verifier.VerifyDetached(manifest, File.ReadAllText(signaturePath), out string code))
            {
                return new IntegrityResult(BadSignature, [LicenseCodes.Describe(code)]);
            }

            IntegrityDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<IntegrityDocument>(manifest);
            }
            catch (JsonException)
            {
                return new IntegrityResult(BadSignature, ["integrity.json không đọc được"]);
            }

            List<string> problems = [];
            string root = Path.GetFullPath(directory);
            foreach (IntegrityEntry entry in document?.Files ?? [])
            {
                string full = Path.GetFullPath(Path.Combine(root, entry.Path));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{entry.Path}: đường dẫn ngoài thư mục");
                }
                else if (!File.Exists(full))
                {
                    problems.Add($"{entry.Path}: thiếu");
                }
                else if (!string.Equals(HashFile(full), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{entry.Path}: đã bị thay đổi");
                }
                if (problems.Count >= 20)
                {
                    break;
                }
            }
            return new IntegrityResult(problems.Count == 0 ? Valid : Changed, problems);
        }

        private static string HashFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
    }
}

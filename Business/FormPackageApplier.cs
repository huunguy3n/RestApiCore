using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Models;

namespace Business
{
    public sealed record ApplyFileResult(string Name, int Revision, string Sha256, string Outcome, string? Message);

    /// <param name="Applied">true = đã ghi file; false = chỉ kiểm thử (dry-run) hoặc dừng trước khi ghi</param>
    public sealed record ApplyResult(int PackageId, string Project, string ManifestSha256, string Environment, string Operator, string AppliedAt, bool Applied, bool Success, IReadOnlyList<ApplyFileResult> Files);

    /// <summary>
    /// Áp gói form lên thư mục App_Data/Form của một môi trường (chạy bằng CLI, không qua portal). Quy trình:
    /// đối chiếu SHA-256 manifest với giá trị người mang gói nhập (lấy từ portal) → đọc gói → kiểm LẠI từng form bằng chính bộ kiểm của portal
    /// và bắt trùng route → chỉ khi mọi form đạt mới ghi. Mỗi file ghi qua file tạm cùng thư mục rồi thay nguyên tử (bản cũ sao lưu vào _backup),
    /// nên FormReloader không bao giờ thấy file ghi dở. Lỗi giữa chừng được báo rõ file nào đã áp, file nào chưa
    /// </summary>
    public static class FormPackageApplier
    {
        public const string BackupFolder = "_backup";

        public static ApplyResult Run(byte[] zip, string expectedManifestSha256, string targetDirectory, bool apply, string environment, string operatorName, TimeProvider time)
        {
            PackageContents package = FormPackage.Read(zip);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(package.ManifestSha256), Encoding.ASCII.GetBytes((expectedManifestSha256 ?? "").Trim().ToLowerInvariant())))
            {
                throw new FormPackageException("MANIFEST_HASH_MISMATCH", "SHA-256 của manifest không khớp giá trị bạn nhập (lấy ở màn hình Package của portal): dừng, không ghi gì");
            }

            string appliedAt = time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ");
            string Op(string value) => new(value.Where(c => !char.IsControl(c)).Take(64).ToArray());
            ApplyResult Result(bool applied, bool success, List<ApplyFileResult> files) =>
                new(package.Manifest.PackageId, package.Manifest.Project, package.ManifestSha256, environment, Op(operatorName), appliedAt, applied, success, files);

            // 1. Kiểm lại từng form và trùng route; một form hỏng thì dừng cả gói
            List<ApplyFileResult> plan = [];
            List<ImportForm> parsed = [];
            bool valid = true;
            foreach (PackageFormEntry entry in package.Manifest.Forms)
            {
                FormValidation check = FormDefinitionValidator.Validate(entry.Name, package.Files[entry.Name]);
                if (!check.IsValid)
                {
                    valid = false;
                    plan.Add(new ApplyFileResult(entry.Name, entry.Revision, entry.Sha256, "FAILED", string.Join("; ", check.Errors.Select(e => e.Message))));
                    continue;
                }
                parsed.Add(check.Form!);
                plan.Add(new ApplyFileResult(entry.Name, entry.Revision, entry.Sha256, ClassifyAgainstTarget(targetDirectory, entry), null));
            }
            if (valid)
            {
                ImportFormCatalog catalog = ImportFormCatalog.Build(parsed.Concat(ExistingOthers(targetDirectory, package.Manifest.Forms.Select(f => f.Name))));
                foreach (var (file, error) in catalog.Errors)
                {
                    valid = false;
                    int index = plan.FindIndex(p => p.Name == file);
                    ApplyFileResult failure = new(file, index >= 0 ? plan[index].Revision : 0, index >= 0 ? plan[index].Sha256 : "", "FAILED", error);
                    if (index >= 0) { plan[index] = failure; } else { plan.Add(failure); }
                }
            }
            if (!valid)
            {
                return Result(false, false, plan);
            }
            if (!apply)
            {
                return Result(false, true, plan);
            }

            // 2. Ghi từng file qua file tạm rồi thay nguyên tử. Thư mục đích phải có sẵn: gõ nhầm đường dẫn không được âm thầm tạo thư mục mới
            if (!Directory.Exists(targetDirectory))
            {
                throw new FormPackageException("TARGET_NOT_FOUND", $"thư mục đích không tồn tại: {targetDirectory} (tạo trước hoặc sửa --target)");
            }
            string backupDirectory = Path.Combine(targetDirectory, BackupFolder, time.GetUtcNow().ToString("yyyyMMdd-HHmmss"));
            List<ApplyFileResult> done = [];
            foreach (ApplyFileResult item in plan)
            {
                if (item.Outcome == "SAME")
                {
                    done.Add(item);
                    continue;
                }
                try
                {
                    WriteAtomically(targetDirectory, backupDirectory, item.Name, package.Files[item.Name]);
                    done.Add(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    done.Add(item with { Outcome = "FAILED", Message = ex.GetType().Name });
                    done.AddRange(plan.Skip(done.Count).Select(p => p with { Outcome = "NOT_APPLIED", Message = "dừng do lỗi ở file trước" }));
                    return Result(true, false, done);
                }
            }
            return Result(true, true, done);
        }

        public static string ResultToJson(ApplyResult result) =>
            JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });

        private static string PathOf(string directory, string name) => Path.Combine(directory, name + ".xml");

        /// <summary>NEW (chưa có file) / CHANGED / SAME (nội dung giống hệt sau khi chuẩn hóa xuống dòng)</summary>
        private static string ClassifyAgainstTarget(string directory, PackageFormEntry entry)
        {
            string path = PathOf(directory, entry.Name);
            if (!File.Exists(path))
            {
                return "NEW";
            }
            string current = FormDefinitionValidator.Sha256(FormDefinitionValidator.Normalize(File.ReadAllText(path)));
            return string.Equals(current, entry.Sha256, StringComparison.OrdinalIgnoreCase) ? "SAME" : "CHANGED";
        }

        /// <summary>Form đang có ở đích mà gói không thay: dùng để bắt trùng route với form mới</summary>
        private static IEnumerable<ImportForm> ExistingOthers(string directory, IEnumerable<string> replaced)
        {
            if (!Directory.Exists(directory))
            {
                yield break;
            }
            HashSet<string> skip = new(replaced, StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(directory, "*.xml"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (skip.Contains(name))
                {
                    continue;
                }
                ImportForm? form = null;
                try
                {
                    form = FormDefinitionValidator.Validate(name, FormDefinitionValidator.Normalize(File.ReadAllText(file))).Form;
                }
                catch (IOException)
                {
                }
                if (form != null)
                {
                    yield return form;
                }
            }
        }

        private static void WriteAtomically(string directory, string backupDirectory, string name, string content)
        {
            string destination = PathOf(directory, name);
            // Đuôi .tmp không nằm trong bộ lọc *.xml của FormReloader nên bản ghi dở không bao giờ bị nạp
            string temporary = Path.Combine(directory, "." + name + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (File.Exists(destination))
                {
                    Directory.CreateDirectory(backupDirectory);
                    File.Copy(destination, PathOf(backupDirectory, name), overwrite: true);
                    File.Replace(temporary, destination, null);
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}

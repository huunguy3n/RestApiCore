using Business;

namespace Cli
{
    /// <summary>
    /// Áp gói form (xuất từ portal) lên thư mục App_Data\Form của môi trường. Mặc định chỉ kiểm thử (VERIFY), --apply mới ghi.
    /// Người mang gói PHẢI nhập --manifest-sha256 lấy từ màn hình Gói của portal: gói bị thay trên đường đi sẽ không khớp và bị từ chối
    /// </summary>
    public static class ApplyPackageCommand
    {
        public static int Run(string[] args)
        {
            string? package = null, target = null, sha = null, environment = null, operatorName = null, resultPath = null;
            bool apply = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--package": package = CliArgs.Next(args, ref i); break;
                    case "--target": target = CliArgs.Next(args, ref i); break;
                    case "--manifest-sha256": sha = CliArgs.Next(args, ref i); break;
                    case "--environment": environment = CliArgs.Next(args, ref i); break;
                    case "--operator": operatorName = CliArgs.Next(args, ref i); break;
                    case "--result": resultPath = CliArgs.Next(args, ref i); break;
                    case "--apply": apply = true; break;
                    default: throw new ArgumentException($"Tham số không hỗ trợ: {args[i]}");
                }
            }

            if (package == null || !File.Exists(package))
            {
                throw new ArgumentException("Thiếu --package hoặc file không tồn tại");
            }
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new ArgumentException("Thiếu --target <thư mục App_Data\\Form của môi trường>");
            }
            if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64)
            {
                throw new ArgumentException("Thiếu --manifest-sha256 (64 ký tự hex, lấy ở màn hình Gói của portal)");
            }
            if (environment is not ("DEV" or "PROD"))
            {
                throw new ArgumentException("--environment phải là DEV hoặc PROD");
            }
            if (string.IsNullOrWhiteSpace(operatorName))
            {
                throw new ArgumentException("Thiếu --operator <tên người thực hiện>");
            }
            if (new FileInfo(package).Length > FormPackage.MaxTotalBytes)
            {
                throw new ArgumentException("File gói quá lớn");
            }

            ApplyResult result;
            try
            {
                result = FormPackageApplier.Run(File.ReadAllBytes(package), sha, Path.GetFullPath(target), apply, environment, operatorName, TimeProvider.System);
            }
            catch (FormPackageException ex)
            {
                Console.Error.WriteLine($"{ex.Code}: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"Gói #{result.PackageId} của dự án {result.Project} ({result.Files.Count} form) → {Path.GetFullPath(target)} [{environment}]");
            foreach (ApplyFileResult file in result.Files)
            {
                Console.WriteLine($"  {file.Outcome,-11} {file.Name} (revision {file.Revision}){(file.Message != null ? " - " + file.Message : "")}");
            }

            string output = resultPath ?? $"{(apply ? "apply" : "verify")}-result-{result.PackageId}.json";
            File.WriteAllText(output, FormPackageApplier.ResultToJson(result));
            bool changed = result.Files.Any(f => f.Outcome is "NEW" or "CHANGED");
            Console.WriteLine(result.Applied
                ? (!result.Success ? "Áp gói DỪNG GIỮA CHỪNG: xem file nào đã áp ở trên, khôi phục từ _backup nếu cần."
                    : changed ? "Đã áp gói. Form bị thay được sao lưu trong thư mục _backup. FormReloader tự nạp lại."
                    : "Không có gì thay đổi: mọi form ở đích đã giống gói.")
                : (result.Success ? "Kiểm thử đạt (chưa ghi). Thêm --apply để áp." : "Gói KHÔNG đạt kiểm tra, chưa ghi gì."));
            Console.WriteLine($"Kết quả: {Path.GetFullPath(output)} (nhập vào portal ở màn hình Đồng bộ)");
            return result.Success ? 0 : 1;
        }
    }
}

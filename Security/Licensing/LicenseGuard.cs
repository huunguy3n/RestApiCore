using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Security.Licensing
{
    /// <summary>
    /// Kiểm license của một folder restApi: đọc restApi.lic cạnh appsettings, so MAC máy hiện tại, và (license có hạn) chặn chỉnh lùi
    /// đồng hồ bằng mốc giờ lớn nhất đã thấy, lưu mã hóa DPAPI theo máy ở ProgramData. Dùng chung cho PortalApp và WebApi
    /// </summary>
    public sealed class LicenseGuard(LicenseVerifier verifier, Func<IReadOnlyCollection<string>> macs, TimeProvider time, string? clockFile = null)
    {
        public static readonly TimeSpan ClockTolerance = TimeSpan.FromDays(1);

        public static string DefaultClockFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FastRestApi", "license-clock.bin");

        public static LicenseGuard CreateDefault(TimeProvider? time = null) =>
            new(new LicenseVerifier(TrustedLicenseKeys.Load()), () => MachineFingerprint.GetMacs(), time ?? TimeProvider.System, DefaultClockFile);

        public LicenseCheck Check(string apiFolder, string projectCode)
        {
            string path = Path.Combine(apiFolder, LicenseRules.FileName);
            string? text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            DateTimeOffset now = time.GetUtcNow();
            LicenseCheck check = verifier.Verify(text, projectCode, macs(), now);
            if (check.IsValid && check.Payload!.ExpiresAt != null && clockFile != null && OperatingSystem.IsWindows())
            {
                return ClockMovedBack(now) ? new LicenseCheck(LicenseCodes.ClockRollback, check.Payload) : check;
            }
            return check;
        }

        [SupportedOSPlatform("windows")]
        private bool ClockMovedBack(DateTimeOffset now)
        {
            DateTimeOffset? seen = ReadSeen();
            if (seen != null && now + ClockTolerance < seen.Value)
            {
                return true;
            }
            if (seen == null || now > seen.Value)
            {
                WriteSeen(now);
            }
            return false;
        }

        [SupportedOSPlatform("windows")]
        private DateTimeOffset? ReadSeen()
        {
            try
            {
                if (!File.Exists(clockFile))
                {
                    return null;
                }
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(clockFile!), Entropy, DataProtectionScope.LocalMachine);
                return DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(plain));
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        [SupportedOSPlatform("windows")]
        private void WriteSeen(DateTimeOffset now)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(clockFile!)!);
                File.WriteAllBytes(clockFile!, ProtectedData.Protect(BitConverter.GetBytes(now.ToUnixTimeSeconds()), Entropy, DataProtectionScope.LocalMachine));
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                // Không ghi được mốc giờ (quyền thư mục): bỏ qua, license vẫn kiểm chữ ký + MAC + hạn
            }
        }

        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("FastRestApi.LicenseClock.v1");
    }
}

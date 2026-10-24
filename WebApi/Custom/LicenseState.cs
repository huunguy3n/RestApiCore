using Microsoft.Extensions.Diagnostics.HealthChecks;
using Models;
using Security.Licensing;

namespace Web.Custom
{
    /// <summary>
    /// License (restApi.lic) và manifest toàn vẹn (integrity.json/.sig) của folder WebApi, kiểm một lần lúc khởi động.
    /// Chỉ kiểm khi appSettings:environment = PROD; DEV/UAT nội bộ không cần license.
    /// Mặc định chỉ cảnh báo (log + /health/ready); appSettings:licenseEnforce = true thì /api trả 503 LICENSE_INVALID
    /// </summary>
    public sealed class LicenseState
    {
        private LicenseState(bool checkedLicense, LicenseCheck license, IntegrityResult integrity)
        {
            Checked = checkedLicense;
            License = license;
            Integrity = integrity;
        }

        public bool Checked { get; }
        public LicenseCheck License { get; }
        public IntegrityResult Integrity { get; }

        /// <summary>Không kiểm (DEV) hoặc license hợp lệ và nhị phân không bị sửa. Thiếu manifest toàn vẹn chỉ cảnh báo</summary>
        public bool IsValid => !Checked || License.IsValid && Integrity.Code is IntegrityManifest.Valid or IntegrityManifest.Missing;

        public static LicenseState NotChecked() =>
            new(false, new LicenseCheck(LicenseCodes.Valid), new IntegrityResult(IntegrityManifest.Valid, []));

        public static LicenseState Evaluate(AppConfig appConfig, string contentRoot, LicenseGuard guard, LicenseVerifier verifier)
        {
            if (!appConfig.IsProduction)
            {
                return NotChecked();
            }
            LicenseCheck license = appConfig.projectCode.Length == 0 ? new LicenseCheck(LicenseCodes.WrongProject) : guard.Check(contentRoot, appConfig.projectCode);
            return new LicenseState(true, license, IntegrityManifest.Verify(AppContext.BaseDirectory, verifier));
        }

        public string Describe() => !Checked
            ? "Không kiểm (environment khác PROD)"
            : $"{LicenseCodes.Describe(License.Code)} Toàn vẹn: {Integrity.Code}{(Integrity.Problems.Count > 0 ? " (" + string.Join("; ", Integrity.Problems.Take(3)) + ")" : "")}";
    }

    public sealed class LicenseHealthCheck(LicenseState state) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(state.IsValid ? HealthCheckResult.Healthy(state.Describe()) : HealthCheckResult.Degraded(state.Describe()));
    }
}

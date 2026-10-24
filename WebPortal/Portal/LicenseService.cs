using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Security.Licensing;

namespace WebPortal.Portal
{
    public sealed record LicenseKeyStatus(bool Configured, string? Kid, string? TrustedKeyEntry, bool Trusted, string Message);

    public sealed record LicenseFile(string FileName, string Content, LicensePayload Payload);

    /// <summary>
    /// Khóa riêng ký license (ECDSA P-256). Nạp từ PFX (Portal:License:KeyPath + KeyPassword) hoặc Certificate Store (KeyThumbprint).
    /// Không có trong repo, DB hay log. Public key tương ứng phải được nhúng vào Security/Licensing/trusted-keys.json của bản phát hành
    /// </summary>
    public sealed class LicenseKeyProvider(LicenseKeySettings settings, ILogger<LicenseKeyProvider> logger)
    {
        private LicenseSigner? _signer;
        private readonly object _gate = new();

        public LicenseSigner? Signer
        {
            get
            {
                lock (_gate)
                {
                    return _signer ??= Load();
                }
            }
        }

        public LicenseKeyStatus Status()
        {
            LicenseSigner? signer = Signer;
            if (signer == null)
            {
                return new LicenseKeyStatus(false, null, null, false,
                    settings.KeyPath.Length > 0 && !File.Exists(settings.KeyPath) ? "Chưa có file khóa tại Portal:License:KeyPath: tạo cặp khóa mới." : "Chưa cấu hình khóa ký license.");
            }
            bool trusted = TrustedLicenseKeys.Load().ContainsKey(signer.Kid);
            return new LicenseKeyStatus(true, signer.Kid, TrustedLicenseKeys.EntryJson(signer.PublicKey), trusted,
                trusted ? "Khóa đang dùng đã có trong bản phát hành." : "Khóa CHƯA có trong trusted-keys.json của bản phát hành: PortalApp/WebApi sẽ từ chối license ký bằng khóa này.");
        }

        /// <summary>Tạo cặp khóa mới vào PFX tại KeyPath (chỉ khi chưa có file). Trả false nếu chưa khai KeyPath/KeyPassword hoặc file đã tồn tại</summary>
        public bool Generate()
        {
            if (settings.KeyPath.Length == 0 || settings.KeyPassword.Length < 12 || File.Exists(settings.KeyPath))
            {
                return false;
            }
            using ECDsa key = LicenseSigner.GenerateKey();
            CertificateRequest request = new("CN=Fast RestApi License", key, HashAlgorithmName.SHA256);
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.KeyPath))!);
            File.WriteAllBytes(settings.KeyPath, certificate.Export(X509ContentType.Pfx, settings.KeyPassword));
            lock (_gate)
            {
                _signer = null;
            }
            return true;
        }

        private LicenseSigner? Load()
        {
            try
            {
                X509Certificate2? certificate = null;
                if (settings.KeyThumbprint.Length > 0)
                {
                    using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadOnly);
                    certificate = store.Certificates.Find(X509FindType.FindByThumbprint, settings.KeyThumbprint, false).FirstOrDefault();
                }
                else if (settings.KeyPath.Length > 0 && File.Exists(settings.KeyPath))
                {
                    certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.KeyPath, settings.KeyPassword, X509KeyStorageFlags.EphemeralKeySet);
                }
                ECDsa? key = certificate?.GetECDsaPrivateKey();
                return key == null ? null : new LicenseSigner(key);
            }
            catch (CryptographicException ex)
            {
                logger.LogError("Không nạp được khóa ký license: {Message}", ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// Cấp license cho PortalApp/WebApi tại PROD theo file yêu cầu (mã dự án + MAC). SA cấp cho mọi dự án, Admin cấp cho dự án của mình.
    /// SA ký manifest toàn vẹn cho bản phát hành
    /// </summary>
    public sealed class LicenseService(IPortalStore store, LicenseKeyProvider keys, TimeProvider time)
    {
        public async Task<OpResult> IssueAsync(PortalIdentity actor, PortalProject project, string? requestText, int? validDays, string? ip, CancellationToken cancellationToken = default)
        {
            if (!PortalGuard.CanAdmin(actor, project.Id))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            LicenseSigner? signer = keys.Signer;
            if (signer == null)
            {
                return OpResult.Fail("LICENSE_KEY_MISSING");
            }
            LicenseRequest? request = LicenseRequest.Parse(requestText);
            if (request == null)
            {
                return OpResult.Fail("INVALID_LICENSE_REQUEST");
            }
            if (!string.Equals(request.ProjectCode, project.MaDa, StringComparison.OrdinalIgnoreCase))
            {
                await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "license.issue", request.ProjectCode, "REJECTED", "yêu cầu của dự án khác", ip), cancellationToken);
                return OpResult.Fail("WRONG_PROJECT");
            }
            if (validDays is < 0 or > 3650)
            {
                return OpResult.Fail("INVALID_INPUT");
            }

            DateTimeOffset now = time.GetUtcNow();
            var (payload, content) = signer.Issue(request with { ProjectCode = project.MaDa }, now, validDays is > 0 ? TimeSpan.FromDays(validDays.Value) : null);
            await store.AddLicenseAsync(new LicenseRecord(payload.LicenseId, project.Id, payload.Kid, string.Join(",", payload.Macs), payload.MachineName,
                now.LocalDateTime, actor.Login, payload.ExpiresAt?.LocalDateTime, null, null), LicenseRequest.Sha256(requestText!), actor.AccountId, cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, project.Id, "license.issue", payload.LicenseId.ToString(), "SUCCESS",
                $"máy {payload.MachineName}, {payload.Macs.Count} MAC, {(payload.ExpiresAt == null ? "không hạn" : "hạn " + payload.ExpiresAt.Value.LocalDateTime.ToString("yyyy-MM-dd"))}", ip), cancellationToken);
            return new OpResult("SUCCESS", new LicenseFile(LicenseRules.FileName, content, payload));
        }

        public async Task<OpResult> ListAsync(PortalIdentity actor, int? projectId, CancellationToken cancellationToken = default)
        {
            if (projectId == null ? !actor.IsSuperAdmin : !PortalGuard.CanAdmin(actor, projectId.Value))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            return new OpResult("SUCCESS", await store.ListLicensesAsync(projectId, cancellationToken));
        }

        /// <summary>
        /// Đánh dấu thu hồi trên portal (không cấp lại). License đã giao không bị vô hiệu từ xa vì PROD không gọi được portal: dùng license có hạn nếu cần cắt
        /// </summary>
        public async Task<OpResult> RevokeAsync(PortalIdentity actor, Guid licenseId, int? projectId, string? reason, string? ip, CancellationToken cancellationToken = default)
        {
            if (projectId == null ? !actor.IsSuperAdmin : !PortalGuard.CanAdmin(actor, projectId.Value))
            {
                return OpResult.Fail("FORBIDDEN");
            }
            bool done = await store.RevokeLicenseAsync(licenseId, actor.IsSuperAdmin ? null : projectId, string.IsNullOrWhiteSpace(reason) ? "thu hồi" : reason, cancellationToken);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, projectId, "license.revoke", licenseId.ToString(), done ? "SUCCESS" : "REJECTED", reason, ip), cancellationToken);
            return done ? OpResult.Ok : OpResult.Fail("NOT_FOUND");
        }

        /// <summary>SA ký integrity.json của một bản phát hành (tạo khi build Release) → integrity.sig đặt cạnh nó</summary>
        public async Task<OpResult> SignIntegrityAsync(PortalIdentity actor, byte[] manifest, string? ip, CancellationToken cancellationToken = default)
        {
            if (!actor.IsSuperAdmin)
            {
                return OpResult.Fail("FORBIDDEN");
            }
            LicenseSigner? signer = keys.Signer;
            if (signer == null)
            {
                return OpResult.Fail("LICENSE_KEY_MISSING");
            }
            IntegrityDocument? document;
            try
            {
                document = System.Text.Json.JsonSerializer.Deserialize<IntegrityDocument>(manifest);
            }
            catch (System.Text.Json.JsonException)
            {
                document = null;
            }
            if (document is not { Format: 1, Files.Count: > 0 })
            {
                return OpResult.Fail("INVALID_MANIFEST");
            }
            string signature = signer.SignDetached(manifest);
            await store.WriteAuditAsync(PortalGuard.Audit(actor, null, "integrity.sign", $"{document.Product} {document.Version}", "SUCCESS",
                $"{document.Files.Count} file, sha {Convert.ToHexStringLower(SHA256.HashData(manifest))[..12]}", ip), cancellationToken);
            return new OpResult("SUCCESS", Encoding.UTF8.GetBytes(signature));
        }
    }
}

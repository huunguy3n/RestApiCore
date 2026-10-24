using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Security.Licensing
{
    /// <summary>
    /// Ký license và manifest toàn vẹn bằng ECDSA P-256 + SHA-256 (chữ ký IEEE P1363). Khóa riêng chỉ nằm ở WebPortal
    /// </summary>
    public sealed class LicenseSigner
    {
        private readonly ECDsa _key;

        public LicenseSigner(ECDsa key)
        {
            if (key.KeySize != 256)
            {
                throw new ArgumentException("Khóa license phải là ECDSA P-256");
            }
            _key = key;
            PublicKey = key.ExportSubjectPublicKeyInfo();
            Kid = KidOf(PublicKey);
        }

        /// <summary>SubjectPublicKeyInfo (DER) để nhúng vào bản phát hành PortalApp / WebApi</summary>
        public byte[] PublicKey { get; }

        public string Kid { get; }

        /// <summary>kid = 16 ký tự hex đầu của SHA-256(SPKI)</summary>
        public static string KidOf(byte[] subjectPublicKeyInfo) => Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo))[..16];

        /// <summary>Cặp khóa mới (WebPortal tạo một lần, lưu PFX ngoài repo)</summary>
        public static ECDsa GenerateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

        /// <param name="validFor">Null = không hạn</param>
        public (LicensePayload Payload, string FileText) Issue(LicenseRequest request, DateTimeOffset now, TimeSpan? validFor)
        {
            LicensePayload payload = new(LicensePayload.CurrentFormat, Guid.NewGuid(), request.ProjectCode, request.Macs, request.MachineName ?? "",
                now, validFor == null ? null : now + validFor.Value, Kid);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, LicenseJson.Options);
            LicenseEnvelope envelope = new(Kid, Convert.ToBase64String(json), Convert.ToBase64String(SignBytes(json)));
            return (payload, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
        }

        public byte[] SignBytes(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        /// <summary>Ký nội dung bất kỳ (manifest toàn vẹn), trả JSON { kid, signature }</summary>
        public string SignDetached(byte[] data) =>
            JsonSerializer.Serialize(new DetachedSignature(Kid, Convert.ToBase64String(SignBytes(data))), new JsonSerializerOptions { WriteIndented = true });
    }

    public sealed record DetachedSignature(
        [property: System.Text.Json.Serialization.JsonPropertyName("kid")] string Kid,
        [property: System.Text.Json.Serialization.JsonPropertyName("signature")] string Signature);

    /// <summary>
    /// Kiểm license bằng các public key tin cậy (nhúng trong bản phát hành). Không cần mạng, không cần bí mật nào
    /// </summary>
    public sealed class LicenseVerifier(IReadOnlyDictionary<string, byte[]> trustedKeys)
    {
        public IReadOnlyCollection<string> TrustedKids => trustedKeys.Keys.ToList();

        public LicenseCheck Verify(string? fileText, string projectCode, IReadOnlyCollection<string> machineMacs, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(fileText))
            {
                return new LicenseCheck(LicenseCodes.Missing);
            }

            LicenseEnvelope? envelope;
            byte[] payloadBytes, signature;
            try
            {
                envelope = JsonSerializer.Deserialize<LicenseEnvelope>(fileText);
                if (envelope == null || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature))
                {
                    return new LicenseCheck(LicenseCodes.Malformed);
                }
                payloadBytes = Convert.FromBase64String(envelope.Payload);
                signature = Convert.FromBase64String(envelope.Signature);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                return new LicenseCheck(LicenseCodes.Malformed);
            }

            if (!VerifyBytes(envelope.Kid, payloadBytes, signature, out string code))
            {
                return new LicenseCheck(code);
            }

            LicensePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes, LicenseJson.Options);
            }
            catch (JsonException)
            {
                return new LicenseCheck(LicenseCodes.Malformed);
            }
            // kid trong phần được ký phải trùng kid ngoài phong bì: không đổi phong bì sang khóa khác được
            if (payload is not { Format: LicensePayload.CurrentFormat } || payload.Kid != envelope.Kid || payload.Macs == null)
            {
                return new LicenseCheck(LicenseCodes.Malformed);
            }
            if (!string.Equals(payload.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase))
            {
                return new LicenseCheck(LicenseCodes.WrongProject, payload);
            }
            if (!payload.Macs.Any(mac => machineMacs.Contains(mac, StringComparer.OrdinalIgnoreCase)))
            {
                return new LicenseCheck(LicenseCodes.WrongMachine, payload);
            }
            if (payload.ExpiresAt != null && payload.ExpiresAt.Value <= now)
            {
                return new LicenseCheck(LicenseCodes.Expired, payload);
            }
            return new LicenseCheck(LicenseCodes.Valid, payload);
        }

        /// <summary>Kiểm chữ ký tách rời ({ kid, signature }) trên dữ liệu (manifest toàn vẹn)</summary>
        public bool VerifyDetached(byte[] data, string? signatureJson, out string code)
        {
            try
            {
                DetachedSignature? detached = string.IsNullOrWhiteSpace(signatureJson) ? null : JsonSerializer.Deserialize<DetachedSignature>(signatureJson);
                if (detached == null || string.IsNullOrEmpty(detached.Signature))
                {
                    code = LicenseCodes.Malformed;
                    return false;
                }
                return VerifyBytes(detached.Kid, data, Convert.FromBase64String(detached.Signature), out code);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                code = LicenseCodes.Malformed;
                return false;
            }
        }

        private bool VerifyBytes(string? kid, byte[] data, byte[] signature, out string code)
        {
            if (string.IsNullOrEmpty(kid) || !trustedKeys.TryGetValue(kid, out byte[]? spki))
            {
                code = LicenseCodes.UnknownKey;
                return false;
            }
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            bool ok;
            try
            {
                ok = key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException)
            {
                ok = false;
            }
            code = ok ? LicenseCodes.Valid : LicenseCodes.BadSignature;
            return ok;
        }
    }

    /// <summary>
    /// Public key tin cậy nhúng trong Security.dll (Licensing/trusted-keys.json, embedded resource). Thêm khóa mới = sửa file đó rồi build bản phát hành.
    /// Bản DEBUG nhận thêm file chỉ định bằng biến môi trường FAST_LICENSE_TRUSTED_KEYS (máy dev/test); bản Release bỏ qua biến này
    /// </summary>
    public static class TrustedLicenseKeys
    {
        public const string DebugOverrideVariable = "FAST_LICENSE_TRUSTED_KEYS";

        /// <summary>Public: kiểu dùng với System.Text.Json phải giữ tên tham số constructor khi làm rối (Obfuscar chỉ giữ API public)</summary>
        public sealed record Entry(
            [property: System.Text.Json.Serialization.JsonPropertyName("kid")] string Kid,
            [property: System.Text.Json.Serialization.JsonPropertyName("spki")] string Spki);

        public static IReadOnlyDictionary<string, byte[]> Load()
        {
            Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);
            using (Stream? stream = typeof(TrustedLicenseKeys).Assembly.GetManifestResourceStream("Security.Licensing.trusted-keys.json"))
            {
                if (stream != null)
                {
                    using StreamReader reader = new(stream, Encoding.UTF8);
                    AddFrom(reader.ReadToEnd(), keys);
                }
            }
#if DEBUG
            string? overridePath = Environment.GetEnvironmentVariable(DebugOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                AddFrom(File.ReadAllText(overridePath), keys);
            }
#endif
            return keys;
        }

        /// <summary>Đọc mảng [{ kid, spki }]; kid phải khớp SHA-256 của spki (gõ nhầm thì bỏ qua)</summary>
        public static void AddFrom(string json, IDictionary<string, byte[]> keys)
        {
            foreach (Entry entry in JsonSerializer.Deserialize<List<Entry>>(json) ?? [])
            {
                try
                {
                    byte[] spki = Convert.FromBase64String(entry.Spki);
                    if (LicenseSigner.KidOf(spki) == entry.Kid)
                    {
                        keys[entry.Kid] = spki;
                    }
                }
                catch (FormatException)
                {
                }
            }
        }

        /// <summary>Dòng JSON để thêm vào trusted-keys.json</summary>
        public static string EntryJson(byte[] spki) => JsonSerializer.Serialize(new Entry(LicenseSigner.KidOf(spki), Convert.ToBase64String(spki)));
    }
}

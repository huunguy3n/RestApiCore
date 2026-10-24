using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Security.Licensing
{
    /// <summary>
    /// Yêu cầu cấp license do PortalApp sinh tại máy PROD. Không chứa bí mật: mang đi bằng file rồi tải lên WebPortal
    /// </summary>
    public sealed record LicenseRequest(
        [property: JsonPropertyName("format")] int Format,
        [property: JsonPropertyName("projectCode")] string ProjectCode,
        [property: JsonPropertyName("macs")] IReadOnlyList<string> Macs,
        [property: JsonPropertyName("machineName")] string MachineName,
        [property: JsonPropertyName("appVersion")] string AppVersion,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt)
    {
        public const int CurrentFormat = 1;
        public const int MaxBytes = 16 * 1024;

        public static LicenseRequest Create(string projectCode, IReadOnlyList<string> macs, string machineName, string appVersion, DateTimeOffset now) =>
            new(CurrentFormat, projectCode, macs, machineName, appVersion, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), now);

        public string ToJson() => JsonSerializer.Serialize(this, LicenseJson.Options);

        /// <summary>Null nếu không đúng định dạng (sai JSON, thiếu mã dự án, MAC sai dạng, quá cỡ)</summary>
        public static LicenseRequest? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > MaxBytes)
            {
                return null;
            }
            try
            {
                LicenseRequest? request = JsonSerializer.Deserialize<LicenseRequest>(text, LicenseJson.Options);
                return request is { Format: CurrentFormat }
                    && LicenseRules.IsValidProjectCode(request.ProjectCode)
                    && request.Macs is { Count: > 0 and <= 16 }
                    && request.Macs.All(MachineFingerprint.IsValidMac)
                    && (request.MachineName ?? "").Length <= 64
                    ? request
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Nội dung được ký của license</summary>
    public sealed record LicensePayload(
        [property: JsonPropertyName("format")] int Format,
        [property: JsonPropertyName("licenseId")] Guid LicenseId,
        [property: JsonPropertyName("projectCode")] string ProjectCode,
        [property: JsonPropertyName("macs")] IReadOnlyList<string> Macs,
        [property: JsonPropertyName("machineName")] string MachineName,
        [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("kid")] string Kid)
    {
        public const int CurrentFormat = 1;
    }

    /// <summary>File restApi.lic: payload (JSON UTF-8, base64) + chữ ký ECDSA P-256 SHA-256 (IEEE P1363, base64)</summary>
    public sealed record LicenseEnvelope(
        [property: JsonPropertyName("kid")] string Kid,
        [property: JsonPropertyName("payload")] string Payload,
        [property: JsonPropertyName("signature")] string Signature);

    public static class LicenseCodes
    {
        public const string Valid = "VALID";
        public const string Missing = "LICENSE_MISSING";
        public const string Malformed = "LICENSE_MALFORMED";
        public const string UnknownKey = "LICENSE_UNKNOWN_KEY";
        public const string BadSignature = "LICENSE_BAD_SIGNATURE";
        public const string WrongProject = "LICENSE_WRONG_PROJECT";
        public const string WrongMachine = "LICENSE_WRONG_MACHINE";
        public const string Expired = "LICENSE_EXPIRED";
        public const string ClockRollback = "LICENSE_CLOCK_ROLLBACK";

        public static string Describe(string code) => code switch
        {
            Valid => "License hợp lệ.",
            Missing => "Chưa có file license (restApi.lic) trong folder restApi.",
            Malformed => "File license hỏng hoặc không đúng định dạng.",
            UnknownKey => "License ký bằng khóa không được tin cậy (sai bản phát hành hoặc khóa đã xoay).",
            BadSignature => "Chữ ký license không đúng: file đã bị sửa.",
            WrongProject => "License cấp cho dự án khác.",
            WrongMachine => "License cấp cho máy khác (không có card mạng nào khớp MAC trong license).",
            Expired => "License đã hết hạn.",
            ClockRollback => "Đồng hồ máy bị chỉnh lùi so với lần chạy trước.",
            _ => code
        };
    }

    public sealed record LicenseCheck(string Code, LicensePayload? Payload = null)
    {
        public bool IsValid => Code == LicenseCodes.Valid;
    }

    public static class LicenseRules
    {
        public const string FileName = "restApi.lic";

        /// <summary>Mã dự án: như đoạn URL /{ma_da} của portal</summary>
        public static bool IsValidProjectCode(string? code) =>
            !string.IsNullOrEmpty(code) && code.Length <= 32 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }

    internal static class LicenseJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false
        };
    }
}

namespace Security
{
    public enum RekeyStatus
    {
        /// <summary>Mật khẩu trống, bỏ qua</summary>
        Empty,
        /// <summary>Giải được bằng khóa cũ, đã có bản mã theo khóa mới</summary>
        Rekeyed,
        /// <summary>Đã mã hóa bằng khóa mới từ lần chạy trước, bỏ qua</summary>
        AlreadyNew,
        /// <summary>Giải được bằng cả hai khóa, không xác định được, phải dừng</summary>
        Ambiguous,
        /// <summary>Không giải được bằng khóa nào, phải dừng</summary>
        Unreadable
    }

    public record RekeyResult(RekeyStatus Status, string? NewCipher);

    /// <summary>
    /// Mã hóa lại restApiUser.password từ khóa cũ (secretKey) sang passwordKey, giữ định dạng của EncryptionHelper
    /// </summary>
    public static class PasswordRekey
    {
        public static RekeyResult Rekey(string? cipher, string oldKey, string newKey)
        {
            if (string.IsNullOrWhiteSpace(cipher))
            {
                return new RekeyResult(RekeyStatus.Empty, null);
            }

            string? plainOld = TryDecrypt(cipher, oldKey);
            string? plainNew = TryDecrypt(cipher, newKey);

            if (plainOld != null && plainNew != null)
            {
                return new RekeyResult(RekeyStatus.Ambiguous, null);
            }
            if (plainNew != null)
            {
                return new RekeyResult(RekeyStatus.AlreadyNew, null);
            }
            if (plainOld == null)
            {
                return new RekeyResult(RekeyStatus.Unreadable, null);
            }

            string newCipher = EncryptionHelper.EncryptString(plainOld, newKey);
            if (EncryptionHelper.DecryptString(newCipher, newKey) != plainOld)
            {
                return new RekeyResult(RekeyStatus.Unreadable, null);
            }

            return new RekeyResult(RekeyStatus.Rekeyed, newCipher);
        }

        /// <summary>
        /// Giải mã, coi là sai khóa nếu lỗi padding hoặc ra ký tự không hợp lệ (sai khóa đôi khi vẫn qua padding)
        /// </summary>
        private static string? TryDecrypt(string cipher, string key)
        {
            try
            {
                string plain = EncryptionHelper.DecryptString(cipher, key);
                if (plain.Length == 0 || plain.Any(c => c == '�' || char.IsControl(c)))
                {
                    return null;
                }
                return plain;
            }
            catch (Exception ex) when (ex is FormatException || ex is System.Security.Cryptography.CryptographicException)
            {
                return null;
            }
        }
    }
}

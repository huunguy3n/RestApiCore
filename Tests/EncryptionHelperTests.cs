using Security;

namespace Tests
{
    /// <summary>
    /// Chốt hành vi hiện tại của EncryptionHelper trước khi đổi chữ ký đăng nhập sang HMAC (pha 2)
    /// </summary>
    public class EncryptionHelperTests
    {
        private const string Key32 = "0123456789abcdef0123456789abcdef";

        [Fact]
        public void EncryptDecrypt_KhoiPhucDungChuoiGoc()
        {
            string cipher = EncryptionHelper.EncryptString("mat-khau-api", Key32);

            Assert.NotEqual("mat-khau-api", cipher);
            Assert.Equal("mat-khau-api", EncryptionHelper.DecryptString(cipher, Key32));
        }
    }
}

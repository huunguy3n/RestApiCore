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
        public void ComputeMD5Hash_TraVeHexHoa()
        {
            Assert.Equal("900150983CD24FB0D6963F7D28E17F72", EncryptionHelper.ComputeMD5Hash("abc"));
        }

        [Fact]
        public void EncryptDecrypt_KhoiPhucDungChuoiGoc()
        {
            string cipher = EncryptionHelper.EncryptString("mat-khau-api", Key32);

            Assert.NotEqual("mat-khau-api", cipher);
            Assert.Equal("mat-khau-api", EncryptionHelper.DecryptString(cipher, Key32));
        }

        [Fact]
        public void GenerateAuthKey_LayKyTuTheoViTriTrenMd5()
        {
            string password = EncryptionHelper.EncryptString("pw", Key32);
            string md5 = EncryptionHelper.ComputeMD5Hash("pw" + "user01" + 1792574400);

            string key = EncryptionHelper.GenerateAuthKey(" user01 ", 1792574400, password, Key32, ["2", " 9", "32"]);

            Assert.Equal($"{md5[1]}{md5[8]}{md5[31]}", key);
        }
    }
}

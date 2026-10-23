using Security;

namespace Tests
{
    public class PasswordRekeyTests
    {
        private const string OldKey = "0123456789abcdef0123456789abcdef";
        private const string NewKey = "fedcba9876543210fedcba9876543210";

        [Fact]
        public void Rekey_KhoaCu_RaBanMaGiaiDuocBangKhoaMoi()
        {
            string cipher = EncryptionHelper.EncryptString("mat-khau-api", OldKey);

            RekeyResult result = PasswordRekey.Rekey(cipher, OldKey, NewKey);

            Assert.Equal(RekeyStatus.Rekeyed, result.Status);
            Assert.Equal("mat-khau-api", EncryptionHelper.DecryptString(result.NewCipher!, NewKey));
        }

        [Fact]
        public void Rekey_DaTheoKhoaMoi_BoQua()
        {
            string cipher = EncryptionHelper.EncryptString("mat-khau-api", NewKey);

            Assert.Equal(RekeyStatus.AlreadyNew, PasswordRekey.Rekey(cipher, OldKey, NewKey).Status);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void Rekey_Trong_BoQua(string? cipher)
        {
            Assert.Equal(RekeyStatus.Empty, PasswordRekey.Rekey(cipher, OldKey, NewKey).Status);
        }

        [Fact]
        public void Rekey_KhoaKhac_KhongGiaiDuoc()
        {
            string cipher = EncryptionHelper.EncryptString("mat-khau-api", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz");

            Assert.Equal(RekeyStatus.Unreadable, PasswordRekey.Rekey(cipher, OldKey, NewKey).Status);
            Assert.Equal(RekeyStatus.Unreadable, PasswordRekey.Rekey("khong-phai-base64", OldKey, NewKey).Status);
        }

        [Fact]
        public void Rekey_ChayLaiTrenKetQua_KhongDoiNua()
        {
            string once = PasswordRekey.Rekey(EncryptionHelper.EncryptString("pw", OldKey), OldKey, NewKey).NewCipher!;

            Assert.Equal(RekeyStatus.AlreadyNew, PasswordRekey.Rekey(once, OldKey, NewKey).Status);
        }
    }
}

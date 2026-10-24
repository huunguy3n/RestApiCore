using Models;

namespace Tests
{
    public class UserRightsTests
    {
        [Theory]
        [InlineData("192.168.1.10", "192.168.1.10", true)]
        [InlineData("192.168.1.10", "192.168.1.255", true)]
        [InlineData("192.168.2.10", "192.168.1.255", false)]
        [InlineData("10.20.30.40", "10.255.30.40", true)]
        [InlineData("1.2.3.4", "255", true)]
        [InlineData("192.168.1.100", "192.168.1.10", false)]
        [InlineData("192x168.1.10", "192.168.1.10", false)]
        public void MatchIp_NhuLikeCuaSql(string ip, string pattern, bool expected)
        {
            Assert.Equal(expected, UserRights.MatchIp(ip, pattern));
        }

        [Fact]
        public void IsIpAllowed_KhongKhaiIp_ChoMoiIp_TruBiChan()
        {
            UserRights rights = new() { BlackListIps = ["10.0.0.255"] };

            Assert.True(rights.IsIpAllowed("192.168.1.10"));
            Assert.False(rights.IsIpAllowed("10.0.0.7"));
        }

        [Fact]
        public void IsIpAllowed_CoKhaiIp_ChiChoIpDaKhai()
        {
            UserRights rights = new() { AllowIps = ["192.168.1.255"] };

            Assert.True(rights.IsIpAllowed("192.168.1.10"));
            Assert.False(rights.IsIpAllowed("192.168.2.10"));
        }

        [Fact]
        public void CanAccessForm_KhongKhaiThiMoiForm_CoKhaiThiChiKiemForm_AdminLuonDuoc()
        {
            UserRights rights = new();
            Assert.True(rights.CanAccessForm("getCustomer"));

            rights.AllowForms.Add("getCustomer");
            Assert.True(rights.CanAccessForm("getcustomer"));
            Assert.False(rights.CanAccessForm("setCustomer"));

            rights.ErpAdmin = true;
            Assert.True(rights.CanAccessForm("setCustomer"));
        }

        [Theory]
        [InlineData('1', 1, '1', true)]
        [InlineData('0', 1, '1', false)]
        [InlineData('1', null, '0', false)]
        [InlineData('1', 1, '0', false)]
        public void IsActive_CanCaHaiStatus(char status, int? userId, char erpStatus, bool expected)
        {
            UserRights rights = new() { Status = status, UserId = userId, ErpStatus = erpStatus };
            Assert.Equal(expected, rights.IsActive);
        }
    }
}

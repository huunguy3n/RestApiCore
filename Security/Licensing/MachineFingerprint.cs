using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Security.Licensing
{
    /// <summary>
    /// MAC các card mạng vật lý của máy: bỏ loopback, tunnel, card ảo (Hyper-V, VMware, VirtualBox, VPN, TAP, Bluetooth).
    /// Lấy cả card đang tắt để rút cáp không làm license mất hiệu lực. Định dạng "AA-BB-CC-DD-EE-FF", sắp xếp, không trùng
    /// </summary>
    public static partial class MachineFingerprint
    {
        private static readonly string[] VirtualMarkers =
        [
            "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "tap ", "tunnel", "bluetooth", "pseudo", "loopback", "wan miniport", "teredo", "npcap"
        ];

        public static IReadOnlyList<string> GetMacs()
        {
            List<string> macs = [];
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                {
                    continue;
                }
                string text = (nic.Description + " " + nic.Name).ToLowerInvariant();
                if (VirtualMarkers.Any(text.Contains))
                {
                    continue;
                }
                string? mac = Normalize(nic.GetPhysicalAddress().GetAddressBytes());
                if (mac != null)
                {
                    macs.Add(mac);
                }
            }
            return macs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        }

        /// <summary>6 byte, khác toàn 0; null nếu không phải MAC dùng được</summary>
        public static string? Normalize(byte[] bytes) =>
            bytes.Length == 6 && bytes.Any(b => b != 0) ? string.Join("-", bytes.Select(b => b.ToString("X2"))) : null;

        public static bool IsValidMac(string? mac) => mac != null && MacPattern().IsMatch(mac);

        [GeneratedRegex("^[0-9A-F]{2}(-[0-9A-F]{2}){5}$")]
        private static partial Regex MacPattern();
    }
}

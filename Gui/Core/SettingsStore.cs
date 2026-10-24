using System.Text.Json;

namespace Gui.Core
{
    /// <summary>
    /// Nhớ giá trị đã nhập giữa các lần mở app (đường dẫn, tên user…). Mật khẩu không bao giờ được lưu:
    /// caller tự bỏ qua các tham số kiểu Password.
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly string FilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fast.Api.Gui", "settings.json");

        private readonly Dictionary<string, string> values;

        public SettingsStore() => values = Read();

        public string Get(string key, string fallback = "") => values.TryGetValue(key, out string? v) ? v : fallback;

        public void Set(string key, string value) => values[key] = value;

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(values));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static Dictionary<string, string> Read()
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []; }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
        }
    }
}

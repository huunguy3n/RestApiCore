using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Models.V3;

namespace Business.V3
{
    public enum RouteMatch
    {
        Found,
        NotFound,
        MethodNotAllowed
    }

    /// <summary>
    /// Bảng route v3 dựng lúc khởi động từ App_Data/Form/*.xml (KE_HOACH_V3 4.1). File lỗi hoặc trùng route bị bỏ qua và ghi log,
    /// form khác vẫn chạy. Bất biến sau khi dựng
    /// </summary>
    public sealed partial class ImportFormCatalog
    {
        private readonly Dictionary<string, ImportForm> _routes;

        public IReadOnlyCollection<ImportForm> Forms => _routes.Values;

        /// <summary>Lỗi nạp theo tên file, để /health hoặc CLI báo lại</summary>
        public IReadOnlyDictionary<string, string> Errors { get; }

        [GeneratedRegex("^/api/v3(/[a-z0-9-]+){1,2}$")]
        private static partial Regex RoutePattern();

        private ImportFormCatalog(Dictionary<string, ImportForm> routes, Dictionary<string, string> errors)
        {
            _routes = routes;
            Errors = errors;
        }

        public static ImportFormCatalog Build(IEnumerable<ImportForm> forms, IDictionary<string, string>? loadErrors = null)
        {
            Dictionary<string, string> errors = new(loadErrors ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            Dictionary<string, ImportForm> routes = new(StringComparer.Ordinal);
            HashSet<string> duplicated = new(StringComparer.Ordinal);

            foreach (ImportForm form in forms)
            {
                if (duplicated.Contains(form.Route))
                {
                    errors[form.Name] = $"trùng route {form.Route}";
                }
                else if (routes.Remove(form.Route, out ImportForm? existing))
                {
                    // Trùng route: bỏ cả hai, không đoán file nào đúng
                    duplicated.Add(form.Route);
                    errors[existing.Name] = $"trùng route {form.Route} với {form.Name}";
                    errors[form.Name] = $"trùng route {form.Route} với {existing.Name}";
                }
                else
                {
                    routes[form.Route] = form;
                }
            }

            return new ImportFormCatalog(routes, errors);
        }

        /// <summary>
        /// Nạp mọi *.xml trong thư mục (không đệ quy: thư mục con như Include không phải form)
        /// </summary>
        public static ImportFormCatalog LoadDirectory(string directory, ILogger? logger = null)
        {
            List<ImportForm> forms = [];
            Dictionary<string, string> errors = new(StringComparer.Ordinal);

            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        ImportForm form = ImportFormLoader.Load(file);
                        forms.Add(form);
                        foreach (string warning in form.Warnings)
                        {
                            logger?.LogWarning("Form {Form}: {Warning}", form.Name, warning);
                        }
                    }
                    catch (ImportFormException ex)
                    {
                        errors[ex.FileName] = ex.Message;
                    }
                }
            }

            ImportFormCatalog catalog = Build(forms, errors);
            foreach (var (file, error) in catalog.Errors)
            {
                logger?.LogError("Bỏ qua form {Form}: {Error}", file, error);
            }
            logger?.LogInformation("Nạp {Count} form v3 từ {Directory}", catalog._routes.Count, directory);
            return catalog;
        }

        /// <summary>
        /// Phân giải route: không phân biệt hoa thường, chỉ nhận [a-z0-9-]. Nghiệp vụ chỉ dùng POST, method khác → 405
        /// </summary>
        public RouteMatch Resolve(string method, string path, out ImportForm? form)
        {
            form = null;
            string normalized = path.TrimEnd('/').ToLowerInvariant();
            if (!RoutePattern().IsMatch(normalized) || !_routes.TryGetValue(normalized, out ImportForm? found) || !found.Enable)
            {
                return RouteMatch.NotFound;
            }
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return RouteMatch.MethodNotAllowed;
            }
            form = found;
            return RouteMatch.Found;
        }
    }
}

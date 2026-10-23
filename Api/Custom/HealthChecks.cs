using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Models;
using Newtonsoft.Json;

namespace Web.Custom
{
    /// <summary>
    /// /health/ready: mở được kết nối SQL và chạy SELECT 1
    /// </summary>
    public class SqlHealthCheck(AppConfig appConfig) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await using var connection = new SqlConnection(appConfig.appConnectionString);
                await connection.OpenAsync(cancellationToken);

                await using var command = new SqlCommand("SELECT 1", connection) { CommandTimeout = 5 };
                await command.ExecuteScalarAsync(cancellationToken);

                return HealthCheckResult.Healthy();
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Không kết nối được SQL Server", ex);
            }
        }
    }

    /// <summary>
    /// /health/ready: thư mục log tồn tại và ghi được
    /// </summary>
    public class LogDirectoryHealthCheck(AppConfig appConfig) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                string probe = Path.Combine(appConfig.logDirectory, $".health-{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(probe, "", cancellationToken);
                File.Delete(probe);

                return HealthCheckResult.Healthy();
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Không ghi được thư mục log", ex);
            }
        }
    }

    public static class HealthCheckResponse
    {
        /// <summary>
        /// Trả trạng thái từng mục, chi tiết lỗi chỉ hiện khi isDevState
        /// </summary>
        public static Func<HttpContext, HealthReport, Task> Writer(AppConfig appConfig) => async (context, report) =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";

            var json = JsonConvert.SerializeObject(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.ToDictionary(e => e.Key, e => new
                {
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    error = appConfig.isDevState ? e.Value.Exception?.Message : null
                })
            });
            await context.Response.WriteAsync(json);
        };
    }
}

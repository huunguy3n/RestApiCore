using Microsoft.Data.SqlClient;
using Provider;
using System.Data;

namespace DataAccess
{
    /// <summary>
    /// restApi_InsertLogError cho lỗi chưa xử lý. Log request v3 ghi qua SqlRequestLogSink (restApiLogReq)
    /// </summary>
    public class LogRepository(SqlDatabase database)
    {
        /// <summary>
        /// Mọi giá trị đi qua tham số có kiểu và độ dài khớp cột #logError (sửa lỗi 1.4 #3: ghép chuỗi)
        /// </summary>
        public async Task WriteAsync(string path, string errorMessage, string stackTrace, CancellationToken cancellationToken = default)
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);

            await database.ExecuteAsync(connection, "create table #logError(timeStamp datetime, path nvarchar(128), message nvarchar(4000), stacktrace nvarchar(4000))", cancellationToken);
            await database.ExecuteAsync(connection, "insert #logError values(getdate(), @path, @errorMessage, @stackTrace)", cancellationToken,
                Parameter("@path", SqlDbType.NVarChar, 128, path),
                Parameter("@errorMessage", SqlDbType.NVarChar, 4000, errorMessage),
                Parameter("@stackTrace", SqlDbType.NVarChar, 4000, stackTrace)
                );
            await database.ExecuteAsync(connection, "exec restApi_InsertLogError '#logError'", cancellationToken);
        }

        /// <summary>
        /// Tham số chuỗi cắt theo độ dài cột để không lỗi "String or binary data would be truncated"
        /// </summary>
        private static SqlParameter Parameter(string name, SqlDbType type, int size, string? value)
        {
            object dbValue = value == null ? DBNull.Value : (value.Length > size ? value.Substring(0, size) : value);
            return new SqlParameter(name, type, size) { Value = dbValue };
        }
    }
}

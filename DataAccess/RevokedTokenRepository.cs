using Business;
using Microsoft.Data.SqlClient;
using Provider;
using System.Data;

namespace DataAccess
{
    /// <summary>
    /// restApiRevokedToken: JWT đã thu hồi theo jti. Dòng hết hạn không còn tác dụng (token tự hết hiệu lực) nên dọn được
    /// </summary>
    public class RevokedTokenRepository(SqlDatabase database) : IRevokedTokenRepository
    {
        public async Task<bool> IsRevokedAsync(string jti, CancellationToken cancellationToken = default)
        {
            var ds = await database.RetrieveAsync(
                "SELECT 1 FROM restApiRevokedToken WHERE jti = @jti", cancellationToken,
                new SqlParameter("@jti", SqlDbType.VarChar, 36) { Value = jti });

            return ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0;
        }

        /// <summary>
        /// Ghi jti (chạy lại không lỗi), đồng thời dọn các dòng đã hết hạn
        /// </summary>
        public async Task RevokeAsync(string jti, int uId, DateTime expiresAt, CancellationToken cancellationToken = default)
        {
            await database.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM restApiRevokedToken WHERE jti = @jti)
                    INSERT INTO restApiRevokedToken (jti, u_id, expires_at) VALUES (@jti, @u_id, @expires_at)
                DELETE FROM restApiRevokedToken WHERE expires_at < GETDATE()
                """, cancellationToken,
                new SqlParameter("@jti", SqlDbType.VarChar, 36) { Value = jti },
                new SqlParameter("@u_id", SqlDbType.Int) { Value = uId },
                new SqlParameter("@expires_at", SqlDbType.DateTime) { Value = expiresAt });
        }
    }
}

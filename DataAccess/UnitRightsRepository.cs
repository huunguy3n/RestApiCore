using System.Data;
using Business;
using Microsoft.Data.SqlClient;
using Provider;

namespace DataAccess
{
    /// <summary>
    /// restApi_CheckUnitRights: unitCode đi bằng tham số, không ghép vào SQL. Cache do UnitRightsChecker lo
    /// </summary>
    public class UnitRightsRepository(SqlDatabase database) : IUnitRightsRepository
    {
        public async Task<string> CheckAsync(int userId, string unitCode, string action, CancellationToken cancellationToken = default)
        {
            var ds = await database.RetrieveAsync("exec restApi_CheckUnitRights @UserID, @UnitCode, @Action", cancellationToken,
                new SqlParameter("@UserID", SqlDbType.Int) { Value = userId },
                new SqlParameter("@UnitCode", SqlDbType.VarChar, 33) { Value = unitCode },
                new SqlParameter("@Action", SqlDbType.VarChar, 5) { Value = action });
            return ds.Tables[0].Rows[0]["code"].ToString()!.Trim();
        }
    }
}

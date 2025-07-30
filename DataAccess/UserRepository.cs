using Microsoft.Data.SqlClient;
using Models;
using System.Data;

namespace DataAccess
{
    public class UserRepository
    {
        private readonly AppConfig _appConfig;
        public UserRepository(AppConfig appConfig)
        {
            _appConfig = appConfig;
            Provider.Provider.CommandTimeout = _appConfig.commandTimeout;
        }
        /// <summary>
        /// Lấy user theo id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public User GetUser(int id)
        {
            var ds = Provider.Provider.Retrieve(_appConfig.appConnectionString, "select * from restApiUser where id = @id and status = '1'", 
                new SqlParameter { ParameterName = "@id", Value = id });

            return SetUser(ds);
        }

        /// <summary>
        /// Lấy user theo name
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public User GetUser(string name)
        {
            var ds = Provider.Provider.Retrieve(_appConfig.appConnectionString, "select * from restApiUser where [name] = @name and status = '1'", 
                new SqlParameter { ParameterName = "@name", Value = name });

            return SetUser(ds);
        }

        private User SetUser(DataSet dataSet)
        {
            if (dataSet != null)
            {
                if (dataSet.Tables[0].Rows.Count > 0)
                {
                    var row = dataSet.Tables[0].Rows[0];
                    return new User
                    {
                        Id = Convert.ToInt32(row["id"]),
                        Name = row["name"].ToString(),
                        NameErp = row["name_erp"].ToString(),
                        Password = row["password"].ToString(),
                        IsAdmin = Convert.ToBoolean(row["admin"]),
                        IsGroup = Convert.ToBoolean(row["group"]),
                        AccessYN = Convert.ToBoolean(row["access_yn"]),
                        NewYN = Convert.ToBoolean(row["new_yn"]),
                        EditYN = Convert.ToBoolean(row["edit_yn"]),
                        DeleteYN = Convert.ToBoolean(row["delete_yn"]),
                        Status = Convert.ToChar(row["status"]),
                        UserId0 = row["user_id0"] as int?,
                        UserId2 = row["user_id2"] as int?,
                        DateTime0 = row["datetime0"] as DateTime?,
                        DateTime2 = row["datetime2"] as DateTime?
                    };
                }
            }
            return null;
        }

        /// <summary>
        /// Lấy danh sách user
        /// </summary>
        /// <returns></returns>
        public List<User> GetUserList()
        {
            var ds = Provider.Provider.Retrieve(_appConfig.appConnectionString, "select * from restApiUser where status = '1'");

            if (ds != null)
            {
                if (ds.Tables[0].Rows.Count > 0)
                {
                    List<User> users = new List<User>();
                    foreach (DataRow row in ds.Tables[0].Rows)
                    {
                        users.Add(new User
                        {
                            Id = Convert.ToInt32(row["id"]),
                            Name = row["name"].ToString(),
                            NameErp = row["name_erp"].ToString(),
                            Password = row["password"].ToString(),
                            IsAdmin = Convert.ToBoolean(row["admin"]),
                            IsGroup = Convert.ToBoolean(row["group"]),
                            AccessYN = Convert.ToBoolean(row["access_yn"]),
                            NewYN = Convert.ToBoolean(row["new_yn"]),
                            EditYN = Convert.ToBoolean(row["edit_yn"]),
                            DeleteYN = Convert.ToBoolean(row["delete_yn"]),
                            Status = Convert.ToChar(row["status"]),
                            UserId0 = row["user_id0"] as int?,
                            UserId2 = row["user_id2"] as int?,
                            DateTime0 = row["datetime0"] as DateTime?,
                            DateTime2 = row["datetime2"] as DateTime?
                        });
                    }

                    return users;
                }
            }

            return null;
        }

        // Add new User
        public void AddUser(User user)
        {

        }

        // Update User details
        public void UpdateUser(User user)
        {

        }

        // Delete User
        public void DeleteUser(int userId)
        {

        }
    }
}

using Microsoft.Data.SqlClient;
using Models;
using Models.Response;
using Newtonsoft.Json;
using System.Data;

namespace DataAccess
{
    public class CacheRepository
    {
        /// <summary>
        /// 0 - FormName, 1 - CacheID, 2 - PageIndex
        /// </summary>
        public const string FILECACHE_PARTERN = $"Cache/{{0}}/{{1}}/{{2}}.json";
        public const string PATHCACHE_PARTERN = $"Cache/{{0}}/{{1}}";

        public string fileCachePartern => FILECACHE_PARTERN;

        private readonly AppConfig _appConfig;

        public CacheRepository(AppConfig appConfig)
        {
            _appConfig = appConfig;
        }

        public void WriteResponseCache(string formName, string cacheID, int pageIndex, ResponseData responseData)
        {
            string cacheFilePath = Path.Combine(_appConfig.pathDirectory, string.Format(FILECACHE_PARTERN, formName, cacheID, pageIndex));

            if(Directory.Exists(Path.GetDirectoryName(cacheFilePath)) == false)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath));
            }

            using (StreamWriter sw = new StreamWriter(cacheFilePath))
            {
                sw.Write(JsonConvert.SerializeObject(responseData));
            }

            if(pageIndex == 0) InsertResponseCache(formName, cacheID);
        }

        public void InsertResponseCache(string formName, string cacheID)
        {
            //Lưu cache vào SQL
            using (var connection = Provider.Provider.OpenConn(_appConfig.appConnectionString))
            {
                Provider.Provider.Execute(connection, "insert restApiCache values(getdate(), @form, @cacheID)",
                    new SqlParameter { ParameterName = "@form", Value = formName },
                    new SqlParameter { ParameterName = "@cacheID", Value = cacheID }
                );
                Provider.Provider.CloseConn(connection);
            }
        }

        public ResponseData ReadResponseData(string formName, string cacheID, int pageIndex)
        {
            string cacheFilePath = Path.Combine(_appConfig.pathDirectory, string.Format(FILECACHE_PARTERN, formName, cacheID, pageIndex));

            if (File.Exists(cacheFilePath) == false)
            {
                return null;
            }

            using (StreamReader sr = new StreamReader(cacheFilePath))
            {
                return JsonConvert.DeserializeObject<ResponseData>(sr.ReadToEnd());
            }
        }
        public void RemoveCache()
        {
            DataTable cacheData = new DataTable();
            using (var connection = Provider.Provider.OpenConn(_appConfig.appConnectionString))
            {
                cacheData = Provider.Provider.Retrieve(connection, "exec restApi_RemoveCache").Tables[0];
                Provider.Provider.CloseConn(connection);
            }

            foreach (DataRow row in cacheData.Rows)
            {
                string formName = row["form"].ToString();
                string cacheID = row["cacheID"].ToString();

                //Xóa toàn bộ cache trong thư mục
                string cacheDirectory = Path.Combine(_appConfig.pathDirectory, string.Format(PATHCACHE_PARTERN, formName, cacheID));
                if (Directory.Exists(cacheDirectory))
                {
                    Directory.Delete(cacheDirectory, true);
                }
                
            }
        }
    }
}

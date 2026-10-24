using Microsoft.Data.SqlClient;
using Models;
using Models.Response;
using Newtonsoft.Json;
using Provider;

namespace DataAccess
{
    /// <summary>
    /// Engine v2 cũ: trang phân trang lưu file Cache/{form}/{cacheID}/{page}.json và dòng restApiCache.
    /// v3 phân trang qua CursorStore (HybridCache), không dùng lớp này
    /// </summary>
    public class CacheRepository(AppConfig appConfig, SqlDatabase database)
    {
        /// <summary>
        /// 0 - FormName, 1 - CacheID, 2 - PageIndex
        /// </summary>
        public const string FILECACHE_PARTERN = $"Cache/{{0}}/{{1}}/{{2}}.json";

        public void WriteResponseCache(string formName, string cacheID, int pageIndex, ResponseData responseData)
        {
            string cacheFilePath = Path.Combine(appConfig.pathDirectory, string.Format(FILECACHE_PARTERN, formName, cacheID, pageIndex));

            Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);

            using (StreamWriter sw = new StreamWriter(cacheFilePath))
            {
                sw.Write(JsonConvert.SerializeObject(responseData));
            }

            if (pageIndex == 0) InsertResponseCache(formName, cacheID);
        }

        private void InsertResponseCache(string formName, string cacheID)
        {
            //Lưu cache vào SQL
            using var connection = database.OpenConnection();
            database.Execute(connection, "insert restApiCache values(getdate(), @form, @cacheID)",
                new SqlParameter { ParameterName = "@form", Value = formName },
                new SqlParameter { ParameterName = "@cacheID", Value = cacheID }
            );
        }

        public ResponseData? ReadResponseData(string formName, string cacheID, int pageIndex)
        {
            string cacheFilePath = Path.Combine(appConfig.pathDirectory, string.Format(FILECACHE_PARTERN, formName, cacheID, pageIndex));

            if (File.Exists(cacheFilePath) == false)
            {
                return null;
            }

            using (StreamReader sr = new StreamReader(cacheFilePath))
            {
                return JsonConvert.DeserializeObject<ResponseData>(sr.ReadToEnd());
            }
        }
    }
}

using Models;
using Models.Interface;
using Models.Options;
using Models.Response;
using Newtonsoft.Json;
using System.Data;
using System.Net;
using System.Text;

namespace Business
{
    public class ResponseManager
    {
        private readonly AppConfig _appConfig;
        private readonly Options _options;

        public delegate void ResponseEventHandler(IRequest request, IResponse response);
        public event ResponseEventHandler OnResponseGenerated;

        public ResponseManager(AppConfig appConfig, Options options)
        {
            _appConfig = appConfig;
            _options = options;
        }

        #region RESPONSE
        #region GENERAL
        public IResponse GetResponse(string code)
        {
            Message message = _options.Messages.Get(code);
            IResponse response = new Response();

            if (message != null)
            {
                response.code = message.code;
                response.success = message.success;
                response.message = message.message;
                response.httpStatusCode = message.httpStatusCode;
            }
            else
            {
                response.code = "EXCEPTION";
                response.success = false;
                response.message = "Exception";
                response.httpStatusCode = 400;
            }

            return response;
        }

        public IResponse GetResponse(string code, string message)
        {
            IResponse response = GetResponse(code);
            if (!string.IsNullOrEmpty(message)) response.message = message;

            return response;
        }
        #endregion

        #region AUTH
        public IResponse GetResponse(string code, string tokenType, int expiryToken, string accessToken)
        {
            ResponseAuth response = new ResponseAuth(GetResponse(code))
            {
                token_type = tokenType,
                expires_in = expiryToken,
                access_token = accessToken
            };

            return response;
        }
        #endregion

        #region QUERY
        /// <summary>
        /// Bảng 0 trả về code, message
        /// Bảng 1 trả về data
        /// </summary>
        /// <param name="dataSet"></param>
        /// <returns></returns>
        public IResponse GetResponse(string formName, DataSet dataSet)
        {
            string code = "SUCCESS", message = "";
            DataSet data = null;

            //Mặc định là SUCCESS
            try
            {
                if (dataSet != null)
                {
                    if (dataSet.Tables.Count > 0)
                    {
                        if (dataSet.Tables[0].Rows.Count > 0)
                        {
                            DataRow dr = dataSet.Tables[0].Rows[0];

                            if (dr.Table.Columns.Contains("code"))
                            {
                                code = dr["code"].ToString();
                            }
                            if (dr.Table.Columns.Contains("message"))
                            {
                                message = dr["message"].ToString();
                            }
                        }

                        //Lấy dữ liệu từ bảng thứ 2 trở đi, cột tableName được gán làm tên bảng
                        if (dataSet.Tables.Count > 1)
                        {
                            for (int i = 1; i < dataSet.Tables.Count; i++)
                            {
                                string tableName = $"d{i}";
                                DataTable dt = dataSet.Tables[i].Copy();

                                if (dataSet.Tables[i].Columns.Contains("tableName"))
                                {
                                    tableName = dataSet.Tables[i].Rows[0]["tableName"].ToString();
                                    dt.Columns.Remove(tableName);
                                    dt.TableName = tableName;
                                }
                                data.Tables.Add(dt);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return GetResponse("EXCEPTION", _appConfig.isDevState ? ex.Message : "");
            }

            if (data != null)
            {
                return GetResponse(formName, code, message, data);
            }

            return GetResponse(code, message);
        }

        /// <summary>
        /// Phan trang dữ liệu và trả về response
        /// </summary>
        /// <param name="code"></param>
        /// <param name="message"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public IResponse GetResponse(string formName, string code, string message, DataSet data)
        {
            string cacheID = GenerateCacheID();

            ResponseQuery response = new ResponseQuery(GetResponse(code, message))
            {
                cacheID = cacheID,
            };


            foreach (DataTable dt in data.Tables)
            {
                DataSet dataPagination = PaginationDataResponse(dt);
                int totalRowCount = dt.Rows.Count, pageSize = (_appConfig.maxRowInResponse > dt.Rows.Count ? dt.Rows.Count : _appConfig.maxRowInResponse), pageCount = dataPagination.Tables.Count;

                response.data.Add(dt.TableName, new ResponseData
                {
                    totalRowCount = totalRowCount,
                    pageIndex = 0, // Mặc định là 0, có thể thay đổi nếu cần
                    pageSize = pageSize,
                    pageCount = pageCount,
                    data = dataPagination.Tables[0] // Chỉ lấy trang đầu tiên
                });

                //Cache sẽ lưu dưới 2 dạng
                // + File json: Dữ liệu cắt sẽ lưu xuống file và tự động xóa sau khi hết hạn
                //      * Path: Cache/{Form}/{yyyyMMdd}/{cacheID}/{tableName}/{pageIndex}.json
                // + MemoryCache: Lưu metadata
                //      * Name: {Form}_{cacheID}_{tableName}_{pageIndex}


                ObjectCache cache = MemoryCache.Default;
                string cacheKey = $"{formName}_{cacheID}_{{0}}_{dt.TableName}"; // {0} will be replaced by pageIndex

                for (int i = 0; i < dataPagination.Tables.Count; i++)
                {
                    ResponseData responseDataPagination = new ResponseData
                    {
                        totalRowCount = totalRowCount,
                        pageIndex = i,
                        pageSize = pageSize,
                        pageCount = pageCount,
                        data = dataPagination.Tables[i]
                    };

                    string cacheFilePath = $"Cache/{formName}/{DateTime.Now:yyyyMMdd}/{cacheID}/{i}/{dt.TableName}.json";

                    // Lưu dữ liệu vào file
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath));
                    File.WriteAllText(cacheFilePath, JsonConvert.SerializeObject(responseDataPagination));

                    // Lưu metadata vào MemoryCache
                    cache.Add(string.Format(cacheKey, i), responseDataPagination, DateTimeOffset.Now.AddMinutes(_appConfig.cacheDurationInMinutes));
                }
            }

            return response;
        }

        /// <summary>
        /// Lấy response được lưu trong cache
        /// </summary>
        /// <param name="formName"></param>
        /// <param name="code"></param>
        /// <param name="message"></param>
        /// <param name="cacheID"></param>
        /// <returns></returns>
        public IResponse GetResponse(string formName, string cacheID, int pageIndex)
        {
            // Nếu tìm thấy cache, trả về dữ liệu từ cache
            ResponseQuery response = new ResponseQuery(GetResponse("SUCCESS"))
            {
                cacheID = cacheID
            };

            //Kiểm tra cacheID có tồn tại trong MemoryCache không
            ObjectCache cache = MemoryCache.Default;
            string cacheKey = $"{formName}_{cacheID}_{pageIndex}_*"; // Sử dụng wildcard để tìm tất cả các cache liên quan đến cacheID

            var cacheItems = cache.Where(item => item.Key.StartsWith(cacheKey)).ToList();
            if (cacheItems.Count > 0)
            {
                foreach (var item in cacheItems)
                {
                    string[] parts = item.Key.Split('_');
                    string tableName = parts[3]; // Lấy tên bảng từ cache key
                    response.data.Add(tableName, (ResponseData)item.Value);
                }
                return response;
            }

            //Nếu không tìm thấy cache, tìm trong file
            string cacheFilePath = $"Cache/{formName}/{DateTime.Now:yyyyMMdd}/{cacheID}/{pageIndex}";

            if (Directory.Exists(cacheFilePath))
            {
                //Lấy tất cả file có trong folder
                var files = Directory.GetFiles(cacheFilePath, "*.json");

                //Mỗi file tương ứng với một bảng dữ liệu
                foreach (var file in files)
                {
                    string tableName = Path.GetFileNameWithoutExtension(file);

                    // Đọc dữ liệu từ file
                    string jsonData = File.ReadAllText(file);
                    ResponseData responseData = JsonConvert.DeserializeObject<ResponseData>(jsonData);

                    // Thêm dữ liệu vào response
                    response.data.Add(tableName, responseData);
                }

                // Nếu có dữ liệu, trả về response
                return response;
            }

            return GetResponse("INVALID_CACHEID");
        }

        /// <summary>
        /// Tách nhỏ data trả về
        /// </summary>
        /// <param name="dataTable"></param>
        /// <returns></returns>
        private DataSet PaginationDataResponse(DataTable dataTable)
        {
            DataSet dataPagination = new DataSet();

            if (dataTable.Rows.Count > _appConfig.maxRowInResponse)
            {
                int pageCount = dataTable.Rows.Count / _appConfig.maxRowInResponse;

                for (int i = 0; i <= pageCount; i++)
                {
                    DataTable dt = dataTable.AsEnumerable().Take(_appConfig.maxRowInResponse).CopyToDataTable();

                    dataPagination.Tables.Add(dt.Copy());

                    dataTable.AsEnumerable()
                               .Take(_appConfig.maxRowInResponse)
                               .ToList()
                               .ForEach(r => r.Delete());

                    dataTable.AcceptChanges();
                }
            }
            else
            {
                dataPagination.Tables.Add(dataTable.Copy());
            }

            return dataPagination;
        }
        private string GenerateCacheID()
        {
            // Generate a random GUID and convert it to a string
            return Guid.NewGuid().ToString();
        }
        #endregion
        #endregion

        #region HTTP RESPONSE
        public HttpResponseMessage GetResponseMessage(IRequest request, IResponse response)
        {
            OnResponseGenerated?.Invoke(request, response);

            return new HttpResponseMessage((HttpStatusCode)response.httpStatusCode)
            {
                Content = new StringContent(JsonConvert.SerializeObject(response), Encoding.UTF8, "application/json")
            };
        }

        public HttpResponseMessage GetResponseMessage(IRequest request, string code)
        {
            return GetResponseMessage(request, GetResponse(code));
        }
        #endregion
    }
}

using DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Interface;
using Models.Options;
using Models.Response;
using System.Data;

namespace Business
{
    public class ResponseManager(AppConfig appConfig, Options options, IMemoryCache cache)
    {
        /// <summary>
        /// 0 - FormName, 1 - CacheID, 2 - PageIndex
        /// </summary>
        public const string MEMORYCACHE_PARTERN = $"{{0}}_{{1}}_{{2}}";

        public string memoryCachePartern { get => MEMORYCACHE_PARTERN; }

        #region RESPONSE
        #region GENERAL
        public IResponse GetResponse(string code)
        {
            Message message = options.Messages.Get(code);
            Response response = new();

            if (message != null)
            {
                response.code = message.code;
                response.success = message.success;
                response.message = message.message;
                response.statusCode = message.httpStatusCode;
            }
            else
            {
                response.code = "EXCEPTION";
                response.success = false;
                response.message = "Exception";
                response.statusCode = 400;
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
            ResponseAuth response = new(GetResponse(code))
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
        /// <param name="formName"></param>
        /// <param name="dataSet"></param>
        /// <returns></returns>
        public IResponse GetResponse(string formName, DataSet dataSet)
        {
            string code = "SUCCESS", message = "", field = "", condition = "";
            DataTable data = new();

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
                            List<string> colResponse = new() { "code", "message", "field", "condition" };

                            //Lấy dữ liệu bảng đầu tiên
                            foreach (string col in colResponse)
                            {
                                if (dr.Table.Columns.Contains(col))
                                {
                                    switch (col)
                                    {
                                        case "code":
                                            code = dr[col].ToString() ?? code;
                                            break;
                                        case "message":
                                            message = dr[col].ToString() ?? "";
                                            break;
                                        case "field":
                                            field = dr[col].ToString() ?? "";
                                            break;
                                        case "condition":
                                            condition = dr[col].ToString() ?? "";
                                            break;
                                    }
                                }
                            }
                        }

                        //Lấy dữ liệu bảng thứ 2
                        if (dataSet.Tables.Count > 1)
                        {
                            data = dataSet.Tables[1].Copy();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return GetResponse("EXCEPTION", appConfig.isDevState ? ex.Message : "");
            }

            if(code != "SUCCESS")
            {
                return GetResponse(code, data, field, condition);
            }
            else if (data != null)
            {
                return GetResponse(formName, code, message, data);
            }

            return GetResponse(code, message);
        }

        /// <summary>
        /// Phân trang dữ liệu và trả về response
        /// </summary>
        /// <param name="formName"></param>
        /// <param name="code"></param>
        /// <param name="message"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public IResponse GetResponse(string formName, string code, string message, DataTable data)
        {
            if (data.Rows.Count <= appConfig.maxRowInResponse) {
                return GetResponse(code, message, data);
            }

            CacheRepository cacheRepository = new(appConfig);

            int totalRowCount = data.Rows.Count;
            string cacheID = GenerateCacheID();

            ResponseQuery response = new(GetResponse(code, message))
            {
                cacheID = cacheID,
            };

            //Tách data thành các trang nhỏ
            DataSet dataPagination = PaginationDataResponse(data);
            DataTable dataFirst = dataPagination.Tables[0];
            int pageSize = dataFirst.Rows.Count, 
                pageCount = dataPagination.Tables.Count;

            response.data = new ResponseData
            {
                totalRowCount = totalRowCount,
                pageIndex = 0, // Mặc định là 0, có thể thay đổi nếu cần
                pageSize = pageSize,
                pageCount = pageCount,
                data = dataFirst // Chỉ lấy trang đầu tiên
            };

            //Cache sẽ lưu dưới 2 dạng
            // + File json: Dữ liệu cắt sẽ lưu xuống file và tự động xóa sau khi hết hạn
            //      * Path: Cache/{Form}/{cacheID}/{pageIndex}.json
            // + MemoryCache: Lưu metadata
            //      * Name: {Form}_{cacheID}_{pageIndex}

            for (int i = 0; i < dataPagination.Tables.Count; i++)
            {
                pageSize = dataPagination.Tables[i].Rows.Count;

                ResponseData responseDataPagination = new()
                {
                    totalRowCount = totalRowCount,
                    pageIndex = i,
                    pageSize = pageSize,
                    pageCount = pageCount,
                    data = dataPagination.Tables[i]
                };

                // Lưu dữ liệu vào file
                cacheRepository.WriteResponseCache(formName, cacheID, i, responseDataPagination);
                
                // Lưu dữ liệu vào MemoryCache
                cache.Set(string.Format(MEMORYCACHE_PARTERN, formName, cacheID, i), responseDataPagination, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(appConfig.cacheDurationInMinutes)
                });
            }

            return response;
        }

        /// <summary>
        /// Lấy response được lưu trong cache
        /// </summary>
        /// <param name="formName"></param>
        /// <param name="cacheID"></param>
        /// <param name="pageIndex"></param>
        /// <returns></returns>
        public IResponse GetResponse(string formName, string cacheID, int pageIndex)
        {
            // Nếu tìm thấy cache, trả về dữ liệu từ cache
            ResponseQuery response = new(GetResponse("SUCCESS"))
            {
                cacheID = cacheID
            };

            //Kiểm tra cacheID có tồn tại trong MemoryCache không
            string cacheKeyPrefix = string.Format(MEMORYCACHE_PARTERN, formName, cacheID, pageIndex);

            cache.TryGetValue<ResponseData>(cacheKeyPrefix, out var cachedData);

            if (cachedData != null)
            {
                response.data = cachedData;
                return response;
            }

            //Nếu không tìm thấy cache, tìm trong file
            CacheRepository cacheRepository = new(appConfig);

            response.data = cacheRepository.ReadResponseData(formName, cacheID, pageIndex);
            if (response.data != null) return response;

            return GetResponse("INVALID_CACHEID");
        }

        /// <summary>
        /// Trả response không cần phân trang
        /// </summary>
        /// <param name="code"></param>
        /// <param name="data"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public IResponse GetResponse(string code, DataTable data, params string[] args)
        {
            ResponseQuery response = new(GetResponse(code));

            response.message = string.Format(response.message, args);
            response.data = new ResponseData
            {
                totalRowCount = data.Rows.Count,
                pageIndex = 0, // Mặc định là 0, có thể thay đổi nếu cần
                pageSize = data.Rows.Count,
                pageCount = 1,
                data = data
            };

            return response;
        }

        /// <summary>
        /// Trả response không cần phân trang
        /// </summary>
        /// <param name="code"></param>
        /// <param name="message"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public IResponse GetResponse(string code, string message, DataTable data)
        {
            ResponseQuery response = new(GetResponse(code, message));

            response.data = new ResponseData
            {
                totalRowCount = data.Rows.Count,
                pageIndex = 0, // Mặc định là 0, có thể thay đổi nếu cần
                pageSize = data.Rows.Count,
                pageCount = 1,
                data = data
            };

            return response;
        }

        /// <summary>
        /// Tách nhỏ data trả về
        /// </summary>
        /// <param name="dataTable"></param>
        /// <returns></returns>
        private DataSet PaginationDataResponse(DataTable dataTable)
        {
            DataSet dataPagination = new();

            if (dataTable.Rows.Count > appConfig.maxRowInResponse)
            {
                int pageCount = dataTable.Rows.Count / appConfig.maxRowInResponse;

                for (int i = 0; i <= pageCount; i++)
                {
                    DataTable dt = dataTable.AsEnumerable().Take(appConfig.maxRowInResponse).CopyToDataTable();

                    dataPagination.Tables.Add(dt.Copy());

                    dataTable.AsEnumerable()
                               .Take(appConfig.maxRowInResponse)
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
        private static string GenerateCacheID()
        {
            // Generate a random GUID and convert it to a string
            return Guid.NewGuid().ToString();
        }
        #endregion
        #endregion
    }
}

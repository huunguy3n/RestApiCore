using Business.FormMethod;
using Business.Interface;
using DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Form;
using Models.Interface;
using Models.Options;
using Models.Request;
using Models.Response;

namespace Business
{
    public class FormManager(AppConfig appConfig, Options options, IMemoryCache cache)
    {
        public IResponse ExecuteFrom(string name, RequestQuery request)
        {
            //Check form enable
            ResponseManager responseManager = new ResponseManager(appConfig, options, cache);
            FormRepository formRepository = new FormRepository(appConfig, options );

            Form form = formRepository.GetForm(name, request.userName);

            string memoryCacheID = string.Format(responseManager.memoryCachePartern, request.formName, request.cacheID, request.pageIndex);
            string fileCacheID = string.Format(responseManager.fileCachePartern, request.formName, request.cacheID, request.pageIndex); ;

            IFormMethod formType = null;
            if (CheckCacheIDExists(memoryCacheID, fileCacheID))
            {
                formType = new FormMethodCache(appConfig, options, cache);
            }
            else
            {
                formType = new FormMethodBase(appConfig, options, cache);
            }

            return formType.ExecuteForm(request, form);
        }

        private bool CheckCacheIDExists(string memoryCacheID, string fileCacheID)
        {
            cache.TryGetValue<ResponseData>(memoryCacheID, out var cachedData);

            if (cachedData != null)
            {
                return true;
            }

            if(File.Exists(fileCacheID))
            {
                return true;
            }

            return false;
        }

    }
}

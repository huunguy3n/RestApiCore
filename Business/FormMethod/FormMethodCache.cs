using Business.Interface;
using DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Form;
using Models.Interface;
using Models.Options;
using Models.Request;

namespace Business.FormMethod
{
    public class FormMethodCache(AppConfig appConfig, Options options, IMemoryCache cache) : IFormMethod
    {
        public IResponse ExecuteForm(RequestQuery request, Form form)
        {
            ResponseManager responseManager = new(appConfig, options, cache);
            FormRepository formRepository = new(appConfig, options);

            return responseManager.GetResponse(form.Name, request.cacheID, request.pageIndex);
        }
    }
}

using Business.Interface;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Form;
using Models.Interface;
using Models.Options;
using Models.Request;

namespace Business.FormMethod
{
    public class FormMethodCache : IFormMethod
    {
        private readonly AppConfig _appConfig;
        private readonly Options _options;
        private readonly IMemoryCache _cache;
        private readonly Form _form;
        public FormMethodCache(AppConfig appConfig, Options options, IMemoryCache cache)
        {
            _appConfig = appConfig;
            _options = options;
            _cache = cache;
        }
        public IResponse ExecuteForm(RequestQuery request, Form form)
        {
            throw new NotImplementedException();
        }
    }
}

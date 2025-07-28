using Business.Interface;
using Models;
using Models.Interface;
using Models.Request;

namespace Business.FormMethod
{
    public class FormMethodCache : IFormMethod
    {
        private readonly AppConfig _appConfig;
        private readonly Options _options;
        private readonly Form _form;
        public FormMethodCache(AppConfig appConfig, Options options)
        {
            _appConfig = appConfig;
            _options = options;
        }
        public IResponse ExecuteForm(RequestQuery request, Form form)
        {
            throw new NotImplementedException();
        }
    }
}

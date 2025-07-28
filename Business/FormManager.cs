using Business.FormMethod;
using Business.Interface;
using DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Form;
using Models.Interface;
using Models.Options;
using Models.Request;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Business
{
    public class FormManager
    {
        private readonly AppConfig _appConfig;
        private readonly Options _options;
        private readonly IMemoryCache _cache;

        public FormManager(AppConfig appConfig, Options options, IMemoryCache cache)
        {
            _appConfig = appConfig;
            _options = options;
            _cache = cache;
        }

        public IResponse ExecuteFrom(string name, RequestQuery request)
        {
            //Check form enable
            ResponseManager responseManager = new ResponseManager(_appConfig, _options, _cache);
            FormRepository formRepository = new FormRepository(_appConfig);

            Form form = formRepository.GetForm(name, request.userName);

            IFormMethod formType = null;
            if (request.cacheID != "")
            {
                formType = new FormMethodCache(_appConfig, _options, _cache);
            }
            else
            {
                formType = new FormMethodBase(_appConfig, _options);
            }

            return formType.ExecuteForm(request, form);
        }

    }
}

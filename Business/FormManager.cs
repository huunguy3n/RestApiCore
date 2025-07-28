using Business.FormMethod;
using Business.Interface;
using Models;
using Models.Interface;
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

        public FormManager(AppConfig appConfig, Options options)
        {
            _appConfig = appConfig;
            _options = options;
        }

        public IResponse ExecuteFrom(string name, RequestQuery request)
        {
            //Check form enable
            ResponseManager responseManager = new ResponseManager(_appConfig, _options);
            FormRepository formRepository = new FormRepository(_appConfig);

            Form form = formRepository.GetForm(name, request.userName);

            IFormMethod formType = null;
            if (request.cacheID != "")
            {
                formType = new FormMethodCache(_appConfig, _options);
            }
            else
            {
                formType = new FormMethodBase(_appConfig, _options);
            }

            return formType.ExecuteForm(request, form);
        }

    }
}

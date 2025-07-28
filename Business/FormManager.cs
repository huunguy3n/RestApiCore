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
    public class FormManager(AppConfig appConfig, Options options, IMemoryCache cache)
    {
        public IResponse ExecuteFrom(string name, RequestQuery request)
        {
            //Check form enable
            ResponseManager responseManager = new ResponseManager(appConfig, options, cache);
            FormRepository formRepository = new FormRepository(appConfig, options );

            Form form = formRepository.GetForm(name, request.userName);

            IFormMethod formType = null;
            if (request.cacheID != "")
            {
                formType = new FormMethodCache(appConfig, options, cache);
            }
            else
            {
                formType = new FormMethodBase(appConfig, options, cache);
            }

            return formType.ExecuteForm(request, form);
        }

    }
}

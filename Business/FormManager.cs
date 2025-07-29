using Business.FormMethod;
using Business.Interface;
using DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Form;
using Models.Interface;
using Models.Options;
using Models.Request;

namespace Business
{
    public class FormManager(AppConfig appConfig, Options options, ResponseManager responseManager)
    {
        public IResponse ExecuteFrom(string name, RequestQuery request)
        {
            //Check form enable
            FormRepository formRepository = new FormRepository(appConfig, options );

            Form form = formRepository.GetForm(name, request.userName);

            IFormMethod formType = null;
            if (request.cacheID != "")
            {
                formType = new FormMethodCache(responseManager);
            }
            else
            {
                formType = new FormMethodBase(appConfig, options, responseManager);
            }

            return formType.ExecuteForm(request, form);
        }
    }
}

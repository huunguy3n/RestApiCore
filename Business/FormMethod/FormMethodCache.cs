using Business.Interface;
using Models.Form;
using Models.Interface;
using Models.Request;

namespace Business.FormMethod
{
    public class FormMethodCache(ResponseManager responseManager) : IFormMethod
    {
        public IResponse ExecuteForm(RequestQuery request, Form form)
        {
            return responseManager.GetResponse(form.Name, request.cacheID, request.pageIndex);
        }
    }
}

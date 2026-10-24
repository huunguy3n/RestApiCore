using Models.Form;
using Models.Interface;
using Models.Request;

namespace Web.Legacy
{
    public class FormMethodCache(LegacyResponseManager responseManager) : IFormMethod
    {
        public IResponse ExecuteForm(RequestQuery request, Form form)
        {
            return responseManager.GetResponse(form.Name, request.cacheID, request.pageIndex);
        }
    }
}

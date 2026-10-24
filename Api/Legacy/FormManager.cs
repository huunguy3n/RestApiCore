using DataAccess;
using Models.Form;
using Models.Interface;
using Models.Request;

namespace Web.Legacy
{
    /// <summary>
    /// Engine v2 cũ (POST api/query/{name}): có cacheID thì đọc trang đã lưu, không thì chạy form
    /// </summary>
    public class FormManager(FormRepository formRepository, FormMethodCache cacheMethod, FormMethodBase executeMethod)
    {
        public IResponse ExecuteFrom(string name, RequestQuery request)
        {
            Form form = formRepository.GetForm(name, request.userName);

            IFormMethod formMethod = request.cacheID != "" ? cacheMethod : executeMethod;

            return formMethod.ExecuteForm(request, form);
        }
    }
}

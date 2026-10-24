using Models.Form;
using Models.Interface;
using Models.Request;

namespace Web.Legacy
{
    /// <summary>
    /// Chiến lược của engine v2: FormMethodBase chạy form, FormMethodCache đọc trang phân trang đã lưu
    /// </summary>
    public interface IFormMethod
    {
        IResponse ExecuteForm(RequestQuery request, Form form);
    }
}

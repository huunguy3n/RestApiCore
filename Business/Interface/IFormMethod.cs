using Models.Form;
using Models.Interface;
using Models.Request;
namespace Business.Interface
{
    public interface IFormMethod
    {
        IResponse ExecuteForm(RequestQuery request, Form form);
    }
}

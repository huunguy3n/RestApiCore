using Business.Interface;
using DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Models;
using Models.Form;
using Models.Interface;
using Models.Options;
using Models.Request;
using System.Data;
using Utilities;

namespace Business.FormMethod
{
    public class FormMethodBase : IFormMethod
    {
        private readonly AppConfig _appConfig;
        private readonly Options _options;
        public FormMethodBase(AppConfig appConfig, Options options)
        {
            _appConfig = appConfig;
            _options = options;
        }

        //Lấy struct lưu vào cache
        //Làm file struct ký hiệu
        public IResponse ExecuteForm(RequestQuery request, Form form)
        {
            ResponseManager responseManager = new ResponseManager(_appConfig, _options);
            FormRepository formRepository = new FormRepository(_appConfig);

            //Lấy cấu trúc sqlType
            foreach (Struct @struct in form.Structs)
            {
                DataTable dtTable = formRepository.GetStructTable(@struct.BaseTable);

                foreach (Field field in @struct.Fields)
                {
                    GetTypeByName(dtTable, field);

                    //Render update
                    //@@table, @@field, @@reference
                    if (field.Reference.Controller != null)
                    {
                        var update = _options.Updates.Get(field.Reference.Controller);

                        if (update != null)
                        {
                            field.Reference.Script = update.query
                            .Replace("@@table", "#" + @struct.Name)
                            .Replace("@@field", field.Mapping)
                            .Replace("@@reference", field.Reference.Name);
                        }
                    }
                }
            }

            //Phân rã json
            //Check đơn giản
            CustomJsonConverter customJsonConverter = new CustomJsonConverter(form, _options.Irregular);
            DataSet dsJson = new DataSet();

            try
            {
                customJsonConverter.DeserializeObject(request.jsonBody);
            }
            catch (Exception ex)
            {
                return responseManager.GetResponse("INVALID_JSON", ex.Message);
            }

            if (!string.IsNullOrEmpty(customJsonConverter.fieldName))
            {
                IResponse response = responseManager.GetResponse(customJsonConverter.errorCode);
                string message = response.message
                    .Replace("@@field", customJsonConverter.fieldName)
                    .Replace("@@condition", customJsonConverter.condition);

                return responseManager.GetResponse(request.formName, customJsonConverter.errorCode, message, customJsonConverter.dsError);
            }

            //Trả về kết quả
            DataSet dsExecute = formRepository.ExecuteForm(dsJson, form, request);

            return responseManager.GetResponse(request.formName, dsExecute);
        }

        private void GetTypeByName(DataTable table, Field field)
        {
            switch (field.Type)
            {
                case FieldType.String:
                    if (field.MaxLength == 0)
                    {
                        if (table != null)
                        {
                            DataRow[] foundRows = table.Select($"name = '{field.Mapping}'");

                            if (foundRows.Length > 0)
                            {
                                string sqlType = foundRows[0]["type"].ToString();
                                int.TryParse(foundRows[0]["maxLength"].ToString(), out int maxLength);

                                if (sqlType.Contains("char"))
                                {
                                    field.Type = FieldType.String;
                                }
                                else if (sqlType.Contains("date"))
                                {
                                    field.Type = FieldType.DateTime;
                                }
                                else if (sqlType.Contains("bit"))
                                {
                                    field.Type = FieldType.Boolean;
                                }
                                else if (sqlType.Contains("int"))
                                {
                                    field.Type = FieldType.Int;
                                }
                                else
                                {
                                    field.Type = FieldType.Decimal;
                                }

                                field.MaxLength = maxLength;
                                field.SqlType = sqlType;
                            }
                        }
                    }
                    break;
                case FieldType.DateTime:
                case FieldType.UnixTimestamp:
                    field.SqlType = "datetime";
                    break;
                case FieldType.Decimal:
                    field.SqlType = "numeric(38,6)";
                    break;
                case FieldType.Boolean:
                    field.SqlType = "bit";
                    break;
                case FieldType.Int:
                    field.SqlType = "int";
                    break;
            }
        }
    }
}

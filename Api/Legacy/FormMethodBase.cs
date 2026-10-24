using DataAccess;
using Models.Form;
using Models.Interface;
using Models.Options;
using Models.Request;
using System.Data;
using Utilities;

namespace Web.Legacy
{
    public class FormMethodBase(Options options, FormRepository formRepository, LegacyResponseManager responseManager) : IFormMethod
    {

        //Lấy struct lưu vào cache
        //Làm file struct ký hiệu
        public IResponse ExecuteForm(RequestQuery request, Form form)
        {
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
                        var update = options.Updates.Get(field.Reference.Controller);

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
            DataSet dsJson;
            
            try
            {
                dsJson = request.jsonBody.DeserializeObject(form, options.Irregular, out DataSet dsError);

                if (dsError.Tables.Count > 0)
                {
                    DataRow rowError = dsError.Tables[0].Rows[0];
                    string code = rowError["code"].ToString();
                    string field = rowError["field"].ToString();

                    return responseManager.GetResponse(code, dsError.Tables[1], field);
                }
            }
            catch (Exception ex)
            {
                return responseManager.GetResponse("INVALID_JSON", ex.Message);
            }

            //Trả về kết quả
            DataSet dsExecute = formRepository.ExecuteForm(dsJson, form, request);

            return responseManager.GetResponse(request.formName, dsExecute);
        }

        private static void GetTypeByName(DataTable table, Field field)
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
                                if (int.TryParse(foundRows[0]["maxLength"].ToString(), out int maxLength))
                                {
                                    field.MaxLength = maxLength;
                                }
                                else
                                {
                                    field.MaxLength = -1; // Default to -1 if maxLength is not a valid integer
                                }
                                    
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

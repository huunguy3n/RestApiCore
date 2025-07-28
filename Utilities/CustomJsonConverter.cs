using Models.Form;
using Newtonsoft.Json.Linq;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Utilities
{
    public static class CustomJsonConverter
    {
        public static DataSet? DeserializeObject(this string jsonString, Form form, string irregular, out DataSet dsError)
        {
            dsError = new();
            Regex regex = new(irregular);

            DataSet dataSet = TransformFormToDataSet(form);

            JObject jsonObject = JObject.Parse(jsonString);

            foreach (Struct @struct in form.Structs)
            {
                DataTable table = dataSet.Tables[@struct.Name];

                if (jsonObject[@struct.Name] != null)
                {
                    JArray rows = (JArray)jsonObject[@struct.Name];

                    foreach (JObject row in rows.Cast<JObject>())
                    {
                        DataRow dataRow = table.NewRow();
                        List<Field> fields = [.. @struct.Fields.Where(o => o.Hidden == false)];

                        foreach (Field field in fields)
                        {
                            dataRow[field.Name] = ConvertJValueToDValue(row[field.Name], field.Type, field.DataFormatString);
                        }

                        try
                        {
                            table.Rows.Add(dataRow);
                        }
                        catch (Exception ex)
                        {
                            throw new Exception("The " + @struct.Name + ": " + ex.Message);
                        }
                    }
                }

                foreach (Field fieldCondition in @struct.Fields.Where(o => o.CheckIrregular))
                {
                    if (fieldCondition.CheckIrregular)
                    {
                        DataRow[] drError = [.. table.AsEnumerable().Where(row => regex.IsMatch(row.Field<string>(fieldCondition.Name)))];

                        if (drError.Length > 0)
                        {
                            DataTable dtResponse = new("Response");
                            dtResponse.Columns.Add("code", typeof(string));
                            dtResponse.Columns.Add("field", typeof(string));

                            dtResponse.Rows.Add(["INVALID_FIELD_IRREGULAR", fieldCondition.Name]);

                            dsError.Tables.Add(dtResponse);
                            dsError.Tables.Add(drError.CopyToDataTable());

                            return null;
                        }
                    }
                }
            }


            //Chuyển name ==> mapping
            foreach (DataTable dt in dataSet.Tables)
            {
                foreach (DataColumn dataColumn in dt.Columns)
                {
                    dataColumn.ColumnName = dataColumn.Caption;
                }
            }

            return dataSet;
        }

        /// <summary>
        /// Chuyển đổi Form thành DataSet
        /// </summary>
        /// <param name="form"></param>
        /// <returns></returns>
        private static DataSet TransformFormToDataSet(Form form)
        {
            DataSet dataSet = new();
            foreach (Struct @struct in form.Structs)
            {
                dataSet.Tables.Add(TransformStructToDataTable(@struct));
            }

            return dataSet;
        }

        /// <summary>
        /// Chuyển đổi Struct thành DataTable
        /// </summary>
        /// <param name="struct"></param>
        /// <returns></returns>
        private static DataTable TransformStructToDataTable(Struct @struct)
        {
            DataTable dt = new()
            {
                TableName = @struct.Name
            };
            int n = @struct.Fields.Where(o => o.IsPrimaryKey == true).Count(), i = 0;

            DataColumn[] primaryKeyList = new DataColumn[n == 0 ? 1 : n];
            foreach (Field field in @struct.Fields)
            {
                DataColumn column = new()
                {
                    ColumnName = field.Name,
                    Caption = field.Mapping,
                    DataType = MapFieldTypeToColumnType(field.Type),
                    AllowDBNull = field.AllowNulls,

                };
                if (field.IsPrimaryKey)
                {
                    primaryKeyList[i] = column;
                    i++;
                }
                if (!string.IsNullOrEmpty(field.DefaultValue) || field.Type == FieldType.Boolean)
                {
                    column.DefaultValue = ConvertJValueToDValue(field.DefaultValue, field.Type, field.DataFormatString);
                }

                if (field.Type == FieldType.String) column.MaxLength = field.MaxLength;

                dt.Columns.Add(column);
            }

            dt.PrimaryKey = primaryKeyList;

            return dt;
        }

        /// <summary>
        /// Chuyển đổi giá trị từ kiểu dữ liệu định nghĩa sang Column Type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static Type MapFieldTypeToColumnType(FieldType type)
        {
            return type switch
            {
                FieldType.Boolean => typeof(bool),
                FieldType.String => typeof(string),
                FieldType.DateTime => typeof(DateTime),
                FieldType.Decimal => typeof(decimal),
                FieldType.Int => typeof(int),
                _ => typeof(string),
            };
        }

        /// <summary>
        /// Chuyển đổi giá trị từ JValue sang giá trị tương ứng với kiểu dữ liệu trong định nghĩa
        /// </summary>
        /// <param name="value"></param>
        /// <param name="type"></param>
        /// <param name="format"></param>
        /// <returns></returns>
        /// <exception cref="FormatException"></exception>
        private static object ConvertJValueToDValue(object value, FieldType type, string format)
        {
            switch (type)
            {
                case FieldType.String:
                    return value;
                case FieldType.Boolean:
                    return (value.ToString() == "1" || (value.ToString() == "true"));
                case FieldType.Decimal:
                    return Convert.ToDecimal(value);
                case FieldType.Int:
                    return Convert.ToInt16(value);
                case FieldType.DateTime:
                    if (value.ToString()?.ToUpper() == "GETDATE()")
                    {
                        return DateTime.Now;
                    }
                    else
                    {
                        if (DateTime.TryParseExact(value.ToString(), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
                        {
                            return result;
                        }
                        throw new FormatException($"Cannot convert value {value}, with format {format}");
                    }
                case FieldType.UnixTimestamp:
                    if (long.TryParse(value.ToString(), out long unixTime))
                    {
                        return DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
                    }
                    else return DBNull.Value;
                default:
                    return value;
            }
        }
    }
}

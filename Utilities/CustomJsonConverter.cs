using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Utilities
{
    public class CustomJsonConverter
    {
        private readonly Form _form;
        private readonly string _irregular;

        public string fieldName { get; private set; }
        public string errorCode { get; private set; }
        public string condition { get; private set; }
        public DataSet dsError { get; private set; }
        public CustomJsonConverter(Form form, string irregular)
        {
            _form = form;
            _irregular = irregular;
        }

        public DataSet DeserializeObject(string jsonString)
        {
            DataSet dataSet = CreateDataSetFromForm(_form);

            JObject jsonObject = JObject.Parse(jsonString);


            foreach (Struct @struct in _form.Structs)
            {
                DataTable table = dataSet.Tables[@struct.Name];

                if (jsonObject[@struct.Name] != null)
                {
                    JArray rows = (JArray)jsonObject[@struct.Name];
                    foreach (JObject row in rows)
                    {
                        DataRow dataRow = table.NewRow();
                        List<Field> fields = @struct.Fields.Where(o => o.Hidden == false).ToList();

                        foreach (Field field in fields)
                        {
                            dataRow[field.Name] = SetValue(row[field.Name], field.Type, field.DataFormatString);
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

                foreach (Field fieldCondition in @struct.Fields.Where(o => o.Check.Condition != "" || o.CheckIrregular))
                {
                    DataRow[] drError;

                    if (fieldCondition.CheckIrregular)
                    {
                        Regex regex = new Regex(_irregular);

                        drError = table.AsEnumerable()
                             .Where(row => regex.IsMatch(row.Field<string>(fieldCondition.Name))).ToArray();

                        this.errorCode = "INVALID_FIELD_IRREGULAR";
                    }
                    else
                    {
                        drError = table.Select(fieldCondition.Check.Condition);
                        this.errorCode = fieldCondition.Check.ErrorCode;
                        this.condition = fieldCondition.Check.Condition;
                    }

                    if (drError.Length > 0)
                    {
                        this.fieldName = fieldCondition.Name;
                        this.dsError.Tables.Add(drError.CopyToDataTable());
                        return null;
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

        private DataSet CreateDataSetFromForm(Form form)
        {
            DataSet dataSet = new DataSet();
            foreach (Struct @struct in form.Structs)
            {
                dataSet.Tables.Add(CreateDataTableFromStruct(@struct));
            }

            return dataSet;
        }
        private DataTable CreateDataTableFromStruct(Struct @struct)
        {
            DataTable dt = new DataTable
            {
                TableName = @struct.Name
            };
            int n = @struct.Fields.Where(o => o.IsPrimaryKey == true).Count(), i = 0;

            DataColumn[] primaryKeyList = new DataColumn[n == 0 ? 1 : n];
            foreach (Field field in @struct.Fields)
            {
                DataColumn column = new DataColumn
                {
                    ColumnName = field.Name,
                    Caption = field.Mapping,
                    DataType = GetType(field.Type),
                    AllowDBNull = field.AllowNulls,

                };
                if (field.IsPrimaryKey)
                {
                    primaryKeyList[i] = column;
                    i++;
                }
                if (!string.IsNullOrEmpty(field.DefaultValue) || field.Type == FieldType.Boolean)
                {
                    column.DefaultValue = SetValue(field.DefaultValue, field.Type, field.DataFormatString);
                }

                if (field.Type == FieldType.String) column.MaxLength = field.MaxLength;

                dt.Columns.Add(column);
            }

            dt.PrimaryKey = primaryKeyList;
            return dt;
        }
        private Type GetType(FieldType type)
        {
            switch (type)
            {
                case FieldType.Boolean:
                    return typeof(bool);
                case FieldType.String:
                    return typeof(string);
                case FieldType.DateTime:
                    return typeof(DateTime);
                case FieldType.Decimal:
                    return typeof(decimal);
                case FieldType.Int:
                    return typeof(int);
                default:
                    return typeof(string);
            }
        }
        private object SetValue(object value, FieldType type, string format)
        {
            switch (type)
            {
                case FieldType.String:
                    return value;
                case FieldType.Boolean:
                    return (value.ToString() == "1" ? true : value.ToString() == "true" ? true : false);
                case FieldType.Decimal:
                    return Convert.ToDecimal(value);
                case FieldType.Int:
                    return Convert.ToInt16(value);
                case FieldType.DateTime:
                    if (value.ToString().ToUpper() == "GETDATE()")
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
                    else return null;
                default:
                    return value;
            }
        }
    }
}

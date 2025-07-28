using Models;
using Models.Form;
using Models.Request;
using Provider;
using System.Collections.Concurrent;
using System.Data;
using System.Xml;
using Utilities;

namespace DataAccess
{
    public class FormRepository
    {
        private readonly AppConfig _appConfig;
        private static ConcurrentDictionary<string, Form> _forms = new ConcurrentDictionary<string, Form>();

        public FormRepository(AppConfig appConfig)
        {
            _appConfig = appConfig;
            Provider.Provider.CommandTimeout = _appConfig.commandTimeout;
        }

        #region Get Form
        public Form GetForm(string name, string userName)
        {
            //So sánh LastModified
            string pathFile = Path.Combine(_appConfig.pathDirectory, "App_Data/Form", name + ".xml");

            if (File.Exists(pathFile))
            {
                ServerRepository serverRepository = new ServerRepository(_appConfig);

                if (!serverRepository.CheckFormRights(name, userName))
                {
                    throw new ArgumentNullException(name, "ACCESS_DENIED");
                }

                DateTime lastModified = File.GetLastWriteTime(pathFile);

                var form = _forms.Where(o => o.Key == name).FirstOrDefault().Value;

                if (form == null)
                {
                    form = GetForm(name);
                }
                else if (form.LastModified != lastModified)
                {
                    form = GetForm(name);
                }

                if (form.Enable == false)
                {
                    throw new ArgumentNullException(name, "INVALID_FORM");
                }

                return form;
            }
            else
            {
                throw new ArgumentNullException(name, "INVALID_FORM");
            }
        }

        /// <summary>
        /// Lấy form từ tên đã có lưu cache
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private Form GetForm(string name)
        {
            string pathXML = Path.Combine(_appConfig.pathDirectory, $"App_Data/Form{name}.xml");

            if (!File.Exists(pathXML))
            {
                return null;
            }

            Form form = new Form(name)
            {
                LastModified = File.GetLastWriteTime(pathXML)
            };
            try
            {
                var doc = XMLReader.GetDocument(pathXML);

                var xmlNamespace = new XmlNamespaceManager(doc.NameTable);
                xmlNamespace.AddNamespace("ns", "urn:schemas-fast-com:rest-api");

                //Get Form
                //type (Voucher, List, Report), id, enable
                var nodeFormProperties = doc.SelectSingleNode("ns:form", xmlNamespace);
                form.Type = (FormType)XMLReader.GetAttributes(nodeFormProperties, "type", typeof(FormType));
                switch (form.Type)
                {
                    case FormType.List:
                    case FormType.Voucher:
                        form.Method = FormMethod.Set;
                        break;
                    default:
                        form.Method = FormMethod.Get;
                        break;
                }
                form.Id = XMLReader.GetAttributes(nodeFormProperties, "id");

                string enable = XMLReader.GetAttributes(nodeFormProperties, "enable");
                form.Enable = (enable == "0" ? false : enable == "false" ? false : true);//Không có mặc định true

                //Get Struct
                //name, baseTable
                var nodeStructs = doc.SelectNodes("ns:form/ns:struct", xmlNamespace);
                foreach (XmlNode structNode in nodeStructs)
                {
                    Struct @struct = new Struct();
                    @struct.Name = XMLReader.GetAttributes(structNode, "name");
                    @struct.BaseTable = XMLReader.GetAttributes(structNode, "baseTable");

                    //Get Fields
                    var nodeFields = doc.SelectNodes($"ns:form/ns:struct[@name=\"{@struct.Name}\"]/ns:field", xmlNamespace);

                    foreach (XmlNode nodeField in nodeFields)
                    {
                        Field field = new Field();
                        //name, mapping, type (Decimal, DateTime, String, Boolean, Int, UnixTimestamp), dataFormatString (dd/MM/yyyy, dd/MM/yyyy HH:mm)
                        //maxLength, upperCase, allowNulls, isPrimaryKey, defaultValue, hidden
                        //check, update

                        field.Name = XMLReader.GetAttributes(nodeField, "name");
                        field.Mapping = XMLReader.GetAttributes(nodeField, "mapping");

                        //Nếu mapping rỗng, lấy giá trị name
                        field.Mapping = string.IsNullOrEmpty(field.Mapping) ? field.Name : field.Mapping;

                        string type = XMLReader.GetAttributes(nodeField, "type");
                        field.Type = string.IsNullOrEmpty(type) ? FieldType.String : (FieldType)Enum.Parse(typeof(FieldType), type);
                        field.DataFormatString = XMLReader.GetAttributes(nodeField, "dataFormatString");

                        string maxLength = XMLReader.GetAttributes(nodeField, "maxLength");
                        field.MaxLength = string.IsNullOrEmpty(maxLength) ? 0 : Convert.ToInt16(maxLength);

                        //Mặc định fasle
                        string upperCase = XMLReader.GetAttributes(nodeField, "upperCase");
                        field.UpperCase = upperCase == "1" ? true : upperCase == "true" ? true : false;

                        //Mặc định true
                        string allowNulls = XMLReader.GetAttributes(nodeField, "allowNulls");
                        field.AllowNulls = allowNulls == "0" ? false : allowNulls == "false" ? false : true;

                        //Mặc định fasle
                        string isPrimaryKey = XMLReader.GetAttributes(nodeField, "isPrimaryKey");
                        field.IsPrimaryKey = isPrimaryKey == "1" ? true : isPrimaryKey == "true" ? true : false;

                        //Mặc định fasle
                        string checkIrregular = XMLReader.GetAttributes(nodeField, "checkIrregular");
                        field.CheckIrregular = checkIrregular == "1" ? true : checkIrregular == "true" ? true : false;

                        field.DefaultValue = XMLReader.GetAttributes(nodeField, "defaultValue");

                        //Mặc định fasle
                        string hidden = XMLReader.GetAttributes(nodeField, "hidden");
                        field.Hidden = hidden == "1" ? true : hidden == "true" ? true : false;

                        if (field.Type == FieldType.String)
                        {
                            field.SqlType = $"nvarchar({maxLength})";
                        }

                        var nodeCheckProperties = nodeField.SelectSingleNode("ns:check", xmlNamespace);
                        field.Check.Condition = XMLReader.GetAttributes(nodeCheckProperties, "condition");
                        field.Check.ErrorCode = XMLReader.GetAttributes(nodeCheckProperties, "errorCode");

                        var nodeReferenceProperties = nodeField.SelectSingleNode("ns:reference", xmlNamespace);
                        field.Reference.Controller = XMLReader.GetAttributes(nodeReferenceProperties, "controller");
                        field.Reference.Name = XMLReader.GetAttributes(nodeReferenceProperties, "name");

                        @struct.Fields.Add(field);
                    }

                    form.Structs.Add(@struct);
                }

                //Get Query
                var nodeQuery = doc.SelectSingleNode("ns:form/ns:query/ns:text", xmlNamespace);
                form.Query = nodeQuery.InnerText;
            }
            catch (Exception ex)
            {
                throw ex;
            }

            //Lưu form vào cache
            _forms.AddOrUpdate(name, form, (key, oldValue) => form);

            return form;
        }
        #endregion

        public DataTable GetStructTable(string table)
        {
            if (string.IsNullOrEmpty(table)) return null;
            string sql = $"select COLUMN_NAME as [name], DATA_TYPE + case when DATA_TYPE like '%char%' then '(' + cast(CHARACTER_MAXIMUM_LENGTH as varchar) + ')' when DATA_TYPE in ('numeric', 'decimal') then '(' + cast(NUMERIC_PRECISION AS varchar) + ',' + cast(NUMERIC_SCALE AS varchar) + ')' else '' end as [type], CHARACTER_MAXIMUM_LENGTH as maxLength from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = '{table}' order by ORDINAL_POSITION";
            return Provider.Provider.Retrieve(_appConfig.appConnectionString, sql).Tables[0];
        }

        public DataSet ExecuteForm(DataSet dsJson, Form form, RequestQuery request)
        {
            using (var connnection = Provider.Provider.OpenConn(_appConfig.appConnectionString))
            {
                string scriptUpdate = "";

                foreach (Struct @struct in form.Structs)
                {
                    //Tạo bảng cấu trúc sql
                    Provider.Provider.Execute(connnection, $"create table #{@struct.Name}(" + string.Join(",", @struct.Fields.Select(o => o.Mapping + " " + o.SqlType)) + ", stt int identity(1, 1))");

                    //Bulk insert
                    Provider.Provider.Bulk(connnection, dsJson.Tables[@struct.Name], $"#{@struct.Name}");

                    if (request.debug)
                    {
                        Provider.Provider.Execute(connnection, $"exec restApi_TableDebug '{@struct.Name}', '{form.Name}';");
                    }

                    scriptUpdate += string.Join(";", @struct.Fields.Where(o => o.Reference.Script != "").Select(o => o.Reference.Script));
                }

                //Render update
                Provider.Provider.Execute(connnection, scriptUpdate);

                //Thực thi query
                return Provider.Provider.Retrieve(connnection, ReplaceDefaultVaraible(form.Query, form, request));
            }
        }

        private string ReplaceDefaultVaraible(string sql, Form form, RequestQuery request)
        {
            //@@form, @@userName, @@sysDatabaseName, @@appDatabaseName, @@id, @@unit, @@1.primaryKeyList

            sql = sql.Replace("@@form", $"'{form.Name}'");
            sql = sql.Replace("@@userName", $"'{request.userName}'");
            sql = sql.Replace("@@userERP", $"'{request.userERP}'");
            sql = sql.Replace("@@id", $"'{form.Id}'");
            sql = sql.Replace("@@unit", $"'{request.unit}'");

            sql = sql.Replace("@@appDatabaseName", _appConfig.appDatabaseName);
            sql = sql.Replace("@@sysDatabaseName", _appConfig.sysDatabaseName);

            sql = sql.Replace("@@type", $"'{form.Type.ToString()}'");

            int n = form.Structs.Count;

            for (int i = 0; i < n; i++)
            {
                sql = sql.Replace($"@@{i}.primaryKeyList", $"'{string.Join(",", form.Structs[i].Fields.Where(o => o.IsPrimaryKey).Select(o => o.Mapping))}'");
            }

            return sql;
        }

        public string GetScriptUpdate(string controller, Form form)
        {
            throw new NotImplementedException();
        }

        public string GetScriptCheck(string id, Form form)
        {
            throw new NotImplementedException();
        }
    }
}

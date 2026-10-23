using Models;
using Models.Form;
using Models.Options;
using Models.Request;
using Provider;
using System.Collections.Concurrent;
using System.Data;
using System.Linq;
using System.Xml;
using Utilities;

namespace DataAccess
{
    public class FormRepository
    {
        private static readonly ConcurrentDictionary<string, Form> _forms = new();

        private readonly AppConfig _appConfig;
        private readonly Options _options;

        public FormRepository(AppConfig appConfig, Options options)
        {
            _appConfig = appConfig;
            _options = options;
        }

        #region Get Form
        public Form GetForm(string name, string userName)
        {
            //So sánh LastModified
            string pathFile = Path.Combine(_appConfig.pathDirectory, @$"App_Data\Form\{name}.xml");

            if (File.Exists(pathFile))
            {
                ServerRepository serverRepository = new(_appConfig);

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
            string pathXML = Path.Combine(_appConfig.pathDirectory, $"App_Data\\Form\\{name}.xml");

            if (!File.Exists(pathXML))
            {
                return null;
            }

            Form form = new(name)
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
                form.Method = form.Type switch
                {
                    FormType.List or FormType.Voucher => FormMethod.Set,
                    _ => FormMethod.Get,
                };
                form.Id = XMLReader.GetAttributes(nodeFormProperties, "id");

                string enable = XMLReader.GetAttributes(nodeFormProperties, "enable");
                form.Enable = (enable != "0" && (enable != "false"));//Không có mặc định true

                //Get Struct
                //name, baseTable
                var nodeStructs = doc.SelectNodes("ns:form/ns:struct", xmlNamespace);
                foreach (XmlNode structNode in nodeStructs)
                {
                    Struct @struct = new()
                    {
                        Name = XMLReader.GetAttributes(structNode, "name"),
                        BaseTable = XMLReader.GetAttributes(structNode, "baseTable")
                    };

                    //Get Fields
                    var nodeFields = doc.SelectNodes($"ns:form/ns:struct[@name=\"{@struct.Name}\"]/ns:field", xmlNamespace);

                    foreach (XmlNode nodeField in nodeFields)
                    {
                        //name, mapping, type (Decimal, DateTime, String, Boolean, Int, UnixTimestamp), dataFormatString (dd/MM/yyyy, dd/MM/yyyy HH:mm)
                        //maxLength, upperCase, allowNulls, isPrimaryKey, defaultValue, hidden
                        //check, update
                        Field field = new()
                        {

                            Name = XMLReader.GetAttributes(nodeField, "name"),
                            Mapping = XMLReader.GetAttributes(nodeField, "mapping")
                        };

                        //Nếu mapping rỗng, lấy giá trị name
                        field.Mapping = string.IsNullOrEmpty(field.Mapping) ? field.Name : field.Mapping;

                        string type = XMLReader.GetAttributes(nodeField, "type");
                        field.Type = string.IsNullOrEmpty(type) ? FieldType.String : (FieldType)Enum.Parse(typeof(FieldType), type);
                        field.DataFormatString = XMLReader.GetAttributes(nodeField, "dataFormatString");

                        string maxLength = XMLReader.GetAttributes(nodeField, "maxLength");
                        field.MaxLength = string.IsNullOrEmpty(maxLength) ? 0 : Convert.ToInt16(maxLength);

                        //Mặc định fasle
                        string upperCase = XMLReader.GetAttributes(nodeField, "upperCase");
                        field.UpperCase = upperCase == "1" || (upperCase == "true");

                        //Mặc định true
                        string allowNulls = XMLReader.GetAttributes(nodeField, "allowNulls");
                        field.AllowNulls = allowNulls != "0" && (allowNulls != "false");

                        //Mặc định fasle
                        string isPrimaryKey = XMLReader.GetAttributes(nodeField, "isPrimaryKey");
                        field.IsPrimaryKey = isPrimaryKey == "1" || (isPrimaryKey == "true");

                        //Mặc định fasle
                        string checkIrregular = XMLReader.GetAttributes(nodeField, "checkIrregular");
                        field.CheckIrregular = checkIrregular == "1" || (checkIrregular == "true");

                        field.DefaultValue = XMLReader.GetAttributes(nodeField, "defaultValue");

                        //Mặc định fasle
                        string hidden = XMLReader.GetAttributes(nodeField, "hidden");
                        field.Hidden = hidden == "1" || (hidden == "true");

                        if (field.Type == FieldType.String)
                        {
                            field.SqlType = $"nvarchar({maxLength})";
                        }

                        var nodeChecks = nodeField.SelectNodes("ns:check", xmlNamespace);

                        foreach(XmlNode nodeCheck in nodeChecks)
                        {
                            FieldCheck check = new()
                            {
                                Condition = XMLReader.GetAttributes(nodeCheck, "condition"),
                                ErrorCode = XMLReader.GetAttributes(nodeCheck, "errorCode"),
                                Index = Convert.ToInt16(XMLReader.GetAttributes(nodeCheck, "index"))
                            };

                            check.Script = $"if exists (select 1 from #{@struct.Name} where {check.Condition}) begin\n" +
                                $"select '{check.ErrorCode}' as [code], '{field.Name}' as [field], '{check.Condition.Replace("''", "''''")}' as [condition]\n" +
                                $"select top {_appConfig.maxRowInResponse} @@primaryKeyListAlias from #{@struct.Name} where {check.Condition} group by @@primaryKeyList\n" +
                                "return\nend";

                            field.Checks.Add(check);
                        }


                        var nodeReference = nodeField.SelectSingleNode("ns:reference", xmlNamespace);
                        field.Reference.Controller = XMLReader.GetAttributes(nodeReference, "controller");
                        field.Reference.Name = XMLReader.GetAttributes(nodeReference, "name");

                        string index = XMLReader.GetAttributes(nodeReference, "index");
                        field.Reference.Index = string.IsNullOrEmpty(index) ? 0 : Convert.ToInt16(index);

                        field.Reference.Script = _options.Updates.Get(field.Reference.Controller)?.query
                            .Replace("@@table", $"#{@struct.Name}")
                            .Replace("@@field", field.Mapping)
                            .Replace("@@reference", field.Reference.Name) ?? string.Empty;

                        @struct.Fields.Add(field);
                    }

                    form.Structs.Add(@struct);
                }

                //Get Queries
                //Cấu trúc query[action=""]/text
                var nodeQueries = doc.SelectNodes("ns:form/ns:queries/ns:query", xmlNamespace);
                if (nodeQueries != null)
                {
                    foreach (XmlNode nodeQuery in nodeQueries)
                    {
                        string actionString = XMLReader.GetAttributes(nodeQuery, "action");
                        QueryAction action = string.IsNullOrEmpty(actionString) ? QueryAction.Checking : (QueryAction)Enum.Parse(typeof(QueryAction), actionString);

                        var text = nodeQuery.SelectSingleNode("ns:text", xmlNamespace).InnerText;
                        form.Queries.Add(action, text);
                    }
                }
            }
            catch (Exception)
            {
                throw;
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
            using var connnection = Provider.Provider.OpenConn(_appConfig.appConnectionString);

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
            }

            GetScriptUpdate(form);
            GetScriptCheck(form);

            //Thực thi query
            DataSet dsChecking = Provider.Provider.Retrieve(connnection, ReplaceDefaultVaraible(form.Queries[QueryAction.Checking], form, request));

            if (dsChecking.Tables.Count == 0) return Provider.Provider.Retrieve(connnection, ReplaceDefaultVaraible(form.Queries[QueryAction.Processing], form, request));
            else return dsChecking;
        }

        private string ReplaceDefaultVaraible(string sql, Form form, RequestQuery request)
        {
            //@@form, @@userName, @@userERP, @@id, @@unit, @@appDatabaseName, @@sysDatabaseName, @@type, @@primaryKeyList, @@primaryKeyListAlias

            sql = sql.Replace("@@form", $"'{form.Name}'");
            sql = sql.Replace("@@userName", $"'{request.userName}'");
            sql = sql.Replace("@@userERP", $"'{request.userERP}'");
            sql = sql.Replace("@@id", $"'{form.Id}'");
            sql = sql.Replace("@@unit", $"'{request.unit}'");

            sql = sql.Replace("@@appDatabaseName", _appConfig.appDatabaseName);
            sql = sql.Replace("@@sysDatabaseName", _appConfig.sysDatabaseName);

            sql = sql.Replace("@@type", $"'{form.Type}'");

            sql = sql.Replace($"@@primaryKeyListAlias", $"{string.Join(",", form.Structs[0].Fields.Where(o => o.IsPrimaryKey).Select(o => o.Mapping + " as " + o.Name))}");
            sql = sql.Replace($"@@primaryKeyList", $"{string.Join(",", form.Structs[0].Fields.Where(o => o.IsPrimaryKey).Select(o => o.Mapping))}");
            
            return sql;
        }

        public void GetScriptUpdate(Form form)
        {
            //Trong checking sẽ khai báo script update dạng @@update(index)
            //updateList được join với nhau bằng \n
            var updateList = form.Structs.SelectMany(s => s.Fields)
                .Where(f => !string.IsNullOrEmpty(f.Reference.Script))
                .GroupBy(f => f.Reference.Index)
                .Select(g => new
                {
                    Index = g.Key,
                    Script = string.Join("\n", g.Select(f => f.Reference.Script))
                })
                .OrderBy(i => i.Index)
                .ToList();

            if (updateList.Count > 0)
            {
                for (int i = 0; i < updateList.Count; i++)
                {
                    string updateScript = form.Queries[QueryAction.Checking].Replace($"@@update({i})", updateList[i].Script);
                    form.Queries[QueryAction.Checking] = updateScript;
                }
            }
        }

        public void GetScriptCheck(Form form)
        {
            //Trong checking sẽ khai báo script check dạng @@checking(index)
            //checkingList dược join với nhau bằng \n

            var checkingList = form.Structs.SelectMany(s => s.Fields)
                .SelectMany(f => f.Checks)
                .GroupBy(c => c.Index)
                .Select(g => new
                {
                    Index = g.Key,
                    Script = string.Join("\n", g.Select(c => c.Script))
                })
                .OrderBy(i => i.Index)
                .ToList();

            if (checkingList.Count > 0)
            {
                for (int i = 0; i < checkingList.Count; i++)
                {
                    string checkScript = form.Queries[QueryAction.Checking].Replace($"@@checking({i})", checkingList[i].Script);
                    form.Queries[QueryAction.Checking] = checkScript;
                }
            }

        }
    }
}

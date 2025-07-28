using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace DataAccess
{
    public class OptionsRepository : Options
    {
        private readonly AppConfig _appConfig;

        public OptionsRepository(AppConfig appConfig)
        {
            _appConfig = appConfig;
            Irregular = GetIrregular();
            Messages = GetMessageList();
            Updates = GetUpdateList();
        }

        public Options GetOptions()
        {
            return new Options
            {
                Irregular = GetIrregular(),
                Messages = GetMessageList(),
                Updates = GetUpdateList()
            };
        }

        #region Irregular
        public string GetIrregular()
        {
            string pathText = Path.Combine(_appConfig.pathDirectory, "App_Data/Options/Irregular.txt");
            using (StreamReader sr = new StreamReader(pathText))
            {
                return sr.ReadToEnd();
            }
        }
        #endregion

        #region Message
        public MessageList GetMessageList()
        {
            var messages = new MessageList();
            string pathXML = Path.Combine(_appConfig.pathDirectory, "App_Data/Options/Message.xml");

            if (!File.Exists(pathXML))
            {
                return null;
            }
            else
            {
                var doc = XMLReader.GetDocument(pathXML);

                var xmlNamespace = new XmlNamespaceManager(doc.NameTable);
                xmlNamespace.AddNamespace("ns", "urn:schemas-fast-com:data-message");

                var nodeMessages = doc.SelectNodes("ns:messages/ns:message", xmlNamespace);

                foreach (XmlNode nodeMessage in nodeMessages)
                {
                    messages.Add(new Message
                    {
                        code = XMLReader.GetAttributes(nodeMessage, "code"),
                        success = XMLReader.GetAttributes(nodeMessage, "success") == "1",
                        httpStatusCode = Convert.ToInt16(XMLReader.GetAttributes(nodeMessage, "httpStatusCode")),
                        message = XMLReader.GetAttributes(nodeMessage, "httpMessage")
                    });
                }
            }

            return messages;
        }
        #endregion

        #region Update
        public UpdateList GetUpdateList()
        {
            var updates = new UpdateList();
            string pathXML = Path.Combine(_appConfig.pathDirectory, "App_Data/Options/Update.xml");

            if (!File.Exists(pathXML))
            {
                return null;
            }
            else
            {
                var doc = XMLReader.GetDocument(pathXML);

                var xmlNamespace = new XmlNamespaceManager(doc.NameTable);
                xmlNamespace.AddNamespace("ns", "urn:schemas-fast-com:data-update");

                var nodeUpdates = doc.SelectNodes("ns:update/ns:controller", xmlNamespace);

                foreach (XmlNode nodeUpdate in nodeUpdates)
                {
                    updates.Add(new Update
                    {
                        controller = XMLReader.GetAttributes(nodeUpdate, "name"),
                        query = nodeUpdate.SelectSingleNode("ns:text", xmlNamespace).InnerText,
                    });
                }
            }

            return updates;
        }
        #endregion
    }
}

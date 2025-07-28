using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Utilities
{
    public static class XMLReader
    {
        public static string GetAttributes(XmlNode node, string name)
        {
            if (node == null)
            {
                return string.Empty;
            }
            try
            {
                var attr = node.Attributes[name];
                if (attr == null) { return string.Empty; }
                var temp = attr.Value;
                if (temp == null) { return string.Empty; }

                return temp;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static object GetAttributes(XmlNode node, string name, Type type)
        {
            return Enum.Parse(type, GetAttributes(node, name));
        }

        public static XmlDocument GetDocument(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }
            else
            {
                try
                {
                    var doc = new XmlDocument();
                    var resol = new XmlUrlResolver();
                    resol.Credentials = CredentialCache.DefaultCredentials;
                    doc.XmlResolver = resol;
                    doc.Load(filePath);

                    return doc;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }
    }
}

#if NETCOREAPP
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FboWeb.ApiLog
{
    /// <summary>
    /// Che bí mật trong log trước khi trả về trình duyệt. Không phụ thuộc System.Web để Tests (.NET) dùng chung file.
    /// JSON: đọc từng token, giữ nguyên chữ của chuỗi/số (không đổi độ chính xác), chỉ thay giá trị bí mật bằng "***".
    /// </summary>
    public static class LogRedactor
    {
        public const string Mask = "***";
        private const int MaxDepth = 128;

        /// <summary>Trùng nguyên tên</summary>
        private static readonly HashSet<string> SecretNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "pwd", "pass", "auth", "authorization", "jwt", "bearer", "sessionid", "hmac", "otp"
        };

        /// <summary>Có mặt ở bất kỳ đâu trong tên (newPassword, clientSecretValue, privateKeyPem)</summary>
        private static readonly string[] SecretParts = { "password", "passwd", "secret", "privatekey", "apikey" };

        /// <summary>Đứng cuối tên (refreshToken, xFastSignature, setCookie) — đứng đầu thì vô hại (tokenType, signatureResult)</summary>
        private static readonly string[] SecretSuffixes = { "token", "signature", "cookie", "credential", "credentials", "accesskey" };

        private static readonly Regex JwtValue = new Regex(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);
        private static readonly Regex AuthSchemeValue = new Regex(@"^\s*(Bearer|Basic|Digest|HMAC[\w-]*)\s+\S", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Tên khóa bí mật: so sau khi bỏ ký tự không phải chữ-số (access_token, X-Fast-Signature, clientSecret…)</summary>
        public static bool IsSecretKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            StringBuilder sb = new StringBuilder(key.Length);
            foreach (char c in key)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
            }
            string name = sb.ToString();
            if (name.Length == 0) return false;
            if (SecretNames.Contains(name)) return true;
            foreach (string part in SecretParts)
            {
                if (name.IndexOf(part, StringComparison.Ordinal) >= 0) return true;
            }
            foreach (string suffix in SecretSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Giá trị tự lộ là bí mật dù tên khóa vô hại: JWT, "Bearer ...", khóa PEM</summary>
        public static bool IsSecretValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return JwtValue.IsMatch(value) || AuthSchemeValue.IsMatch(value) || value.IndexOf("PRIVATE KEY-----", StringComparison.Ordinal) >= 0;
        }

        /// <summary>JSON hợp lệ → bản gọn (không khoảng trắng) đã che bí mật; không phải JSON → false</summary>
        public static bool TryRedactJson(string json, out string redacted)
        {
            redacted = null;
            if (json == null) return false;
            Reader reader = new Reader(json);
            StringBuilder output = new StringBuilder(json.Length);
            try
            {
                reader.SkipBom();
                reader.SkipWhitespace();
                reader.Value(output, 0);
                reader.SkipWhitespace();
                if (!reader.AtEnd) return false;
            }
            catch (FormatException)
            {
                return false;
            }
            redacted = output.ToString();
            return true;
        }

        /// <summary>Query string: che giá trị của tham số bí mật, giữ nguyên phần còn lại</summary>
        public static string RedactQuery(string query)
        {
            if (string.IsNullOrEmpty(query)) return query;
            bool leading = query[0] == '?';
            string[] parts = (leading ? query.Substring(1) : query).Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                string name = eq < 0 ? parts[i] : parts[i].Substring(0, eq);
                string value = eq < 0 ? "" : parts[i].Substring(eq + 1);
                string decodedName = Decode(name);
                if (eq >= 0 && (IsSecretKey(decodedName) || IsSecretValue(Decode(value)))) parts[i] = name + "=" + Mask;
            }
            return (leading ? "?" : "") + string.Join("&", parts);
        }

        private static string Decode(string text)
        {
            try { return Uri.UnescapeDataString(text.Replace('+', ' ')); }
            catch (UriFormatException) { return text; }
        }

        private sealed class Reader
        {
            private readonly string text;
            private int pos;
            private int literalStart;

            public Reader(string text) { this.text = text; }

            public bool AtEnd { get { return pos >= text.Length; } }

            public void SkipBom()
            {
                if (pos < text.Length && text[pos] == '\uFEFF') pos++;
            }

            public void SkipWhitespace()
            {
                while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\r' || text[pos] == '\n')) pos++;
            }

            /// <param name="output">null = đọc để kiểm hợp lệ rồi bỏ (giá trị bị che)</param>
            public void Value(StringBuilder output, int depth)
            {
                if (depth > MaxDepth) throw new FormatException();
                if (AtEnd) throw new FormatException();
                char c = text[pos];
                if (c == '{') Object(output, depth + 1);
                else if (c == '[') Array(output, depth + 1);
                else if (c == '"')
                {
                    int start = pos;
                    string value = String();
                    if (output != null) output.Append(IsSecretValue(value) ? "\"" + Mask + "\"" : text.Substring(start, pos - start));
                }
                else if (c == '-' || (c >= '0' && c <= '9')) Copy(output, Number());
                else if (Literal("true") || Literal("false") || Literal("null")) Copy(output, text.Substring(literalStart, pos - literalStart));
                else throw new FormatException();
            }

            private bool Literal(string word)
            {
                if (string.CompareOrdinal(text, pos, word, 0, word.Length) != 0) return false;
                literalStart = pos;
                pos += word.Length;
                return true;
            }

            private static void Copy(StringBuilder output, string token)
            {
                if (output != null) output.Append(token);
            }

            private void Object(StringBuilder output, int depth)
            {
                pos++;
                Copy(output, "{");
                SkipWhitespace();
                if (Peek('}')) { pos++; Copy(output, "}"); return; }
                while (true)
                {
                    SkipWhitespace();
                    if (!Peek('"')) throw new FormatException();
                    int start = pos;
                    string key = String();
                    Copy(output, text.Substring(start, pos - start));
                    SkipWhitespace();
                    Expect(':');
                    Copy(output, ":");
                    SkipWhitespace();
                    if (IsSecretKey(key))
                    {
                        Value(null, depth);
                        Copy(output, "\"" + Mask + "\"");
                    }
                    else
                    {
                        Value(output, depth);
                    }
                    SkipWhitespace();
                    if (Peek(',')) { pos++; Copy(output, ","); continue; }
                    Expect('}');
                    Copy(output, "}");
                    return;
                }
            }

            private void Array(StringBuilder output, int depth)
            {
                pos++;
                Copy(output, "[");
                SkipWhitespace();
                if (Peek(']')) { pos++; Copy(output, "]"); return; }
                while (true)
                {
                    SkipWhitespace();
                    Value(output, depth);
                    SkipWhitespace();
                    if (Peek(',')) { pos++; Copy(output, ","); continue; }
                    Expect(']');
                    Copy(output, "]");
                    return;
                }
            }

            /// <summary>Đọc chuỗi JSON (con trỏ đứng ở dấu "), trả giá trị đã giải escape</summary>
            private string String()
            {
                pos++;
                StringBuilder value = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw new FormatException();
                    char c = text[pos++];
                    if (c == '"') return value.ToString();
                    if (c < ' ') throw new FormatException();
                    if (c != '\\') { value.Append(c); continue; }
                    if (AtEnd) throw new FormatException();
                    char e = text[pos++];
                    switch (e)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            int code;
                            if (pos + 4 > text.Length || !int.TryParse(text.Substring(pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) throw new FormatException();
                            value.Append((char)code);
                            pos += 4;
                            break;
                        default: throw new FormatException();
                    }
                }
            }

            private string Number()
            {
                int start = pos;
                if (Peek('-')) pos++;
                if (Peek('0')) pos++;
                else if (!Digits()) throw new FormatException();
                if (Peek('.')) { pos++; if (!Digits()) throw new FormatException(); }
                if (Peek('e') || Peek('E'))
                {
                    pos++;
                    if (Peek('+') || Peek('-')) pos++;
                    if (!Digits()) throw new FormatException();
                }
                return text.Substring(start, pos - start);
            }

            private bool Digits()
            {
                int start = pos;
                while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9') pos++;
                return pos > start;
            }

            private bool Peek(char c)
            {
                return pos < text.Length && text[pos] == c;
            }

            private void Expect(char c)
            {
                if (!Peek(c)) throw new FormatException();
                pos++;
            }
        }
    }
}

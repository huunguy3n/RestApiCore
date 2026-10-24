using System.Collections.Specialized;
using System.Text.Json.Nodes;
using WebProject.ApiLog;

namespace Tests
{
    /// <summary>WebProject (trang FBO xem log, .NET Framework): file nguồn link vào đây vì không phụ thuộc System.Web</summary>
    public sealed class FboLogRedactorTests
    {
        private static string Redact(string json)
        {
            Assert.True(LogRedactor.TryRedactJson(json, out string redacted), json);
            return redacted;
        }

        [Fact]
        public void Masks_secret_keys_at_every_level_and_keeps_other_values()
        {
            string output = Redact("""
                { "user": "ADMIN", "password": "p@ss", "data": { "items": [ { "access_token": "abc", "qty": 2 } ], "clientSecret": { "nested": 1 } } }
                """);

            JsonNode node = JsonNode.Parse(output)!;
            Assert.Equal("ADMIN", (string?)node["user"]);
            Assert.Equal("***", (string?)node["password"]);
            Assert.Equal("***", (string?)node["data"]!["items"]![0]!["access_token"]);
            Assert.Equal(2, (int)node["data"]!["items"]![0]!["qty"]!);
            Assert.Equal("***", (string?)node["data"]!["clientSecret"]);
        }

        [Theory]
        [InlineData("password")]
        [InlineData("newPassword")]
        [InlineData("private_key")]
        [InlineData("privateKeyPem")]
        [InlineData("X-Fast-Signature")]
        [InlineData("refreshToken")]
        [InlineData("Authorization")]
        [InlineData("Set-Cookie")]
        [InlineData("api_key")]
        [InlineData("pass\\u0077ord")]
        public void Detects_secret_key_variants(string key)
        {
            string output = Redact("{\"" + key + "\":\"x\",\"keep\":\"y\"}");
            Assert.Equal("{\"" + key + "\":\"***\",\"keep\":\"y\"}", output);
        }

        [Theory]
        [InlineData("tokenType")]
        [InlineData("signatureResult")]
        [InlineData("code")]
        [InlineData("ma_kh")]
        public void Keeps_harmless_keys(string key)
        {
            Assert.False(LogRedactor.IsSecretKey(key));
        }

        [Theory]
        [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
        [InlineData("Bearer abc.def")]
        [InlineData("-----BEGIN PRIVATE KEY-----MIIE")]
        public void Masks_secret_looking_values_under_harmless_keys(string value)
        {
            Assert.Equal("{\"note\":\"***\"}", Redact("{\"note\":\"" + value + "\"}"));
        }

        [Fact]
        public void Keeps_number_and_string_literals_exactly()
        {
            Assert.Equal("{\"id\":12345678901234567890,\"price\":1.50,\"e\":-2E+3,\"s\":\"a\\u00e9\\\"b\",\"n\":null,\"b\":[true,false]}",
                Redact("{ \"id\" : 12345678901234567890, \"price\": 1.50, \"e\": -2E+3, \"s\": \"a\\u00e9\\\"b\", \"n\": null, \"b\": [ true , false ] }"));
        }

        [Fact]
        public void Accepts_bom_and_top_level_array()
        {
            Assert.Equal("[{\"token\":\"***\"}]", Redact("\uFEFF [ { \"token\": [1, {\"a\": 2}] } ] "));
        }

        [Theory]
        [InlineData("")]
        [InlineData("{a:1}")]
        [InlineData("{\"a\":1")]
        [InlineData("[1,]")]
        [InlineData("01")]
        [InlineData("{} x")]
        [InlineData("password=abc&user=x")]
        [InlineData("{\"a\":\"line\nbreak\"}")]
        public void Rejects_invalid_json(string text)
        {
            Assert.False(LogRedactor.TryRedactJson(text, out string? redacted));
            Assert.Null(redacted);
        }

        [Fact]
        public void Rejects_too_deep_nesting_instead_of_overflowing()
        {
            Assert.False(LogRedactor.TryRedactJson(new string('[', 500) + new string(']', 500), out _));
        }

        [Theory]
        [InlineData("?token=abc&x=1", "?token=***&x=1")]
        [InlineData("a=1&client_secret=s3&b", "a=1&client_secret=***&b")]
        [InlineData("?auth=Bearer%20xyz", "?auth=***")]
        [InlineData("?note=Bearer%20xyz", "?note=***")]
        [InlineData("?from=2026-10-01", "?from=2026-10-01")]
        public void Redacts_query_string(string query, string expected)
        {
            Assert.Equal(expected, LogRedactor.RedactQuery(query));
        }
    }

    public sealed class FboApiLogQueryTests
    {
        private static readonly DateTime Today = new(2026, 10, 24);

        private static string? Parse(out ApiLogFilter? filter, params (string Key, string Value)[] pairs)
        {
            NameValueCollection query = new();
            foreach ((string key, string value) in pairs)
            {
                query[key] = value;
            }
            string? error = ApiLogQuery.TryParseList(query, Today, out ApiLogFilter result);
            filter = result;
            return error;
        }

        [Fact]
        public void Defaults_to_today_and_first_page()
        {
            Assert.Null(Parse(out ApiLogFilter? f));
            Assert.Equal(Today, f!.From);
            Assert.Equal(Today.AddDays(1), f.ToExclusive);
            Assert.Equal(1, f.Page);
            Assert.Equal(ApiLogQuery.DefaultPageSize, f.PageSize);
            Assert.Null(f.UserName);
            Assert.Null(f.StatusFrom);
            Assert.Null(f.ResultNot);
            Assert.Null(f.ReqId);
        }

        [Fact]
        public void Range_is_inclusive_and_limited_to_31_days()
        {
            Assert.Null(Parse(out _, ("from", "2026-10-01"), ("to", "2026-10-31")));
            Assert.Equal("INVALID_RANGE", Parse(out _, ("from", "2026-10-01"), ("to", "2026-11-01")));
            Assert.Equal("INVALID_RANGE", Parse(out _, ("from", "2026-10-02"), ("to", "2026-10-01")));
            Assert.Equal("INVALID_DATE", Parse(out _, ("from", "24/10/2026")));
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("99")]
        [InlineData("600")]
        [InlineData("-1")]
        [InlineData("6xx")]
        [InlineData("4x")]
        public void Rejects_bad_status(string status)
        {
            Assert.Equal("INVALID_STATUS", Parse(out _, ("status", status)));
        }

        [Fact]
        public void Parses_status_req_id_and_clamps_page_size()
        {
            Assert.Null(Parse(out ApiLogFilter? f, ("status", "500"), ("reqId", " 9e972090-a334-46e2-ae26-80d55e56f523 "), ("page", "3"), ("pageSize", "1000")));
            Assert.Equal(500, f!.StatusFrom);
            Assert.Equal(500, f.StatusTo);
            Assert.Equal(Guid.Parse("9e972090-a334-46e2-ae26-80d55e56f523"), f.ReqId);
            Assert.Equal(3, f.Page);
            Assert.Equal(ApiLogQuery.MaxPageSize, f.PageSize);
            Assert.Equal("INVALID_REQ_ID", Parse(out _, ("reqId", "not-a-guid")));
        }

        [Theory]
        [InlineData("2xx", 200, 299)]
        [InlineData("4XX", 400, 499)]
        [InlineData("5xx", 500, 599)]
        public void Parses_status_group(string status, int from, int to)
        {
            Assert.Null(Parse(out ApiLogFilter? f, ("status", status)));
            Assert.Equal(from, f!.StatusFrom);
            Assert.Equal(to, f.StatusTo);
        }

        [Fact]
        public void Bang_prefix_means_result_code_other_than()
        {
            Assert.Null(Parse(out ApiLogFilter? f, ("result", "!SUCCESS")));
            Assert.Null(f!.ResultCode);
            Assert.Equal("SUCCESS", f.ResultNot);

            Assert.Null(Parse(out f, ("result", "FORBIDDEN")));
            Assert.Equal("FORBIDDEN", f!.ResultCode);
            Assert.Null(f.ResultNot);
        }

        [Fact]
        public void Cleans_text_filters_and_escapes_like_wildcards()
        {
            Assert.Null(Parse(out ApiLogFilter? f, ("path", "/api/_meta/x%'; DROP--"), ("user", "admin@fast.vn<script>"), ("result", "INVALID_SIGNATURE")));
            Assert.Equal("/api/[_]meta/xDROP--", f!.PathPrefix);
            Assert.Equal("admin@fast.vnscript", f.UserName);
            Assert.Equal("INVALID_SIGNATURE", f.ResultCode);
        }
    }
}

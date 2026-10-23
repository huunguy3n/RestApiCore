// Fast API - ký request, kiểm chữ ký response (.NET 6+, không cần package ngoài)
// Chạy: dotnet run                                  -> kiểm vector thử
//       dotnet run -- <base_url> <user> <private_key> -> lấy token rồi gọi _meta/ping
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

FastSign.TestVector();
if (args.Length == 3)
{
    var client = new FastSign(args[0].TrimEnd('/'), args[2]);
    string user = args[1];
    var token = await client.SendAsync(HttpMethod.Post, "/api/auth/token", JsonSerializer.Serialize(new { userName = user }), extra: ("X-Fast-User", user));
    Console.WriteLine(await client.SendAsync(HttpMethod.Post, "/api/_meta/ping", "", token.GetProperty("accessToken").GetString()));
}

public class FastSign(string baseUrl, string privateKeyBase64)
{
    private readonly HttpClient _http = new();
    private readonly byte[] _requestKey = DeriveKey(privateKeyBase64, "FAST-API-V3-REQUEST");
    private readonly byte[] _responseKey = DeriveKey(privateKeyBase64, "FAST-API-V3-RESPONSE");

    public static byte[] DeriveKey(string privateKeyBase64, string label) =>
        HMACSHA256.HashData(Convert.FromBase64String(privateKeyBase64), Encoding.ASCII.GetBytes(label));

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string HmacHex(byte[] key, string text) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Encode RFC 3986: giữ A-Z a-z 0-9 - . _ ~, còn lại %XX hex hoa</summary>
    public static string Rfc3986(string value)
    {
        StringBuilder sb = new();
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string CanonicalQuery(string? rawQuery)
    {
        string query = (rawQuery ?? "").TrimStart('?');
        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            int i = part.IndexOf('=');
            string name = i < 0 ? part : part[..i], value = i < 0 ? "" : part[(i + 1)..];
            return (Name: Rfc3986(Uri.UnescapeDataString(name.Replace('+', ' '))), Value: Rfc3986(Uri.UnescapeDataString(value.Replace('+', ' '))));
        }).OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal);
        return string.Join("&", pairs.Select(p => $"{p.Name}={p.Value}"));
    }

    public static Dictionary<string, string> SignRequest(byte[] requestKey, string method, string pathAndQuery, byte[] body, long timestamp, string nonce, string idempotencyKey = "")
    {
        int q = pathAndQuery.IndexOf('?');
        string path = q < 0 ? pathAndQuery : pathAndQuery[..q];
        string contentSha256 = Sha256Hex(body);
        string canonical = string.Join("\n", "FAST-HMAC-SHA256", method.ToUpperInvariant(), path.ToLowerInvariant(),
            CanonicalQuery(q < 0 ? "" : pathAndQuery[q..]), timestamp, nonce, idempotencyKey, contentSha256);
        var headers = new Dictionary<string, string>
        {
            ["X-Fast-Timestamp"] = timestamp.ToString(),
            ["X-Fast-Nonce"] = nonce,
            ["X-Fast-Content-SHA256"] = contentSha256,
            ["X-Fast-Signature"] = HmacHex(requestKey, canonical)
        };
        if (idempotencyKey.Length > 0) headers["Idempotency-Key"] = idempotencyKey;
        return headers;
    }

    public static bool VerifyResponse(byte[] responseKey, int status, string requestId, string requestNonce, string timestamp, string contentSha256, string signature, byte[] body)
    {
        string actualSha256 = Sha256Hex(body);
        string canonical = string.Join("\n", "FAST-HMAC-SHA256-RESPONSE", status, requestId, requestNonce, timestamp, actualSha256);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualSha256), Encoding.ASCII.GetBytes(contentSha256.ToLowerInvariant()))
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HmacHex(responseKey, canonical)), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string pathAndQuery, string body, string? token = null, (string Name, string Value)? extra = null)
    {
        long serverTime = (await _http.GetFromJsonAsync<JsonElement>(baseUrl + "/api/_meta/server")).GetProperty("serverTime").GetInt64();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        using var request = new HttpRequestMessage(method, baseUrl + pathAndQuery) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        foreach (var (name, value) in SignRequest(_requestKey, method.Method, pathAndQuery, bytes, serverTime, nonce)) request.Headers.Add(name, value);
        if (token != null) request.Headers.Authorization = new("Bearer", token);
        if (extra is var (extraName, extraValue)) request.Headers.Add(extraName, extraValue);

        using var response = await _http.SendAsync(request);
        byte[] responseBody = await response.Content.ReadAsByteArrayAsync();
        string Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.First() : "";
        if (!VerifyResponse(_responseKey, (int)response.StatusCode, Header("X-Request-ID"), nonce, Header("X-Fast-Timestamp"),
            Header("X-Fast-Content-SHA256"), Header("X-Fast-Signature"), responseBody))
        {
            throw new InvalidOperationException("Chữ ký response sai: không tin nội dung này");
        }
        return JsonSerializer.Deserialize<JsonElement>(responseBody);
    }

    public static void TestVector()
    {
        const string privateKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        byte[] requestKey = DeriveKey(privateKey, "FAST-API-V3-REQUEST"), responseKey = DeriveKey(privateKey, "FAST-API-V3-RESPONSE");
        Check(Convert.ToHexString(requestKey).ToLowerInvariant() == "97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f", "K_request");
        Check(Convert.ToHexString(responseKey).ToLowerInvariant() == "62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd", "K_response");

        byte[] body = Encoding.UTF8.GetBytes("{\"master\":{\"YourID\":\"HD001\"},\"detail\":[{\"ItemID\":\"VT01\",\"Quantity\":2}]}");
        var headers = SignRequest(requestKey, "POST", "/api/sale-invoice?unitCode=CTY&dryRun=false", body, 1792574400, "9b2e4c1f0a7d4e3b", "6f1c2d3e-0000-4000-8000-000000000001");
        Check(headers["X-Fast-Content-SHA256"] == "534275e0a5da4d457614f2f130e8db3dfe3a21cef90e3f5e13ae77c3d60ed12f", "SHA-256 body");
        Check(headers["X-Fast-Signature"] == "4588c91a82467e239600b66fd21e08e18f626037cc91ff4197aa9cf137dc81b2", "Chữ ký request");

        byte[] response = Encoding.UTF8.GetBytes("{\"code\":\"SUCCESS\"}");
        Check(VerifyResponse(responseKey, 200, "8c2f0000-0000-4000-8000-000000000002", "9b2e4c1f0a7d4e3b", "1792574401", Sha256Hex(response),
            "1972c2ae0b53f37dd745becadfd19669a8da4d968fb282891ed43fddec17fd68", response), "Chữ ký response");
        Console.WriteLine("Vector thử: đạt");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("Sai: " + name);
    }
}

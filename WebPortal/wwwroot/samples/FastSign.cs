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
    var token = await client.SendAsync(HttpMethod.Post, "/api/auth/token", JsonSerializer.Serialize(new { userName = user }));
    Console.WriteLine(await client.SendAsync(HttpMethod.Post, "/api/_meta/ping", "", token.GetProperty("accessToken").GetString()));
}

public class FastSign(string baseUrl, string privateKeyBase64)
{
    private readonly HttpClient _http = new();
    private readonly byte[] _requestKey = DeriveKey(privateKeyBase64, "FAST-API-V3-REQUEST");
    private readonly byte[] _responseKey = DeriveKey(privateKeyBase64, "FAST-API-V3-RESPONSE");

    public static byte[] DeriveKey(string privateKeyBase64, string label) =>
        HMACSHA256.HashData(Convert.FromBase64String(privateKeyBase64), Encoding.ASCII.GetBytes(label));

    public static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    public static string HmacHex(byte[] key, string text) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>X-Fast-Signature = HMAC-SHA256(K_request, md5Hex(body) + nonce); nonce = "&lt;unix giây&gt;-&lt;ngẫu nhiên&gt;"</summary>
    public static Dictionary<string, string> SignRequest(byte[] requestKey, byte[] body, string nonce, string idempotencyKey = "")
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Fast-Nonce"] = nonce,
            ["X-Fast-Signature"] = HmacHex(requestKey, Md5Hex(body) + nonce)
        };
        if (idempotencyKey.Length > 0) headers["Idempotency-Key"] = idempotencyKey;
        return headers;
    }

    /// <summary>X-Fast-Signature = HMAC-SHA256(K_response, md5Hex(body) + nonce của request)</summary>
    public static bool VerifyResponse(byte[] responseKey, string requestNonce, string signature, byte[] body) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HmacHex(responseKey, Md5Hex(body) + requestNonce)), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));

    public async Task<JsonElement> SendAsync(HttpMethod method, string pathAndQuery, string body, string? token = null)
    {
        long serverTime = (await _http.GetFromJsonAsync<JsonElement>(baseUrl + "/api/_meta/server")).GetProperty("serverTime").GetInt64();
        string nonce = $"{serverTime}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        using var request = new HttpRequestMessage(method, baseUrl + pathAndQuery) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        foreach (var (name, value) in SignRequest(_requestKey, bytes, nonce)) request.Headers.Add(name, value);
        if (token != null) request.Headers.Authorization = new("Bearer", token);

        using var response = await _http.SendAsync(request);
        byte[] responseBody = await response.Content.ReadAsByteArrayAsync();
        string Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.First() : "";
        if (!VerifyResponse(_responseKey, nonce, Header("X-Fast-Signature"), responseBody))
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
        const string nonce = "1792574400-9b2e4c1f0a7d4e3b";
        Check(Md5Hex(body) == "8e0a0e9704e5282159769a91441af571", "MD5 body");
        Check(SignRequest(requestKey, body, nonce)["X-Fast-Signature"] == "9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac", "Chữ ký request");

        byte[] response = Encoding.UTF8.GetBytes("{\"code\":\"SUCCESS\"}");
        Check(VerifyResponse(responseKey, nonce, "0736896e2b8b43d7215b2e4366f133f2a4d1f4ff472fc089f08365fc79bbd50f", response), "Chữ ký response");
        Console.WriteLine("Vector thử: đạt");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("Sai: " + name);
    }
}

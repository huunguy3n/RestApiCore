// Fast API - ký request, kiểm chữ ký response (Java 11+, chỉ dùng thư viện chuẩn)
// Chạy: java FastSign.java                                   -> kiểm vector thử
//       java FastSign.java <base_url> <user> <private_key>   -> lấy token rồi gọi _meta/ping
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

public class FastSign {
    static byte[] hmac(byte[] key, byte[] data) throws Exception {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(key, "HmacSHA256"));
        return mac.doFinal(data);
    }

    static String hex(byte[] bytes) {
        StringBuilder sb = new StringBuilder();
        for (byte b : bytes) sb.append(String.format("%02x", b));
        return sb.toString();
    }

    static byte[] deriveKey(String privateKeyBase64, String label) throws Exception {
        return hmac(Base64.getDecoder().decode(privateKeyBase64), label.getBytes(StandardCharsets.US_ASCII));
    }

    static String sha256Hex(byte[] data) throws Exception {
        return hex(MessageDigest.getInstance("SHA-256").digest(data));
    }

    static String md5Hex(byte[] data) throws Exception {
        return hex(MessageDigest.getInstance("MD5").digest(data));
    }

    static String hmacHex(byte[] key, String text) throws Exception {
        return hex(hmac(key, text.getBytes(StandardCharsets.UTF_8)));
    }

    /** X-Fast-Signature = HMAC-SHA256(K_request, md5Hex(body) + nonce); nonce = "<unix giây>-<ngẫu nhiên>" */
    static Map<String, String> signRequest(byte[] requestKey, byte[] body, String nonce, String idempotencyKey) throws Exception {
        Map<String, String> headers = new LinkedHashMap<>();
        headers.put("X-Fast-Nonce", nonce);
        headers.put("X-Fast-Signature", hmacHex(requestKey, md5Hex(body) + nonce));
        if (!idempotencyKey.isEmpty()) headers.put("Idempotency-Key", idempotencyKey);
        return headers;
    }

    static boolean verifyResponse(byte[] responseKey, int status, String requestId, String requestNonce, String timestamp, String contentSha256, String signature, byte[] body) throws Exception {
        String actualSha256 = sha256Hex(body);
        String canonical = String.join("\n", "FAST-HMAC-SHA256-RESPONSE", Integer.toString(status), requestId, requestNonce, timestamp, actualSha256);
        return MessageDigest.isEqual(actualSha256.getBytes(), contentSha256.toLowerCase().getBytes())
            && MessageDigest.isEqual(hmacHex(responseKey, canonical).getBytes(), signature.trim().toLowerCase().getBytes());
    }

    static void check(boolean ok, String name) {
        if (!ok) throw new IllegalStateException("Sai: " + name);
    }

    static void testVector() throws Exception {
        String privateKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        byte[] requestKey = deriveKey(privateKey, "FAST-API-V3-REQUEST"), responseKey = deriveKey(privateKey, "FAST-API-V3-RESPONSE");
        check(hex(requestKey).equals("97e74368c9f83ca77fc672a8a56e39b9c6f02527fd3e548454aa29aade37172f"), "K_request");
        check(hex(responseKey).equals("62ed65120ef56ee55f108c4966a34cf71c10b004191f83cef00532c54b8993dd"), "K_response");

        byte[] body = "{\"master\":{\"YourID\":\"HD001\"},\"detail\":[{\"ItemID\":\"VT01\",\"Quantity\":2}]}".getBytes(StandardCharsets.UTF_8);
        String nonce = "1792574400-9b2e4c1f0a7d4e3b";
        check(md5Hex(body).equals("8e0a0e9704e5282159769a91441af571"), "MD5 body");
        check(signRequest(requestKey, body, nonce, "").get("X-Fast-Signature").equals("9f79234564e12b262c72b20339c00b520874f00efe9678cf5be7052c920339ac"), "Chu ky request");

        byte[] response = "{\"code\":\"SUCCESS\"}".getBytes(StandardCharsets.UTF_8);
        check(verifyResponse(responseKey, 200, "8c2f0000-0000-4000-8000-000000000002", nonce, "1792574401", sha256Hex(response),
            "ad5353907e543ef9429cfe159bdaf1ded0ae2c37a27208110bbc2a47ef902bac", response), "Chu ky response");
        System.out.println("Vector thu: dat");
    }

    static final HttpClient HTTP = HttpClient.newHttpClient();

    static String send(String baseUrl, String privateKey, String method, String pathAndQuery, String body, String... extraHeaders) throws Exception {
        String server = HTTP.send(HttpRequest.newBuilder(URI.create(baseUrl + "/api/_meta/server")).build(), HttpResponse.BodyHandlers.ofString()).body();
        Matcher m = Pattern.compile("\"serverTime\"\\s*:\\s*(\\d+)").matcher(server);
        check(m.find(), "serverTime");
        byte[] random = new byte[8];
        new SecureRandom().nextBytes(random);
        String nonce = m.group(1) + "-" + hex(random);
        byte[] bytes = body.getBytes(StandardCharsets.UTF_8);

        HttpRequest.Builder builder = HttpRequest.newBuilder(URI.create(baseUrl + pathAndQuery))
            .method(method, HttpRequest.BodyPublishers.ofByteArray(bytes)).header("Content-Type", "application/json");
        for (Map.Entry<String, String> h : signRequest(deriveKey(privateKey, "FAST-API-V3-REQUEST"), bytes, nonce, "").entrySet())
            builder.header(h.getKey(), h.getValue());
        for (int i = 0; i + 1 < extraHeaders.length; i += 2) builder.header(extraHeaders[i], extraHeaders[i + 1]);

        HttpResponse<byte[]> response = HTTP.send(builder.build(), HttpResponse.BodyHandlers.ofByteArray());
        java.util.function.Function<String, String> header = name -> response.headers().firstValue(name).orElse("");
        check(verifyResponse(deriveKey(privateKey, "FAST-API-V3-RESPONSE"), response.statusCode(), header.apply("X-Request-ID"), nonce,
            header.apply("X-Fast-Timestamp"), header.apply("X-Fast-Content-SHA256"), header.apply("X-Fast-Signature"), response.body()),
            "Chu ky response: khong tin noi dung nay");
        return new String(response.body(), StandardCharsets.UTF_8);
    }

    public static void main(String[] args) throws Exception {
        testVector();
        if (args.length == 3) {
            String baseUrl = args[0].replaceAll("/$", ""), user = args[1], privateKey = args[2];
            String tokenResponse = send(baseUrl, privateKey, "POST", "/api/auth/token", "{\"userName\":\"" + user + "\"}");
            Matcher m = Pattern.compile("\"accessToken\"\\s*:\\s*\"([^\"]+)\"").matcher(tokenResponse);
            check(m.find(), "accessToken: " + tokenResponse);
            System.out.println(send(baseUrl, privateKey, "POST", "/api/_meta/ping", "", "Authorization", "Bearer " + m.group(1)));
        }
    }
}

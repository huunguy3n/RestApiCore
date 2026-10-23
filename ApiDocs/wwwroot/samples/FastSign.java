// Fast API - ký request, kiểm chữ ký response (Java 11+, chỉ dùng thư viện chuẩn)
// Chạy: java FastSign.java                                   -> kiểm vector thử
//       java FastSign.java <base_url> <user> <private_key>   -> lấy token rồi gọi _meta/ping
import java.net.URI;
import java.net.URLDecoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.List;
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

    static String hmacHex(byte[] key, String text) throws Exception {
        return hex(hmac(key, text.getBytes(StandardCharsets.UTF_8)));
    }

    /** RFC 3986: giữ A-Z a-z 0-9 - . _ ~, còn lại %XX hex hoa trên byte UTF-8 */
    static String rfc3986(String value) {
        StringBuilder sb = new StringBuilder();
        for (byte b : value.getBytes(StandardCharsets.UTF_8)) {
            char c = (char) (b & 0xff);
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~') sb.append(c);
            else sb.append('%').append(String.format("%02X", b & 0xff));
        }
        return sb.toString();
    }

    static String formDecode(String value) {
        try {
            return URLDecoder.decode(value, StandardCharsets.UTF_8);
        } catch (IllegalArgumentException e) {
            return value.replace('+', ' ');
        }
    }

    static String canonicalQuery(String raw) {
        String query = raw == null ? "" : raw.startsWith("?") ? raw.substring(1) : raw;
        List<String[]> pairs = new ArrayList<>();
        for (String part : query.split("&")) {
            if (part.isEmpty()) continue;
            int i = part.indexOf('=');
            pairs.add(new String[] { rfc3986(formDecode(i < 0 ? part : part.substring(0, i))), rfc3986(formDecode(i < 0 ? "" : part.substring(i + 1))) });
        }
        pairs.sort((a, b) -> a[0].equals(b[0]) ? a[1].compareTo(b[1]) : a[0].compareTo(b[0]));
        List<String> joined = new ArrayList<>();
        for (String[] p : pairs) joined.add(p[0] + "=" + p[1]);
        return String.join("&", joined);
    }

    static Map<String, String> signRequest(byte[] requestKey, String method, String pathAndQuery, byte[] body, long timestamp, String nonce, String idempotencyKey) throws Exception {
        int q = pathAndQuery.indexOf('?');
        String path = q < 0 ? pathAndQuery : pathAndQuery.substring(0, q);
        String contentSha256 = sha256Hex(body);
        String canonical = String.join("\n", "FAST-HMAC-SHA256", method.toUpperCase(), path.toLowerCase(),
            canonicalQuery(q < 0 ? "" : pathAndQuery.substring(q)), Long.toString(timestamp), nonce, idempotencyKey, contentSha256);
        Map<String, String> headers = new LinkedHashMap<>();
        headers.put("X-Fast-Timestamp", Long.toString(timestamp));
        headers.put("X-Fast-Nonce", nonce);
        headers.put("X-Fast-Content-SHA256", contentSha256);
        headers.put("X-Fast-Signature", hmacHex(requestKey, canonical));
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
        Map<String, String> headers = signRequest(requestKey, "POST", "/api/sale-invoice?unitCode=CTY&dryRun=false", body, 1792574400L, "9b2e4c1f0a7d4e3b", "6f1c2d3e-0000-4000-8000-000000000001");
        check(headers.get("X-Fast-Content-SHA256").equals("534275e0a5da4d457614f2f130e8db3dfe3a21cef90e3f5e13ae77c3d60ed12f"), "SHA-256 body");
        check(headers.get("X-Fast-Signature").equals("4588c91a82467e239600b66fd21e08e18f626037cc91ff4197aa9cf137dc81b2"), "Chu ky request");

        byte[] response = "{\"code\":\"SUCCESS\"}".getBytes(StandardCharsets.UTF_8);
        check(verifyResponse(responseKey, 200, "8c2f0000-0000-4000-8000-000000000002", "9b2e4c1f0a7d4e3b", "1792574401", sha256Hex(response),
            "1972c2ae0b53f37dd745becadfd19669a8da4d968fb282891ed43fddec17fd68", response), "Chu ky response");
        System.out.println("Vector thu: dat");
    }

    static final HttpClient HTTP = HttpClient.newHttpClient();

    static String send(String baseUrl, String privateKey, String method, String pathAndQuery, String body, String... extraHeaders) throws Exception {
        String server = HTTP.send(HttpRequest.newBuilder(URI.create(baseUrl + "/api/_meta/server")).build(), HttpResponse.BodyHandlers.ofString()).body();
        Matcher m = Pattern.compile("\"serverTime\"\\s*:\\s*(\\d+)").matcher(server);
        check(m.find(), "serverTime");
        byte[] random = new byte[16];
        new SecureRandom().nextBytes(random);
        String nonce = hex(random);
        byte[] bytes = body.getBytes(StandardCharsets.UTF_8);

        HttpRequest.Builder builder = HttpRequest.newBuilder(URI.create(baseUrl + pathAndQuery))
            .method(method, HttpRequest.BodyPublishers.ofByteArray(bytes)).header("Content-Type", "application/json");
        for (Map.Entry<String, String> h : signRequest(deriveKey(privateKey, "FAST-API-V3-REQUEST"), method, pathAndQuery, bytes, Long.parseLong(m.group(1)), nonce, "").entrySet())
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
            String tokenResponse = send(baseUrl, privateKey, "POST", "/api/auth/token", "{\"userName\":\"" + user + "\"}", "X-Fast-User", user);
            Matcher m = Pattern.compile("\"accessToken\"\\s*:\\s*\"([^\"]+)\"").matcher(tokenResponse);
            check(m.find(), "accessToken: " + tokenResponse);
            System.out.println(send(baseUrl, privateKey, "POST", "/api/_meta/ping", "", "Authorization", "Bearer " + m.group(1)));
        }
    }
}

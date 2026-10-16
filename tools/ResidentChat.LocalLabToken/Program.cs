using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string issuer = "residentchat.synthetic.test";
const string audience = "residentchat.synthetic.operator";
const string tenantAlpha = "11111111-1111-4111-8111-111111111111";
const string tenantBeta = "22222222-2222-4222-8222-222222222222";

if (args.Length != 1 || args[0] is not ("alpha" or "beta"))
{
    Console.Error.WriteLine("SYNTHETIC LAB ONLY: choose alpha or beta");
    return 2;
}

var secret = Environment.GetEnvironmentVariable("RC07_SIGNING_KEY");
if (string.IsNullOrEmpty(secret) || secret.Length < 48 ||
    Encoding.UTF8.GetByteCount(secret) < 48)
{
    Console.Error.WriteLine("RC-07: missing ephemeral signing key");
    return 2;
}

// All identities are imaginary and short-lived. Never use this issuer in
// production; it is generated only in a short-lived disposable offline lab.
var tenant = args[0] == "alpha" ? tenantAlpha : tenantBeta;
var subject = args[0] == "alpha" ? "fictional-operator-alpha" : "fictional-operator-beta";
var expires = DateTimeOffset.UtcNow.AddMinutes(90).ToUnixTimeSeconds();
var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
{
    alg = "HS256", typ = "JWT"
}));
var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
{
    iss = issuer, aud = audience, sub = subject,
    tenant_id = tenant, role = "operator", exp = expires
}));
var signed = header + "." + payload;
var key = Encoding.UTF8.GetBytes(secret);
var bytes = Encoding.ASCII.GetBytes(signed);
try
{
    var mac = HMACSHA256.HashData(key, bytes);
    Console.WriteLine(signed + "." + Base64Url(mac));
    CryptographicOperations.ZeroMemory(mac);
}
finally
{
    CryptographicOperations.ZeroMemory(key);
    CryptographicOperations.ZeroMemory(bytes);
}
return 0;

static string Base64Url(byte[] data) =>
    Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

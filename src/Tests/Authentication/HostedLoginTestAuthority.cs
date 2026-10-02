using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Quaestura.Tests.Authentication;

// Synthetic HTTP authority used by the real Quaestura Host; no production identity data.
internal sealed class HostedLoginTestAuthority : HttpMessageHandler
{
    internal const string Issuer = "https://authority.test";
    internal const string ClientId = "quaestura-hosted-test";
    internal const string Secret = "synthetic-hosted-client-secret-canary";
    internal const string Subject = "synthetic-hosted-admin";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _wrong = RSA.Create(2048);
    internal SecurityKey Key => new RsaSecurityKey(_rsa) { KeyId = "test-key" };
    internal string Origin = Issuer;
    internal string Mode = "admin", Nonce = "", LogoutMode = "success", LastAccessToken = "", LastIdToken = "", LogoutState = "";
    internal int Prepares, Exchanges, DiscoveryRequests;
    internal TaskCompletionSource PrepareStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ExchangeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string Token(string role = "admin", string subject = Subject, string? audience = null)
        => CreateToken(false, "admin", role, subject, audience);

    private string CreateToken(bool id, string mode, string role = "admin", string subject = Subject, string? audience = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["sub"] = !id && mode == "subject-mismatch" ? "synthetic-other" : subject };
        if (id) { claims["nonce"] = mode == "bad-nonce" ? "synthetic-wrong-nonce" : Nonce; claims["name"] = "Synthetic administrator"; }
        else if (mode != "missing-role") claims["role"] = mode == "nonadmin" ? "student" : role;
        if (!id && mode == "missing-subject") claims.Remove("sub");
        if (!id && mode == "empty-subject") claims["sub"] = " ";
        if (!id && mode == "duplicate-subject") claims["sub"] = new[] { subject, subject };
        var encoded = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = !id && mode == "wrong-issuer" ? "https://other.test" : Origin,
            Audience = !id && mode == "wrong-audience" ? "other-client" : audience ?? ClientId,
            Claims = claims, IssuedAt = now.AddMinutes(-2), NotBefore = now.AddMinutes(-2),
            Expires = !id && mode == "expired" ? now.AddMinutes(-1) : now.AddMinutes(15), TokenType = "JWT",
            SigningCredentials = new SigningCredentials(!id && mode == "bad-signature"
                ? new RsaSecurityKey(_wrong) { KeyId = "test-key" } : Key,
                !id && mode == "wrong-algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256)
        });
        if (!id && mode is "missing-issuer" or "duplicate-issuer")
        {
            var segments = encoded.Split('.');
            var payload = JsonNode.Parse(Base64UrlEncoder.Decode(segments[1]))!.AsObject();
            if (mode == "missing-issuer") payload.Remove("iss");
            else payload["iss"] = new JsonArray(Origin, Origin);
            segments[1] = Base64UrlEncoder.Encode(payload.ToJsonString());
            var input = segments[0] + "." + segments[1];
            encoded = input + "." + Base64UrlEncoder.Encode(_rsa.SignData(
                Encoding.UTF8.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        return encoded;
    }

    internal Task<HttpResponseMessage> Forward(HttpRequestMessage request, CancellationToken cancellationToken) => SendAsync(request, cancellationToken);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/openid-configuration")
        {
            Interlocked.Increment(ref DiscoveryRequests);
            if (Mode == "unreachable") throw new HttpRequestException("synthetic-provider-error-canary");
            return Json(new { issuer = Origin, authorization_endpoint = Origin + "/oauth2/authorize", token_endpoint = Origin + "/oauth2/token", jwks_uri = Origin + "/jwks" });
        }
        if (path == "/jwks")
        {
            var key = _rsa.ExportParameters(false);
            return Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = "test-key", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) } } });
        }
        if (path == "/oauth2/token")
        {
            Interlocked.Increment(ref Exchanges);
            ExchangeStarted.TrySetResult();
            if (Mode == "cancel-exchange") await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Mode == "exchange-failed") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            LastAccessToken = CreateToken(false, Mode); LastIdToken = CreateToken(true, Mode);
            return Json(new { access_token = LastAccessToken, id_token = LastIdToken, token_type = "Bearer", expires_in = 3600 });
        }
        if (path == "/oauth2/logout/requests")
        {
            Interlocked.Increment(ref Prepares);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = QueryHelpers.ParseQuery(body);
            LogoutState = form["state"].ToString();
            PrepareStarted.TrySetResult();
            if (LogoutMode == "cancel") await Task.Delay(Timeout.Infinite, cancellationToken);
            if (LogoutMode == "timeout") throw new TaskCanceledException("synthetic-timeout-canary");
            if (LogoutMode == "failed") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Json(new { logout_uri = LogoutMode == "forged" ? "https://evil.test/logout?logout_handle=" + new string('a', 43) : "/oauth2/logout?logout_handle=" + new string('a', 43) });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    // HttpClientFactory owns the handler; tests dispose RSA after the factory stops.
    protected override void Dispose(bool disposing) { if (disposing) { _rsa.Dispose(); _wrong.Dispose(); } base.Dispose(disposing); }
}

internal sealed class HostedLoginTestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan time) => _now += time;
    internal void Reset() => _now = DateTimeOffset.UtcNow;
}

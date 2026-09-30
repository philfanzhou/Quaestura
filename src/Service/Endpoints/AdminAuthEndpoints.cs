using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quaestura.Common.Constants;
using Quaestura.Service.Models;
using Quaestura.Service.Options;

namespace Quaestura.Service.Endpoints;

/// <summary>
/// Admin sign-in through SignaCore.
/// <c>login</c> exchanges username/password for a JWT with the password grant, presenting
/// Quaestura's AppId/AppSecret so SignaCore calls back to <c>callback</c> during issuance.
/// <c>callback</c> returns <c>["admin"]</c> for whitelisted users, which SignaCore embeds
/// as <c>role: admin</c> in the JWT.
/// Passwords, the AppSecret, and access tokens are never logged.
/// </summary>
public static class AdminAuthEndpoints
{
    private const string UnavailableMessage = "Identity service unavailable.";

    private static readonly JsonSerializerOptions SignaCoreJsonOptions = new(JsonSerializerDefaults.Web);

    public static WebApplication MapAdminAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/auth").AllowAnonymous();

        // The two anonymous JSON entry points carry credential-bearing request bodies and return
        // an access token or role decisions, so they are marked for the fixed ServiceMantle
        // security response-header baseline (Cache-Control: no-store and friends). The marking
        // applies to exactly these two endpoints: the rest of /admin and the Vue SPA never
        // receive the API-only Content-Security-Policy.
        group.MapPost("/login", Login).RequireServiceMantleSecurityResponseHeaders();
        group.MapPost("/callback", Callback).RequireServiceMantleSecurityResponseHeaders();

        return app;
    }

    private static async Task<IResult> Login(
        AdminLoginRequest? request,
        IOptions<IdentityServiceClientOptions> identityOptions,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        HttpContext httpContext)
    {
        var logger = loggerFactory.CreateLogger(typeof(AdminAuthEndpoints));

        if (request == null
            || string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return Failure(StatusCodes.Status400BadRequest, "Username and password are required.");
        }

        var options = identityOptions.Value;
        if (string.IsNullOrWhiteSpace(options.Authority)
            || string.IsNullOrWhiteSpace(options.AppId)
            || string.IsNullOrWhiteSpace(options.AppSecret))
        {
            logger.LogWarning("Admin login rejected: IdentityService Authority, AppId, or AppSecret is not configured");
            return Failure(StatusCodes.Status503ServiceUnavailable, "Admin login is not configured.");
        }

        var cancellationToken = httpContext.RequestAborted;
        try
        {
            var client = httpClientFactory.CreateClient(IdentityServiceClientOptions.HttpClientName);

            // SignaCore binds camelCase fields only; "grant_type" would not bind.
            using var tokenRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{options.Authority.TrimEnd('/')}/api/auth/token")
            {
                Content = JsonContent.Create(new
                {
                    grantType = "password",
                    username = request.Username,
                    password = request.Password,
                }),
            };
            tokenRequest.Headers.TryAddWithoutValidation("X-Admin-AppId", options.AppId);
            tokenRequest.Headers.TryAddWithoutValidation("X-Admin-AppSecret", options.AppSecret);

            using var response = await client.SendAsync(tokenRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Admin login failed: identity service returned HTTP {StatusCode}", (int)response.StatusCode);
                return Failure(StatusCodes.Status502BadGateway, UnavailableMessage);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var token = TryParseTokenResponse(body);
            if (token == null)
            {
                logger.LogWarning(
                    "Admin login failed: identity service returned an unreadable response (HTTP {StatusCode})",
                    (int)response.StatusCode);
                return Failure(StatusCodes.Status502BadGateway, UnavailableMessage);
            }

            if (!token.Success)
            {
                var message = string.IsNullOrWhiteSpace(token.Message) ? "Login failed." : token.Message;
                logger.LogWarning("Admin login rejected by identity service: {Message}", message);
                return Failure(StatusCodes.Status400BadRequest, message);
            }

            if (string.IsNullOrEmpty(token.AccessToken))
            {
                logger.LogWarning(
                    "Admin login failed: identity service reported success without an access token (HTTP {StatusCode})",
                    (int)response.StatusCode);
                return Failure(StatusCodes.Status502BadGateway, UnavailableMessage);
            }

            // Refresh token and user info are dropped on purpose; the admin signs in again after expiry.
            return Results.Ok(new
            {
                success = true,
                message = token.Message,
                accessToken = token.AccessToken,
                expiresIn = token.ExpiresIn,
                expiresAt = token.ExpiresAt,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client disconnected; there is nobody to answer.
            return Results.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError("Admin login failed: identity service request error {ExceptionType}", ex.GetType().Name);
            return Failure(StatusCodes.Status502BadGateway, UnavailableMessage);
        }
    }

    private static IResult Callback(
        AdminAuthCallbackRequest? request,
        IOptions<AdminPortalOptions> adminPortalOptions,
        ILoggerFactory loggerFactory)
    {
        var userId = request?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Ok(new AdminAuthCallbackResponse());
        }

        var adminUserIds = adminPortalOptions.Value.AdminUserIds ?? Array.Empty<string>();
        if (adminUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase))
        {
            loggerFactory.CreateLogger(typeof(AdminAuthEndpoints))
                .LogInformation("Identity callback: user {UserId} granted the admin role", userId);
            return Results.Ok(new AdminAuthCallbackResponse { Roles = { RoleConstants.Admin } });
        }

        return Results.Ok(new AdminAuthCallbackResponse());
    }

    private static SignaCoreTokenResponse? TryParseTokenResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SignaCoreTokenResponse>(body, SignaCoreJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Failure(int statusCode, string message) =>
        Results.Json(new { success = false, message }, statusCode: statusCode);

    private sealed class SignaCoreTokenResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? AccessToken { get; set; }
        public long ExpiresIn { get; set; }
        public long ExpiresAt { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Web.Http;
using SignaCore.Client.AspNetCore;

namespace Quaestura.Host.Authentication;

internal sealed class AdminOidcResponseWriter : ISignaCoreHostedLoginResponseWriter
{
    public Task WriteSignInFailureAsync(HttpContext context, SignaCoreSignInReason reason,
        CancellationToken cancellationToken)
    {
        if (TryWriteNavigationFailure(context, reason switch
        {
            SignaCoreSignInReason.AccessDenied => "cancelled",
            SignaCoreSignInReason.AuthorityUnreachable => "provider_unavailable",
            SignaCoreSignInReason.RequiresReauthentication => "requires_reauthentication",
            _ => "signin_failed"
        }, cancellationToken)) return Task.CompletedTask;
        return WriteProblemAsync(context, reason == SignaCoreSignInReason.AuthorityUnreachable ? 503 : 400,
            "signin_failed", "QUAESTURA_OIDC_SIGNIN_FAILED", "Hosted sign-in could not complete.", cancellationToken);
    }

    public Task WriteFailurePageAsync(HttpContext context, SignaCoreSignInReason? reason,
        CancellationToken cancellationToken) => WriteSignInFailureAsync(
            context, reason ?? SignaCoreSignInReason.InvalidResponse, cancellationToken);

    public Task WriteSessionStatusAsync(HttpContext context, SignaCoreSessionStatus status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(status, cancellationToken);
    }

    // Only completed package outcomes on real top-level HTML navigations are projected.
    internal static bool IsHtmlNavigation(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || context.Request.Headers["Sec-Fetch-Mode"] != "navigate"
            || context.Request.Headers["Sec-Fetch-Dest"] != "document") return false;
        var path = context.Request.Path.Value?.TrimEnd('/');
        if (path is null || !(path.Equals(AdminOidcComposition.Prefix + "/start", StringComparison.OrdinalIgnoreCase)
            || path.Equals(AdminOidcComposition.Prefix + "/callback", StringComparison.OrdinalIgnoreCase)
            || path.Equals(AdminOidcComposition.Prefix + "/signin-failed", StringComparison.OrdinalIgnoreCase)
            || path.Equals(AdminOidcComposition.Prefix + "/logout/return", StringComparison.OrdinalIgnoreCase))) return false;
        try { return context.Request.GetTypedHeaders().Accept?.Any(value =>
            string.Equals(value.MediaType.Value, "text/html", StringComparison.OrdinalIgnoreCase) && (value.Quality ?? 1) > 0) == true; }
        catch (FormatException) { return false; }
    }

    internal static bool TryWriteNavigationFailure(HttpContext context, string reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Response.HasStarted || !IsHtmlNavigation(context)) return false;
        context.Response.ContentLength = null;
        context.Response.ContentType = null;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect("/login?reason=" + reason);
        return true;
    }

    internal static Task WriteProblemAsync(HttpContext context, int status, string code, string applicationCode,
        string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.Headers.CacheControl = "no-store";
        context.TryGetServiceMantleCorrelationId(out var correlationId);
        return Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?>
        {
            ["errorCode"] = "quaestura.oidc." + code,
            [QuaesturaProblemDetailsExtensions.ErrorCodeFieldName] = applicationCode,
            ["correlationId"] = correlationId
        }).ExecuteAsync(context);
    }
}

internal sealed class AdminApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult result)
    {
        await _default.HandleAsync(next, context, policy, result);
        // Keep the package's CSRF 403; a missing/expired browser session on a JSON API is 401.
        if (result.Challenged && !context.Response.HasStarted && context.Response.StatusCode == 302
            && context.Request.Path.StartsWithSegments("/admin")
            && !context.Request.Path.StartsWithSegments(AdminOidcComposition.Prefix))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.Remove("Location");
        }
    }
}

internal static class AdminOidcResponseMiddleware
{
    internal static void UseQuaesturaHostedLoginResponses(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AdminOidcConfigurationStatus>().IsConfigured) return;
        app.Use(async (context, next) =>
        {
            var isOidc = context.Request.Path.StartsWithSegments(AdminOidcComposition.Prefix);
            var path = context.Request.Path.Value?.TrimEnd('/');
            if (!isOidc || context.Response.HasStarted) { await next(context); return; }

            // Product route adaptation: never return straight into the hosted-auth routes.
            if (string.Equals(path, AdminOidcComposition.Prefix + "/start", StringComparison.OrdinalIgnoreCase)
                && context.Request.Query.TryGetValue("returnUrl", out var values)
                && values.Count == 1 && Uri.TryCreate(new Uri("https://local.invalid"), values[0], out var target)
                && target.AbsolutePath.StartsWith(AdminOidcComposition.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!AdminOidcResponseWriter.TryWriteNavigationFailure(context, "signin_failed", context.RequestAborted))
                    await AdminOidcResponseWriter.WriteProblemAsync(context, 400, "invalid_return_url",
                        "QUAESTURA_OIDC_INVALID_RETURN_URL", "The return address is not allowed.", context.RequestAborted);
                return;
            }

            var logoutPost = string.Equals(path, AdminOidcComposition.Prefix + "/logout", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(context.Request.Method);
            var logoutReturnNavigation = string.Equals(path, AdminOidcComposition.Prefix + "/logout/return", StringComparison.OrdinalIgnoreCase)
                && AdminOidcResponseWriter.IsHtmlNavigation(context);
            if (!logoutPost && !logoutReturnNavigation)
            {
                try { await next(context); }
                catch (AdminAdmissionDeniedException)
                {
                    if (!AdminOidcResponseWriter.TryWriteNavigationFailure(context, "denied", context.RequestAborted))
                        await AdminOidcResponseWriter.WriteProblemAsync(context, 403, "admin_denied",
                            "QUAESTURA_ADMIN_DENIED", "Administrator access denied.", context.RequestAborted);
                }
                return;
            }

            // Only presentation is buffered, with a fixed limit. Official protocol, cookies, state,
            // revocation, URI checks, and the logout gate have completed before this projection.
            var original = context.Features.Get<IHttpResponseBodyFeature>()!;
            await using var buffer = new BoundedResponseBuffer();
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer));
            try { await next(context); }
            finally { context.Features.Set(original); }
            context.RequestAborted.ThrowIfCancellationRequested();
            if (context.Response.HasStarted) return;

            if (logoutReturnNavigation)
            {
                if (context.Response.StatusCode == 400
                    && AdminOidcResponseWriter.TryWriteNavigationFailure(context, "logout_failed", context.RequestAborted)) return;
                buffer.Position = 0;
                await buffer.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            else if (context.Response.StatusCode == 302 && !string.IsNullOrEmpty(context.Response.Headers.Location))
            {
                var location = context.Response.Headers.Location.ToString();
                context.Response.StatusCode = 200;
                context.Response.Headers.Remove("Location");
                context.Response.ContentLength = null;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new { outcome = "prepared", logoutUrl = location }, context.RequestAborted);
            }
            else if (context.Response.StatusCode == 400 && IsCsrfRejected(buffer))
            {
                context.Response.ContentLength = null;
                await AdminOidcResponseWriter.WriteProblemAsync(context, 403, "csrf_rejected",
                    "QUAESTURA_CSRF_REJECTED", "The request failed antiforgery validation.", context.RequestAborted);
            }
            else
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        });
    }

    private static bool IsCsrfRejected(MemoryStream buffer)
    {
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.TryGetProperty("outcome", out var outcome)
                && outcome.ValueKind == JsonValueKind.String && outcome.GetString() == "csrf_rejected";
        }
        catch (JsonException) { return false; }
    }

    private sealed class BoundedResponseBuffer : MemoryStream
    {
        private const int Limit = 16 * 1024;
        private void EnsureBound(int count)
        {
            if (Position + count > Limit) throw new InvalidOperationException("Hosted logout response exceeded its limit.");
        }
        public override void SetLength(long value)
        {
            if (value > Limit) throw new InvalidOperationException("Hosted logout response exceeded its limit.");
            base.SetLength(value);
        }
        public override void WriteByte(byte value) { EnsureBound(1); base.WriteByte(value); }
        public override void Write(byte[] buffer, int offset, int count)
        { EnsureBound(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { EnsureBound(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { EnsureBound(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { EnsureBound(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quaestura.Common.Constants;
using Quaestura.Service.Models;
using Quaestura.Service.Options;
using ServiceMantle.Web.Http;

namespace Quaestura.Service.Endpoints;

/// <summary>Retired password entry and the independent SignaCore role callback.</summary>
public static class AdminAuthEndpoints
{
    public static WebApplication MapAdminAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/auth").AllowAnonymous();
        // Metadata also selects the existing Bearer handler before the anonymous endpoint,
        // preventing the hosted-cookie antiforgery handler from reading retired form bodies.
        group.MapPost("/login", RetiredPasswordLogin)
            .WithMetadata(new RetiredPasswordLoginMetadata())
            .RequireServiceMantleSecurityResponseHeaders();
        group.MapPost("/callback", Callback).RequireServiceMantleSecurityResponseHeaders();
        return app;
    }

    private static IResult RetiredPasswordLogin(HttpContext context)
    {
        context.TryGetServiceMantleCorrelationId(out var correlationId);
        return Results.Problem(statusCode: StatusCodes.Status410Gone,
            title: "Password sign-in is no longer available.",
            extensions: new Dictionary<string, object?>
            {
                ["errorCode"] = "quaestura.auth.password_login_retired",
                ["quaesturaErrorCode"] = "QUAESTURA_PASSWORD_LOGIN_RETIRED",
                ["correlationId"] = correlationId
            });
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
            // The callback is invoked by the identity service on behalf of an anonymous
            // issuance flow: the user identifier is personal data and is never logged.
            loggerFactory.CreateLogger(typeof(AdminAuthEndpoints))
                .LogInformation("Identity callback: granted the admin role to a whitelisted user");
            return Results.Ok(new AdminAuthCallbackResponse { Roles = { RoleConstants.Admin } });
        }

        return Results.Ok(new AdminAuthCallbackResponse());
    }
}

/// <summary>Marks only the mapped retired password POST; it does not exempt other writes.</summary>
public sealed class RetiredPasswordLoginMetadata { }

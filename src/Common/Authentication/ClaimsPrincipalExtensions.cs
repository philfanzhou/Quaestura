using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Quaestura.Common.Constants;

namespace Quaestura.Common.Authentication;

/// <summary>
/// Extensions for extracting user identity information from a <see cref="ClaimsPrincipal"/>.
/// Centralizes userId/role lookup so endpoints avoid scattered, inconsistent
/// <c>User.FindFirst(...)</c> patterns.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Returns the user id from the JWT, preferring <see cref="ClaimTypes.NameIdentifier"/>
    /// with a fallback to the short "sub" claim. Returns null if not present.
    /// </summary>
    public static string? GetUserId(this ClaimsPrincipal user)
    {
        if (user is null) return null;
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? user.FindFirst("sub")?.Value;
    }

    /// <summary>
    /// Returns the user id, throwing <see cref="UnauthorizedAccessException"/> if missing.
    /// Use this in endpoints where authentication is guaranteed by [Authorize].
    /// </summary>
    public static string GetRequiredUserId(this ClaimsPrincipal user)
    {
        var userId = user.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException("User id is missing from the security context");
        }
        return userId;
    }

    /// <summary>
    /// Returns all roles from the JWT, reading both <see cref="ClaimTypes.Role"/>
    /// and the short "role" claim. Duplicates are removed.
    /// </summary>
    public static List<string> GetRoles(this ClaimsPrincipal user)
    {
        if (user is null) return new List<string>();
        return user.FindAll(ClaimTypes.Role)
            .Concat(user.FindAll("role"))
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns true if the user has any of the specified roles (case-insensitive).
    /// Note: we intentionally do not provide an <c>IsInRole(string)</c> extension because
    /// <see cref="ClaimsPrincipal"/> already declares an instance method with that signature,
    /// which would shadow the extension. Use <see cref="IsInAnyRole"/> or <see cref="IsStaff"/>.
    /// </summary>
    public static bool IsInAnyRole(this ClaimsPrincipal user, params string[] roles)
    {
        if (user is null || roles is null || roles.Length == 0) return false;
        var userRoles = user.GetRoles();
        if (userRoles.Count == 0) return false;
        return roles.Any(r => userRoles.Any(ur =>
            string.Equals(ur, r, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Returns true if the user is a staff member (teacher/assistant/admin).
    /// Equivalent to <c>IsInAnyRole(RoleConstants.StaffRoles)</c>.
    /// Students return false.
    /// </summary>
    public static bool IsStaff(this ClaimsPrincipal user)
    {
        return user.IsInAnyRole(RoleConstants.StaffRoles);
    }
}

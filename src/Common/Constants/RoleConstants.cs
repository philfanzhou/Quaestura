namespace Quaestura.Common.Constants;

/// <summary>
/// JWT role claim string constants.
/// Roles are returned by each Portal's /api/auth/callback endpoint to Identity,
/// which transparently includes them in the JWT "role" claim (one claim per role).
/// Comparison must be case-insensitive (OrdinalIgnoreCase).
/// </summary>
public static class RoleConstants
{
    /// <summary>Student role. Tag consumers only — cannot CRUD tags.</summary>
    public const string Student = "student";

    /// <summary>Teacher role. Can CRUD own tags.</summary>
    public const string Teacher = "teacher";

    /// <summary>Assistant (teaching assistant) role. Can CRUD own tags.</summary>
    public const string Assistant = "assistant";

    /// <summary>Administrator role. Reserved — admin portal uses Cookie auth, not JWT.</summary>
    public const string Admin = "admin";

    /// <summary>
    /// Staff roles that can perform teacher-level operations (tag CRUD, etc.).
    /// Excludes Student.
    /// </summary>
    public static readonly string[] StaffRoles = { Teacher, Assistant, Admin };
}

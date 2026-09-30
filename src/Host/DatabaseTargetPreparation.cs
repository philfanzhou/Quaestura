using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;

namespace Quaestura.Host;

/// <summary>
/// A safe startup failure of the database target preparation stage. Messages and error codes never
/// contain connection strings, credentials, or other secret values.
/// </summary>
public sealed class DatabaseTargetPreparationException : Exception
{
    /// <summary>Creates the exception with a stable, secret-free error code.</summary>
    /// <param name="errorCode">The safe error code describing the refusal.</param>
    /// <param name="message">The human-readable, secret-free refusal reason.</param>
    public DatabaseTargetPreparationException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the stable, secret-free error code describing the refusal.
    /// </summary>
    public string ErrorCode { get; }
}

/// <summary>
/// Creates the PostgreSQL target database when — and only when — it is verifiably missing and
/// explicitly allowed, before the relational table initialization runs. This adapter owns the
/// enablement decision and configuration; the observation, server-identity verification, and
/// creation semantics belong to the shared ServiceMantle provider.
/// </summary>
/// <remarks>
/// Contract (fixed by issue #41):
/// <list type="bullet">
/// <item>A connectable existing target is used as-is: no maintenance-database connection, no
/// CREATE, and no CREATEDB or postgres-database privilege is required.</item>
/// <item>A verifiably missing target is created only when <c>Database:AllowCreate</c> (default
/// <c>true</c>) permits it, using the shared provider with a fixed 30-second preparation budget.
/// The maintenance connection is a copy of the original configuration with only the database
/// changed to <c>postgres</c>; no additional or persisted administrative secret exists.</item>
/// <item>Unreachable servers, authentication failures, permission failures, and owner or
/// server-identity conflicts are refused; they are never reinterpreted as a missing database and
/// never answered with a creation fallback. An existing database is never dropped, overwritten,
/// or re-owned.</item>
/// <item>After a successful preparation the target must observe as connectable again with its own
/// identity before the table initialization may start.</item>
/// <item>Cancellation or timeout stops startup before any table initialization; an already
/// created empty database is kept for the next run and never deleted as compensation.</item>
/// </list>
/// </remarks>
public static class QuaesturaDatabaseTargetPreparer
{
    /// <summary>
    /// The configuration key (environment form <c>Database__AllowCreate</c>) that enables or
    /// disables creation of a missing target database. Defaults to <c>true</c>, preserving the
    /// historical automatic-creation behavior.
    /// </summary>
    public const string AllowCreateConfigurationKey = "Database:AllowCreate";

    /// <summary>
    /// The fixed preparation budget. There is deliberately no configuration key for it.
    /// </summary>
    internal static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(30);

    internal const string CreationNotAllowedErrorCode = "database_target_preparation.creation_not_allowed";
    internal const string InvalidConfigurationErrorCode = "database_target_preparation.invalid_configuration";
    internal const string PostPreparationUnreachableErrorCode = "database_target_preparation.post_preparation_unreachable";

    /// <summary>
    /// Prepares the PostgreSQL target database with the real shared provider.
    /// </summary>
    /// <param name="configuration">The effective host configuration.</param>
    /// <param name="connectionString">The composed target connection string, or null when none resolved.</param>
    /// <param name="logger">The startup logger; no connection value is ever logged.</param>
    /// <param name="cancellationToken">Observed throughout; cancellation stops startup before table initialization.</param>
    /// <exception cref="DatabaseTargetPreparationException">The target must not or could not be prepared.</exception>
    /// <exception cref="OperationCanceledException">Preparation was cancelled.</exception>
    public static Task PrepareAsync(
        IConfiguration configuration,
        string? connectionString,
        ILogger logger,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(
            new PostgreSqlDatabaseTargetPreparationProvider(),
            configuration,
            connectionString,
            logger,
            cancellationToken);

    /// <summary>
    /// Test seam: runs the same preparation flow against an injected provider.
    /// </summary>
    internal static async Task PrepareAsync(
        IDatabaseTargetPreparationProvider provider,
        IConfiguration configuration,
        string? connectionString,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Nothing to prepare. The subsequent table initialization fails on the missing
            // connection exactly as it did before this stage existed.
            logger.LogWarning(
                "No database connection string was resolved; skipping database target preparation");
            return;
        }

        var allowCreate = ReadAllowCreate(configuration);
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            serverVersion: null,
            connectionString);

        var observation = await provider.ObserveAsync(target, cancellationToken).ConfigureAwait(false);
        switch (observation.Status)
        {
            case DatabaseTargetObservationStatus.TargetConnectable:
                // The existing target is used as-is: no maintenance connection and no CREATE.
                logger.LogInformation(
                    "Database target is connectable; skipping creation (existing databases are never modified)");
                return;

            case DatabaseTargetObservationStatus.TargetMissing:
                if (!allowCreate)
                {
                    throw new DatabaseTargetPreparationException(
                        CreationNotAllowedErrorCode,
                        $"The target database is missing and {AllowCreateConfigurationKey} is false; " +
                        "refusing to start. Create the database manually or enable creation.");
                }

                await CreateMissingTargetAsync(provider, target, connectionString, logger, cancellationToken)
                    .ConfigureAwait(false);
                return;

            default:
                // ServerUnreachable and TargetUnreachable (authentication, permission, or identity
                // conflicts) are refusals, never a missing-database signal with a creation fallback.
                throw new DatabaseTargetPreparationException(
                    observation.ErrorCode ?? PostPreparationUnreachableErrorCode,
                    $"The database target could not be used (status {observation.Status}" +
                    (observation.ErrorCode is null ? string.Empty : $", error {observation.ErrorCode}") +
                    "); refusing to start without a creation fallback.");
        }
    }

    private static async Task CreateMissingTargetAsync(
        IDatabaseTargetPreparationProvider provider,
        BootstrapDatabaseConfiguration target,
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The maintenance connection is a copy of the original configuration with only the
        // database changed to "postgres": the same user, host, port, and credentials. No
        // additional administrative secret is introduced, and the value stays in memory.
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
        };
        var request = new DatabaseTargetPreparationRequest(target, maintenanceBuilder.ConnectionString);

        var result = await provider
            .PrepareAsync(request, PreparationTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new DatabaseTargetPreparationException(
                result.ErrorCode ?? WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed,
                $"Database target preparation failed (error {result.ErrorCode}); refusing to start.");
        }

        logger.LogInformation(
            "Database target preparation succeeded with outcome {Outcome}", result.Outcome);

        // Preparation alone is not trusted: the target must be connectable again with its own
        // identity before the table initialization starts.
        var reobservation = await provider.ObserveAsync(target, cancellationToken).ConfigureAwait(false);
        if (!reobservation.IsTargetConnectable)
        {
            throw new DatabaseTargetPreparationException(
                reobservation.ErrorCode ?? PostPreparationUnreachableErrorCode,
                $"The prepared database target is still not connectable (status {reobservation.Status}" +
                (reobservation.ErrorCode is null ? string.Empty : $", error {reobservation.ErrorCode}") +
                "); refusing to start the table initialization.");
        }
    }

    /// <summary>
    /// Reads the creation switch. A missing value keeps the historical default (<c>true</c>);
    /// an unparsable value fails startup instead of guessing.
    /// </summary>
    internal static bool ReadAllowCreate(IConfiguration configuration)
    {
        var raw = configuration[AllowCreateConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (bool.TryParse(raw, out var allowCreate))
        {
            return allowCreate;
        }

        throw new DatabaseTargetPreparationException(
            InvalidConfigurationErrorCode,
            $"{AllowCreateConfigurationKey} must be 'true' or 'false' but was '{raw}'; refusing to start.");
    }
}

using System;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;
using ServiceMantle.Migration;

namespace Quaestura.Host;

/// <summary>Maps the existing consumer configuration to the shared startup gate inputs.</summary>
public static class QuaesturaStartupDatabaseOptions
{
    public const string AllowCreateConfigurationKey = "Database:AllowCreate";

    public static StartupDatabaseGateOptions Create(IConfiguration configuration, string? connectionString)
    {
        // Validate before logging or I/O. Never retain a driver exception or submitted value.
        var allowCreate = ReadAllowCreate(configuration);
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString ?? string.Empty);
            if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database) ||
                string.IsNullOrWhiteSpace(builder.Username))
                throw InvalidTarget();
            return PostgreSqlStartupDatabaseGateOptions.Create(
                new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.PostgreSql, null, builder.ConnectionString),
                allowTargetCreation: allowCreate);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw InvalidTarget();
        }
    }

    internal static bool ReadAllowCreate(IConfiguration configuration)
    {
        var raw = configuration[AllowCreateConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (bool.TryParse(raw, out var result)) return result;
        throw InvalidTarget();
    }

    private static InvalidOperationException InvalidTarget() => new(
        $"Database startup configuration was rejected ({WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget}).");
}

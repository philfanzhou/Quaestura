using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Migration;
using ServiceMantle.Persistence.Relational.Migration;
using SharedSchemaSnapshot = ServiceMantle.Migration.SchemaSnapshot;

namespace Quaestura.Database;

/// <summary>
/// The consuming-service migration executor for the Quaestura question catalog database.
/// </summary>
/// <remarks>
/// <para>
/// <c>InspectAsync</c> is strictly read-only and classifies the target database against the two
/// known migration versions (<c>20260504115924_InitialCreate</c> and <c>20260926094240_AddTags</c>):
/// </para>
/// <list type="bullet">
/// <item>Empty: no business tables and no applied history (a missing target database also classifies
/// as Empty; creating it remains EF Core <c>Migrate</c>'s existing job, unchanged from before).</item>
/// <item>PendingMigration: the applied history is a known ordered prefix and the schema verifiably
/// matches that version, including the limited legacy takeover of an EnsureCreated database whose
/// initial four tables match exactly while its history is absent or empty.</item>
/// <item>CurrentVersionCompatible: the full known history is applied and the complete current
/// schema verifies; nothing will be written.</item>
/// <item>VersionTooNew: the history contains migration ids this application does not know.</item>
/// <item>InspectionFailed: any unknown or partially matching structure (missing/extra columns,
/// wrong types or nullability, missing/renamed-semantics PK/FK/indexes, partial table sets, or a
/// history that claims a version the schema contradicts). Unknown state is never stamped,
/// repaired, or silently accepted.</item>
/// </list>
/// <para>
/// <c>ExecuteAsync</c> re-verifies first, stamps only the verified InitialCreate baseline for a
/// legacy takeover in its own parameterized transaction, and then lets EF Core run the remaining
/// migrations in EF's own transactions. Cancellation is observed between and inside phases; an
/// already-committed baseline legitimately survives cancellation and is re-verified on the next
/// run. Cross-process serialization is deliberately not handled here (tracked separately).
/// </para>
/// <para>
/// All diagnostics are secret-safe: results, reasons, log messages, and exceptions carry schema
/// identifiers and migration ids only, never connection values.
/// </para>
/// </remarks>
public sealed class QuaesturaMigrationExecutor : IDatabaseMigrationExecutor
{
    internal const string InitialCreateMigrationId = "20260504115924_InitialCreate";
    internal const string AddTagsMigrationId = "20260926094240_AddTags";
    internal const string HistoryTableName = "__EFMigrationsHistory";

    /// <summary>The ordered migration contract this executor supports; frozen by the issue.</summary>
    internal static readonly IReadOnlyList<string> KnownMigrationIds =
        [InitialCreateMigrationId, AddTagsMigrationId];

    /// <summary>Business tables created by <see cref="InitialCreateMigrationId"/>.</summary>
    internal static readonly IReadOnlyList<string> InitialTableNames =
        ["knowledge", "question", "question_content", "question_knowledge"];

    /// <summary>Business tables created by <see cref="AddTagsMigrationId"/>.</summary>
    internal static readonly IReadOnlyList<string> TagTableNames = ["tag", "question_tag"];

    /// <summary>All six business tables of the supported contract.</summary>
    internal static readonly IReadOnlyList<string> KnownTableNames =
        InitialTableNames.Concat(TagTableNames).ToList();

    private const string SchemaName = "public";
    private const string InvalidCatalogSqlState = "3D000";

    private readonly QuaesturaDbContext _context;
    private readonly ILogger? _logger;
    private readonly Func<CancellationToken, Task>? _afterInitialBaselineCommitted;

    /// <summary>
    /// Creates the executor for the given context.
    /// </summary>
    /// <param name="context">The EF Core context of the target database.</param>
    /// <param name="logger">Optional logger; messages never contain connection values.</param>
    public QuaesturaMigrationExecutor(QuaesturaDbContext context, ILogger? logger = null)
        : this(context, logger, afterInitialBaselineCommitted: null)
    {
    }

    /// <summary>
    /// Test seam: invoked with the cancellation token right after the legacy InitialCreate
    /// baseline transaction committed and before EF Core runs the remaining migrations.
    /// </summary>
    internal QuaesturaMigrationExecutor(
        QuaesturaDbContext context,
        ILogger? logger,
        Func<CancellationToken, Task>? afterInitialBaselineCommitted)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _logger = logger;
        _afterInitialBaselineCommitted = afterInitialBaselineCommitted;
    }

    /// <inheritdoc />
    public async ValueTask<MigrationObservationState> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        return inspection.State;
    }

    /// <summary>
    /// Read-only inspection that also returns the safe classification reason for diagnostics.
    /// </summary>
    internal async Task<MigrationInspection> InspectDetailedAsync(CancellationToken cancellationToken)
    {
        EnsureMigrationContract();

        try
        {
            await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
            when (string.Equals(exception.SqlState, InvalidCatalogSqlState, StringComparison.Ordinal))
        {
            // The target database does not exist yet. Exactly like the previous initializer, the
            // empty state lets EF Core Migrate create the database and apply every migration.
            return MigrationInspection.Of(
                MigrationObservationState.Empty,
                "the target database does not exist yet");
        }
        catch (Exception exception) when (IsCancellation(exception, cancellationToken))
        {
            throw NewCancellation(exception, cancellationToken);
        }
        catch (Exception exception)
        {
            // Authentication, network, and permission failures are refused, never interpreted as
            // a missing database and never retried with creation fallbacks.
            _logger?.LogWarning(
                exception,
                "Database inspection could not connect to the target database; refusing to classify it");
            return MigrationInspection.Of(
                MigrationObservationState.InspectionFailed,
                "the target database connection could not be established (see service logs for the driver error)");
        }

        try
        {
            return await InspectOpenDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCancellation(exception, cancellationToken))
        {
            throw NewCancellation(exception, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Database inspection could not read the target structure");
            return MigrationInspection.Of(
                MigrationObservationState.InspectionFailed,
                "the database structure could not be read (see service logs for the driver error)");
        }
        finally
        {
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        EnsureMigrationContract();

        // Re-verify immediately before any write: the classification that got this call scheduled
        // may be stale, and duplicate runs must re-read the legitimate state instead of
        // double-stamping.
        var inspection = await InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        switch (inspection.State)
        {
            case MigrationObservationState.CurrentVersionCompatible:
                _logger?.LogInformation(
                    "Database is already compatible with the current version; nothing to execute");
                return;

            case MigrationObservationState.Empty:
                _logger?.LogInformation(
                    "Applying all known migrations to the empty database state ({Reason})",
                    inspection.Reason);
                cancellationToken.ThrowIfCancellationRequested();
                await _context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                return;

            case MigrationObservationState.PendingMigration:
                if (inspection.RequiresInitialBaseline)
                {
                    _logger?.LogInformation(
                        "Taking over a verified legacy database: registering only the {MigrationId} baseline, then executing the remaining migrations",
                        InitialCreateMigrationId);
                    await StampInitialBaselineAsync(cancellationToken).ConfigureAwait(false);
                    if (_afterInitialBaselineCommitted is not null)
                    {
                        await _afterInitialBaselineCommitted(cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger?.LogInformation(
                        "Executing the remaining migrations for the verified history prefix");
                }

                cancellationToken.ThrowIfCancellationRequested();
                await _context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                return;

            default:
                throw new InvalidOperationException(
                    $"Database migration was requested but the target state is {inspection.State}: " +
                    $"{inspection.Reason}. Refusing to execute; no changes were made.");
        }
    }

    // ---------- inspection ----------

    private async Task<MigrationInspection> InspectOpenDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var expected = BuildExpectedTables();

        var tableNames = await QueryTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        var presentBusinessTables = KnownTableNames.Where(tableNames.Contains).ToList();
        var applied = tableNames.Contains(HistoryTableName)
            ? await QueryAppliedMigrationIdsAsync(connection, cancellationToken).ConfigureAwait(false)
            : [];

        var unknownIds = applied.Where(id => !KnownMigrationIds.Contains(id)).ToList();
        if (unknownIds.Count > 0)
        {
            return MigrationInspection.Of(
                MigrationObservationState.VersionTooNew,
                $"the migration history contains ids this application does not know: {Join(unknownIds)}");
        }

        if (!IsKnownOrderedPrefix(applied, out var prefixLength))
        {
            return MigrationInspection.Failed(
                $"the migration history [{Join(applied)}] is not a known ordered prefix of [{Join(KnownMigrationIds)}]");
        }

        var snapshot = await ReadSchemaSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);

        if (prefixLength == KnownMigrationIds.Count)
        {
            // The history claims the current version; the complete current schema must verify.
            var mismatch = ValidateTables(snapshot, expected, KnownTableNames);
            return mismatch is null
                ? MigrationInspection.Of(
                    MigrationObservationState.CurrentVersionCompatible,
                    "the full known history is applied and the current schema verifies")
                : MigrationInspection.Failed(
                    $"the history claims the current version but the schema does not verify: {mismatch}");
        }

        if (presentBusinessTables.Count == 0)
        {
            // No business tables at all (history absent or empty): EF runs every migration.
            return MigrationInspection.Of(
                MigrationObservationState.Empty,
                "no business tables and no applied migrations are present");
        }

        if (prefixLength == 0 &&
            !InitialTableNames.All(presentBusinessTables.Contains))
        {
            // Partial old tables without history are refused; this is exactly the historical
            // question(id)-only reproduction that must never be stamped again.
            return MigrationInspection.Failed(
                $"business tables are partially present without migration history: found [{Join(presentBusinessTables)}]");
        }

        // Verify the initial four tables against the InitialCreate version.
        var initialMismatch = ValidateTables(snapshot, expected, InitialTableNames);
        if (initialMismatch is not null)
        {
            return MigrationInspection.Failed(
                $"the tables of the {InitialCreateMigrationId} version do not verify: {initialMismatch}");
        }

        var presentTagTables = TagTableNames.Where(tableNames.Contains).ToList();
        if (presentTagTables.Count == 1)
        {
            return MigrationInspection.Failed(
                $"the tag tables are partially present: found [{Join(presentTagTables)}]");
        }

        if (presentTagTables.Count == 2)
        {
            var tagMismatch = ValidateTables(snapshot, expected, TagTableNames);
            if (tagMismatch is not null)
            {
                return MigrationInspection.Failed(
                    $"the tables of the {AddTagsMigrationId} version do not verify: {tagMismatch}");
            }
        }

        // A verified legacy database without history is taken over by registering only the
        // InitialCreate baseline; AddTags then really executes (its DDL is idempotent) and EF
        // records it. Business data is preserved throughout.
        return MigrationInspection.Of(
            MigrationObservationState.PendingMigration,
            prefixLength == 0
                ? "a verified legacy database without history requires the InitialCreate baseline and the remaining migrations"
                : "the applied history is a verified prefix and migrations are pending",
            requiresInitialBaseline: prefixLength == 0);
    }

    private void EnsureMigrationContract()
    {
        var assemblyMigrations = _context.Database.GetMigrations().ToList();
        if (!assemblyMigrations.SequenceEqual(KnownMigrationIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"The migration assembly contains [{string.Join(", ", assemblyMigrations)}] but this executor " +
                $"only supports [{string.Join(", ", KnownMigrationIds)}]. Adding or changing migrations requires " +
                "extending the executor's known-version contract.");
        }
    }

    private static bool IsKnownOrderedPrefix(IReadOnlyList<string> applied, out int prefixLength)
    {
        prefixLength = applied.Count;
        for (var i = 0; i < applied.Count; i++)
        {
            if (i >= KnownMigrationIds.Count ||
                !string.Equals(applied[i], KnownMigrationIds[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<HashSet<string>> QueryTableNamesAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            """;
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<List<string>> QueryAppliedMigrationIdsAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""SELECT "MigrationId" FROM "{HistoryTableName}" ORDER BY "MigrationId" """;
        var ids = new List<string>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<SchemaSnapshot> ReadSchemaSnapshotAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        var tables = KnownTableNames.ToArray();
        var snapshot = new SchemaSnapshot();

        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = """
                SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_attribute a ON a.attrelid = c.oid
                WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname = ANY(@tables)
                  AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY c.relname, a.attnum
                """;
            AddTableArrayParameter(columns, tables);
            await using var reader = await columns
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                snapshot.AddColumn(
                    reader.GetString(0),
                    new ActualColumn(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetBoolean(3)));
            }
        }

        await using (var constraints = connection.CreateCommand())
        {
            constraints.CommandText = """
                SELECT c.relname, con.conname, con.contype::text, con.confdeltype::text, fc.relname,
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum),
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(con.confkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum)
                FROM pg_constraint con
                JOIN pg_class c ON c.oid = con.conrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_class fc ON fc.oid = con.confrelid
                WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
                  AND con.contype = ANY(ARRAY['p', 'f']::"char"[])
                ORDER BY c.relname, con.conname
                """;
            AddTableArrayParameter(constraints, tables);
            await using var reader = await constraints
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                var name = reader.GetString(1);
                var kind = reader.GetString(2);
                var deleteRule = reader.IsDBNull(3) ? null : reader.GetString(3);
                var principalTable = reader.IsDBNull(4) ? null : reader.GetString(4);
                var columns = ReadTextArray(reader, 5) ?? [];
                var principalColumns = ReadTextArray(reader, 6) ?? [];
                if (kind == "p")
                {
                    snapshot.SetPrimaryKey(table, new ActualConstraint(name, columns));
                }
                else
                {
                    snapshot.AddForeignKey(table, new ActualForeignKey(
                        name, columns, principalTable ?? string.Empty, principalColumns, deleteRule ?? "a"));
                }
            }
        }

        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText = """
                SELECT c.relname, ic.relname, i.indisunique, array_length(i.indkey, 1),
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum)
                FROM pg_index i
                JOIN pg_class ic ON ic.oid = i.indexrelid
                JOIN pg_class c ON c.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
                  AND NOT i.indisprimary
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid
                  )
                ORDER BY c.relname, ic.relname
                """;
            AddTableArrayParameter(indexes, tables);
            await using var reader = await indexes
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var keyCount = reader.IsDBNull(3) ? -1 : reader.GetInt32(3);
                var indexColumns = ReadTextArray(reader, 4) ?? [];
                snapshot.AddIndex(
                    reader.GetString(0),
                    new ActualIndex(
                        reader.GetString(1),
                        indexColumns,
                        reader.GetBoolean(2),
                        keyCount));
            }
        }

        return snapshot;
    }

    private static void AddTableArrayParameter(DbCommand command, string[] tables)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tables";
        parameter.Value = tables;
        command.Parameters.Add(parameter);
    }

    private static string[]? ReadTextArray(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<string[]>(ordinal);

    // ---------- shared expectation plus consumer-owned names ----------

    private IReadOnlyDictionary<string, ExpectedTable> BuildExpectedTables()
    {
        var structures = EfCoreExpectedSchemaDerivation.Derive(_context.Model).Tables
            .ToDictionary(table => table.Name, StringComparer.Ordinal);
        var expected = new Dictionary<string, ExpectedTable>(StringComparer.Ordinal);
        foreach (var table in _context.Model.GetRelationalModel().Tables)
        {
            if (!KnownTableNames.Contains(table.Name))
            {
                throw new InvalidOperationException(
                    $"The EF model contains table '{table.Name}' outside the supported migration contract.");
            }

            var derived = structures[table.Name];
            // Preserve the existing comparison dimensions: the model and database use public,
            // while identity generation and stored defaults were never inspection guarantees.
            var structure = new SchemaTable(table.Name,
                derived.Columns.Select(column => new SchemaColumn(
                    column.Name, column.DataType, column.IsNullable)).ToList(),
                derived.PrimaryKey,
                derived.ForeignKeys.Select(key => new SchemaForeignKey(
                    key.Columns, key.ReferencedTable, key.ReferencedColumns, key.DeleteRule,
                    SchemaName)).ToList(),
                derived.Indexes, SchemaName);
            expected[table.Name] = new ExpectedTable(table.Name, structure, table.PrimaryKey?.Name,
                table.ForeignKeyConstraints.Select((key, index) =>
                    new ExpectedForeignKey(key.Name, structure.ForeignKeys[index])).ToList(),
                table.Indexes.Select((index, position) =>
                    new ExpectedIndex(index.Name, structure.Indexes[position])).ToList());
        }

        foreach (var tableName in KnownTableNames.Where(name => !expected.ContainsKey(name)))
        {
            throw new InvalidOperationException(
                $"The EF model does not contain the contract table '{tableName}'.");
        }

        return expected;
    }

    // ---------- validation ----------

    private static string? ValidateTables(
        SchemaSnapshot snapshot,
        IReadOnlyDictionary<string, ExpectedTable> expected,
        IEnumerable<string> tableNames)
    {
        foreach (var tableName in tableNames)
        {
            var mismatch = ValidateTable(snapshot, expected[tableName]);
            if (mismatch is not null)
            {
                return mismatch;
            }
        }

        return null;
    }

    private static string? ValidateTable(SchemaSnapshot snapshot, ExpectedTable expected)
    {
        if (!snapshot.TableExists(expected.Name) || snapshot.GetColumns(expected.Name).Count == 0)
        {
            return $"table '{expected.Name}' is missing";
        }

        var differences = SchemaEvidenceComparer.Compare(
            new SharedSchemaSnapshot([snapshot.ToSharedTable(expected.Name)]),
            new ExpectedSchema([expected.Structure]));
        var columnMismatch = differences.FirstOrDefault(difference => difference.Kind is
            SchemaDifferenceKind.MissingColumn or SchemaDifferenceKind.ExtraColumn or
            SchemaDifferenceKind.ColumnTypeMismatch or SchemaDifferenceKind.ColumnNullabilityMismatch);
        if (columnMismatch is not null)
        {
            return columnMismatch.ToString();
        }

        if (expected.PrimaryKeyName is not null)
        {
            var actualPrimaryKey = snapshot.GetPrimaryKey(expected.Name);
            if (actualPrimaryKey is null)
            {
                return $"table '{expected.Name}' is missing its primary key";
            }

            if (!NameMatches(actualPrimaryKey.Name, expected.PrimaryKeyName))
            {
                return $"table '{expected.Name}' has primary key '{actualPrimaryKey.Name}' but the supported version requires '{expected.PrimaryKeyName}'";
            }
        }

        var primaryKeyMismatch = differences.FirstOrDefault(difference =>
            difference.Kind == SchemaDifferenceKind.PrimaryKeyMismatch);
        if (primaryKeyMismatch is not null)
        {
            return primaryKeyMismatch.ToString();
        }

        var actualForeignKeys = snapshot.GetForeignKeys(expected.Name);
        foreach (var foreignKey in expected.ForeignKeys)
        {
            var actual = actualForeignKeys.FirstOrDefault(candidate =>
                NameMatches(candidate.Name, foreignKey.Name));
            if (actual is null)
            {
                return $"table '{expected.Name}' is missing foreign key '{foreignKey.Name}'";
            }

            if (!actual.Columns.SequenceEqual(foreignKey.Shape.Columns, StringComparer.Ordinal) ||
                !string.Equals(actual.PrincipalTable, foreignKey.Shape.ReferencedTable, StringComparison.Ordinal) ||
                !actual.PrincipalColumns.SequenceEqual(foreignKey.Shape.ReferencedColumns, StringComparer.Ordinal) ||
                SchemaSnapshot.MapDeleteRule(actual.DeleteRule) != foreignKey.Shape.DeleteRule)
            {
                return $"foreign key '{foreignKey.Name}' on table '{expected.Name}' does not match the supported version's columns, reference, or delete behavior";
            }
        }

        if (actualForeignKeys.Count != expected.ForeignKeys.Count)
        {
            return $"table '{expected.Name}' contains a foreign key outside the supported version";
        }

        var actualIndexes = snapshot.GetIndexes(expected.Name);
        foreach (var index in expected.Indexes)
        {
            var actual = actualIndexes.FirstOrDefault(candidate => NameMatches(candidate.Name, index.Name));
            if (actual is null)
            {
                return $"table '{expected.Name}' is missing index '{index.Name}'";
            }

            // Expression or predicate variants keep their declared key count; a column count that
            // does not cover it means the index is not the plain column index the model requires.
            if (actual.IsUnique != index.Shape.IsUnique ||
                actual.ResolvedColumns.Length != index.Shape.Columns.Count ||
                actual.KeyCount != index.Shape.Columns.Count ||
                !actual.ResolvedColumns.SequenceEqual(index.Shape.Columns, StringComparer.Ordinal))
            {
                return $"index '{index.Name}' on table '{expected.Name}' does not match the supported version's columns or uniqueness";
            }
        }

        if (actualIndexes.Count != expected.Indexes.Count)
        {
            return $"table '{expected.Name}' contains an index outside the supported version";
        }

        return differences.FirstOrDefault()?.ToString();
    }

    /// <summary>
    /// Known constraint and index names are compared case-insensitively; the semantics
    /// (columns, uniqueness, references, delete behavior) must still match exactly.
    /// </summary>
    private static bool NameMatches(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    // ---------- execution ----------

    private async Task StampInitialBaselineAsync(CancellationToken cancellationToken)
    {
        await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await new EfCoreMigrationBaselineWriter(_context).WriteBaselineAsync(
                _context.Database.GetDbConnection(), InitialCreateMigrationId, EfProductVersion,
                cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation(
                "Registered the verified {MigrationId} baseline for the legacy database",
                InitialCreateMigrationId);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string EfProductVersion { get; } =
        typeof(DbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(DbContext).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    // ---------- safe diagnostics ----------

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested || exception is OperationCanceledException;

    private static OperationCanceledException NewCancellation(
        Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException canceled
            ? canceled
            : new OperationCanceledException(
                "Database migration work was cancelled.", cancellationToken);
}

/// <summary>
/// The safe outcome of one read-only inspection: the shared observation state plus a diagnostic
/// reason that never contains connection values, and whether a legacy takeover must register the
/// InitialCreate baseline before EF Core runs the remaining migrations.
/// </summary>
internal sealed record MigrationInspection(
    MigrationObservationState State,
    string Reason,
    bool RequiresInitialBaseline)
{
    internal static MigrationInspection Of(
        MigrationObservationState state,
        string reason,
        bool requiresInitialBaseline = false) =>
        new(state, reason, requiresInitialBaseline);

    internal static MigrationInspection Failed(string reason) =>
        new(MigrationObservationState.InspectionFailed, reason, false);
}

// ---------- shapes ----------

internal sealed record ExpectedForeignKey(string Name, SchemaForeignKey Shape);

internal sealed record ExpectedIndex(string Name, SchemaIndex Shape);

internal sealed record ExpectedTable(
    string Name,
    SchemaTable Structure,
    string? PrimaryKeyName,
    IReadOnlyList<ExpectedForeignKey> ForeignKeys,
    IReadOnlyList<ExpectedIndex> Indexes);

internal sealed record ActualColumn(string Name, string Type, bool NotNull);

internal sealed record ActualConstraint(string Name, string[] Columns);

internal sealed record ActualForeignKey(
    string Name,
    string[] Columns,
    string PrincipalTable,
    string[] PrincipalColumns,
    string DeleteRule);

internal sealed record ActualIndex(
    string Name,
    string[] ResolvedColumns,
    bool IsUnique,
    int KeyCount);

/// <summary>
/// The actual structure read from PostgreSQL catalogs for the known business tables.
/// </summary>
internal sealed class SchemaSnapshot
{
    private readonly Dictionary<string, Dictionary<string, ActualColumn>> _columns =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActualConstraint?> _primaryKeys =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ActualForeignKey>> _foreignKeys =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ActualIndex>> _indexes =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _tables = new(StringComparer.Ordinal);

    internal void AddColumn(string table, ActualColumn column)
    {
        _tables.Add(table);
        if (!_columns.TryGetValue(table, out var columns))
        {
            columns = new Dictionary<string, ActualColumn>(StringComparer.Ordinal);
            _columns[table] = columns;
        }

        columns[column.Name] = column;
    }

    internal void SetPrimaryKey(string table, ActualConstraint primaryKey)
    {
        _tables.Add(table);
        _primaryKeys[table] = primaryKey;
    }

    internal void AddForeignKey(string table, ActualForeignKey foreignKey)
    {
        _tables.Add(table);
        if (!_foreignKeys.TryGetValue(table, out var list))
        {
            list = [];
            _foreignKeys[table] = list;
        }

        list.Add(foreignKey);
    }

    internal void AddIndex(string table, ActualIndex index)
    {
        _tables.Add(table);
        if (!_indexes.TryGetValue(table, out var list))
        {
            list = [];
            _indexes[table] = list;
        }

        list.Add(index);
    }

    /// <summary>
    /// Projects only inspected business tables into the shared model. Expression indexes stay
    /// in the local inventory and are rejected by the named-object check before acceptance.
    /// </summary>
    internal SchemaTable ToSharedTable(string table) => new(
        table,
        GetColumns(table).Values.Select(column => new SchemaColumn(
            column.Name, column.Type, !column.NotNull)).ToList(),
        GetPrimaryKey(table) is { } primaryKey ? new SchemaPrimaryKey(primaryKey.Columns) : null,
        GetForeignKeys(table).Select(key => new SchemaForeignKey(
            key.Columns, key.PrincipalTable, key.PrincipalColumns, MapDeleteRule(key.DeleteRule),
            "public")).ToList(),
        GetIndexes(table).Where(index => index.ResolvedColumns.Length > 0 &&
                                       index.KeyCount == index.ResolvedColumns.Length)
            .Select(index => new SchemaIndex(index.ResolvedColumns, index.IsUnique)).ToList(),
        "public");

    internal static SchemaForeignKeyDeleteRule MapDeleteRule(string rule) => rule switch
    {
        "c" => SchemaForeignKeyDeleteRule.Cascade,
        "r" => SchemaForeignKeyDeleteRule.Restrict,
        "n" => SchemaForeignKeyDeleteRule.SetNull,
        "d" => SchemaForeignKeyDeleteRule.SetDefault,
        _ => SchemaForeignKeyDeleteRule.NoAction,
    };

    internal bool TableExists(string table) => _tables.Contains(table);

    internal IReadOnlyDictionary<string, ActualColumn> GetColumns(string table) =>
        _columns.TryGetValue(table, out var columns)
            ? columns
            : new Dictionary<string, ActualColumn>(StringComparer.Ordinal);

    internal ActualConstraint? GetPrimaryKey(string table) =>
        _primaryKeys.TryGetValue(table, out var primaryKey) ? primaryKey : null;

    internal IReadOnlyList<ActualForeignKey> GetForeignKeys(string table) =>
        _foreignKeys.TryGetValue(table, out var list) ? list : [];

    internal IReadOnlyList<ActualIndex> GetIndexes(string table) =>
        _indexes.TryGetValue(table, out var list) ? list : [];
}

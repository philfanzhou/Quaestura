using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ServiceMantle.Database.PostgreSql.Migration;
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
/// Schema evidence (applied history plus the columns, constraints, and indexes of the six business
/// tables) is read by the shared <see cref="PostgreSqlSchemaEvidenceReader"/> with extended object
/// evidence, scoped to the contract tables so unrelated tables never influence the classification.
/// Name comparisons stay consumer-owned and case-insensitive; the shared comparer runs with name
/// comparison off and carries the shape and key-detail differences instead.
/// </para>
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
        var expected = BuildExpectedTables();
        var evidence = await ReadSchemaEvidenceAsync(cancellationToken).ConfigureAwait(false);

        switch (evidence)
        {
            case { State: SchemaEvidenceReadState.TargetDatabaseMissing }:
                // The catalog vanished between opening the connection and reading the evidence;
                // it classifies exactly like a missing target database at open time.
                return MigrationInspection.Of(
                    MigrationObservationState.Empty,
                    "the target database does not exist yet");

            case { State: SchemaEvidenceReadState.ReadFailed }:
                _logger?.LogWarning(
                    "Database inspection could not read the target structure: {Failure}",
                    evidence.Message);
                return MigrationInspection.Of(
                    MigrationObservationState.InspectionFailed,
                    $"the database structure could not be read ({evidence.Message})");

            case { State: SchemaEvidenceReadState.Succeeded, Snapshot: { } snapshot }:
                return ClassifySnapshot(evidence.AppliedMigrationIds, snapshot, expected);

            default:
                throw new InvalidOperationException(
                    $"The schema evidence reader returned an unsupported state: {evidence.State}.");
        }
    }

    private async Task<SchemaEvidenceReadResult> ReadSchemaEvidenceAsync(
        CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection() as NpgsqlConnection
            ?? throw new InvalidOperationException(
                "The migration executor requires the target database connection to be an Npgsql connection.");
        var options = new PostgreSqlSchemaEvidenceReadOptions(
            includeExtendedObjectEvidence: true,
            tables: KnownTableNames.Select(table => (SchemaName, table)).ToList());
        return await new PostgreSqlSchemaEvidenceReader()
            .ReadAsync(connection, options, cancellationToken).ConfigureAwait(false);
    }

    private MigrationInspection ClassifySnapshot(
        IReadOnlyList<string> applied,
        SharedSchemaSnapshot snapshot,
        IReadOnlyDictionary<string, SchemaTable> expected)
    {
        var presentBusinessTables = KnownTableNames
            .Where(table => FindTable(snapshot, table) is not null).ToList();

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

        var presentTagTables = TagTableNames
            .Where(table => FindTable(snapshot, table) is not null).ToList();
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

    // ---------- shared expectation plus consumer-owned names ----------

    private IReadOnlyDictionary<string, SchemaTable> BuildExpectedTables()
    {
        // Extended derivation outputs the PK/FK/index names and key column counts directly, so the
        // expected shape pairs with the reader's extended evidence without local name pairing.
        var derived = EfCoreExpectedSchemaDerivation.Derive(
                _context.Model,
                new EfCoreExpectedSchemaDerivationOptions(includeExtendedObjectEvidence: true))
            .Tables.ToDictionary(table => table.Name, StringComparer.Ordinal);

        var expected = new Dictionary<string, SchemaTable>(StringComparer.Ordinal);
        foreach (var table in _context.Model.GetRelationalModel().Tables)
        {
            if (!KnownTableNames.Contains(table.Name))
            {
                throw new InvalidOperationException(
                    $"The EF model contains table '{table.Name}' outside the supported migration contract.");
            }

            expected[table.Name] = derived[table.Name];
        }

        foreach (var tableName in KnownTableNames.Where(name => !expected.ContainsKey(name)))
        {
            throw new InvalidOperationException(
                $"The EF model does not contain the contract table '{tableName}'.");
        }

        return expected;
    }

    // ---------- validation ----------

    private static SchemaTable? FindTable(SharedSchemaSnapshot snapshot, string tableName) =>
        snapshot.Tables.FirstOrDefault(table =>
            string.Equals(table.Schema, SchemaName, StringComparison.Ordinal) &&
            string.Equals(table.Name, tableName, StringComparison.Ordinal));

    private static string? ValidateTables(
        SharedSchemaSnapshot snapshot,
        IReadOnlyDictionary<string, SchemaTable> expected,
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

    private static string? ValidateTable(SharedSchemaSnapshot snapshot, SchemaTable expected)
    {
        var actual = FindTable(snapshot, expected.Name);
        if (actual is null || actual.Columns.Count == 0)
        {
            return $"table '{expected.Name}' is missing";
        }

        var differences = SchemaEvidenceComparer.Compare(
            new SharedSchemaSnapshot([NormalizeOutsideContract(actual)]),
            new ExpectedSchema([NormalizeOutsideContract(expected)]),
            new SchemaEvidenceComparisonOptions(compareObjectNames: false, compareIndexKeyDetails: true));
        var columnMismatch = differences.FirstOrDefault(difference => difference.Kind is
            SchemaDifferenceKind.MissingColumn or SchemaDifferenceKind.ExtraColumn or
            SchemaDifferenceKind.ColumnTypeMismatch or SchemaDifferenceKind.ColumnNullabilityMismatch);
        if (columnMismatch is not null)
        {
            return columnMismatch.ToString();
        }

        if (expected.PrimaryKey is { Name: { } expectedPrimaryKeyName })
        {
            if (actual.PrimaryKey is null)
            {
                return $"table '{expected.Name}' is missing its primary key";
            }

            if (!NameMatches(actual.PrimaryKey.Name, expectedPrimaryKeyName))
            {
                return $"table '{expected.Name}' has primary key '{actual.PrimaryKey.Name}' but the supported version requires '{expectedPrimaryKeyName}'";
            }
        }

        var primaryKeyMismatch = differences.FirstOrDefault(difference =>
            difference.Kind == SchemaDifferenceKind.PrimaryKeyMismatch);
        if (primaryKeyMismatch is not null)
        {
            return primaryKeyMismatch.ToString();
        }

        var actualForeignKeys = actual.ForeignKeys;
        foreach (var foreignKey in expected.ForeignKeys)
        {
            var match = actualForeignKeys.FirstOrDefault(candidate =>
                NameMatches(candidate.Name, foreignKey.Name));
            if (match is null)
            {
                return $"table '{expected.Name}' is missing foreign key '{foreignKey.Name}'";
            }

            if (!match.Columns.SequenceEqual(foreignKey.Columns, StringComparer.Ordinal) ||
                !string.Equals(match.ReferencedTable, foreignKey.ReferencedTable, StringComparison.Ordinal) ||
                !match.ReferencedColumns.SequenceEqual(foreignKey.ReferencedColumns, StringComparer.Ordinal) ||
                match.DeleteRule != foreignKey.DeleteRule)
            {
                return $"foreign key '{foreignKey.Name}' on table '{expected.Name}' does not match the supported version's columns, reference, or delete behavior";
            }
        }

        if (actualForeignKeys.Count != expected.ForeignKeys.Count)
        {
            return $"table '{expected.Name}' contains a foreign key outside the supported version";
        }

        var actualIndexes = actual.Indexes;
        foreach (var index in expected.Indexes)
        {
            var match = actualIndexes.FirstOrDefault(candidate => NameMatches(candidate.Name, index.Name));
            if (match is null)
            {
                return $"table '{expected.Name}' is missing index '{index.Name}'";
            }

            // Expression or predicate variants keep their declared key count; only a plain column
            // index whose columns and key count equal the model's satisfies the supported version.
            if (match.IsUnique != index.IsUnique ||
                match.HasExpressionKeys ||
                match.KeyColumnCount != index.KeyColumnCount ||
                !match.Columns.SequenceEqual(index.Columns, StringComparer.Ordinal))
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
    /// Projects the evidence dimensions that were never inspection guarantees out of the shared
    /// comparison: the shared reader reports identity generation and stored defaults while the
    /// shared comparer flags identity differences unconditionally. Schema identifiers are
    /// canonicalized because the EF model leaves the schema unset (empty) while the reader
    /// reports the physical <c>public</c> schema.
    /// </summary>
    private static SchemaTable NormalizeOutsideContract(SchemaTable table) => new(
        table.Name,
        table.Columns.Select(column => new SchemaColumn(
            column.Name, column.DataType, column.IsNullable,
            SchemaIdentityKind.None, hasStoredDefault: false)).ToList(),
        table.PrimaryKey is { } primaryKey
            ? new SchemaPrimaryKey(primaryKey.Columns, primaryKey.Name)
            : null,
        table.ForeignKeys.Select(key => new SchemaForeignKey(
            key.Columns, key.ReferencedTable, key.ReferencedColumns, key.DeleteRule,
            SchemaName, key.Name)).ToList(),
        table.Indexes.Select(index => new SchemaIndex(
            index.Columns, index.IsUnique, index.Name, index.KeyColumnCount, index.IncludedColumns))
            .ToList(),
        SchemaName);

    /// <summary>
    /// Known constraint and index names are compared case-insensitively; the semantics
    /// (columns, uniqueness, references, delete behavior) must still match exactly. The shared
    /// comparer matches names ordinally, so name comparison stays in this consumer.
    /// </summary>
    private static bool NameMatches(string? actual, string? expected) =>
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

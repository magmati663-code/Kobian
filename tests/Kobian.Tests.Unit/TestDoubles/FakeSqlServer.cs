using System.Security.Cryptography;
using Kobian.Providers.Sources.Erp;
using Kobian.Providers.Sources.MsSql;

namespace Kobian.Tests.Unit.TestDoubles;

/// <summary>SQL Server w pamięci: odpowiada na zapytania z MsSqlQueries, BACKUP zapisuje losowy plik do stagingu.</summary>
public sealed class FakeSqlServer(string db, string localRoot, string sqlRoot) : IMsSqlSession
{
    private readonly Dictionary<string, Guid> _sets = new(StringComparer.Ordinal);

    public List<SqlCommandSpec> Executed { get; } = [];

    public int Queries { get; private set; }

    public int ConflictQueries { get; private set; }

    public int Major { get; set; } = 16;

    public bool DatabaseListed { get; set; } = true;

    public long DataBytes { get; set; } = 4096;

    public bool Encrypted { get; set; }

    public bool CanBackup { get; set; } = true;

    public bool CanVerifyOnly { get; set; }

    public int ConflictPolls { get; set; }

    public Guid DiffBase { get; private set; } = Guid.NewGuid();

    public bool ChangeDiffBaseOnBackup { get; set; }

    public bool RegisterSet { get; set; } = true;

    public bool SetCopyOnly { get; set; } = true;

    public bool SetChecksums { get; set; } = true;

    public string SetType { get; set; } = "D";

    public int[]? BackupErrors { get; set; }

    public int[]? VerifyErrors { get; set; }

    public Guid LastSetUuid { get; private set; }

    /// <summary>D5.9: tabele widoczne w bazie dla wykrywania profilu ERP (domyślnie brak = baza bez ERP).</summary>
    public List<(string Schema, string Table)> ErpTables { get; } = [];

    /// <summary>D5.11: odpowiedź na zapytanie kontrolne: (tekst zapytania profilu, numer wywołania tego zapytania od 1) → wiersze; wyjątek = błąd serwera.</summary>
    public Func<string, int, IReadOnlyList<IReadOnlyDictionary<string, object?>>>? ControlResponder { get; set; }

    public List<SqlCommandSpec> ControlCalls { get; } = [];

    public List<SqlCommandSpec> DetectCalls { get; } = [];

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(SqlCommandSpec command, CancellationToken cancellationToken)
    {
        Queries++;
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = command.Text switch
        {
            MsSqlQueries.D01Server => [Row(("major", Major), ("instance", "SQLEXPRESS"), ("engine_edition", 4))],
            MsSqlQueries.D02Databases => DatabaseListed
                ? [Row(("name", "inna"), ("data_bytes", 1L)), Row(("name", db), ("data_bytes", DataBytes), ("is_encrypted", Encrypted))]
                : [Row(("name", "inna"), ("data_bytes", 1L))],
            MsSqlQueries.D03Permissions => [Row(("can_backup_db", CanBackup ? 1 : 0), ("can_verifyonly", CanVerifyOnly ? 1 : 0))],
            MsSqlQueries.P01ConflictingOperations => Conflicts(),
            MsSqlQueries.P03DifferentialBase => [Row(("differential_base_guid", DiffBase))],
            MsSqlQueries.BackupSetByDevice => _sets.TryGetValue((string)command.Parameters["@path"], out var id)
                ? [Row(("backup_set_uuid", id), ("is_copy_only", SetCopyOnly), ("has_backup_checksums", SetChecksums), ("type", SetType),
                    ("first_lsn", 41000000012300001m), ("last_lsn", 41000000012500001m), ("database_backup_lsn", 41000000010000001m),
                    ("server_name", "SRV01\\SQLEXPRESS"), ("backup_finish_date", new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc)))]
                : [],
            ControlQueryRunner.WrapperText => ControlRows(command),
            var t when t.Contains(".sys.tables", StringComparison.Ordinal) => Detect(command),
            _ => throw new InvalidOperationException("unexpected query"),
        };
        return Task.FromResult(rows);
    }

    public Task ExecuteAsync(SqlCommandSpec command, CancellationToken cancellationToken)
    {
        Executed.Add(command);
        var path = (string)command.Parameters["@stagingPath"];
        if (command.Text == MsSqlBackupCommand.VerifyOnlyText)
        {
            if (VerifyErrors is { } ve) throw new MsSqlServerException(ve);
            return Task.CompletedTask;
        }

        if (BackupErrors is { } be) throw new MsSqlServerException(be);
        var local = Path.Combine(localRoot, path[(sqlRoot.Length + 1)..].Replace('\\', Path.DirectorySeparatorChar));
        File.WriteAllBytes(local, RandomNumberGenerator.GetBytes(70_000)); // FORMAT, INIT
        LastSetUuid = Guid.NewGuid();
        if (RegisterSet) _sets[path] = LastSetUuid;
        if (ChangeDiffBaseOnBackup) DiffBase = Guid.NewGuid();
        return Task.CompletedTask;
    }

    private List<IReadOnlyDictionary<string, object?>> Detect(SqlCommandSpec command)
    {
        DetectCalls.Add(command);
        return ErpTables.Select(t => (IReadOnlyDictionary<string, object?>)Row(("schema_name", t.Schema), ("table_name", t.Table))).ToList();
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> ControlRows(SqlCommandSpec command)
    {
        ControlCalls.Add(command);
        var q = (string)command.Parameters["@q"];
        var nth = ControlCalls.Count(c => (string)c.Parameters["@q"] == q);
        return ControlResponder is null ? [] : ControlResponder(q, nth);
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> Conflicts()
    {
        ConflictQueries++;
        return ConflictQueries <= ConflictPolls ? [Row(("session_id", 61), ("command", "DBCC"))] : [];
    }

    public static Dictionary<string, object?> Row(params (string Key, object? Value)[] cols) =>
        cols.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
}

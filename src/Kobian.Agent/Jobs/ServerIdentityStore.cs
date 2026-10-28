using System.Globalization;
using System.Text.RegularExpressions;
using Kobian.Catalog.Sqlite;
using Kobian.Domain;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Jobs;

/// <summary>Tożsamość serwera z identyfikatorem celu (lista dla SCR-09 / CLI).</summary>
public sealed record ServerIdentityEntry(string DestinationId, ServerIdentityRow Row);

/// <summary>
/// Katalog tożsamości serwerów celów (D4.11, SEC-14.5; tabele <c>repo_dest_identities</c> i <c>repo_dest_identity_policy</c>). Odczyt — własne połączenie
/// tylko do odczytu; zmiana — przez jedynego pisarza katalogu (<see cref="CatalogWriter"/>): odczyt wierszy celu, czyste przejście
/// <see cref="ServerIdentityMachine.Apply"/> i zapis różnicy w JEDNEJ transakcji (równoległe sesje tego samego celu nie gubią przejść).
/// </summary>
public sealed partial class ServerIdentityStore(string catalogPath, CatalogWriter writer, TimeProvider time)
{
    public const int MaxListRows = 1000;
    public const int MaxPendingTotal = 500;

    [GeneratedRegex("^dest-[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex DestinationIdShape();

    public static bool IsDestinationId(string? id) => id is { Length: 21 } && DestinationIdShape().IsMatch(id);

    public TimeProvider Time => time;

    public IReadOnlyList<ServerIdentityRow> Load(string destinationId)
    {
        if (!IsDestinationId(destinationId)) return [];
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        return Read(c, null, destinationId);
    }

    public bool RequiresPin(string destinationId)
    {
        if (!IsDestinationId(destinationId)) return false;
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT require_pin FROM repo_dest_identity_policy WHERE destination_id=$d;";
        cmd.Parameters.AddWithValue("$d", destinationId);
        return cmd.ExecuteScalar() is long v && v == 1;
    }

    /// <summary>Wszystkie tożsamości (albo jednego celu), oczekujące najpierw; maks. <see cref="MaxListRows"/>.</summary>
    public (IReadOnlyList<ServerIdentityEntry> Identities, IReadOnlyList<string> RequirePin) List(string? destinationId)
    {
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        var list = new List<ServerIdentityEntry>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = Select + " WHERE ($d IS NULL OR destination_id=$d) ORDER BY CASE state WHEN 'pending' THEN 0 WHEN 'pinned' THEN 1 ELSE 2 END, destination_id, first_seen LIMIT $n;";
            cmd.Parameters.AddWithValue("$d", (object?)destinationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", MaxListRows);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new ServerIdentityEntry(r.GetString(0), Row(r)));
        }
        var pin = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT destination_id FROM repo_dest_identity_policy WHERE require_pin=1 AND ($d IS NULL OR destination_id=$d) ORDER BY destination_id LIMIT $n;";
            cmd.Parameters.AddWithValue("$d", (object?)destinationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", MaxListRows);
            using var r = cmd.ExecuteReader();
            while (r.Read()) pin.Add(r.GetString(0));
        }
        return (list, pin);
    }

    /// <summary>Przejście maszyny stanów dla celu (atomowo w pisarzu katalogu). Niepoprawny identyfikator celu → KB-2141 bez zapisu.</summary>
    public Task<ServerIdentityTransition> ApplyAsync(string destinationId, ServerIdentityEvent ev, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (!IsDestinationId(destinationId))
            return Task.FromResult(new ServerIdentityTransition([], KbCode.ServerIdentityNotPending, ServerIdentityNotice.Ignored));
        return writer.ExecuteControlAsync((c, tx) =>
        {
            var before = Read(c, tx, destinationId);
            // DoS (SEC-3.3): sprawdzanie wielu różnych celów (ProbeDestination — Operator) nie rozrośnie katalogu — globalny limit oczekujących.
            if (ev is ServerIdentityEvent.Observed o && !before.Any(r => string.Equals(r.Fingerprint, o.Fingerprint, StringComparison.Ordinal)) && PendingTotal(c, tx) >= MaxPendingTotal)
                return new ServerIdentityTransition(before, null, ServerIdentityNotice.Ignored);
            var t = ServerIdentityMachine.Apply(before, ev, time.GetUtcNow());
            if (t.Code is null && !ReferenceEquals(t.Rows, before))
            {
                foreach (var row in t.Rows)
                {
                    if (before.Contains(row)) continue; // rekord — porównanie wartości
                    Upsert(c, tx, destinationId, row);
                }
            }
            return t;
        }, ct);
    }

    /// <summary>SEC-14.5: cel „wysokiego ryzyka” — pin wymagany także przy ważnym łańcuchu CA.</summary>
    public Task SetRequirePinAsync(string destinationId, bool requirePin, string actor, CancellationToken ct)
    {
        if (!IsDestinationId(destinationId)) throw new ArgumentException("Invalid destination id.", nameof(destinationId));
        if (!ServerIdentityMachine.IsActor(actor)) throw new ArgumentException("Invalid actor.", nameof(actor));
        return writer.ExecuteControlAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO repo_dest_identity_policy(destination_id, require_pin, updated_at, updated_by_sid) VALUES ($d,$p,$t,$s) "
                + "ON CONFLICT(destination_id) DO UPDATE SET require_pin=excluded.require_pin, updated_at=excluded.updated_at, updated_by_sid=excluded.updated_by_sid;";
            cmd.Parameters.AddWithValue("$d", destinationId);
            cmd.Parameters.AddWithValue("$p", requirePin ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", Iso(time.GetUtcNow()));
            cmd.Parameters.AddWithValue("$s", actor);
            return cmd.ExecuteNonQuery();
        }, ct);
    }

    /// <summary>
    /// Kompatybilność wstecz (start Agenta): pin z pola <c>hostKey</c> każdej definicji zadania SFTP/FTPS → <c>pinned</c> (aktor <see cref="ServerIdentityMachine.DefinitionActor"/>),
    /// jeśli cel nie ma jeszcze pinu w katalogu. Definicje nieczytelne pomijane (widać je w ListDestinations). Zwraca liczbę przeniesionych pinów.
    /// </summary>
    public async Task<int> MigrateJobDefinitionsAsync(CancellationToken ct)
    {
        var seeds = new List<(string Id, ServerIdentityEvent.SeedFromDefinition Ev)>();
        using (var c = CatalogBootstrap.Open(catalogPath, readOnly: true))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT type, definition FROM job_jobs ORDER BY job_id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                JobDefinition def;
                try
                {
                    def = JobDefinition.Parse(r.GetString(0), (byte[])r[1]);
                }
                catch (KobianException)
                {
                    continue;
                }
                if (SeedFor(def.Destination) is { } s) seeds.Add((def.DestinationId, s));
            }
        }
        var n = 0;
        foreach (var (id, ev) in seeds)
            if ((await ApplyAsync(id, ev, ct).ConfigureAwait(false)).Notice == ServerIdentityNotice.Seeded) n++;
        return n;
    }

    /// <summary>Zdarzenie „pin z definicji” dla celu SFTP/FTPS z poprawnym <c>hostKey</c>; inaczej null.</summary>
    public static ServerIdentityEvent.SeedFromDefinition? SeedFor(DestinationRequest d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var kind = KindOf(d.Scheme);
        return kind is not null && ServerIdentityMachine.Normalize(d.HostKey) is { } fp
            ? new ServerIdentityEvent.SeedFromDefinition(kind, fp, Endpoint(d.Scheme, d.Host, d.Port?.ToString(CultureInfo.InvariantCulture)))
            : null;
    }

    public static string? KindOf(string scheme) => scheme switch
    {
        "sftp" => ServerIdentityMachine.SshHostKey,
        "ftps" or "webdav" => ServerIdentityMachine.TlsSpki,
        _ => null,
    };

    /// <summary>„schemat://host:port” bez poświadczeń (do listy SCR-09/CLI).</summary>
    public static string Endpoint(string scheme, string? host, string? port)
    {
        var p = string.IsNullOrEmpty(port) ? JobDestination.DefaultPort(scheme) : port;
        var e = scheme + "://" + (string.IsNullOrEmpty(host) ? "?" : host) + ":" + p;
        return e.Length <= ServerIdentityMachine.MaxEndpointLength ? e : e[..ServerIdentityMachine.MaxEndpointLength];
    }

    private const string Select = "SELECT destination_id, fingerprint, kind, state, endpoint, display, first_seen, last_seen, approved_by_sid, approved_at FROM repo_dest_identities";

    private static List<ServerIdentityRow> Read(SqliteConnection c, SqliteTransaction? tx, string destinationId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = Select + " WHERE destination_id=$d ORDER BY first_seen, fingerprint;";
        cmd.Parameters.AddWithValue("$d", destinationId);
        using var r = cmd.ExecuteReader();
        var list = new List<ServerIdentityRow>();
        while (r.Read()) list.Add(Row(r));
        return list;
    }

    private static long PendingTotal(SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM repo_dest_identities WHERE state='pending';";
        return (long)cmd.ExecuteScalar()!;
    }

    private static ServerIdentityRow Row(SqliteDataReader r) => new(
        r.GetString(1), r.GetString(2), ParseState(r.GetString(3)), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        ParseTime(r.GetString(6)), ParseTime(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : ParseTime(r.GetString(9)));

    private static void Upsert(SqliteConnection c, SqliteTransaction tx, string destinationId, ServerIdentityRow row)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO repo_dest_identities(destination_id, fingerprint, kind, state, endpoint, display, first_seen, last_seen, approved_by_sid, approved_at)
            VALUES ($d,$f,$k,$s,$e,$disp,$fs,$ls,$by,$at)
            ON CONFLICT(destination_id, fingerprint) DO UPDATE SET state=excluded.state, endpoint=excluded.endpoint, display=excluded.display,
              first_seen=excluded.first_seen, last_seen=excluded.last_seen, approved_by_sid=excluded.approved_by_sid, approved_at=excluded.approved_at;
            """;
        cmd.Parameters.AddWithValue("$d", destinationId);
        cmd.Parameters.AddWithValue("$f", row.Fingerprint);
        cmd.Parameters.AddWithValue("$k", row.Kind);
        cmd.Parameters.AddWithValue("$s", StateName(row.State));
        cmd.Parameters.AddWithValue("$e", row.Endpoint.Length == 0 ? "?" : row.Endpoint);
        cmd.Parameters.AddWithValue("$disp", (object?)row.Display ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fs", Iso(row.FirstSeen));
        cmd.Parameters.AddWithValue("$ls", Iso(row.LastSeen));
        cmd.Parameters.AddWithValue("$by", (object?)row.DecidedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", row.DecidedAt is { } at ? Iso(at) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static string StateName(IdentityState s) => s switch
    {
        IdentityState.Pending => "pending",
        IdentityState.Pinned => "pinned",
        IdentityState.Rejected => "rejected",
        _ => "retired",
    };

    /// <summary>Nieznany stan z katalogu (ręczna zmiana) = „rejected” — fail-closed, nigdy pin.</summary>
    private static IdentityState ParseState(string s) => s switch
    {
        "pending" => IdentityState.Pending,
        "pinned" => IdentityState.Pinned,
        "retired" => IdentityState.Retired,
        _ => IdentityState.Rejected,
    };

    /// <summary>Nieparsowalny czas (ręczna zmiana) = epoka — oczekiwanie traktowane jako wygasłe (fail-closed), bez wyjątku.</summary>
    private static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTimeOffset.UnixEpoch;

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

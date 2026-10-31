using System.Globalization;
using Kobian.Catalog.Sqlite;
using Kobian.Providers.Storage.Throttling;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Wyuczony limit równoległości celu (D4.16 pkt 2; it. 114): <c>repo_dest_parallelism</c>. Odczyt → <see cref="AimdParallelism"/> (wpis starszy niż 7 dni
/// albo z przyszłości jest ignorowany przez konstruktor); zapis po uruchomieniu: limit poniżej rekomendowanego = UPSERT, powrót do rekomendowanego = DELETE.
/// </summary>
public sealed class CatalogParallelismStore(string catalogPath, CatalogWriter writer, TimeProvider time)
{
    public AimdParallelism Create(string destinationId, int recommended)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationId);
        using var c = CatalogBootstrap.Open(catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT parallelism, learned_at FROM repo_dest_parallelism WHERE destination_id=$d;";
        cmd.Parameters.AddWithValue("$d", destinationId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new AimdParallelism(recommended, time);
        // it. 120 (przegląd rundy 8, Niskie): wpis nieparsowalny = brak wyuczonego limitu (start od rekomendowanego), nie wyjątek — wcześniej FormatException
        // przed otwarciem sesji wysyłki kończył błędem KAŻDĄ kopię na ten cel. Wpis z przyszłości/przeterminowany odrzuca konstruktor AIMD.
        if (!DateTimeOffset.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            return new AimdParallelism(recommended, time);
        return new AimdParallelism(recommended, time, (int)Math.Clamp(r.GetInt64(0), 1, AimdParallelism.MaxParallelism), at);
    }

    public Task SaveAsync(string destinationId, AimdParallelism aimd, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationId);
        ArgumentNullException.ThrowIfNull(aimd);
        var learned = aimd.Learned;
        return writer.ExecuteControlAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$d", destinationId);
            if (learned is { } l)
            {
                cmd.CommandText = "INSERT INTO repo_dest_parallelism(destination_id, parallelism, learned_at) VALUES ($d, $p, $t) "
                    + "ON CONFLICT(destination_id) DO UPDATE SET parallelism=excluded.parallelism, learned_at=excluded.learned_at;";
                cmd.Parameters.AddWithValue("$p", l.Limit);
                cmd.Parameters.AddWithValue("$t", l.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            }
            else
            {
                cmd.CommandText = "DELETE FROM repo_dest_parallelism WHERE destination_id=$d;";
            }
            return cmd.ExecuteNonQuery();
        }, ct);
    }
}

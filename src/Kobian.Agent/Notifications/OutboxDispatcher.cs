using System.Globalization;
using System.Text.Json;
using Kobian.Catalog.Sqlite;
using Kobian.Notifications.Outbox;
using Kobian.Notifications.Webhooks;
using Microsoft.Data.Sqlite;

namespace Kobian.Agent.Notifications;

/// <summary>
/// Konsument trwałej kolejki <c>core_outbox</c> (R2.2.3 NotificationDispatcher, D9.10; it. 111; PROWIZORYCZNIE Qb58): jeden dyspozytor na kanał
/// (<see cref="IOutboxChannel.Name"/> = kolumna <c>channel</c>: „webhook”, „email” — BL-45), ponowienia 1 min, 5 min, 30 min, 2 h, 6 h, potem <c>dead</c>
/// + wpis audytu z kodem KB kanału. Zdarzenie trafia do kolejki w tej samej transakcji, co wynik uruchomienia (wzorzec transakcyjnego outboxa) — awaria
/// usługi niczego nie gubi; dostarczenie „co najmniej raz” (odbiorca deduplikuje po Webhook-Id / Message-ID).
/// </summary>
public sealed class OutboxDispatcher(string catalogPath, CatalogWriter writer, IOutboxChannel channel, TimeProvider time)
{
    public const string WebhookChannel = WebhookOutboxChannel.ChannelName;
    public const string EmailChannel = Kobian.Notifications.Email.EmailSender.ChannelName;
    public const int BatchSize = 20;

    /// <summary>Dyspozytor kanału webhook (zgodność wsteczna).</summary>
    public OutboxDispatcher(string catalogPath, CatalogWriter writer, IWebhookSender sender, TimeProvider time)
        : this(catalogPath, writer, new WebhookOutboxChannel(sender), time)
    {
    }

    public static IReadOnlyList<TimeSpan> RetrySchedule { get; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];

    private sealed record Item(long Id, byte[] Payload, int Attempts);

    /// <summary>it. 112: przejście wpisu w <c>dead</c> = wpis audytu KB-6301 (kanał scheduler, wynik failed) — widoczny w `kobian audit` i w raporcie dziennym.</summary>
    public Kobian.Agent.Audit.IAuditSink? Audit { get; init; }

    /// <summary>Jeden przebieg: należne wpisy (najstarsze najpierw, ≤ <see cref="BatchSize"/>) → wysyłka → stan. Zwraca liczbę dostarczonych.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = new List<Item>();
        using (var c = CatalogBootstrap.Open(catalogPath, readOnly: true))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, payload, attempts FROM core_outbox WHERE state='pending' AND channel=$ch AND next_attempt_at <= $now ORDER BY id LIMIT $n;";
            cmd.Parameters.AddWithValue("$ch", channel.Name);
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$n", BatchSize);
            using var r = cmd.ExecuteReader();
            while (r.Read()) due.Add(new Item(r.GetInt64(0), (byte[])r[1], r.GetInt32(2)));
        }
        var delivered = 0;
        foreach (var item in due)
        {
            ct.ThrowIfCancellationRequested();
            var delivery = await channel.DeliverAsync(EventId(item.Payload) ?? item.Id.ToString(CultureInfo.InvariantCulture), item.Payload, ct).ConfigureAwait(false);
            var result = delivery.Result;
            var attempts = item.Attempts + 1;
            string state;
            var next = now;
            if (result == DeliveryResult.Delivered)
            {
                state = "sent";
                delivered++;
            }
            else if (result == DeliveryResult.Permanent || attempts > RetrySchedule.Count)
            {
                state = "dead";
            }
            else
            {
                state = "pending";
                next = now + RetrySchedule[attempts - 1];
            }
            await writer.ExecuteControlAsync((c, tx) =>
            {
                using var u = c.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE core_outbox SET state=$s, attempts=$a, next_attempt_at=$t WHERE id=$i AND state='pending';";
                u.Parameters.AddWithValue("$s", state);
                u.Parameters.AddWithValue("$a", attempts);
                u.Parameters.AddWithValue("$t", Iso(next));
                u.Parameters.AddWithValue("$i", item.Id);
                return u.ExecuteNonQuery();
            }, ct).ConfigureAwait(false);
            if (state == "dead" && Audit is not null)
                await Audit.AppendAsync(new Kobian.Agent.Audit.AuditRecord("agent", "System", "scheduler", "notify." + channel.Name, "outbox:" + item.Id.ToString(CultureInfo.InvariantCulture),
                    new Dictionary<string, string> { ["attempts"] = attempts.ToString(CultureInfo.InvariantCulture), ["result"] = result.ToString() }, "failed", delivery.KbCode ?? channel.DeadKbCode), ct).ConfigureAwait(false);
        }
        return delivered;
    }

    /// <summary>Pętla co 15 s do zatrzymania hosta; błąd katalogu nie zatrzymuje usługi.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        try
        {
            do
            {
                try
                {
                    await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
                {
                    // następna próba w kolejnym takcie
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Dodanie zdarzenia do kolejki kanału webhook w transakcji wywołującego (np. razem z wynikiem uruchomienia).</summary>
    public static void Enqueue(SqliteConnection c, SqliteTransaction tx, byte[] payload, DateTimeOffset now) => Enqueue(c, tx, WebhookChannel, payload, now);

    /// <summary>Dodanie zdarzenia do kolejki wskazanego kanału w transakcji wywołującego.</summary>
    public static void Enqueue(SqliteConnection c, SqliteTransaction tx, string channelName, byte[] payload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentException.ThrowIfNullOrEmpty(channelName);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO core_outbox(channel, payload, attempts, next_attempt_at, state) VALUES ($ch, $p, 0, $t, 'pending');";
        cmd.Parameters.AddWithValue("$ch", channelName);
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$t", Iso(now));
        cmd.ExecuteNonQuery();
    }

    private static string? EventId(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Konfiguracja webhooka w usłudze (it. 111, PROWIZORYCZNIE Qb58): <c>KOBIAN_WEBHOOK_URL</c> (https, publiczny — <see cref="WebhookUrlPolicy"/>) i sekret
/// w pliku <c>&lt;sekrety&gt;/webhook.secret</c> (≥ 64 znaki hex = ≥ 32 B; katalog z ACL D2.6). <c>KOBIAN_WEBHOOK_MINIMAL=1</c> — bez nazwy zadania (SEC-9.6).
/// Brak URL = powiadomienia wyłączone; zły URL/sekret = błąd startu (kod 64) — nie po cichu.
/// </summary>
public static class WebhookConfig
{
    public const string UrlVariable = "KOBIAN_WEBHOOK_URL";
    public const string MinimalVariable = "KOBIAN_WEBHOOK_MINIMAL";
    public const string SecretFile = "webhook.secret";

    public sealed record Settings(string Url, byte[] Secret, bool Minimal);

    /// <returns>(null, null) = wyłączone; (null, błąd) = konfiguracja niepoprawna.</returns>
    public static (Settings? Settings, string? Error) Load(Func<string, string?> env, string secretsDir)
    {
        ArgumentNullException.ThrowIfNull(env);
        var url = env(UrlVariable)?.Trim();
        if (string.IsNullOrEmpty(url)) return (null, null);
        if (!WebhookUrlPolicy.IsAllowed(url)) return (null, $"{UrlVariable} is not an allowed https URL (KB-6302).");
        var path = Path.Combine(secretsDir, SecretFile);
        string text;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.LinkTarget is not null || fi.Length > 4096) return (null, $"{SecretFile} is missing in the secrets directory.");
            text = File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{SecretFile} cannot be read.");
        }
        if (text.Length < 64 || text.Length % 2 != 0 || !text.All(Uri.IsHexDigit)) return (null, $"{SecretFile} must contain at least 64 hex characters.");
        return (new Settings(url, Convert.FromHexString(text), env(MinimalVariable) == "1"), null);
    }
}

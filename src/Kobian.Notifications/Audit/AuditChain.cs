using System.Security.Cryptography;
using System.Text.Json;
using Kobian.Domain.Serialization;

namespace Kobian.Notifications.Audit;

/// <summary>Wpis audytu (D9.9). Params — JSON zamaskowany (SEC-11.5).</summary>
public sealed record AuditEntry(
    long Seq,
    string AtUtc,
    string ActorSid,
    string Role,
    string Channel,
    string Operation,
    string? Target,
    string? ParamsJson,
    string Outcome,
    string? KbCode);

public sealed record ChainedAuditEntry(AuditEntry Entry, byte[] PrevHash, byte[] Hash);

/// <summary>Łańcuch audytu: hash = SHA-256(prevHash ‖ JCS(entry)); pierwszy prevHash = 32 B zer (D9.9).</summary>
public static class AuditChain
{
    private static readonly HashSet<string> Channels = new(StringComparer.Ordinal) { "ui", "cli", "api", "scheduler", "installer", "updater" };
    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal) { "allowed", "denied", "failed" };

    public static byte[] Genesis => new byte[32];

    public static byte[] CanonicalEntry(AuditEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!Channels.Contains(e.Channel)) throw new ArgumentException("Unknown audit channel.", nameof(e));
        if (!Outcomes.Contains(e.Outcome)) throw new ArgumentException("Unknown audit outcome.", nameof(e));
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", e.Seq);
            w.WriteString("at_utc", e.AtUtc);
            w.WriteString("actor_sid", e.ActorSid);
            w.WriteString("role", e.Role);
            w.WriteString("channel", e.Channel);
            w.WriteString("operation", e.Operation);
            if (e.Target is null) w.WriteNull("target"); else w.WriteString("target", e.Target);
            w.WritePropertyName("params");
            if (e.ParamsJson is null)
            {
                w.WriteNullValue();
            }
            else
            {
                using var p = JsonDocument.Parse(e.ParamsJson);
                p.RootElement.WriteTo(w);
            }
            w.WriteString("outcome", e.Outcome);
            if (e.KbCode is null) w.WriteNull("kb_code"); else w.WriteString("kb_code", e.KbCode);
            w.WriteEndObject();
        }
        return Jcs.Canonicalize(Encoding(ms));
    }

    public static ChainedAuditEntry Append(byte[] prevHash, AuditEntry e)
    {
        ArgumentNullException.ThrowIfNull(prevHash);
        if (prevHash.Length != 32) throw new ArgumentException("prevHash must be 32 bytes.", nameof(prevHash));
        var canon = CanonicalEntry(e);
        var buf = new byte[32 + canon.Length];
        prevHash.CopyTo(buf, 0);
        canon.CopyTo(buf, 32);
        return new ChainedAuditEntry(e, (byte[])prevHash.Clone(), SHA256.HashData(buf));
    }

    /// <summary>Weryfikacja: zwraca seq pierwszego wpisu łamiącego łańcuch (KB-6501) albo null; opcjonalnie zgodność z kotwicą.</summary>
    public static long? Verify(IReadOnlyList<ChainedAuditEntry> chain, (long Seq, byte[] Hash)? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var prev = Genesis;
        long? expectedSeq = null;
        foreach (var c in chain)
        {
            if (expectedSeq is not null && c.Entry.Seq != expectedSeq) return c.Entry.Seq;
            if (!CryptographicOperations.FixedTimeEquals(c.PrevHash, prev)) return c.Entry.Seq;
            var recomputed = Append(prev, c.Entry).Hash;
            if (!CryptographicOperations.FixedTimeEquals(recomputed, c.Hash)) return c.Entry.Seq;
            if (anchor is { } a && a.Seq == c.Entry.Seq && !CryptographicOperations.FixedTimeEquals(a.Hash, c.Hash)) return c.Entry.Seq;
            prev = c.Hash;
            expectedSeq = c.Entry.Seq + 1;
        }
        return null;
    }

    private static string Encoding(MemoryStream ms) => System.Text.Encoding.UTF8.GetString(ms.ToArray());
}

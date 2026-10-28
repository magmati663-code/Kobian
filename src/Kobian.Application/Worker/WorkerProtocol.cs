using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kobian.Domain;

namespace Kobian.Application.Worker;

/// <summary>
/// Naruszenie protokołu usługa ↔ Worker (D2.8): ramka za duża/ucięta, zły JSON, nieznana operacja, niezgodny identyfikator odpowiedzi.
/// Strona, która je wykryje, kończy rozmowę (Worker: kod wyjścia, Agent: zabija proces) — KB-7014.
/// </summary>
public sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public WorkerProtocolException()
        : this("Worker protocol violation")
    {
    }

    public WorkerProtocolException(string message)
        : this(message, null)
    {
    }

    public static string Code => KbCode.WorkerProtocolViolation;
}

/// <summary>
/// Ramki D2.8: <c>u32le długość ‖ UTF-8 JSON</c>, długość 1 … 1 MiB (limit z planu). Odczyt dokładny (pętla), EOF przed pierwszym bajtem = koniec rozmowy (null),
/// EOF w środku ramki = naruszenie. Bez buforowania ponad jedną ramkę.
/// </summary>
public static class WorkerFrames
{
    public const int MaxFrame = 1024 * 1024;

    public static async Task<byte[]?> ReadAsync(Stream input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var header = new byte[4];
        var got = await FillAsync(input, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new WorkerProtocolException("Truncated frame header.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxFrame) throw new WorkerProtocolException("Frame length out of range.");
        var body = new byte[length];
        if (await FillAsync(input, body, ct).ConfigureAwait(false) < body.Length) throw new WorkerProtocolException("Truncated frame body.");
        return body;
    }

    public static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (body.Length is 0 or > MaxFrame) throw new WorkerProtocolException("Frame length out of range.");
        var frame = new byte[4 + body.Length]; // jedna operacja zapisu — ramka nie przeplata się z inną
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsMemory(4));
        await output.WriteAsync(frame, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> FillAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>Operacje roli „vss” (D2.8). Lista zamknięta — inna wartość = naruszenie protokołu.</summary>
public static class WorkerOps
{
    public const string Ping = "ping";
    public const string VssCreate = "vss.create";
    public const string VssRelease = "vss.release";

    public static bool IsKnown(string op) => op is Ping or VssCreate or VssRelease;
}

/// <summary>Żądanie: <see cref="Id"/> rośnie monotonicznie po stronie Agenta; <see cref="Volumes"/> tylko dla vss.create (np. „C:\”).</summary>
public sealed record WorkerRequest(int Id, string Op, IReadOnlyList<string>? Volumes = null);

/// <summary>Odpowiedź: <see cref="Code"/> = KB przy błędzie; <see cref="Devices"/> = wolumin → ścieżka urządzenia migawki (\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\).</summary>
public sealed record WorkerResponse(int Id, bool Ok, string? Code = null, IReadOnlyDictionary<string, string>? Devices = null, string? SnapshotSetId = null);

/// <summary>Serializacja ścisła: nieznane pola odrzucane, głębokość ≤ 4, nazwy camelCase. Limity treści sprawdza <see cref="Validate(WorkerRequest)"/>.</summary>
public static class WorkerJson
{
    public const int MaxVolumes = 26;
    public const int MaxDevicePath = 260;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Write<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static T Read<T>(byte[] frame)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(frame, Options) ?? throw new WorkerProtocolException("Empty message.");
        }
        catch (JsonException ex)
        {
            throw new WorkerProtocolException("Malformed message.", ex);
        }
    }

    /// <summary>Żądanie od Agenta (Worker ufa rodzicowi, ale nie bezgranicznie): znana operacja, woluminy w postaci „X:\”, bez powtórzeń.</summary>
    public static WorkerRequest Validate(WorkerRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Id < 1 || r.Op is null || !WorkerOps.IsKnown(r.Op)) throw new WorkerProtocolException("Unknown operation.");
        if (r.Op == WorkerOps.VssCreate)
        {
            if (r.Volumes is not { Count: > 0 and <= MaxVolumes } v) throw new WorkerProtocolException("Invalid volume list.");
            if (v.Any(x => x is not { Length: 3 } || !char.IsAsciiLetter(x[0]) || x[1] != ':' || x[2] != '\\')) throw new WorkerProtocolException("Invalid volume.");
            if (v.Select(x => char.ToUpperInvariant(x[0])).Distinct().Count() != v.Count) throw new WorkerProtocolException("Duplicate volume.");
        }
        else if (r.Volumes is not null)
        {
            throw new WorkerProtocolException("Unexpected volumes.");
        }
        return r;
    }

    /// <summary>Odpowiedź od Workera (Agent NIE ufa procesowi potomnemu — D2.8, SEC-2.5): ten sam Id, kod KB w rejestrze, ścieżki urządzeń migawki o ograniczonej długości.</summary>
    public static WorkerResponse Validate(WorkerResponse r, int expectedId)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Id != expectedId) throw new WorkerProtocolException("Response id mismatch.");
        if (!r.Ok && (r.Code is null || !KbCode.IsKnown(r.Code))) throw new WorkerProtocolException("Failure without a known KB code.");
        if (r.Devices is { } d)
        {
            if (d.Count > MaxVolumes) throw new WorkerProtocolException("Too many devices.");
            foreach (var (vol, dev) in d)
            {
                if (vol is not { Length: 3 } || dev is null || dev.Length > MaxDevicePath
                    || !dev.StartsWith(@"\\?\GLOBALROOT\Device\", StringComparison.Ordinal) || dev.Contains("..", StringComparison.Ordinal))
                    throw new WorkerProtocolException("Invalid snapshot device.");
            }
        }
        if (r.SnapshotSetId is { } s && !Guid.TryParse(s, out _)) throw new WorkerProtocolException("Invalid snapshot set id.");
        return r;
    }
}

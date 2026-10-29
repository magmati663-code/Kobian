using System.Globalization;
using System.Text.Json;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Jedno źródło zadania „files”: folder lokalny (także UNC) ALBO serwer — SFTP, FTPS, WebDAV — opisany tak samo jak cel (<see cref="JobDestination"/>:
/// host, port, przypięta tożsamość, odwołanie do poświadczenia; bez sekretów). W definicji v1: element tablicy „sources” — napis (ścieżka) albo obiekt (serwer).
/// </summary>
public sealed record JobSource(string? Path, DestinationRequest? Remote)
{
    /// <summary>Najwięcej źródeł w jednym zadaniu (ochrona przed rozrostem definicji ≤ 64 KiB).</summary>
    public const int MaxSources = 32;

    /// <summary>Schematy serwera dopuszczone jako źródło (jawny FTP — jak w celu — tylko poza definicją zadania, D4.3).</summary>
    public static readonly IReadOnlySet<string> RemoteSchemes = new HashSet<string>(StringComparer.Ordinal) { "sftp", "ftps", "webdav" };

    public static JobSource Local(string path) => new(path, null);

    public static JobSource Server(DestinationRequest remote) => new(null, remote);

    public bool IsRemote => Remote is not null;

    /// <summary>Etykieta w snapshotcie i nazwa folderu w kopii: ścieżka albo „sftp://host:port/korzeń”.</summary>
    public string Label => Path ?? Remote!.Scheme + "://" + (Remote.Host ?? "?") + (Remote.Port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : "") + Remote.Root;

    /// <summary>Kod KB, gdy źródło zdalne nie nadaje się do zadania; null = poprawne. Reguły jak dla celu (poświadczenie wymagane, SFTP z przypiętym kluczem hosta).</summary>
    public static string? ValidateRemote(DestinationRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!RemoteSchemes.Contains(r.Scheme)) return Kobian.Domain.KbCode.JobDefinitionInvalid;
        return JobDestination.Validate(r);
    }

    internal static JobSource Parse(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return Local(JobDefinition.AbsolutePath(e, "source"));
        if (e.ValueKind != JsonValueKind.Object) throw JobDefinition.Invalid("A source must be a path or a server object.");
        var r = JobDestination.Parse(e);
        if (ValidateRemote(r) is not null) throw JobDefinition.Invalid("The source server is not valid.");
        return Server(r);
    }

    internal void Write(Utf8JsonWriter w)
    {
        if (Path is not null) w.WriteStringValue(Path);
        else JobDestination.WriteObject(w, Remote!);
    }
}

/// <summary>Lista źródeł z równością po wartościach (definicja zadania jest rekordem porównywanym w testach i przy eksporcie/imporcie).</summary>
public sealed class JobSourceList(IEnumerable<JobSource> items) : IReadOnlyList<JobSource>, IEquatable<JobSourceList>
{
    private readonly JobSource[] _items = [.. items];

    public int Count => _items.Length;

    public JobSource this[int index] => _items[index];

    public IEnumerator<JobSource> GetEnumerator() => ((IEnumerable<JobSource>)_items).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(JobSourceList? other) => other is not null && _items.SequenceEqual(other._items);

    public override bool Equals(object? obj) => obj is JobSourceList l && Equals(l);

    public override int GetHashCode() => _items.Aggregate(0, (h, s) => HashCode.Combine(h, s));

    public override string ToString() => "[" + string.Join(", ", _items.Select(s => s.Label)) + "]";
}

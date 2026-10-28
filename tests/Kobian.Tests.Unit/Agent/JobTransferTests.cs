using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kobian.Agent.Audit;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Cli.Commands;
using Kobian.Domain;
using Kobian.Providers.Storage.Local;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>
/// US-29 (BL-47): eksport/import zadań kobian-export v1 — round-trip, brak sekretów, konflikty nazw, odrzucenie całego pliku (AC-29.2), brak akcji i uruchomień (SEC-6.7),
/// uprawnienia RPC (Administrator), audyt i CLI. Dane syntetyczne w katalogu tymczasowym; sekrety losowe.
/// </summary>
public sealed class JobTransferTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-xfer-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly List<CatalogWriter> _writers = [];
    private readonly List<WebApplication> _hosts = [];

    private string Src => Path.Combine(_dir, "src");
    private string Dst => Path.Combine(_dir, "dst");
    private string Secrets => Path.Combine(_dir, "secrets");
    private string KeyFile => Path.Combine(_dir, "keys", "k.key");
    private string PwFile => Path.Combine(Secrets, "repo-1.pw");
    private const string Pin = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
        Directory.CreateDirectory(Secrets);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        File.WriteAllText(Path.Combine(Src, "a.txt"), "x");
        File.WriteAllText(KeyFile, "{}");
        File.WriteAllText(PwFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts) await h.DisposeAsync();
        foreach (var w in _writers) await w.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private sealed class NoSessions(string scheme) : IStorageProviderFactory
    {
        public string Scheme => scheme;

        public ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct) =>
            throw new InvalidOperationException("eksport/import nie łączy się z celem");
    }

    private sealed class RecordingAudit : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public Task AppendAsync(AuditRecord record, CancellationToken ct)
        {
            lock (Records) Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private string NewCatalog(string name)
    {
        var path = Path.Combine(_dir, name + ".db");
        CatalogBootstrap.Bootstrap(path, TimeProvider.System);
        return path;
    }

    private JobTransferService Service(string catalog, out CatalogWriter writer)
    {
        writer = new CatalogWriter(catalog);
        _writers.Add(writer);
        var providers = new Dictionary<string, IStorageProviderFactory>(StringComparer.Ordinal) { ["local"] = new LocalStorageProviderFactory(), ["sftp"] = new NoSessions("sftp") };
        var probe = new DestinationProbeService(providers);
        return new JobTransferService(catalog, writer, new JobDraftValidator(catalog, probe, TimeProvider.System), probe, "9.9.9", TimeProvider.System) { SecretsDirectory = Secrets };
    }

    private string LocalDef(string? src = null, string? root = null, bool retention = true)
    {
        var o = new JsonObject
        {
            ["version"] = 1,
            ["sources"] = new JsonArray(src ?? Src),
            ["destination"] = new JsonObject { ["scheme"] = "local", ["root"] = root ?? Dst },
            ["key"] = new JsonObject { ["file"] = KeyFile, ["passwordFile"] = PwFile, ["repoHeader"] = null },
            ["packTargetSize"] = 4194304,
        };
        if (retention) o["retention"] = new JsonObject { ["keepLast"] = 7, ["keepDaily"] = 14, ["lockDays"] = 3 };
        return o.ToJsonString();
    }

    private string SftpDef() => new JsonObject
    {
        ["version"] = 1,
        ["sources"] = new JsonArray(Src),
        ["destination"] = new JsonObject { ["scheme"] = "sftp", ["root"] = "/srv/kopie", ["host"] = "backup.example.test", ["port"] = 2222, ["hostKey"] = Pin, ["secretRef"] = "file:nas" },
        ["key"] = new JsonObject { ["file"] = KeyFile },
    }.ToJsonString();

    private static void Exec(string catalog, string sql, params (string, object?)[] args)
    {
        using var c = CatalogBootstrap.Open(catalog);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(string catalog, string sql, params (string, object?)[] args)
    {
        using var c = CatalogBootstrap.Open(catalog, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private static void AddJob(string catalog, string id, string name, string def, string? cron = null, bool enabled = true, bool isProtected = false)
    {
        var bytes = Encoding.UTF8.GetBytes(def);
        Exec(catalog, "INSERT INTO job_jobs(job_id,name,type,definition,definition_sha256,enabled,protected,time_zone,misfire,created_at,updated_at) VALUES ($i,$n,'files',$d,$h,$e,$p,'UTC','skip','2026-10-13T00:00:00Z','2026-10-13T00:00:00Z');",
            ("$i", id), ("$n", name), ("$d", bytes), ("$h", SHA256.HashData(bytes)), ("$e", enabled ? 1 : 0), ("$p", isProtected ? 1 : 0));
        if (cron is not null)
            Exec(catalog, "INSERT INTO sched_triggers(trigger_id,job_id,kind,spec,enabled) VALUES ($t,$j,'cron',$s,1);",
                ("$t", ScheduleService.TriggerIdFor(id)), ("$j", id), ("$s", JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["cron"] = cron })));
    }

    private static string JobsSection(byte[] doc) => JsonNode.Parse(doc)!["jobs"]!.ToJsonString();

    private static long Count(string catalog, string sql) => (long)Scalar(catalog, sql)!;

    // ---------------- format ----------------

    [Fact, Trait("AC", "AC-29.1"), Trait("BL", "BL-47")]
    public void Write_is_canonical_sorted_and_round_trips_through_read()
    {
        var def = Encoding.UTF8.GetBytes(LocalDef());
        var (canonical, problems) = JobExportFormat.CanonicalDefinition("files", def);
        Assert.Empty(problems);
        var doc = new ExportDocument("9.9.9", new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero),
            [new ExportedJob("Zeta", "files", true, "UTC", "skip", null, null, canonical!), new ExportedJob("Alfa ąę", "files", false, "UTC", "runIfMissedWithin", 3600, new ExportedSchedule("0 21 * * *", true), canonical!)]);
        var bytes = JobExportFormat.Write(doc);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.True(text.IndexOf("\"exportedAt\"", StringComparison.Ordinal) < text.IndexOf("\"format\"", StringComparison.Ordinal)); // klucze posortowane
        Assert.True(text.IndexOf("Alfa", StringComparison.Ordinal) < text.IndexOf("Zeta", StringComparison.Ordinal)); // zadania po nazwie
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.DoesNotContain("null", text, StringComparison.Ordinal); // repoHeader:null pominięty
        Assert.All(bytes, b => Assert.True(b < 0x80)); // ASCII (\uXXXX) — te same bajty niezależnie od platformy
        Assert.Contains("\"format\": \"kobian-export\"", text, StringComparison.Ordinal);

        var (read, readProblems) = JobExportFormat.Read(bytes);
        Assert.Empty(readProblems);
        Assert.NotNull(read);
        Assert.Equal(bytes, JobExportFormat.Write(read!)); // idempotentne
        var alfa = read!.Jobs.Single(j => j.Name == "Alfa ąę");
        Assert.Equal((false, "runIfMissedWithin", 3600L, "0 21 * * *"), (alfa.Enabled, alfa.Misfire, alfa.MisfireWithinSeconds!.Value, alfa.Schedule!.Cron));
        Assert.Equal(JobDefinition.Parse("files", def), JobDefinition.Parse("files", alfa.Definition)); // definicje równoważne
    }

    private byte[] ValidDocument(Action<JsonObject>? mutate = null)
    {
        var (canonical, _) = JobExportFormat.CanonicalDefinition("files", Encoding.UTF8.GetBytes(LocalDef()));
        var doc = JobExportFormat.Write(new ExportDocument("9.9.9", DateTimeOffset.UnixEpoch, [new ExportedJob("A", "files", true, "UTC", "skip", null, new ExportedSchedule("0 21 * * *", true), canonical!)]));
        if (mutate is null) return doc;
        var node = JsonNode.Parse(doc)!.AsObject();
        mutate(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static JsonObject Job0(JsonObject root) => root["jobs"]![0]!.AsObject();

    private static JsonObject Def0(JsonObject root) => Job0(root)["definition"]!.AsObject();

    [Fact, Trait("AC", "AC-29.2"), Trait("SEC", "SEC-6.7")]
    public void Unknown_fields_and_wrong_types_reject_whole_file_with_json_path()
    {
        void Rejected(byte[] doc, string path, string code)
        {
            var (d, problems) = JobExportFormat.Read(doc);
            Assert.Null(d);
            Assert.Contains(new ImportProblem(path, code, problems.First(p => p.Path == path).Reason), problems);
        }
        Rejected(ValidDocument(r => r["extra"] = 1), "$.extra", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => Job0(r)["color"] = "red"), "$.jobs[0].color", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => Def0(r)["compression"] = "zstd"), "$.jobs[0].definition.compression", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => Def0(r)["destination"]!["user"] = "x"), "$.jobs[0].definition.destination.user", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => Def0(r)["key"]!["file"] = 5), "$.jobs[0].definition.key.file", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => Def0(r).Remove("key")), "$.jobs[0].definition.key", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => Job0(r)["name"] = null), "$.jobs[0].name", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => r["version"] = 2), "$.version", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => r["format"] = "cobian"), "$.format", KbCode.ImportFormatInvalid);
        Rejected(ValidDocument(r => Def0(r)["destination"]!["root"] = "wzgledna"), "$.jobs[0].definition", KbCode.JobDefinitionInvalid);
        Rejected(ValidDocument(r => Def0(r)["retention"]!["keepLast"] = -1), "$.jobs[0].definition", KbCode.RetentionInvalid);
        Rejected(ValidDocument(r => Job0(r)["schedule"]!["cron"] = "* * * * *"), "$.jobs[0].schedule.cron", KbCode.ScheduleInvalid);
        Rejected(ValidDocument(r => Job0(r)["timeZone"] = "Mars/Olympus"), "$.jobs[0].timeZone", KbCode.ScheduleInvalid);
        Rejected(ValidDocument(r => ((JsonArray)r["jobs"]!).Add(Job0(r).DeepClone())), "$.jobs[1].name", KbCode.ImportFormatInvalid); // duplikat nazwy

        var dup = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ValidDocument()).Replace("\"format\": \"kobian-export\",", "\"format\": \"kobian-export\", \"format\": \"kobian-export\",", StringComparison.Ordinal));
        Assert.Equal(KbCode.ImportFormatInvalid, Assert.Single(JobExportFormat.Read(dup).Problems).Code); // duplikat klucza — bez „ostatni wygrywa”

        // Pola nieznane — także wszystkie naraz (lista, nie pierwszy błąd)
        var many = JobExportFormat.Read(ValidDocument(r => { r["a"] = 1; Job0(r)["b"] = 2; Def0(r)["c"] = 3; })).Problems;
        Assert.Equal(["$.a", "$.jobs[0].b"], many.Select(p => p.Path).Order(StringComparer.Ordinal)); // definicja sprawdzana po kształcie dokumentu
    }

    [Theory, Trait("SEC", "SEC-6.7"), Trait("AC", "AC-29.1"), Trait("Plan", "AS-02")]
    [InlineData("preScript")]
    [InlineData("postScript")]
    [InlineData("actions")]
    [InlineData("runSql")]
    [InlineData("stopService")]
    [InlineData("pre")]
    [InlineData("post_command")]
    public void Script_and_action_fields_are_rejected_not_imported(string field)
    {
        foreach (var doc in new[] { ValidDocument(r => Job0(r)[field] = "C:\\ProgramData\\Kobian\\scripts\\a.ps1"), ValidDocument(r => Def0(r)[field] = new JsonArray(new JsonObject { ["runSql"] = "SELECT 1" })) })
        {
            var (d, problems) = JobExportFormat.Read(doc);
            Assert.Null(d);
            Assert.Contains(problems, p => p.Code == KbCode.ImportActionsRejected);
        }
    }

    [Fact, Trait("SEC", "SEC-6.7"), Trait("AC", "AC-01.3")]
    public void Secret_fields_and_values_are_rejected()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "9a"; // cyfra i litera gwarantowane
        var hex = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var cases = new (Action<JsonObject> Mutate, string Path)[]
        {
            (r => Def0(r)["destination"]!["password"] = token, "$.jobs[0].definition.destination.password"),
            (r => Def0(r)["key"]!["privateKey"] = "x", "$.jobs[0].definition.key.privateKey"),
            (r => Job0(r)["apiToken"] = "x", "$.jobs[0].apiToken"),
            (r => Job0(r)["name"] = token, "$.jobs[0].name"),
            (r => Job0(r)["name"] = "klucz " + hex, "$.jobs[0].name"),
            (r => Job0(r)["name"] = "user=a;password=" + token, "$.jobs[0].name"),
            (r => Def0(r)["destination"]!["root"] = "/kopie/password=" + token, "$.jobs[0].definition.destination.root"),
            (r => Def0(r)["sources"]![0] = "/a/-----BEGIN OPENSSH " + "PRIVATE KEY-----", "$.jobs[0].definition.sources[0]"),
            (r => r["programVersion"] = "https://u:" + token + "@example.test/", "$.programVersion"),
            (r => Def0(r)["destination"]!["hostKey"] = "SHA256:" + token + "!", "$.jobs[0].definition.destination.hostKey"), // nie-pin w hostKey → skanowany
        };
        foreach (var (mutate, path) in cases)
        {
            var (d, problems) = JobExportFormat.Read(ValidDocument(mutate));
            Assert.Null(d);
            Assert.Contains(problems, p => p.Path == path && p.Code == KbCode.ImportContainsSecret);
            Assert.DoesNotContain(problems, p => p.Reason.Contains(token, StringComparison.Ordinal) || p.Path.Contains(token, StringComparison.Ordinal)); // bez wartości (NFR-11)
        }
        // Wartości typowe nie są sekretami (fałszywe alarmy blokowałyby import)
        foreach (var ok in new[] { "Kopia dokumentów księgowych", "ERP-noc_2026", "backup01.biuro-kowalski.example.pl", "file:nas-biuro", "0 21 * * 1-5", "Europe/Warsaw", "1.4.0-rc.2" })
            Assert.False(JobExportFormat.LooksLikeSecret(ok), ok);
        Assert.False(JobExportFormat.LooksLikeSecret(Path.Combine(_dir, "tmp" + hex), isPath: true)); // losowa nazwa folderu w ścieżce — bez entropii
        Assert.Empty(JobExportFormat.Read(ValidDocument(r => Def0(r)["destination"] = new JsonObject
        {
            ["scheme"] = "sftp", ["root"] = "/srv/kopie", ["host"] = "backup.example.test", ["hostKey"] = "SHA256:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('='), ["secretRef"] = "file:nas",
        })).Problems); // pin SHA256 (losowy base64) nie jest sekretem
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("AC", "AC-29.2")]
    public void Size_depth_count_and_string_limits()
    {
        Assert.Equal(KbCode.ImportTooLarge, Assert.Single(JobExportFormat.Read(new byte[JobExportFormat.MaxBytes + 1]).Problems).Code);
        var deep = Encoding.UTF8.GetBytes(new string('[', 10_000) + new string(']', 10_000));
        Assert.Equal(KbCode.ImportTooLarge, Assert.Single(JobExportFormat.Read(deep).Problems).Code);
        Assert.Equal(KbCode.ImportTooLarge, Assert.Single(JobExportFormat.Read(ValidDocument(r => Job0(r)["timeZone"] = new JsonObject { ["a"] = new JsonObject { ["b"] = new JsonObject { ["c"] = new JsonArray(new JsonArray(new JsonArray())) } } })).Problems).Code);
        var tooMany = ValidDocument(r =>
        {
            var jobs = (JsonArray)r["jobs"]!;
            for (var i = 0; i < JobExportFormat.MaxJobs; i++) jobs.Add(new JsonObject());
        });
        Assert.Equal(KbCode.ImportTooLarge, Assert.Single(JobExportFormat.Read(tooMany).Problems).Code);
        Assert.Contains(JobExportFormat.Read(ValidDocument(r => Job0(r)["timeZone"] = new string('a', JobExportFormat.MaxStringLength + 1))).Problems, p => p.Code == KbCode.ImportTooLarge);
        Assert.Equal(KbCode.ImportFormatInvalid, Assert.Single(JobExportFormat.Read("{\"a\":"u8).Problems).Code);
        Assert.Equal(KbCode.ImportFormatInvalid, Assert.Single(JobExportFormat.Read(""u8).Problems).Code);
        Assert.Empty(JobExportFormat.Read([0xEF, 0xBB, 0xBF, .. ValidDocument()]).Problems); // BOM z Notatnika
    }

    /// <summary>
    /// Fuzzing mutacyjny (wzór Round*FuzzTests, SEC-3.3): mutacje tekstowe i strukturalne poprawnego dokumentu. Niezmienniki: brak wyjątku; albo dokument bez problemów
    /// (i zapis kanoniczny idempotentny), albo odrzucenie z kodami z dozwolonego zbioru i ścieżkami bez wartości. Ziarno stałe, &lt; 5 s.
    /// </summary>
    [Fact, Trait("SEC", "SEC-6.7"), Trait("SEC", "SEC-3.3"), Trait("AC", "AC-29.2")]
    public void Mutation_fuzzing_of_import_never_throws_and_keeps_invariants()
    {
        var sw = Stopwatch.StartNew();
        var rnd = new Random(2929);
        var baseDoc = Encoding.UTF8.GetString(ValidDocument());
        string[] allowed = [KbCode.ImportFormatInvalid, KbCode.ImportTooLarge, KbCode.ImportContainsSecret, KbCode.ImportActionsRejected, KbCode.JobDefinitionInvalid,
            KbCode.ScheduleInvalid, KbCode.RetentionInvalid];
        const string Alphabet = "ab./\\:@#?&%[]{}0123456789fFxX \t\r\n\0\u0001-_~*,;\"'<>|é😀\ud800";
        string[] names = ["version", "sources", "destination", "key", "retention", "name", "type", "schedule", "cron", "preScript", "password", "runSql", "x", "secretRef", "host", "port"];
        JsonNode?[] values = [null, 1, -1, 1L << 40, true, "", "/abs", "file:nas", "* * * * *", new JsonArray(), new JsonObject(), "SHA256:" + new string('A', 43), "eyJ.a.b", "-----BEGIN"];
        var accepted = 0;
        for (var iter = 0; iter < 4000; iter++)
        {
            string text;
            if (iter % 2 == 0)
            {
                var sb = new StringBuilder(baseDoc);
                for (var k = rnd.Next(1, 4); k > 0; k--)
                {
                    switch (rnd.Next(4))
                    {
                        case 0 when sb.Length > 0: sb.Remove(rnd.Next(sb.Length), 1); break;
                        case 1: sb.Insert(rnd.Next(sb.Length + 1), Alphabet[rnd.Next(Alphabet.Length)]); break;
                        case 2 when sb.Length > 0: sb[rnd.Next(sb.Length)] = (char)rnd.Next(0x20, 0x7F); break;
                        default: sb.Insert(rnd.Next(sb.Length + 1), "{\"" + names[rnd.Next(names.Length)] + "\":1}"); break;
                    }
                }
                text = sb.ToString();
            }
            else
            {
                var root = JsonNode.Parse(baseDoc)!.AsObject();
                var containers = new List<JsonObject>();
                void Walk(JsonNode? n)
                {
                    if (n is JsonObject o) { containers.Add(o); foreach (var (_, v) in o.ToList()) Walk(v); }
                    else if (n is JsonArray a) foreach (var v in a.ToList()) Walk(v);
                }
                Walk(root);
                for (var k = rnd.Next(1, 3); k > 0; k--)
                {
                    var target = containers[rnd.Next(containers.Count)];
                    var keys = target.Select(p => p.Key).ToList();
                    switch (rnd.Next(3))
                    {
                        case 0 when keys.Count > 0: target.Remove(keys[rnd.Next(keys.Count)]); break;
                        case 1 when keys.Count > 0: target[keys[rnd.Next(keys.Count)]] = values[rnd.Next(values.Length)]?.DeepClone(); break;
                        default: target[names[rnd.Next(names.Length)]] = values[rnd.Next(values.Length)]?.DeepClone(); break;
                    }
                }
                text = root.ToJsonString();
            }
            var (doc, problems) = JobExportFormat.Read(Encoding.UTF8.GetBytes(text));
            if (doc is not null)
            {
                Assert.Empty(problems);
                accepted++;
                var again = JobExportFormat.Write(doc);
                var (doc2, p2) = JobExportFormat.Read(again);
                Assert.Empty(p2);
                Assert.Equal(again, JobExportFormat.Write(doc2!));
                Assert.All(doc.Jobs, j => JobDefinition.Parse(j.Type, j.Definition));
            }
            else
            {
                Assert.NotEmpty(problems);
                Assert.True(problems.Count <= JobExportFormat.MaxProblems);
                Assert.All(problems, p =>
                {
                    Assert.Contains(p.Code, allowed);
                    Assert.StartsWith("$", p.Path, StringComparison.Ordinal);
                    Assert.DoesNotContain(p.Path, c => char.IsControl(c) || c is '/' or '\\' or ' ' or '"');
                });
            }
        }
        Assert.InRange(accepted, 1, 3999);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"fuzz took {sw.Elapsed}");
    }

    // ---------------- usługa: eksport/import na katalogu ----------------

    [Fact, Trait("AC", "AC-29.1"), Trait("SEC", "SEC-6.7"), Trait("BL", "BL-47")]
    public async Task Export_import_round_trip_gives_equivalent_jobs_disabled_and_nothing_runs()
    {
        var a = NewCatalog("a");
        AddJob(a, "j1", "Dokumenty", LocalDef(), "0 21 * * *");
        AddJob(a, "j2", "Serwer SFTP", SftpDef(), enabled: false);
        AddJob(a, "j3", "Bez retencji", LocalDef(retention: false));
        var export = Service(a, out _).Export(null);
        Assert.Null(export.Code);
        Assert.Equal(3, export.Jobs);

        var b = NewCatalog("b");
        var svc = Service(b, out _);
        var dry = await svc.ImportAsync(export.Document!, validateOnly: true, ImportConflictPolicy.Fail, _ct);
        Assert.Null(dry.Code);
        Assert.False(dry.Applied);
        Assert.All(dry.Changes, c => Assert.Equal(("new", "create"), (c.Kind, c.Action)));
        Assert.Equal(0L, Count(b, "SELECT COUNT(*) FROM job_jobs;")); // dry-run nic nie zapisuje

        var r = await svc.ImportAsync(export.Document!, validateOnly: false, ImportConflictPolicy.Fail, _ct);
        Assert.Null(r.Code);
        Assert.True(r.Applied);
        Assert.Equal(3L, Count(b, "SELECT COUNT(*) FROM job_jobs WHERE enabled = 0 AND quarantined_actions = 0;")); // zawsze wyłączone (SEC-6.7)
        Assert.Equal(0L, Count(b, "SELECT COUNT(*) FROM run_runs;")); // import niczego nie uruchamia
        Assert.Equal(1L, Count(b, "SELECT COUNT(*) FROM sched_triggers WHERE kind='cron' AND enabled=1;"));

        var back = svc.Export(null);
        var sectionA = JsonNode.Parse(JobsSection(export.Document!))!.AsArray();
        var sectionB = JsonNode.Parse(JobsSection(back.Document!))!.AsArray();
        foreach (var j in sectionA) j!["enabled"] = false; // jedyna zamierzona różnica: zaimportowane = wyłączone
        Assert.Equal(sectionA.ToJsonString(), sectionB.ToJsonString());
        for (var i = 0; i < 3; i++)
            Assert.Equal(JobDefinition.Parse("files", Encoding.UTF8.GetBytes(sectionA[i]!["definition"]!.ToJsonString())),
                JobDefinition.Parse("files", (byte[])Scalar(b, "SELECT definition FROM job_jobs WHERE name=$n;", ("$n", sectionA[i]!["name"]!.GetValue<string>()))!));

        var again = await svc.ImportAsync(export.Document!, false, ImportConflictPolicy.Fail, _ct); // ponowny import = bez zmian (definicje identyczne)
        Assert.Null(again.Code);
        Assert.False(again.Applied);
        Assert.All(again.Changes, c => Assert.Equal(("unchanged", "none"), (c.Kind, c.Action)));
    }

    [Fact, Trait("AC", "AC-29.1"), Trait("SEC", "SEC-6.7"), Trait("AC", "AC-01.3")]
    public async Task Export_contains_references_but_never_secret_values()
    {
        var pwSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var sftpPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        await File.WriteAllTextAsync(PwFile, pwSecret, _ct);
        await File.WriteAllTextAsync(Path.Combine(Secrets, "nas.json"), "{\"backup\":{\"user\":\"kopia\",\"password\":\"" + sftpPassword + "\"}}", _ct);
        var a = NewCatalog("a");
        AddJob(a, "j1", "Lokalne", LocalDef());
        AddJob(a, "j2", "Zdalne", SftpDef());
        var svcA = Service(a, out _);
        var text = Encoding.UTF8.GetString(svcA.Export(null).Document!);
        Assert.DoesNotContain(pwSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(sftpPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain("kopia", text, StringComparison.Ordinal); // login z magazynu też nie
        Assert.Contains("\"secretRef\": \"file:nas\"", text, StringComparison.Ordinal);
        Assert.Contains(JsonSerializer.Serialize(PwFile), text, StringComparison.Ordinal); // tylko ścieżka do pliku hasła

        // definicja z przemyconym sekretem w katalogu (spoza schematu) — eksport odmawia zamiast go wynieść
        AddJob(a, "j3", "Zepsute", LocalDef()[..^1] + ",\"password\":\"" + pwSecret + "\"}");
        var bad = svcA.Export(null);
        Assert.Equal(KbCode.ImportContainsSecret, bad.Code);
        Assert.Null(bad.Document);
        Assert.DoesNotContain(bad.Problems, p => p.Reason.Contains(pwSecret, StringComparison.Ordinal) || p.Path.Contains(pwSecret, StringComparison.Ordinal));
        Assert.Equal(2, svcA.Export(["Lokalne", "Zdalne"]).Jobs); // filtr nazw
        Assert.Equal(KbCode.JobNotFound, svcA.Export(["Brak"]).Code);
    }

    [Fact, Trait("AC", "AC-29.1"), Trait("BL", "BL-47")]
    public async Task Name_conflicts_follow_policy_fail_skip_overwrite_rename()
    {
        var a = NewCatalog("a");
        AddJob(a, "x1", "Dokumenty", LocalDef(), "0 21 * * *");
        AddJob(a, "x2", "Nowe", LocalDef(retention: false));
        var doc = Service(a, out _).Export(null).Document!;

        var b = NewCatalog("b");
        AddJob(b, "jb", "Dokumenty", LocalDef(retention: false), "0 22 * * *"); // ta sama nazwa, inna definicja i harmonogram
        Exec(b, "INSERT INTO run_runs(run_id,job_id,cause,state) VALUES ('r1','jb','manual','Succeeded');");
        var svc = Service(b, out _);

        var fail = await svc.ImportAsync(doc, false, ImportConflictPolicy.Fail, _ct);
        Assert.Equal(KbCode.ImportNameConflict, fail.Code);
        Assert.Equal("$.jobs[0].name", Assert.Single(fail.Problems).Path);
        Assert.Contains(new ImportChange("Dokumenty", "conflict", "blocked"), fail.Changes);
        Assert.Equal(1L, Count(b, "SELECT COUNT(*) FROM job_jobs;")); // wszystko albo nic — „Nowe” też nie powstało

        var dry = await svc.ImportAsync(doc, true, ImportConflictPolicy.Rename, _ct);
        Assert.Null(dry.Code);
        Assert.Equal([new ImportChange("Dokumenty", "conflict", "rename", "Dokumenty (import)"), new ImportChange("Nowe", "new", "create")], dry.Changes);
        Assert.Equal(1L, Count(b, "SELECT COUNT(*) FROM job_jobs;"));

        var skip = await svc.ImportAsync(doc, false, ImportConflictPolicy.Skip, _ct);
        Assert.True(skip.Applied);
        Assert.Equal(1L, Count(b, "SELECT enabled FROM job_jobs WHERE name='Dokumenty';")); // nietknięte
        Assert.Equal(0L, Count(b, "SELECT enabled FROM job_jobs WHERE name='Nowe';"));

        var rename = await svc.ImportAsync(doc, false, ImportConflictPolicy.Rename, _ct);
        Assert.Contains(new ImportChange("Dokumenty", "conflict", "rename", "Dokumenty (import)"), rename.Changes);
        Assert.Contains(new ImportChange("Nowe", "unchanged", "none"), rename.Changes);
        var rename2 = await svc.ImportAsync(doc, false, ImportConflictPolicy.Rename, _ct);
        Assert.Contains(new ImportChange("Dokumenty", "conflict", "rename", "Dokumenty (import 2)"), rename2.Changes);

        var over = await svc.ImportAsync(doc, false, ImportConflictPolicy.Overwrite, _ct);
        Assert.Contains(new ImportChange("Dokumenty", "conflict", "overwrite"), over.Changes);
        Assert.Equal("jb", Scalar(b, "SELECT job_id FROM job_jobs WHERE name='Dokumenty';")); // historia (run_runs) zostaje przy zadaniu
        Assert.Equal(0L, Count(b, "SELECT enabled FROM job_jobs WHERE name='Dokumenty';")); // nadpisane = wyłączone do sprawdzenia
        Assert.Contains("0 21 * * *", Encoding.UTF8.GetString((byte[])Scalar(b, "SELECT spec FROM sched_triggers WHERE trigger_id='cron-jb';")!), StringComparison.Ordinal);
        Assert.NotNull(JobDefinition.Parse("files", (byte[])Scalar(b, "SELECT definition FROM job_jobs WHERE job_id='jb';")!).Retention);

        Assert.Equal(JobDraftValidator.MaxNameLength, FreeName(new string('n', 200)).Length); // obcięcie bazy do limitu
        static string FreeName(string n) => JobTransferService.FreeName(n, new HashSet<string> { n });
    }

    [Fact, Trait("AC", "AC-29.1"), Trait("Plan", "SEC-7.6")]
    public async Task Protected_job_is_not_overwritten_by_import()
    {
        var a = NewCatalog("a");
        AddJob(a, "x1", "Chronione", LocalDef());
        var doc = Service(a, out _).Export(null).Document!;
        var b = NewCatalog("b");
        AddJob(b, "jb", "Chronione", LocalDef(retention: false), isProtected: true);
        var r = await Service(b, out _).ImportAsync(doc, false, ImportConflictPolicy.Overwrite, _ct);
        Assert.Equal(KbCode.ImportNameConflict, r.Code);
        Assert.Contains(new ImportChange("Chronione", "conflict", "blocked"), r.Changes);
        Assert.Null(JobDefinition.Parse("files", (byte[])Scalar(b, "SELECT definition FROM job_jobs WHERE job_id='jb';")!).Retention);
    }

    [Fact, Trait("AC", "AC-29.2"), Trait("SEC", "SEC-6.7")]
    public async Task Import_uses_wizard_rules_and_rejects_whole_file_without_partial_write()
    {
        var b = NewCatalog("b");
        var svc = Service(b, out _);
        byte[] Doc(params (string Name, string Def)[] jobs) => JobExportFormat.Write(new ExportDocument("1", DateTimeOffset.UnixEpoch,
            [.. jobs.Select(j => new ExportedJob(j.Name, "files", true, "UTC", "skip", null, null, JobExportFormat.CanonicalDefinition("files", Encoding.UTF8.GetBytes(j.Def)).Canonical!))]));

        var r = await svc.ImportAsync(Doc(("Dobre", LocalDef()), ("Brak zrodla", LocalDef(src: Path.Combine(_dir, "nie-ma")))), false, ImportConflictPolicy.Fail, _ct);
        Assert.Equal(KbCode.SourcesNotReadable, r.Code);
        Assert.Equal("$.jobs[0].definition.sources", Assert.Single(r.Problems).Path); // posortowane po nazwie: „Brak zrodla” pierwsze
        Assert.Equal(0L, Count(b, "SELECT COUNT(*) FROM job_jobs;"));

        Assert.Equal(KbCode.SourcesOverlap, (await svc.ImportAsync(Doc(("Petla", LocalDef(root: Path.Combine(Src, "kopie")))), false, ImportConflictPolicy.Fail, _ct)).Code);
        var unc = await svc.ImportAsync(Doc(("Klucz UNC", LocalDef().Replace(JsonSerializer.Serialize(KeyFile), "\"//attacker/share/k.key\"", StringComparison.Ordinal))), true, ImportConflictPolicy.Fail, _ct);
        Assert.Equal((KbCode.JobDefinitionInvalid, "$.jobs[0].definition.key"), (unc.Code, Assert.Single(unc.Problems).Path)); // materiał klucza z UNC — odmowa bez łączenia (Qb85)
        Assert.Equal(0L, Count(b, "SELECT COUNT(*) FROM job_jobs;"));
    }

    [Fact, Trait("AC", "AC-29.1")]
    public async Task Missing_secret_entry_and_key_file_are_flagged_for_the_wizard()
    {
        var a = NewCatalog("a");
        AddJob(a, "j2", "Zdalne", SftpDef());
        var doc = Service(a, out _).Export(null).Document!;
        var svc = Service(NewCatalog("b"), out _);
        Assert.Equal(new ImportChange("Zdalne", "new", "create", null, NeedsCredentials: true, MissingKeyMaterial: false), Assert.Single((await svc.ImportAsync(doc, true, ImportConflictPolicy.Fail, _ct)).Changes));
        await File.WriteAllTextAsync(Path.Combine(Secrets, "nas.json"), "{}", _ct);
        File.Delete(KeyFile);
        Assert.Equal(new ImportChange("Zdalne", "new", "create", null, NeedsCredentials: false, MissingKeyMaterial: true), Assert.Single((await svc.ImportAsync(doc, true, ImportConflictPolicy.Fail, _ct)).Changes));
    }

    // ---------------- RPC: role, audyt, CLI ----------------

    private async Task<(string Pipe, RecordingAudit Audit)> StartAgent(string catalog, AgentRole role)
    {
        var pipe = "kobian-xfer-" + Guid.NewGuid().ToString("N");
        var audit = new RecordingAudit();
        var backend = new CatalogAgentBackend(catalog, "9.9.9") { JobTransfer = Service(catalog, out _) };
        var app = AgentHost.Create(new AgentHostOptions(pipe), backend, new FixedRoleResolver(role), audit);
        _hosts.Add(app);
        await app.StartAsync(_ct);
        return (pipe, audit);
    }

    private async Task<(int Code, string Out, string Err)> Cli(string pipe, params string[] args)
    {
        using var client = new GrpcAgentClient(pipe, TimeSpan.FromSeconds(2));
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, client, o, e, _ct);
        return (code, o.ToString(), e.ToString());
    }

    [Theory, Trait("SEC", "SEC-6.7"), Trait("Plan", "D2.9"), Trait("Category", "Integration")]
    [InlineData(AgentRole.Reader)]
    [InlineData(AgentRole.Operator)]
    public async Task Export_and_import_require_administrator_and_denials_are_audited(AgentRole role)
    {
        Assert.Equal(AgentRole.Administrator, MethodPolicy.Find("/kobian.v1.Agent/ExportJobs")!.Required);
        Assert.Equal(AgentRole.Administrator, MethodPolicy.Find("/kobian.v1.Agent/ImportJobs")!.Required);
        Assert.True(MethodPolicy.Find("/kobian.v1.Agent/ImportJobs")!.Modifying);
        var a = NewCatalog("a");
        AddJob(a, "j1", "Dokumenty", LocalDef());
        var file = Path.Combine(_dir, "in.json");
        await File.WriteAllBytesAsync(file, ValidDocument(), _ct);
        var (pipe, audit) = await StartAgent(a, role);
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(pipe, "export")).Code); // PermissionDenied → brak dostępu (kod 3 jak przy braku usługi)
        Assert.Equal(CliApp.ExitUnavailable, (await Cli(pipe, "import", file)).Code);
        Assert.Equal(1L, Count(a, "SELECT COUNT(*) FROM job_jobs;"));
        Assert.Equal(["ExportJobs", "ImportJobs"], audit.Records.Select(r => r.Operation.Split('/')[^1]));
        Assert.All(audit.Records, r => Assert.Equal(("denied", KbCode.ApiAccessDenied), (r.Outcome, r.KbCode)));
    }

    [Fact, Trait("AC", "AC-29.1"), Trait("AC", "AC-29.2"), Trait("AC", "AC-37.1"), Trait("Category", "Integration")]
    public async Task Cli_export_and_import_over_pipe_with_exit_codes_json_and_audit()
    {
        var a = NewCatalog("a");
        AddJob(a, "j1", "Dokumenty", LocalDef(), "0 21 * * *");
        var (pipeA, auditA) = await StartAgent(a, AgentRole.Administrator);
        var file = Path.Combine(_dir, "eksport.json");

        var ex = await Cli(pipeA, "export", "--output", file, "--json");
        Assert.Equal(CliApp.ExitOk, ex.Code);
        Assert.StartsWith("{\"schema\":\"kobian.cli.export/1\",\"result\":\"ok\",\"jobs\":1", ex.Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitUsage, (await Cli(pipeA, "export", "--output", file)).Code); // bez --force nie nadpisuje
        Assert.Equal(CliApp.ExitOk, (await Cli(pipeA, "export", "--output", file, "--force")).Code);
        var stdout = await Cli(pipeA, "export", "--job", "Dokumenty");
        Assert.Equal(JobsSection(await File.ReadAllBytesAsync(file, _ct)), JobsSection(Encoding.UTF8.GetBytes(stdout.Out)));
        Assert.Equal(CliApp.ExitFailed, (await Cli(pipeA, "export", "--job", "Brak")).Code);
        var exportAudit = auditA.Records.First(r => r.Operation.EndsWith("ExportJobs", StringComparison.Ordinal));
        Assert.Equal(("allowed", "*"), (exportAudit.Outcome, exportAudit.Target)); // eksport (niemodyfikujący) też audytowany

        var b = NewCatalog("b");
        AddJob(b, "jb", "Dokumenty", LocalDef(retention: false));
        var (pipeB, auditB) = await StartAgent(b, AgentRole.Administrator);
        var dry = await Cli(pipeB, "import", file, "--dry-run", "--json");
        Assert.Equal(CliApp.ExitFailed, dry.Code); // konflikt bez --on-conflict
        using (var j = JsonDocument.Parse(dry.Out))
        {
            Assert.Equal("kobian.cli.import/1", j.RootElement.GetProperty("schema").GetString());
            Assert.Equal(KbCode.ImportNameConflict, j.RootElement.GetProperty("code").GetString());
            Assert.True(j.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal("blocked", j.RootElement.GetProperty("changes")[0].GetProperty("action").GetString());
            Assert.Equal("$.jobs[0].name", j.RootElement.GetProperty("issues")[0].GetProperty("path").GetString());
        }
        var ren = await Cli(pipeB, "import", file, "--on-conflict", "rename");
        Assert.Equal(CliApp.ExitOk, ren.Code);
        Assert.Contains("Dokumenty (import)", ren.Out, StringComparison.Ordinal);
        Assert.Contains("DISABLED", ren.Out, StringComparison.Ordinal);
        Assert.Equal(2L, Count(b, "SELECT COUNT(*) FROM job_jobs;"));

        var imp = auditB.Records.Where(r => r.Operation.EndsWith("ImportJobs", StringComparison.Ordinal)).ToList();
        Assert.Equal(["failed", "allowed"], imp.Select(r => r.Outcome));
        Assert.Equal(KbCode.ImportNameConflict, imp[0].KbCode);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file, _ct)));
        Assert.All(imp, r => Assert.Equal(sha, r.Params!["sha256"])); // który plik — bez treści
        Assert.Equal(("true", "Fail"), (imp[0].Params!["validate_only"], imp[0].Params!["on_conflict"]));
        Assert.DoesNotContain(imp.SelectMany(r => r.Params!.Values), v => v.Contains(Src, StringComparison.Ordinal));

        var bad = Path.Combine(_dir, "zly.json");
        await File.WriteAllBytesAsync(bad, ValidDocument(r => Job0(r)["preScript"] = "C:\\x.ps1"), _ct);
        var rej = await Cli(pipeB, "import", bad);
        Assert.Equal(CliApp.ExitFailed, rej.Code);
        Assert.Contains(KbCode.ImportActionsRejected + " $.jobs[0].preScript", rej.Err, StringComparison.Ordinal);
    }

    private sealed class CountingClient : IAgentClient
    {
        public int Imports { get; private set; }

        public Task<JobsImportResult> ImportJobsAsync(byte[] document, bool dryRun, ImportConflict onConflict, CancellationToken ct)
        {
            Imports++;
            return Task.FromResult(new JobsImportResult(OverallState.Ok, null, [], [], false));
        }

        public Task<AgentStatus> GetStatusAsync(CancellationToken ct) => throw new AgentUnavailableException();
        public Task<Kobian.Cli.Commands.RunOutcome> RunJobAsync(string job, bool wait, CancellationToken ct) => throw new AgentUnavailableException();
        public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(string? job, CancellationToken ct) => throw new AgentUnavailableException();
        public Task<Kobian.Cli.Commands.RunOutcome> RestoreAsync(string snapshot, string? pathFilter, string target, CancellationToken ct) => throw new AgentUnavailableException();
        public Task<InitResult> InitJobAsync(InitRequest request, CancellationToken ct) => throw new AgentUnavailableException();
        public Task<IReadOnlyList<ScheduleItem>> ListSchedulesAsync(string? job, CancellationToken ct) => throw new AgentUnavailableException();
        public Task<ScheduleSetResult> SetScheduleAsync(string job, string? cron, CancellationToken ct) => throw new AgentUnavailableException();
        public Task<CheckResult> CheckAsync(string job, int? samplePercent, long? rotation, CancellationToken ct) => throw new AgentUnavailableException();
        public Task<IReadOnlyList<RunItem>> ListRunsAsync(string? job, int? limit, CancellationToken ct) => throw new AgentUnavailableException();
        public Task<RetentionResult> ApplyRetentionAsync(string job, bool dryRun, CancellationToken ct) => throw new AgentUnavailableException();
    }

    [Fact, Trait("AC", "AC-37.1"), Trait("SEC", "SEC-3.3")]
    public async Task Cli_import_validates_arguments_and_size_before_calling_the_agent()
    {
        var c = new CountingClient();
        var big = Path.Combine(_dir, "big.json");
        await File.WriteAllBytesAsync(big, new byte[Kobian.Api.Contracts.ApiConventions.MaxJobsDocumentBytes + 1], _ct);
        var small = Path.Combine(_dir, "small.json");
        await File.WriteAllBytesAsync(small, ValidDocument(), _ct);
        async Task<(int, string)> Run(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var code = await CliApp.RunAsync(args, c, o, e, _ct);
            return (code, o + e.ToString());
        }
        Assert.Equal(CliApp.ExitFailed, (await Run("import", big)).Item1);
        Assert.Contains(KbCode.ImportTooLarge, (await Run("import", big, "--json")).Item2, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitUsage, (await Run("import", small, "--on-conflict", "merge")).Item1);
        Assert.Equal(CliApp.ExitUsage, (await Run("import", small, "--on-conflict")).Item1);
        Assert.Equal(CliApp.ExitUsage, (await Run("import", Path.Combine(_dir, "brak.json"))).Item1);
        Assert.Equal(CliApp.ExitUsage, (await Run("import")).Item1);
        Assert.Equal(0, c.Imports);
        Assert.Equal(CliApp.ExitOk, (await Run("import", small, "--dry-run", "--on-conflict", "skip")).Item1);
        Assert.Equal(1, c.Imports);
        Assert.Equal(CliApp.ExitUnavailable, (await Run("export")).Item1); // klient bez eksportu = brak usługi
    }
}

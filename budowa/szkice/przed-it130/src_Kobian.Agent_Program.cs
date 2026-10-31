// Host Agenta (BL-14/BL-15): gRPC po named pipe. Status/snapshoty z katalogu SQLite; RunJob = CatalogJobService (backup plików na dostawcę Local).
// Rola wywołującego: usługa — z tokenu klienta pipe (BL-17, TokenCallerResolver); konsola dev — Operator (bramką jest pipe CurrentUserOnly).
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Application.Storage;
using Kobian.Api.Contracts;
using Kobian.Catalog.Sqlite;

// it. 100 (D2.12, Qb50): custom action MSI — `--provision` / `--unprovision` (LocalSystem), bez startu hosta i bez dotykania katalogu.
if (AgentInstallPlan.Mode(args) is { } installMode)
    return AgentInstallCommand.Run(installMode, Console.Out);

// BL-72 (it. 91): tryb usługi Windows (SCM) albo konsola; ścieżki z AgentPaths (usługa: domyślnie %ProgramData%\Kobian\catalog.db — PROWIZORYCZNIE Qb41).
var runningAsService = AgentServiceRuntime.IsWindowsService();
var (paths, pathError) = AgentPaths.Resolve(Environment.GetEnvironmentVariable, runningAsService,
    OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) : null);
if (paths is null)
{
    await Console.Error.WriteLineAsync(pathError).ConfigureAwait(false);
    AgentServiceRuntime.ReportStartupFailure(pathError ?? "configuration error");
    return AgentExitCodes.Configuration;
}
var catalog = paths.Catalog;

// D2.15 pkt 3 / AC-37.3: pojedyncza instancja PRZED dotknięciem katalogu (druga instancja → KB-7007, catalog.db bez zmian).
using var instance = SingleInstanceGuard.TryAcquire(catalog, useGlobalMutex: OperatingSystem.IsWindows());
if (instance is null)
{
    var msg = Kobian.Domain.KbCode.SecondInstance + ": Kobian Agent is already running for this catalog — this instance stops without changes.";
    await Console.Error.WriteLineAsync(msg).ConfigureAwait(false);
    AgentServiceRuntime.ReportStartupFailure(msg);
    return AgentExitCodes.AlreadyRunning;
}

var mode = CatalogBootstrap.Bootstrap(catalog, TimeProvider.System);
if (mode == CatalogMode.Emergency)
{
    await Console.Error.WriteLineAsync("Catalog integrity check failed — emergency mode, service not started.").ConfigureAwait(false);
    AgentServiceRuntime.ReportStartupFailure("Catalog integrity check failed — emergency mode.");
    return AgentExitCodes.Emergency;
}

var version = typeof(AgentHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
await using var writer = new CatalogWriter(catalog);
// Katalog stanu Agenta (Qb14): KOBIAN_STATE_DIR, domyślnie katalog z catalog.db; sekrety (Qb16): KOBIAN_SECRETS_DIR, domyślnie <stan>/secrets — AgentPaths.
var stateDir = paths.StateDir;
var secretsDir = paths.SecretsDir;
// BL-73: cel SMB tylko na Windows (WNetAddConnection2 bez litery dysku; połączenia współdzielone przez równoległe sesje, rozłączane po ostatniej).
// BL-35 (D4.11, SEC-14.5): piny SFTP/FTPS z katalogu tożsamości (zatwierdzanie przez Administratora — ApproveServerIdentity); piny z pól hostKey
// istniejących definicji zadań przenoszone przy starcie jako zatwierdzone (kompatybilność wstecz; tylko gdy cel nie ma jeszcze pinu).
var identities = new ServerIdentityStore(catalog, writer, TimeProvider.System);
if (mode == CatalogMode.Normal) await identities.MigrateJobDefinitionsAsync(CancellationToken.None).ConfigureAwait(false);
#if KOBIAN_WINDOWS
using var smbConnector = new Kobian.Providers.Storage.Smb.SharedSmbConnector(new Kobian.Platform.Windows.Smb.WNetSmbConnector());
var providers = AgentProviders.Create(secretsDir, smbConnector, identities: identities);
#else
var providers = AgentProviders.Create(secretsDir, identities: identities);
#endif
// SEC-17.4: wstrzymanie retencji po skoku zegara (A17) — wspólne dla schedulera (ustawia), retencji (respektuje) i API (status, zwolnienie przez Administratora).
var hold = new RetentionHoldService(catalog, writer, TimeProvider.System);
var retention = new CatalogRetentionService(catalog, writer, TimeProvider.System, hold);
// PROWIZORYCZNIE Qb28: retencja (logiczne zapomnienie) automatycznie po udanej kopii zadania z polityką; KOBIAN_AUTO_RETENTION=0 = tylko `kobian forget`.
// RD-39 (PROWIZORYCZNIE Qb35): serwery SQL dozwolone dla konta usługi; brak konfiguracji = tylko ten komputer. Niepoprawny wpis = błąd startu.
var sqlServers = Kobian.Providers.Sources.MsSql.SqlServerAllowList.Parse(Environment.GetEnvironmentVariable("KOBIAN_SQL_ALLOWED_SERVERS"));
// it. 111 (D9.10, PROWIZORYCZNIE Qb58): webhook — KOBIAN_WEBHOOK_URL + <sekrety>/webhook.secret; zdarzenia przez trwały outbox w katalogu.
var (webhook, webhookError) = Kobian.Agent.Notifications.WebhookConfig.Load(Environment.GetEnvironmentVariable, secretsDir);
if (webhookError is not null)
{
    await Console.Error.WriteLineAsync(webhookError).ConfigureAwait(false);
    AgentServiceRuntime.ReportStartupFailure(webhookError);
    return AgentExitCodes.Configuration;
}
// BL-45 (9.6, SEC-9.1; PROWIZORYCZNIE Qb64): e-mail — KOBIAN_SMTP_* + <sekrety>/smtp.credentials; brak KOBIAN_SMTP_HOST = kanał wyłączony.
var email = Kobian.Notifications.Email.EmailConfig.Load(Environment.GetEnvironmentVariable, secretsDir, Environment.MachineName);
if (email.Error is not null)
{
    await Console.Error.WriteLineAsync(email.Error).ConfigureAwait(false);
    AgentServiceRuntime.ReportStartupFailure(email.Error);
    return AgentExitCodes.Configuration;
}
if (email.Warning is not null) await Console.Error.WriteLineAsync(email.Warning).ConfigureAwait(false);
List<Kobian.Agent.Notifications.NotifyChannel> notifyChannels = [];
if (webhook is not null) notifyChannels.Add(new(Kobian.Agent.Notifications.OutboxDispatcher.WebhookChannel, webhook.Minimal));
if (email.Settings is { } mail) notifyChannels.Add(new(Kobian.Agent.Notifications.OutboxDispatcher.EmailChannel, mail.Minimal, mail.ProblemsOnly));
var jobs = new CatalogJobService(catalog, writer, providers, CatalogJobService.HostIdFor(Environment.MachineName), TimeProvider.System)
{
    NotifyChannels = notifyChannels,
    AutoRetention = Environment.GetEnvironmentVariable("KOBIAN_AUTO_RETENTION") == "0" ? null : retention,
    // BL-40 cz. 2: zadania „mssql” (SqlClient 7.1.1, Encrypt=Mandatory + pin; login SQL z katalogu sekretów — PROWIZORYCZNIE Qb29).
    MsSqlSessions = Kobian.Agent.Secrets.AgentMsSql.CreateFactory(secretsDir),
    AllowedSqlServers = sqlServers,
    // BL-74: jedna kolejka zasobów (D7.10) dla harmonogramu i RunJob; limit równoległości PROWIZORYCZNIE 2 (jak dotąd w CatalogScheduler).
    Locks = new Kobian.Scheduler.Locks.LockManager(2),
    // RD-73 cz. 2 (PROWIZORYCZNIE Qb63): równoległe wysyłki packów z limitem AIMD celu; KOBIAN_PARALLEL_UPLOADS=0 = sekwencyjnie.
    UploadParallelism = Environment.GetEnvironmentVariable("KOBIAN_PARALLEL_UPLOADS") == "0" ? null : new CatalogParallelismStore(catalog, writer, TimeProvider.System),
};
await jobs.RecoverInterruptedAsync(CancellationToken.None).ConfigureAwait(false);
// Staging kopii baz (PROWIZORYCZNIE Qb31): KOBIAN_DB_STAGING (ścieżka Agenta) i KOBIAN_DB_SQL_STAGING (ta sama, widziana przez SQL Server); domyślnie <stan>/staging.
var dbStaging = Environment.GetEnvironmentVariable("KOBIAN_DB_STAGING");
var dbSqlStaging = Environment.GetEnvironmentVariable("KOBIAN_DB_SQL_STAGING");
var init = new RepoInitService(catalog, writer, providers, stateDir, TimeProvider.System)
{
    DatabaseStagingRoot = string.IsNullOrWhiteSpace(dbStaging) ? null : dbStaging,
    DatabaseSqlStagingRoot = string.IsNullOrWhiteSpace(dbSqlStaging) ? null : dbSqlStaging,
};
// BL-40 cz. 3: discovery MSSQL dla kreatora S1-db (piny certyfikatów w <stan>/certs — PROWIZORYCZNIE Qb30); ta sama walidacja źródła w UpsertJob/InitRepository.
var discovery = new DatabaseDiscoveryService(Path.Combine(stateDir, "certs"), Kobian.Agent.Secrets.AgentMsSql.CreateConnectionFactory(secretsDir)) { AllowedServers = sqlServers };
var restore = new CatalogRestoreService(catalog, writer, providers, TimeProvider.System)
{
    // BL-68: odtwarzanie baz MSSQL do nowej bazy (ta sama fabryka sesji i lista serwerów co kopie).
    MsSqlSessions = Kobian.Agent.Secrets.AgentMsSql.CreateFactory(secretsDir),
    AllowedSqlServers = sqlServers,
};
var probe = new DestinationProbeService(providers);
var jobValidator = new JobDraftValidator(catalog, probe, TimeProvider.System, discovery);
// RD-42 (D9.9): łańcuch audytu w katalogu — każda odmowa i metoda modyfikująca API (interceptor); weryfikacja przez VerifyAudit / `kobian audit verify`.
var audit = new Kobian.Agent.Audit.AuditLogService(catalog, writer, TimeProvider.System);
// BL-62: poziom poprawek .NET (≥ 10.0.13) — niższy = WARNING KB-7013 w statusie + zdarzenie (kopie działają dalej, PROWIZORYCZNIE Qb37).
var runtimeCode = RuntimeGuard.EvaluateCurrent();
// BL-45: jawne SMTP (KOBIAN_SMTP_INSECURE=1) — ostrzeżenie KB-6207 także w dzienniku audytu (widoczne w `kobian audit` i przeglądzie bezpieczeństwa).
if (email.Warning is not null)
    await audit.AppendAsync(new Kobian.Agent.Audit.AuditRecord("agent", "System", "scheduler", "notify.email.config", "smtp", new Dictionary<string, string> { ["tls"] = "none" },
        "allowed", Kobian.Domain.KbCode.SmtpInsecureEnabled), CancellationToken.None).ConfigureAwait(false);
if (runtimeCode is not null)
{
    await writer.ExecuteControlAsync((c, tx) =>
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO core_events(at,type,aggregate,payload) VALUES ($a,'pl.kobian.host.runtime_unpatched.v1','host:runtime',$p);";
        cmd.Parameters.AddWithValue("$a", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$p", System.Text.Encoding.UTF8.GetBytes("{\"code\":\"" + runtimeCode + "\",\"runtime\":\"" + Environment.Version + "\"}"));
        return cmd.ExecuteNonQuery();
    }).ConfigureAwait(false);
}
// BL-26 (3.7 V3): ta sama usługa check dla RPC i weryfikacji z harmonogramu; run.finished V3 przez outbox (A04).
var check = new CatalogCheckService(catalog, writer, providers, TimeProvider.System) { NotifyChannels = notifyChannels };
var app = AgentHost.Create(new AgentHostOptions(ApiConventions.DefaultPipeName) { WindowsService = runningAsService, RestoreTargets = RestoreTargets(runningAsService) }, new CatalogAgentBackend(catalog, version, jobs, init, restore, new ScheduleService(catalog, writer),
    check, TimeProvider.System, probe, jobValidator,
    retention, new CatalogPruneService(catalog, providers, TimeProvider.System, writer, CatalogJobService.HostIdFor(Environment.MachineName)), hold)
{
    Discovery = discovery,
    // BL-40 cz. 5: zatwierdzanie certyfikatów SQL (TOFU z potwierdzeniem Operatora — PROWIZORYCZNIE Qb32) → pliki .cer w <stan>/certs.
    SqlCertificates = new ServerCertificateService(Path.Combine(stateDir, "certs"), new Kobian.Providers.Sources.MsSql.Tls.TdsCertificateFetcher()) { AllowedServers = sqlServers },
    Audit = audit,
    RuntimeCode = runtimeCode,
    Identities = identities,
    // US-29 (BL-47): eksport/import zadań kobian-export v1 (Administrator); import sprawdza istnienie wpisów magazynu sekretów (AC-29.1).
    JobTransfer = new JobTransferService(catalog, writer, jobValidator, probe, version, TimeProvider.System) { SecretsDirectory = secretsDir },
}, CallerRoles(runningAsService), audit);
// Wyzwalacze czasowe (RD-26): KOBIAN_SCHEDULER=0 wyłącza pętlę (diagnostyka); domyślnie włączona.
Task schedulerLoop = Task.CompletedTask;
if (Environment.GetEnvironmentVariable("KOBIAN_SCHEDULER") != "0")
{
    // BL-26: V3 z harmonogramu wg polityki zadania (definicja „verify”, domyślnie co 7 dni, 5%); KOBIAN_VERIFY_SCHEDULE=0 wyłącza (tylko `kobian check`).
    var verify = Environment.GetEnvironmentVariable("KOBIAN_VERIFY_SCHEDULE") == "0" ? null : new CatalogVerifyScheduler(catalog, check, jobs.Locks!, TimeProvider.System, jobs);
    schedulerLoop = new CatalogScheduler(catalog, writer, jobs, TimeProvider.System) { RetentionHold = hold, Verify = verify }.RunAsync(app.Lifetime.ApplicationStopping);
}
// D9.9 (it. 99): kotwica dzienna łańcucha audytu o 06:55 czasu lokalnego (audit_anchors); zaległa po przerwie = jedna przy starcie.
var anchorLoop = new Kobian.Agent.Audit.AuditAnchorLoop(audit, TimeProvider.System, TimeZoneInfo.Local).RunAsync(app.Lifetime.ApplicationStopping);
using var webhookSender = webhook is null ? null : new Kobian.Notifications.Webhooks.WebhookSender(webhook.Url, webhook.Secret);
var outboxLoop = webhookSender is null ? Task.CompletedTask
    : new Kobian.Agent.Notifications.OutboxDispatcher(catalog, writer, webhookSender, TimeProvider.System) { Audit = audit }.RunAsync(app.Lifetime.ApplicationStopping);
// BL-45: osobny dyspozytor kanału „email” na tym samym outboxie (ponowienia i dead-letter jak webhook; dead = audyt KB-6203/KB-6201/KB-6206).
var emailLoop = email.Settings is null ? Task.CompletedTask
    : new Kobian.Agent.Notifications.OutboxDispatcher(catalog, writer, new Kobian.Notifications.Email.EmailSender(email.Settings, new Kobian.Notifications.Email.MailKitSmtpTransport()),
        TimeProvider.System) { Audit = audit }.RunAsync(app.Lifetime.ApplicationStopping);
// it. 112 (D9.11 + RD-61, PROWIZORYCZNIE Qb59): raport dzienny 07:00 z kotwicą audytu — przez ten sam outbox do każdego włączonego kanału (bez kanałów nie powstaje).
List<string> reportChannels = [.. notifyChannels.Select(ch => ch.Name)];
var reportLoop = reportChannels.Count == 0 ? Task.CompletedTask
    : new Kobian.Agent.Notifications.DailyReportService(catalog, writer, Convert.ToHexStringLower(CatalogJobService.HostIdFor(Environment.MachineName)), TimeProvider.System, TimeZoneInfo.Local)
    {
        Channels = reportChannels,
    }.RunAsync(app.Lifetime.ApplicationStopping);
// BL-82 (D9.7, D9.8): ewaluacja alertów A01–A17 co 60 s — stan w alert_alerts (pulpit), przejścia → outbox alert.*.v1 do włączonych kanałów.
// Błąd danych reguł (FormatException) wyłącza tylko ten komponent (D9.8 „błąd startu komponentu, nie usługi”); komunikat na stderr.
Task alertLoop = Task.CompletedTask;
Kobian.Notifications.Alerts.Rules.AlertRuleCatalog? alertRules = null;
try
{
    alertRules = Kobian.Notifications.Alerts.Rules.AlertRuleCatalog.LoadEmbedded();
}
catch (FormatException)
{
    await Console.Error.WriteLineAsync("Alert rules are invalid — alert evaluation disabled.").ConfigureAwait(false);
}
if (alertRules is not null && mode == CatalogMode.Normal)
    alertLoop = new Kobian.Agent.Notifications.AlertService(catalog, writer, Convert.ToHexStringLower(CatalogJobService.HostIdFor(Environment.MachineName)), TimeProvider.System, alertRules)
    {
        Channels = notifyChannels,
        SecurityUpdateAvailable = runtimeCode is not null,
    }.RunAsync(app.Lifetime.ApplicationStopping);
// RD-69 (it. 116, PROWIZORYCZNIE Qb61): sprzątanie katalogu — dziennik restore_* i wysłane powiadomienia po 30 dniach (RODO: nazwy plików klienta).
var maintenanceLoop = new Kobian.Agent.Maintenance.CatalogMaintenance(catalog, writer, TimeProvider.System).RunAsync(app.Lifetime.ApplicationStopping);
await app.RunAsync().ConfigureAwait(false);
await schedulerLoop.ConfigureAwait(false);
await anchorLoop.ConfigureAwait(false);
await outboxLoop.ConfigureAwait(false);
await emailLoop.ConfigureAwait(false);
await reportLoop.ConfigureAwait(false);
await alertLoop.ConfigureAwait(false);
await maintenanceLoop.ConfigureAwait(false);
return AgentExitCodes.Ok;

// SEC-6.3 (it. 101): w usłudze folder docelowy odtwarzania sprawdzany tokenem klienta (AccessCheck); konsola — ten sam użytkownik.
static IRestoreTargetAuthorizer RestoreTargets(bool service)
{
#if KOBIAN_WINDOWS
    if (service && OperatingSystem.IsWindows()) return new NamedPipeRestoreTargetAuthorizer();
#endif
    _ = service;
    return SameUserRestoreTargets.Instance;
}

// BL-17 (D2.9): w usłudze rola z tokenu klienta pipe (SID + grupy, AgentRoleMap); tryb konsoli (dev, CurrentUserOnly) — stała rola Operator.
static ICallerRoleResolver CallerRoles(bool service)
{
#if KOBIAN_WINDOWS
    if (service) return new TokenCallerResolver(new NamedPipeIdentityReader(), AgentRoleMap.ForLocalMachine());
#endif
    _ = service;
    return new FixedRoleResolver(AgentRole.Operator);
}

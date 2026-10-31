using System.Globalization;
using Kobian.UI.Services;
using Kobian.UI.Wizard;
using Xunit;

namespace Kobian.Tests.UI;

/// <summary>
/// S1-db kreatora (BL-40 cz. 4, R2.5.2, SCR-03): wybór „baza”, discovery tylko do odczytu, jedna baza, blokada „Dalej” bez uprawnień (AC-02.3 część UI),
/// szkic bez sekretów (AC-01.3) i bez folderów źródłowych (ADR-0018). Dane syntetyczne.
/// </summary>
public sealed class WizardDatabaseTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public WizardDatabaseTests() => CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

    private static WizardViewModel Db(FakeWizardService svc, string server = @"SRV01\SQLEXPRESS")
    {
        var vm = new WizardViewModel(svc);
        vm.SelectedSourceKind = vm.SourceKinds[1];
        vm.DbServer = server;
        return vm;
    }

    [Fact, Trait("BL", "BL-40"), Trait("Plan", "R2.5.2")]
    public void Service_without_databases_shows_only_files()
    {
        var vm = new WizardViewModel(new FakeWizardService());
        Assert.False(vm.ShowSourceKinds);
        Assert.True(vm.IsFilesSource);
        Assert.Null(vm.Draft().Database);
        Assert.True(new WizardViewModel(new FakeWizardService { SupportsDatabases = true }).ShowSourceKinds);
    }

    [Fact, Trait("BL", "BL-40"), Trait("Plan", "R2.5.2"), Trait("AC", "AC-02.3")]
    public async Task Database_step_discovers_lists_and_blocks_next_for_database_without_permission()
    {
        var svc = new FakeWizardService { SupportsDatabases = true, Discovery = FakeWizardService.SampleDiscovery() };
        var vm = Db(svc);

        await vm.NextCommand.ExecuteAsync(null); // bez discovery: komunikat, bez wywołania usługi
        Assert.Equal(WizardState.S1Sources, vm.State);
        Assert.True(vm.HasMessages);
        Assert.DoesNotContain(svc.Calls, c => c.StartsWith("validate", StringComparison.Ordinal));

        await vm.DiscoverDatabasesCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Databases.Count);
        Assert.Null(vm.SelectedDatabase); // dwie bazy z prawem kopii — wybór należy do użytkownika
        Assert.Contains("SQL Server 2022 (wersja 16)", vm.ServerInfo, StringComparison.Ordinal);
        Assert.Contains("Express", vm.ServerInfo, StringComparison.Ordinal);
        Assert.True(vm.HasPermissionScript);
        Assert.False(vm.IsPermissionScriptVisible);
        var archive = vm.Databases[1];
        Assert.Contains("TDE", archive.Notes, StringComparison.Ordinal); // ostrzeżenia KB jako tekst (WCAG 1.4.1)
        Assert.Equal("", vm.Databases[0].Notes);
        Assert.Contains("brak uprawnienia", vm.Databases[2].Notes, StringComparison.Ordinal);
        Assert.Single(vm.Databases[2].Notes.Split(" · ")); // KB-3003 nie dublowany obok „brak uprawnienia”

        vm.SelectedDatabase = vm.Databases[2]; // Kadry: bez BACKUP DATABASE
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S1Sources, vm.State);
        Assert.Contains(vm.Messages, m => m.Contains("KB-3003", StringComparison.Ordinal));
        Assert.True(vm.IsPermissionScriptVisible); // akcja naprawcza od razu widoczna (6.3)
        Assert.DoesNotContain(svc.Calls, c => c.StartsWith("validate", StringComparison.Ordinal));

        vm.SelectedDatabase = vm.Databases[0];
        Assert.Equal("Kadry", vm.JobName); // podpowiedź z pierwszego wyboru — nie nadpisywana kolejnym
        vm.JobName = "";
        vm.SelectedDatabase = vm.Databases[1];
        vm.SelectedDatabase = vm.Databases[0];
        Assert.Equal("Archiwum_2019", vm.JobName);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardState.S2Destination, vm.State);
        var draft = svc.Drafts[^1];
        Assert.Empty(draft.Sources); // zadanie „database” bez folderów (ADR-0018)
        Assert.Equal(new DatabaseSource(@"SRV01\SQLEXPRESS", false, null, null, "Firma"), draft.Database);
        Assert.Equal(@"Baza Firma na serwerze SRV01\SQLEXPRESS", vm.SourcesText);
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-01.3")]
    public async Task Sql_login_is_a_secret_reference_and_changing_server_invalidates_the_list()
    {
        var svc = new FakeWizardService { SupportsDatabases = true, Discovery = FakeWizardService.SampleDiscovery() };
        var vm = Db(svc);
        vm.SelectedDatabaseAuth = vm.DatabaseAuths[1];
        Assert.True(vm.IsSqlLogin);
        vm.DbCredential = " erp-sql ";
        vm.DbCertificate = "srv01.cer";
        await vm.DiscoverDatabasesCommand.ExecuteAsync(null);
        Assert.Equal(new DatabaseServerRequest(@"SRV01\SQLEXPRESS", true, "file:erp-sql", "srv01.cer"), svc.DiscoveryRequests[^1]);
        vm.SelectedDatabase = vm.Databases[0];
        Assert.Equal(new DatabaseSource(@"SRV01\SQLEXPRESS", true, "file:erp-sql", "srv01.cer", "Firma"), vm.Draft().Database);

        vm.DbServer = "SRV02"; // inny serwer: lista i wybór nieaktualne
        Assert.Empty(vm.Databases);
        Assert.Null(vm.SelectedDatabase);
        Assert.Null(vm.Draft().Database);
        Assert.False(vm.HasPermissionScript);
        Assert.Equal("", vm.ServerInfo);

        vm.SelectedSourceKind = vm.SourceKinds[0]; // powrót do plików: szkic bez bazy
        vm.NewSource = @"C:\Dane";
        vm.AddSourceCommand.Execute(null);
        Assert.Null(vm.Draft().Database);
        Assert.Equal([@"C:\Dane"], vm.Draft().Sources);
    }

    [Fact, Trait("BL", "BL-40"), Trait("Plan", "R2.6.5")]
    public async Task Discovery_failure_and_unsupported_service_show_kb_message()
    {
        var svc = new FakeWizardService { SupportsDatabases = true, Discovery = new DatabaseDiscovery("KB-3002", 0, false, false, [], false, null) };
        var vm = Db(svc);
        await vm.DiscoverDatabasesCommand.ExecuteAsync(null);
        Assert.False(vm.HasDatabases);
        Assert.Contains(vm.Messages, m => m.Contains("KB-3002", StringComparison.Ordinal));

        svc.Discovery = null; // usługa bez obsługi baz
        await vm.DiscoverDatabasesCommand.ExecuteAsync(null);
        Assert.Single(vm.Messages); // poprzedni komunikat wyczyszczony
        Assert.Contains("KB-7012", vm.Messages[0], StringComparison.Ordinal);

        svc.Discovery = FakeWizardService.SampleDiscovery() with { Databases = [new DatabaseItem("Jedyna", "FULL", 1_000, true, false, [])], PermissionScript = null };
        await vm.DiscoverDatabasesCommand.ExecuteAsync(null);
        Assert.False(vm.HasMessages);
        Assert.Equal("Jedyna", vm.SelectedDatabase?.Name); // jedna baza z prawem kopii — wybrana od razu
    }

    [Fact, Trait("BL", "BL-40"), Trait("Plan", "D5.8")]
    public void Server_line_names_product_year_and_conditions()
    {
        Assert.Equal("2019", WizardViewModel.ProductYear(15));
        Assert.Equal("18", WizardViewModel.ProductYear(18)); // nieznana wersja: numer, nie zgadywanie
        var line = WizardViewModel.ServerLine(new DatabaseDiscovery(null, 13, false, true, [], true, null));
        Assert.StartsWith("SQL Server 2016 (wersja 13)", line, StringComparison.Ordinal);
        Assert.Contains("500", line, StringComparison.Ordinal);
        Assert.Contains("brak baz", line, StringComparison.Ordinal);
    }

    [Fact, Trait("BL", "BL-40"), Trait("AC", "AC-01.3")]
    public void Grpc_adapter_maps_database_source_without_secrets()
    {
        var d = new WizardDraft("ERP", [], DestinationKind.Local, @"E:\Kopie", null, 22, null, null, null, 7, 4, 12,
            new DatabaseSource(@" SRV01\SQLEXPRESS ", true, "file:erp-sql", "srv01.cer", "Firma"));
        var spec = GrpcWizardService.Database(d)!;
        Assert.Equal((@"SRV01\SQLEXPRESS", "Firma", "sql", "file:erp-sql", "srv01.cer"), (spec.Server, spec.Name, spec.Auth, spec.SecretRef, spec.Certificate));
        var w = GrpcWizardService.Database(d with { Database = new DatabaseSource("srv", false, null, null, "B") })!;
        Assert.Equal("integrated", w.Auth);
        Assert.False(w.HasSecretRef);
        Assert.False(w.HasCertificate);
        Assert.Null(GrpcWizardService.Database(d with { Database = null }));
        Assert.Contains("database", GrpcWizardService.FieldsFor(WizardState.S1Sources));
    }
}

/// <summary>
/// BL-40 cz. 5 (Qb32): zatwierdzenie certyfikatu serwera SQL w S1-db — odcisk do porównania, zatwierdzenie wypełnia pole .cer,
/// KB-3007 (zmiana certyfikatu) zdejmuje nieaktualny odcisk, zmiana serwera unieważnia oczekujący certyfikat.
/// </summary>
public sealed class WizardCertificateTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public WizardCertificateTests() => CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

    private static WizardViewModel Db(FakeWizardService svc)
    {
        var vm = new WizardViewModel(svc) { };
        vm.SelectedSourceKind = vm.SourceKinds[1];
        vm.DbServer = @"SRV01\SQLEXPRESS";
        return vm;
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    public async Task Fetch_shows_grouped_fingerprint_and_approve_fills_certificate_field()
    {
        var svc = new FakeWizardService { SupportsDatabases = true, Discovery = FakeWizardService.SampleDiscovery(), Certificate = FakeWizardService.SampleCertificate() };
        var vm = Db(svc);
        Assert.True(vm.SupportsCertificateFetch);
        await vm.DiscoverDatabasesCommand.ExecuteAsync(null);
        Assert.True(vm.HasDatabases);

        await vm.FetchCertificateCommand.ExecuteAsync(null);
        Assert.True(vm.HasPendingCertificate);
        Assert.Equal("3F9A 0C7E 51B2 D844 66A1 E0C9 F27B 53D1\n8E6A 4C0B 92F1 D7E3 A5B8 C604 9D2E 7F10", vm.CertificateFingerprintText);
        Assert.Contains("CN=SRV01.firma.local", vm.CertificateDetailsText, StringComparison.Ordinal);
        Assert.Contains("samopodpisany", vm.CertificateDetailsText, StringComparison.Ordinal);
        Assert.Equal("", vm.DbCertificate); // pobranie niczego nie zatwierdza

        await vm.ApproveCertificateCommand.ExecuteAsync(null);
        Assert.Equal([(@"SRV01\SQLEXPRESS", FakeWizardService.SampleCertificate().Sha256, "SRV01-SQLEXPRESS-3f9a0c7e.cer")], svc.Approvals);
        Assert.Equal("SRV01-SQLEXPRESS-3f9a0c7e.cer", vm.DbCertificate);
        Assert.False(vm.HasPendingCertificate);
        Assert.Contains("SRV01-SQLEXPRESS-3f9a0c7e.cer", vm.CertificateStatus, StringComparison.Ordinal);
        Assert.False(vm.HasDatabases); // lista z discovery bez pinu unieważniona — ponowne „Pokaż bazy” idzie z pinem

        await vm.DiscoverDatabasesCommand.ExecuteAsync(null);
        Assert.Equal("SRV01-SQLEXPRESS-3f9a0c7e.cer", svc.DiscoveryRequests[^1].Certificate);
    }

    [Fact, Trait("BL", "BL-40"), Trait("SEC", "SEC-5.6")]
    public async Task Changed_certificate_on_approve_shows_KB3007_and_drops_stale_fingerprint()
    {
        var svc = new FakeWizardService { SupportsDatabases = true, Certificate = FakeWizardService.SampleCertificate(), Approval = new SqlCertificateApproval("KB-3007", null) };
        var vm = Db(svc);
        await vm.FetchCertificateCommand.ExecuteAsync(null);
        await vm.ApproveCertificateCommand.ExecuteAsync(null);
        Assert.True(vm.HasMessages);
        Assert.False(vm.HasPendingCertificate);
        Assert.Equal("", vm.DbCertificate);
        Assert.False(vm.ApproveCertificateCommand.CanExecute(null));
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Name_taken_keeps_pending_certificate_and_error_codes_are_shown()
    {
        var svc = new FakeWizardService { SupportsDatabases = true, Certificate = FakeWizardService.SampleCertificate(), Approval = new SqlCertificateApproval("KB-3008", null) };
        var vm = Db(svc);
        await vm.FetchCertificateCommand.ExecuteAsync(null);
        await vm.ApproveCertificateCommand.ExecuteAsync(null);
        Assert.True(vm.HasMessages);
        Assert.True(vm.HasPendingCertificate);

        svc.Certificate = new SqlCertificateCandidate("KB-3006", "", "", "", default, false, false, "");
        await vm.FetchCertificateCommand.ExecuteAsync(null);
        Assert.True(vm.HasMessages);
        Assert.False(vm.HasPendingCertificate);
    }

    [Fact, Trait("BL", "BL-40")]
    public async Task Changing_server_or_rejecting_clears_pending_certificate()
    {
        var svc = new FakeWizardService { SupportsDatabases = true, Certificate = FakeWizardService.SampleCertificate() };
        var vm = Db(svc);
        await vm.FetchCertificateCommand.ExecuteAsync(null);
        vm.DbServer = "INNY";
        Assert.False(vm.HasPendingCertificate);

        await vm.FetchCertificateCommand.ExecuteAsync(null);
        vm.RejectCertificateCommand.Execute(null);
        Assert.False(vm.HasPendingCertificate);
        Assert.Empty(svc.Approvals);
    }

    [Fact, Trait("BL", "BL-40")]
    public void Trusted_certificate_says_approval_is_optional()
    {
        var c = FakeWizardService.SampleCertificate() with { SelfIssued = false, SystemTrusted = true, Issuer = "CN=Firma CA" };
        var text = WizardViewModel.CertificateDetails(c);
        Assert.Contains("wystawca CN=Firma CA", text, StringComparison.Ordinal);
        Assert.Contains("zatwierdzenie opcjonalne", text, StringComparison.Ordinal);
        Assert.DoesNotContain("samopodpisany", text, StringComparison.Ordinal);
    }
}

namespace Kobian.Agent.Hosting;

/// <summary>Mapa ról i klasyfikacji „modyfikująca” per metoda kobian.v1.Agent (D2.9, D2.16). Metoda spoza mapy = odmowa.</summary>
public static class MethodPolicy
{
    /// <summary><c>Audited</c>: audyt także metody niemodyfikującej (np. eksport konfiguracji — ujawnienie, RD-42).</summary>
    public sealed record Entry(AgentRole Required, bool Modifying, bool Audited = false);

    private static readonly Dictionary<string, Entry> Map = new(StringComparer.Ordinal)
    {
        ["/kobian.v1.Agent/GetStatus"] = new(AgentRole.Reader, false),
        ["/kobian.v1.Agent/ListSnapshots"] = new(AgentRole.Reader, false),
        ["/kobian.v1.Agent/RunJob"] = new(AgentRole.Operator, true),
        ["/kobian.v1.Agent/Restore"] = new(AgentRole.Operator, true),
        ["/kobian.v1.Agent/InitJob"] = new(AgentRole.Operator, true),
        ["/kobian.v1.Agent/ListSchedules"] = new(AgentRole.Reader, false),
        ["/kobian.v1.Agent/SetSchedule"] = new(AgentRole.Operator, true),
        ["/kobian.v1.Agent/Check"] = new(AgentRole.Operator, true),
        ["/kobian.v1.Agent/ListRuns"] = new(AgentRole.Reader, false),
        // Probe łączy się z celem na koncie Agenta (koszt, ślad w logach serwera) → Operator; niczego nie zmienia → bez kontroli rewizji.
        ["/kobian.v1.Agent/ProbeDestination"] = new(AgentRole.Operator, false),
        ["/kobian.v1.Agent/UpsertJob"] = new(AgentRole.Operator, true),
        ["/kobian.v1.Agent/InitRepository"] = new(AgentRole.Operator, true),
        ["/kobian.v1.Agent/ApplyRetention"] = new(AgentRole.Operator, true),
        ["/kobian.v1.Agent/PreviewRetention"] = new(AgentRole.Reader, false),
        // Plan prune czyta cel na koncie Agenta (koszt) → Operator; tylko podgląd, bez zmian → bez kontroli rewizji.
        ["/kobian.v1.Agent/PlanPrune"] = new(AgentRole.Operator, false),
        // Prune usuwa dane na celu (po karencji) → Operator + kontrola rewizji klienta (stary klient nie wywoła operacji o nieznanej mu semantyce).
        ["/kobian.v1.Agent/Prune"] = new(AgentRole.Operator, true),
        // SEC-17.4 / D9.3: zwolnienie wstrzymania retencji po skoku zegara — wyłącznie Administrator (Operator mógłby sam „odblokować” zapominanie).
        ["/kobian.v1.Agent/ReleaseRetentionHold"] = new(AgentRole.Administrator, true),
        // Discovery MSSQL (BL-40 cz. 3): połączenie z serwerem SQL na koncie Agenta → Operator; tylko odczyt → bez kontroli rewizji.
        ["/kobian.v1.Agent/DiscoverDatabases"] = new(AgentRole.Operator, false),
        // BL-40 cz. 5: odczyt certyfikatu TLS serwera SQL (połączenie bez logowania) → Operator, niemodyfikujące.
        ["/kobian.v1.Agent/FetchSqlCertificate"] = new(AgentRole.Operator, false),
        // Zatwierdzenie zapisuje pin w <stan>/certs (zmienia zaufanie Agenta) → Operator + kontrola rewizji.
        ["/kobian.v1.Agent/ApproveSqlCertificate"] = new(AgentRole.Operator, true),
        // BL-63, SCR-06: przeglądanie folderu kopii — rola Viewer wg R2.6.1 (nazwy plików widzi każdy, kto widzi kopie); tylko odczyt drzew.
        ["/kobian.v1.Agent/BrowseTree"] = new(AgentRole.Reader, false),
        // RD-42: weryfikacja łańcucha audytu zwraca tylko liczbę wpisów, pierwszy zły seq i hash — bez treści wpisów → Reader.
        ["/kobian.v1.Agent/VerifyAudit"] = new(AgentRole.Reader, false),
        // SCR-09 (R2.6.1: Admin) — PROWIZORYCZNIE Qb38 Operator: tylko odczyt konfiguracji celów (ścieżki/hosty), bez sekretów.
        ["/kobian.v1.Agent/ListDestinations"] = new(AgentRole.Operator, false),
        // BL-68: odtworzenie bazy do NOWEJ bazy (bez nadpisania) → Operator jak restore plików; nadpisanie produkcji (AC-32.2) — Administrator + step-up, później.
        ["/kobian.v1.Agent/RestoreDatabase"] = new(AgentRole.Operator, true),
        // it. 98, R2.8.1: podgląd odtworzenia — czyta cel kopii na koncie Agenta i sprawdza folder docelowy (plik próbny usuwany przy zamknięciu) → Operator jak Restore;
        // danych nie zapisuje → bez kontroli rewizji.
        ["/kobian.v1.Agent/PlanRestore"] = new(AgentRole.Operator, false),
        // BL-35 (SCR-09, D4.11): lista tożsamości serwerów — publiczne odciski, bez sekretów → Operator jak ListDestinations (PROWIZORYCZNIE Qb38), niemodyfikujące.
        ["/kobian.v1.Agent/ListServerIdentities"] = new(AgentRole.Operator, false),
        // Zatwierdzenie/odrzucenie klucza serwera zmienia zaufanie Agenta (STRIDE S/E „malware w sesji Operatora”) → wyłącznie Administrator (D4.11, SEC-4.1).
        ["/kobian.v1.Agent/ApproveServerIdentity"] = new(AgentRole.Administrator, true),
        // SEC-14.5: „wysokie ryzyko” (pin mimo ważnego CA) — opcja Administratora.
        ["/kobian.v1.Agent/SetServerIdentityPolicy"] = new(AgentRole.Administrator, true),
        // US-29 (BL-47): eksport i import zadań — Administrator wg kontraktu D2.10 („Admin (bez sekretów)”, „Admin (kwarantanna akcji — SEC-6.7)”).
        // Eksport niczego nie zmienia (bez kontroli rewizji), ale ujawnia konfigurację (ścieżki, hosty) → audytowany. Import (także dry-run) — modyfikujący.
        ["/kobian.v1.Agent/ExportJobs"] = new(AgentRole.Administrator, false, Audited: true),
        ["/kobian.v1.Agent/ImportJobs"] = new(AgentRole.Administrator, true),
    };

    public static Entry? Find(string fullMethod) => Map.GetValueOrDefault(fullMethod);

    public static IReadOnlyCollection<string> Methods => Map.Keys;
}

# ADR-0008: Pulpit UI — model stanu ochrony i źródło danych (PROWIZORYCZNIE Qb17, Qb18)

- Status: PROWIZORYCZNIE (it. 57, 2026-10-14)
- Kontekst: 6.3 (model stanu), R2.6.1 (SCR-01), R2.6.4 (brak połączenia), D6.1 (Avalonia 12), D2.4/D2.10 (API).
- Decyzja:
  1. Stan ochrony liczy czysta funkcja `ProtectionEvaluator` w Kobian.UI (bez zegara i UI); waga Chronione < Wstrzymane < Uwaga < Zagrożone; stan globalny = najgorszy.
  2. Tolerancja zaległości = max(1 h, 25% cyklu) (Qb17); < 15% miejsca = Uwaga (plan).
  3. Źródło danych: `GrpcDashboardSource` (GetStatus + ListSnapshots) przez named pipe; brak połączenia = IOException → nagłówek „Usługa Kobian nie działa” (KB-9001), nigdy „brak zadań”.
  4. Braki kontraktu (Qb18): następne uruchomienie, cykl, verify_level, cel/miejsce, rola — wartości zastępcze opisane w docs/ui/README.md; rozszerzenie kontraktu = BL-60.
- Alternatywy: stan liczony w Agencie i podawany gotowy (lepsze dla zasobnika i CLI — rozważyć przy BL-60); dodanie Cronos do UI dla następnego uruchomienia (odrzucone — dane powinny przyjść z Agenta).
- Konsekwencje: UI bez nowych pakietów poza audytowanymi; logika stanu testowana tabelą; po BL-60 adapter zmienia się bez zmian w widokach.
- Koszt zmiany zdania: mały (adapter + stałe).

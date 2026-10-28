# ADR-0030 — Dławienie otwarć sesji i breaker celu (D4.16 pkt 3, SEC-24.4) — PROWIZORYCZNIE Qb47

- Data: 2026-10-15, iteracja 97 (prowadzi rola 7; 4, 11, 13). Status: przyjęte prowizorycznie (Qb47).
- Kontekst: D4.16 pkt 3 wymaga ≤ 1 otwarcia sesji na 2 s na cel (kubełek, burst 2, jitter) i breakera celu na 10 min po 3 nieudanych otwarciach w godzinie; SEC-24.4 traktuje to jako ochronę konta przed blokadą. Do it. 96 każde `OpenAsync` szło prosto do serwera (PM-26: auto-blokada IP NAS/fail2ban przy burzy ponowień).
- Decyzja: `SessionOpenGovernor` (Kobian.Providers.Storage/Throttling) + dekorator `GovernedStorageProviderFactory` dla `sftp` i `smb` w `AgentProviders` (jedna instancja na proces Agenta; `local` bez dławienia). Klucz = `schemat|DestinationId`. Zegar monotoniczny. Anulowanie przez wywołującego nie jest porażką, anulowany czekający oddaje rezerwację. Breaker zwraca `StorageException(Transient, KB-2114)` bez kontaktu z serwerem. Bez nowych pakietów (Polly nie jest potrzebne dla tego elementu).
- Konsekwencje: zadanie SMB z 4 sesjami czeka dodatkowo ≈ 2–4 s na starcie (kolejne sesje na wspólnym połączeniu WNet też są liczone — świadome uproszczenie); probe w kreatorze po 3 błędnych hasłach dostaje KB-2114 na 10 min; restart usługi zeruje stan.
- Alternatywy: KB-2003 (plan używa go dla breakera operacji D4.10); klucz per serwer+konto; zapis stanu w katalogu (`dest_targets`); zerowanie okna po sukcesie.
- Koszt zmiany zdania: mały (jedna klasa, parametry `init`).
- Otwarte: AIMD (D4.16 pkt 2, KB-2112 → równoległość × 0,5) i unieważnianie puli po wznowieniu/zmianie sieci (pkt 4).

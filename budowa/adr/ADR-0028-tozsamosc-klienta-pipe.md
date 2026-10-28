# ADR-0028 — Tożsamość klienta pipe i role z tokenu (BL-17)

- Status: PROWIZORYCZNIE (Qb45), it. 95 (rola 5 prowadzi; 5,2,11)
- Dotyczy: D2.9, D2.15, SP-02 (Q44), SEC-2.2, D9.9 (audyt), ADR-0024/Qb41 (usługa), Qb36 (aktor „local-pipe”)

## Kontekst
Usługa (BL-72) przyjmowała połączenia od SYSTEM/Administrators/„Kobian Operators”, ale każdy dopuszczony klient dostawał rolę Operator (`FixedRoleResolver`, Qb41), a audyt zapisywał aktora „local-pipe”. Klienci (CLI/UI) łączyli się z domyślnym poziomem impersonacji (Impersonation), choć D2.9 wymaga Identification.

## Decyzja
1. Port `IPipeClientIdentityReader` (Agent/Hosting) + adapter Windows `NamedPipeIdentityReader`: `IConnectionNamedPipeFeature.NamedPipe.RunAsClient` → `WindowsIdentity.GetCurrent(Query)` → SID, grupy włączone (bez deny-only), poziom tokenu. Cache per połączenie w `ConditionalWeakTable` po obiekcie strumienia.
2. `AgentRoleMap` (czysta logika, testy w Linuksie): SYSTEM / Administratorzy z grupą włączoną (= podniesiony) / „Kobian Admins” → Administrator; „Kobian Operators” → Operator; reszta dopuszczonych → Reader. Fail-closed → `None`: brak tokenu, Anonymous, token procesu zamiast klienta (`ImpersonationLevel = None`), niepoprawny SID, SID NETWORK.
3. `TokenCallerResolver`: wyjątek odczytu = brak tożsamości; interceptor zwraca `Unauthenticated` + **KB-7001** (nowy kod) i audytuje odmowę; aktor w audycie = SID klienta.
4. CLI i UI łączą się z `TokenImpersonationLevel.Identification`.
5. **PROWIZORYCZNIE Qb45:** grupa „Kobian Admins” (z mapy ról D2.9) dostaje wpis ACL pipe **tylko ReadWrite** — D2.9 wymienia ją w mapie ról, ale nie w ACL; bez wpisu jej członek spoza Administratorów nie mógłby się połączyć.
6. Tryb konsoli (dev, CurrentUserOnly) — bez zmian: `FixedRoleResolver(Operator)`, aktor „local-pipe”.

## Alternatywy
- (5) bez wpisu ACL dla „Kobian Admins” (dosłownie D2.9): rola Administrator tylko dla podniesionych administratorów lokalnych; grupa Kobian Admins byłaby martwa, chyba że jej członek jest też w Operators.
- (1) `ImpersonateNamedPipeClient` przez P/Invoke (plan B SP-02) — gdy `RunAsClient` w Kestrel nie zadziała na Identification.
- Cache per połączenie przez `IConnectionLifetimeFeature` zamiast słabej tabeli — więcej kodu, ten sam efekt.

## Skutki i ryzyka
- Do potwierdzenia na Windows (W-17, W-37): czy `GetCurrent(Query)` na tokenie Identification zwraca klienta (a nie proces), czy `Groups` pomija deny-only. Niepowodzenie = fail-closed (KB-7001), nie eskalacja.
- Brak jeszcze: SEC-21.3 (`operators[]` per zadanie), limit odmów A15, RPC `GetCallerInfo` dla UI (wyszarzanie akcji) — kandydaci RD.
- Koszt zmiany zdania: mały (jeden argument `AgentPipeAcl.Plan`, jedna linia w `AgentRoleMap`).

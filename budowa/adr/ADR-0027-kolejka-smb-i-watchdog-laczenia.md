# ADR-0027 — Zasób kolejki `smb:<serwer>` i watchdog łączenia SMB

- Status: PROWIZORYCZNIE (Qb44), it. 94 (rola 4 prowadzi; 4,7,6,11)
- Dotyczy: D7.4, D7.10 (L4), Q20, D4.13, D4.8 (1219 → KB-2101), ADR-0025/Qb42, ADR-0026/Qb43, scenariusz E6

## Kontekst
`SharedSmbConnector` odmawia od razu (1219 → KB-2101) drugiemu kontu do serwera z aktywnym połączeniem — zgodnie z planem rozdzielać takie zadania ma kolejka (D7.4).
`CatalogScheduler` miał tylko semafor równoległości; `LockManager` (D7.10, z obsługą `smb:` i konta — Q20) istniał w Kobian.Scheduler, ale nie był używany.
`WNetAddConnection2` blokuje wątek bez limitu (DNS, timeout SMB), a `SharedSmbConnector` trzyma przy tym bramkę serwera; słownik bramek nie był czyszczony (Niskie it. 93).

## Decyzja
1. `CatalogScheduler` nabywa „wszystko albo nic” zasoby uruchomienia w `LockManager(maxParallel)` (slot globalny + zasoby z `RunResourceLocks`); brak zasobu → wiersz zostaje `Queued` do kolejnego obrotu pętli (kolejność `queued_at`; postarzanie priorytetu D7.10 — gdy pojawią się priorytety).
2. Cel `smb` → `LockRequest("smb:" + serwer małymi literami, Shared, Account = secretRef)`. **PROWIZORYCZNIE:** kontem jest odwołanie do wpisu sekretu, nie nazwa użytkownika — scheduler nie czyta sekretów.
3. Watchdog łączenia w `SharedSmbConnector`: próba w wątku puli z limitem 30 s (D4.13, bezczynność „sterowania”); po limicie/anulowaniu błąd 121 (→ KB-2001, przejściowy) albo OCE, a spóźnione połączenie jest zamykane w tle. Kolejna próba do serwera czeka (≤ limit) na powrót porzuconej, potem 121. Bramki per serwer z licznikiem użytkowników — usuwane, gdy nieużywane.
4. Uruchomienie ręczne (RunJob) jeszcze nie przechodzi przez kolejkę — BL-74.

## Alternatywy
- (2) konto = rzeczywista nazwa użytkownika z magazynu sekretów (RD-55): dokładne, ale scheduler czyta sekrety przy każdym obrocie pętli; albo `smb:<serwer>` wyłącznie (wszystkie zadania SMB do serwera po kolei) — prostsze, wolniejsze.
- (3) czekanie w dostawcy na zwolnienie innego konta zamiast 1219 — zakleszcza się, gdy jedno uruchomienie samo trzyma oba konta (backup + retencja).

## Skutki i ryzyka
- Dwa różne wpisy z tym samym kontem są niepotrzebnie serializowane (bezpieczne).
- Jeden wpis z różnymi kontami backup/maintenance (zalecane D4.1) w dwóch równoległych uruchomieniach może trafić na KB-2101 w fazie retencji — udokumentowane w docs/admin/cel-smb.md; RD-55.
- Porzucony wątek wisi do powrotu wywołania systemowego — najwyżej jeden na serwer (kolejne próby czekają zamiast tworzyć nowe).
- Koszt zmiany zdania: mały (jedna metoda `RunResourceLocks.For`; konstruktor `SharedSmbConnector` z parametrem limitu).

# Ankieta startowa — Kobian Backup (budowa)

Wpisz odpowiedź po „ODPOWIEDŹ:” (np. „B — uzasadnienie”) i zapisz plik. Bez S1–S5 kodu nie piszę. Pytania S6–S10 mają wartości domyślne (użyję ich, jeśli zostawisz puste).

S1: Kierunek pierwszego wydania [A — tylko pliki: lokalne, USB, SMB, SFTP / B — pliki + bazy MSSQL/ERP wg D1.3 (rekomendowane) / C — pełne 1.0 z planu] → ODPOWIEDŹ:
S2: Złożoność instalacji [A — portable ZIP bez usługi (brak VSS) / B — MSI per-machine z usługą Windows (rekomendowane) / C — jak B + instalacja cicha/GPO/winget + podpisane aktualizacje / D — najpierw A jako prototyp, potem B] → ODPOWIEDŹ:
S3: Interfejs [A — CLI + JSON najpierw, GUI później / B — GUI Avalonia + CLI równolegle (rekomendowane) / C — tylko GUI] → ODPOWIEDŹ:
S4: Środowisko testowe Windows [A — maszyna/VM Windows, uruchomię kolejkę testów / B — runner CI (jaki?) / C — tylko Linux w piaskownicy; moduły Windows weryfikuję później sam] → ODPOWIEDŹ:
S5: Zgody techniczne (tak/nie osobno) [(a) SDK .NET w przestrzeni użytkownika z weryfikacją sum; (b) pakiety NuGet z audytowanej listy; (c) kontenery Docker do testów (OpenSSH, vsftpd, Samba, SQL Server Developer); (d) git w folderze projektu i prawo usuwania plików w nim] → ODPOWIEDŹ:
S6: Tempo [domyślnie: co 1 h, 14 iteracji w rundzie, ≤ 800 linii kodu + testy na iterację] → ODPOWIEDŹ:
S7: Poziom bezpieczeństwa [A — standard planu (rekomendowane) / B — podwyższony: fuzzing od wczesnych iteracji + zewnętrzny przegląd krypto przed M2] → ODPOWIEDŹ:
S8: Języki i systemy [domyślnie UI PL/EN; Windows 10 22H2 / 11 / Server 2016–2025 (D1.5) — potwierdź lub zawęź] → ODPOWIEDŹ:
S9: Licencja i model [domyślnie open-core Apache-2.0 (D10.4); nazwa produktu wg Q39 — potwierdź] → ODPOWIEDŹ:
S10: Powiadomienia [domyślnie: koniec rundy / wysoka podatność / potrzebna decyzja / błąd przebiegu — zmień, jeśli chcesz inaczej] → ODPOWIEDŹ:

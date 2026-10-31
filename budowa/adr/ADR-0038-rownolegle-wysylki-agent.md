# ADR-0038 — Równoległe wysyłki packów w Agencie (RD-73 cz. 2, D4.16 pkt 2–3)

- Status: PROWIZORYCZNIE (Qb63), it. 119, role 14,3,4,11,13
- Kontekst: it. 118 dodała w silniku `BackupPipeline.PackUploaders` (każdy uploader z własną sesją) i port `IUploadParallelism`; Agent wysyłał sekwencyjnie. D4.16: równoległość celu sterowana AIMD (pkt 2), otwarcia sesji przez governor (pkt 3).
- Decyzja:
  1. `CatalogJobService.UploadParallelism` (`CatalogParallelismStore`); Program.cs włącza domyślnie, `KOBIAN_PARALLEL_UPLOADS=0` wyłącza.
  2. N = `AimdParallelism.Current` (wyuczony limit celu z `repo_dest_parallelism`, inaczej `RecommendedParallelism` dostawcy: Local 2, SMB 4, SFTP 3). Pula = główny uploader + N−1 sesji dodatkowych otwieranych przez fabrykę (dla sftp/smb — `GovernedStorageProviderFactory`). Główny uploader jest bezczynny w fazie packów (indeks i snapshot po `DrainAsync`), więc może być w puli.
  3. Odmowa otwarcia sesji dodatkowej: KB-2112 → sygnał AIMD (×0,5) i kopia na otwartych; inny błąd przejściowy → kopia na otwartych bez sygnału; błąd trwały/bezpieczeństwa → uruchomienie kończy się błędem (np. zmiana klucza hosta między otwarciami).
  4. Wzrost AIMD w trakcie uruchomienia nie otwiera nowych sesji (pula stała na czas uruchomienia); wyuczony limit zapisywany po uruchomieniu także po błędzie (best effort — błąd katalogu nie zmienia wyniku kopii).
  5. Pula 1 sesji = ścieżka sekwencyjna potoku (bez zmian względem it. 117).
- Alternatywy: (a) zawsze `RecommendedParallelism` sesji, limit tylko z AIMD — więcej otwarć przy wyuczonym niskim limicie; (b) otwieranie sesji na żądanie przy wzroście AIMD — złożoność w silniku (sesje w trakcie potoku); (c) wyłączone domyślnie — wolniej, bez ryzyka pamięci.
- Konsekwencje: pełna kopia na Local w chmurze 58–60 → 69–70 MiB/s (+~17%), szczyt zestawu roboczego Bench 236–245 → 367–426 MiB (pomiary.md, it. 119) — NFR-05 (≤ 500 MB) do sprawdzenia na Windows (W-48) przed domyślnym włączeniem w MSI; koszt zmiany zdania: jedna linia w Program.cs (domyślnie wyłączone) albo zmienna środowiskowa.

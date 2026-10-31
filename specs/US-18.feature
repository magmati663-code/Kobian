# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-18 @M
Funkcja: US-18 — Wznawianie przerwanego transferu na granicy packa

  @AC-18.1
  Scenariusz: AC-18.1
    Zakładając upload 2 GB na SFTP, zerwanie połączenia przy 60%
    Gdy restart usługi i wznowienie
    Wtedy ponownie wysłany ≤ 1 pack; powstaje dokładnie jeden snapshot (E4)

  @AC-18.2
  Scenariusz: AC-18.2
    Zakładając cel SFTP bez ochrony; pack o tej samej nazwie już istnieje
    Gdy CommitAsync StagedRename
    Wtedy KB-2301, istniejący obiekt nietknięty (porównanie sumy przed i po), plik .tmp posprzątany jeśli DeleteAllowed

  @AC-18.3
  Scenariusz: AC-18.3
    Zakładając pack 64 MiB, limit 300 KiB/s
    Gdy upload
    Wtedy zakończony bez KB-2001 z powodu timeoutu; przy zerwaniu łącza na 70 s wykrycie ≤ 90 s i wznowienie

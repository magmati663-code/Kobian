# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-43 @M
Funkcja: US-43 — kobian-restore odtwarza repo bez instalacji i usługi

  @AC-43.1
  Scenariusz: AC-43.1
    Zakładając repo utworzone przez wersję N; komputer bez instalacji
    Gdy kobian-restore.exe (podpisany, przenośny) + hasło
    Wtedy drzewo odtworzone identyczne z restore przez usługę (porównanie hashy i metadanych)

# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-30 @M
Funkcja: US-30 — Wykrycie ERP po schemacie: Optima, Subiekt GT (nexo/Symfonia — S po Q22)

  @AC-30.1
  Scenariusz: AC-30.1
    Zakładając instancja z bazą konfiguracyjną Optimy i 3 firmami
    Gdy krok 1 kreatora
    Wtedy komunikat „Comarch ERP Optima — 1 konfiguracyjna, 3 firmy”; grupa spójności 4 baz w jednym snapshotcie

  @AC-30.2
  Scenariusz: AC-30.2
    Zakładając baza o dowolnej nazwie z tabelami dok__Dokument, tw__Towar, kh__Kontrahent
    Gdy discovery
    Wtedy rozpoznana jako Subiekt GT; COPY_ONLY wymuszone, „Łańcuch Kobian” tylko z ostrzeżeniem

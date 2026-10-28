# ADR-0037 — Webhook z transakcyjnym outboxem (D9.10, SEC-9.3, SEC-9.6) — PROWIZORYCZNIE Qb58

- Data: 2026-10-17, iteracja 111 (prowadzi rola 6; 9, 11). Status: przyjęte prowizorycznie (Qb58).
- Kontekst: BL-82 wymaga powiadomień bez utraty zdarzeń przy restarcie usługi i bez nowych pakietów; webhook to cel SSRF (adres podaje administrator, ale DNS może wskazywać sieć wewnętrzną).
- Decyzja: `CatalogJobService.FinishRunAsync` dopisuje zdarzenie do `core_outbox` (kanał „webhook”) w tej samej transakcji co wynik; `OutboxDispatcher` (co 15 s, partie 20) wysyła przez `WebhookSender` (HttpClient/SocketsHttpHandler: bez przekierowań, bez proxy, bez cookies, ConnectCallback tylko z adresami publicznymi po rozwiązaniu DNS, limity 10 s/30 s/64 KiB). Podpis `v1=HMAC-SHA256(sekret, ts + "." + treść)` (WebhookSignature, okno 300 s po stronie odbiorcy). Dostarczenie co najmniej raz; Webhook-Id stały przy ponowieniach.
- PROWIZORYCZNIE: (a) jeden odbiorca; (b) brak alertu KB-6301 przy przejściu w dead (BL-45 cz. 2); (c) sekret w pliku katalogu sekretów, nie w DPAPI.
- Alternatywy: wysyłka synchroniczna po zakończeniu uruchomienia (gubi zdarzenia przy awarii); osobna tabela kolejki per kanał.
- Koszt zmiany zdania: mały.

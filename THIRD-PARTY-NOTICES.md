# Zależności zewnętrzne i licencje

Kobian Backup — licencja projektu: Apache-2.0 (D10.4, potwierdzone S9). Lista obejmuje pakiety dystrybuowane z produktem oraz narzędzia testowe (oznaczone). Aktualizowana przy każdej zmianie `Directory.Packages.props` (audyt: `budowa/zaleznosci.md`).

| Pakiet | Wersja | Licencja | Użycie | Dystrybuowany |
|---|---|---|---|---|
| .NET runtime (self-contained) | 10.0.x | MIT | całość | tak |
| System.Formats.Cbor | 10.0.12 | MIT | Kobian.Format | tak |
| ZstdSharp.Port | 0.8.8 | MIT | Kobian.Engine | tak |
| Cronos | 0.13.0 | MIT | Kobian.Scheduler | tak |
| FluentFTP | 55.0.0 | MIT | Kobian.Providers.Storage (cel FTP/FTPS, BL-34) | tak |
| MailKit, MimeKit | 4.18.1 | MIT | Kobian.Notifications (e-mail, BL-45) | tak |
| BouncyCastle.Cryptography (zależność MimeKit) | 2.7.0 | MIT | Kobian.Notifications (pośrednio) | tak |
| xunit.v3 (+ zależności Microsoft.Testing.Platform) | 4.0.2 | Apache-2.0 / MIT | testy | nie |

Pełne teksty licencji MIT i Apache-2.0 zostaną dołączone do instalatora (BL-48) wraz z SBOM (D10.6).

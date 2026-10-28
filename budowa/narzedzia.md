# Narzędzia budowy (S5a)

| Data | Narzędzie | Wersja | Źródło | Weryfikacja | Gdzie | Uwagi |
|---|---|---|---|---|---|---|
| 2026-10-12 | .NET SDK | 10.0.401 (runtime 10.0.12, wydanie 2026-09-08, security) | builds.dotnet.microsoft.com (oficjalne) | SHA-512 zgodny z `release-metadata/10.0/releases.json` | środowisko budowy Linux (chmura sesji) | Próba instalacji w folderze projektu (`.tools/`) przerwana: zapis ~700 MB na zamontowany dysk Windows zbyt wolny (> 3 min/216 MB), `$HOME` maszyny lokalnej ma ~100 MB wolnego. Częściowa instalacja usunięta (za zgodą S5d). |

## Przepływ synchronizacji
Kod jest budowany i testowany w środowisku Linux sesji, a następnie pakowany `tools/pack-sync.sh` do `budowa/sync/kobian-sync.tgz` i rozpakowywany do folderu (`tar --overwrite`). Źródłem prawdy jest folder projektu: przebieg zaczyna od spakowania folderu i rozpakowania go w środowisku budowy.

Uwaga dla przebiegów zaplanowanych (bez chmury z SDK): jeśli `dotnet` nie jest dostępny, zmiany oznaczaj „niezweryfikowana kompilatorem” (prompt, krok 3).

## 2026-10-13 (it. 42) — SDK na urządzeniu
| Data | Narzędzie | Wersja | Weryfikacja | Gdzie | Uwagi |
|---|---|---|---|---|---|
| 2026-10-13 | .NET SDK | 10.0.401 | SHA-512 z releases.json zgodny (`sha512sum -c`: OK); rozmiary wszystkich plików po rozpakowaniu zweryfikowane względem tar | `.toolchain/dotnet` (skrypt środowiska: `.toolchain/env.sh` — NUGET_PACKAGES i DOTNET_CLI_HOME w `.toolchain`, bo `$HOME` i `/tmp` urządzenia są pełne) | `dotnet --version` = 10.0.401 działa. ALE `dotnet restore Kobian.sln` na zamontowanym dysku wisi >150 s na „Determining projects to restore” (wolny I/O), a procesy w tle nie przeżywają końca wywołania device_bash → build lokalny niepraktyczny; użyto ścieżki chmurowej (pack → stage → ci.sh w chmurze). Rozpakowanie wymagało `tar --skip-old-files` w kilku podejściach (limit 180 s); `rm` plików w `.toolchain` niedozwolone (brak zgody na usuwanie) — `.toolchain/dl/sdk.tgz` (240 MB) do usunięcia przy najbliższej zgodzie. |

## 2026-10-14 (it. 71) — SDK 10.0.402 / runtime 10.0.13 (biuletyn bezpieczeństwa .NET z 2026-10-13)
| Data | Narzędzie | Wersja | Weryfikacja | Gdzie | Uwagi |
|---|---|---|---|---|---|
| 2026-10-14 | .NET SDK | 10.0.402 (runtime 10.0.13, ASP.NET Core 10.0.13) | SHA-512 z `release-metadata/10.0/releases.json` zgodny (`sha512sum -c`: OK) w chmurze i na urządzeniu; rozmiary 4907 plików po rozpakowaniu zgodne z archiwum (skrypt tarfile), muxer `dotnet`/`dnx` nadpisany nowszym | chmura (`~/.dotnet`) i urządzenie (`.toolchain/dotnet`, obok 10.0.401) | `global.json` → 10.0.402 (latestPatch). Powód: 10.0.12 ma krytyczne CVE ASP.NET Core (patrz bezpieczenstwo.md it. 71). Archiwum `.toolchain/dl/sdk-10.0.402.tgz` (240 MB) i stare `sdk.tgz` do usunięcia przy okazji (dozwolone w folderze roboczym). |

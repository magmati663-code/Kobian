# Kobian Repo v1 — pack, klucze, klucz odzyskiwania (stan implementacji)

Dokument opisuje to, co jest **zaimplementowane i przetestowane** w `Kobian.Crypto` i `Kobian.Format` (iteracje 3, 6, 11). Normatywny opis formatu: `plan.md` R2.3 (D3.8–D3.11), K8.1, K13.1, K14.1. Wektory testowe: `tests/Kobian.Tests.Unit/Vectors/v1.json`, generowane niezależną implementacją `tools/vectors/ref_v1.py` (Python, pyca/cryptography) — obie implementacje dają identyczne bajty (V-01, V-02, V-05, V-06).

## Klucze (D3.9)
HKDF-SHA256(IKM, salt, info), wynik 32 B. Etykiety ASCII: `kobian/v1/data`, `kobian/v1/meta`, `kobian/v1/chunk-id`, `kobian/v1/header-mac` (IKM = MK, salt = repoId); `kobian/v1/pack` i `kobian/v1/pack-commit` (IKM = K_data, salt = packId); `kobian/v1/obj/index` | `kobian/v1/obj/snapshot` (IKM = K_meta, salt = objectId). `commitTag = HMAC-SHA256(K_commit, "kobian/v1/commit" ‖ packId)`. `chunkId = HMAC-SHA256(K_id, plaintext)`.

## Pack (D3.10)
| Część | Układ |
|---|---|
| Nagłówek 64 B | `KOBPACK1` ‖ u16le formatVersion=1 ‖ u16le flags=0 ‖ u32le blobCount ‖ packId[16] ‖ commitTag[32] |
| Ramka i | u32le frameLength ‖ AES-256-GCM(K_pack, nonce = 00000000 ‖ u64be(i), AAD = repoId ‖ packId ‖ u64le(i) ‖ chunkId ‖ u16le(1)) |
| Plaintext ramki | blobVersion=1 ‖ blobType (1 data, 2 tree) ‖ algo (0 none, 1 zstd, 2 brotli) ‖ flags=0 ‖ u32le rawLength ‖ payload |
| Trailer | ramka z nonce 12 × FF, AAD = repoId ‖ packId ‖ `trailer`; plaintext = kanoniczny CBOR `[[chunkId, offset, frameLength, blobType, rawLength], …]` |
| Stopka 16 B | u64le trailerOffset ‖ u32le trailerFrameLength ‖ `KTRL` |

Doprecyzowania implementacji (do potwierdzenia w specyfikacji): `offset` w trailerze wskazuje pole `frameLength` ramki (nie początek szyfrogramu); `frameLength` = długość szyfrogramu + 16 B tagu (bez 4 B prefiksu); czytnik wymaga ramek ciągłych od offsetu 64 do `trailerOffset` i `trailerOffset + 4 + trailerFrameLength + 16 = rozmiar pliku`.

Limity czytnika (R2.3.8): frameLength ≤ 8 MiB + 64 (KB-1301), rawLength ≤ 4 MiB dane / 16 MiB drzewo (KB-1302), ramek ≤ 65 536 (KB-1308), trailer ≤ 8 MiB, CBOR kanoniczny o sztywnej strukturze (KB-1304), układ/kompletność (KB-1309, V-06), commitTag porównywany w czasie stałym przed odszyfrowaniem (KB-1101).

## Klucz odzyskiwania (K14.1)
32 B klucza ‖ CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, bez odbicia, xorout 0; wartość kontrolna „123456789” = 0x29B1) zapisane **big-endian** = 34 B → Base32 RFC 4648 bez dopełnienia = 55 znaków (3 ostatnie bity dopełnienia muszą być zerowe) w 14 grupach 13 × 4 + 1 × 3, rozdzielonych `-`. Dekoder ignoruje spacje/myślniki/wielkość liter i czyta `0` jako `O`, `1` jako `I`. Wariant CRC i kolejność bajtów nie były określone w K14.1 — ustalone w implementacji (it. 6).

## Kompresja (D3.3)
zstd poziom 3 (ZstdSharp.Port 0.8.8); blok zapisywany bez kompresji, gdy zysk < 1/64. Dekompresja wyłącznie do bufora o długości `rawLength` (ochrona przed bombą dekompresji).

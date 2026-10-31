#!/usr/bin/env python3
"""
Niezależna implementacja referencyjna Kobian Repo v1 (Python + pyca/cryptography) do generowania wektorów
testowych V-01, V-02, V-05, V-06 (R2.3.8). Implementacja C# (Kobian.Crypto, Kobian.Format) musi dawać
identyczne bajty. Wejścia są stałe (bez CSPRNG), aby wektory były powtarzalne.
Użycie: python3 tools/vectors/ref_v1.py > tests/Kobian.Tests.Unit/Vectors/v1.json
"""
import json, struct, sys
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives import hashes, hmac
from cryptography.hazmat.primitives.ciphers.aead import AESGCM


def hkdf(ikm, salt, info):
    return HKDF(algorithm=hashes.SHA256(), length=32, salt=salt, info=info).derive(ikm)


def hmac256(key, data):
    h = hmac.HMAC(key, hashes.SHA256()); h.update(data); return h.finalize()


def cbor_uint(major, n):
    if n < 24: return bytes([(major << 5) | n])
    if n < 0x100: return bytes([(major << 5) | 24, n])
    if n < 0x10000: return bytes([(major << 5) | 25]) + struct.pack('>H', n)
    if n < 0x100000000: return bytes([(major << 5) | 26]) + struct.pack('>I', n)
    return bytes([(major << 5) | 27]) + struct.pack('>Q', n)


def cbor(v):
    if isinstance(v, bool): raise TypeError
    if isinstance(v, int): return cbor_uint(0, v) if v >= 0 else cbor_uint(1, -1 - v)
    if isinstance(v, str):
        b = v.encode('utf-8'); return cbor_uint(3, len(b)) + b
    if isinstance(v, dict):
        items = sorted(v.items(), key=lambda kv: cbor(kv[0]))  # kanonicznie: po bajtach zakodowanego klucza
        return cbor_uint(5, len(items)) + b''.join(cbor(k) + cbor(x) for k, x in items)
    if isinstance(v, (bytes, bytearray)): return cbor_uint(2, len(v)) + bytes(v)
    if isinstance(v, list): return cbor_uint(4, len(v)) + b''.join(cbor(x) for x in v)
    raise TypeError(type(v))


MK = bytes(range(32))
REPO_ID = bytes.fromhex('00112233445566778899aabbccddeeff')
PACK_ID = bytes.fromhex('a0a1a2a3a4a5a6a7a8a9aaabacadaeaf')
OBJ_ID = bytes.fromhex('0f0e0d0c0b0a09080706050403020100')

k_data = hkdf(MK, REPO_ID, b'kobian/v1/data')
k_meta = hkdf(MK, REPO_ID, b'kobian/v1/meta')
k_id = hkdf(MK, REPO_ID, b'kobian/v1/chunk-id')
k_mac = hkdf(MK, REPO_ID, b'kobian/v1/header-mac')
k_pack = hkdf(k_data, PACK_ID, b'kobian/v1/pack')
k_commit = hkdf(k_data, PACK_ID, b'kobian/v1/pack-commit')
commit_tag = hmac256(k_commit, b'kobian/v1/commit' + PACK_ID)
k_obj_index = hkdf(k_meta, OBJ_ID, b'kobian/v1/obj/index')
k_obj_snap = hkdf(k_meta, OBJ_ID, b'kobian/v1/obj/snapshot')


def nonce_blob(i): return b'\x00' * 4 + struct.pack('>Q', i)


def frame_aad(i, chunk_id): return REPO_ID + PACK_ID + struct.pack('<Q', i) + chunk_id + struct.pack('<H', 1)


def blob_plain(btype, algo, raw_len, payload): return bytes([1, btype, algo, 0]) + struct.pack('<I', raw_len) + payload


aes = AESGCM(k_pack)
v02 = []
for i in (0, 1, 2 ** 32):
    payload = b'kobian-v02-' + str(i).encode()
    cid = hmac256(k_id, payload)
    pt = blob_plain(1, 0, len(payload), payload)
    v02.append({'i': i, 'nonce': nonce_blob(i).hex(), 'chunkId': cid.hex(), 'aad': frame_aad(i, cid).hex(),
                'plaintext': pt.hex(), 'sealed': aes.encrypt(nonce_blob(i), pt, frame_aad(i, cid)).hex()})

# V-05: pack z 3 blobami (none / zstd* / tree) + trailer + stopka. *ładunek „zstd” to stałe bajty — wektor układu, nie kompresji.
blobs = [
    (1, 0, b'Zazolc gesla jazn - blok danych bez kompresji'),
    (1, 1, bytes.fromhex('28b52ffd2004410000666f6f0a0300')),  # przykładowa ramka zstd; rawLength podany jawnie
    (2, 0, cbor([[b'\x01' * 32]])),
]
raw_lengths = [len(blobs[0][2]), 4, len(blobs[2][2])]
body = bytearray()
entries = []
offset = 64
for i, ((btype, algo, payload), raw) in enumerate(zip(blobs, raw_lengths)):
    plain_for_id = payload if algo == 0 else b'foo\n'
    cid = hmac256(k_id, plain_for_id)
    sealed = aes.encrypt(nonce_blob(i), blob_plain(btype, algo, raw, payload), frame_aad(i, cid))
    body += struct.pack('<i', len(sealed)) + sealed
    entries.append([cid, offset, len(sealed), btype, raw])
    offset += 4 + len(sealed)
trailer_plain = cbor(entries)
trailer_sealed = aes.encrypt(b'\xff' * 12, trailer_plain, REPO_ID + PACK_ID + b'trailer')
trailer_offset = 64 + len(body)
header = b'KOBPACK1' + struct.pack('<HHI', 1, 0, len(blobs)) + PACK_ID + commit_tag
assert len(header) == 64
pack = header + bytes(body) + struct.pack('<i', len(trailer_sealed)) + trailer_sealed \
    + struct.pack('<QI', trailer_offset, len(trailer_sealed)) + b'KTRL'

# V-07: drzewo z nazwami PL, emoji i długą nazwą; wpisy posortowane bajtowo po UTF-8 nazwy
names = ['zażółć gęślą jaźń.txt', 'Faktura 2026.pdf', '📁 Dokumenty', 'a' * 200 + '.dat', 'Ąkcent', 'z']
entries_tree = []
for i, n in enumerate(sorted(names, key=lambda x: x.encode('utf-8'))):
    e = {1: n, 2: 1 if n.startswith('📁') else 0, 3: 1000 * i, 4: 1_760_000_000_000_000_000 + i, 7: 32}
    if e[2] == 0: e[9] = [bytes([i]) * 32]; e[15] = bytes([0xA0 + i]) * 32
    else: e[3] = 0; e[10] = bytes([0x55]) * 32
    entries_tree.append(e)
tree = cbor({1: entries_tree})
tree_id = hmac256(k_id, tree)
# V-08: ta sama treść, ale długość pierwszej nazwy zakodowana nieminimalnie (0x78 + 1 B zamiast krótkiej formy) → odrzucenie
first = entries_tree[0][1].encode('utf-8')
noncanon = tree.replace(cbor_uint(3, len(first)) + first, bytes([0x78, len(first)]) + first, 1) if len(first) < 24 else None

# V-03/V-04: plik klucza hasła (D3.11): PBKDF2-HMAC-SHA256(UTF-8 hasła, sól 32 B, 600 000) → KEK; wrapped = AES-256-GCM(KEK, nonce, MK, AAD=JCS(obiekt bez wrapped/revoked))
import base64
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
PASSWORD = 'zażółć gęślą jaźń'
SALT = bytes(range(0x10, 0x30))
KNONCE = bytes(range(0xB0, 0xBC))
KEY_ID = '00112233445566778899aabbccddeeff'


def jcs(o): return json.dumps(o, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def key_file(iters, aad_iters):
    kek = PBKDF2HMAC(algorithm=hashes.SHA256(), length=32, salt=SALT, iterations=iters).derive(PASSWORD.encode('utf-8'))
    obj = {'keyId': KEY_ID, 'type': 'password', 'kdf': {'alg': 'pbkdf2-sha256', 'iter': aad_iters, 'salt': base64.b64encode(SALT).decode()},
           'nonce': base64.b64encode(KNONCE).decode(), 'createdAt': '2026-10-13T00:00:00Z'}
    wrapped = AESGCM(kek).encrypt(KNONCE, MK, jcs(obj))
    return kek, obj, wrapped, jcs(obj)


kek3, obj3, wrapped3, aad3 = key_file(600000, 600000)
v03 = {'password': PASSWORD, 'salt': SALT.hex(), 'iter': 600000, 'KEK': kek3.hex(), 'nonce': KNONCE.hex(), 'keyId': KEY_ID,
       'aad': aad3.decode('utf-8'), 'wrapped': base64.b64encode(wrapped3).decode(), 'createdAt': '2026-10-13T00:00:00Z'}
# V-04: ten sam plik, ale napastnik zmienia iter na 1 (wrapped zostaje) → AAD inne → odrzucenie
v04 = {'tamperedIter': 1, 'wrapped': base64.b64encode(wrapped3).decode()}

out = {
    'generator': 'tools/vectors/ref_v1.py (pyca/cryptography)',
    'inputs': {'MK': MK.hex(), 'repoId': REPO_ID.hex(), 'packId': PACK_ID.hex(), 'objectId': OBJ_ID.hex()},
    'V-01': {'K_data': k_data.hex(), 'K_meta': k_meta.hex(), 'K_id': k_id.hex(), 'K_mac': k_mac.hex(),
             'K_pack': k_pack.hex(), 'commitTag': commit_tag.hex(), 'K_obj_index': k_obj_index.hex(),
             'K_obj_snapshot': k_obj_snap.hex()},
    'V-02': v02,
    'V-05': {'pack': pack.hex(), 'trailerPlaintext': trailer_plain.hex(),
             'entries': [[e[0].hex(), e[1], e[2], e[3], e[4]] for e in entries],
             'payloads': [b[2].hex() for b in blobs]},
    'V-07': {'tree': tree.hex(), 'treeId': tree_id.hex(), 'names': [e[1] for e in entries_tree]},
    'V-08': {'nonCanonicalTree': noncanon.hex() if noncanon else None},
    'V-03': v03,
    'V-04': v04,
    'V-06': {'note': 'V-05 bez ostatniego bajtu → KB-1309', 'packTruncated': pack[:-1].hex()},
}
json.dump(out, sys.stdout, indent=1)
sys.stdout.write('\n')

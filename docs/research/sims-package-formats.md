# Sims 4 package formats — notes for the props converter

Everything here is from the SimsWiki format pages plus a byte-level dump of a
real package. Where a fact was *verified against a file* it says so; treat the
rest as documentation that still needs a sample to confirm.

Sample used: `B.C[P.E]'GRWM'PurseFirstPoseLaterPack.package` (a pose pack —
11 CLIP, 18 STBL, 1 DDS, 1 XML. **No mesh resources**, so nothing below about
MLOD/GEOM has been exercised against real bytes yet).

## DBPF container — VERIFIED

Header: magic `DBPF` at 0, major/minor at 4/8, entry count at 36, index
position at 64, index size at 44. Sample reads v2.1, 42 entries.

The index starts with a **constant-field bitmask** DWORD:

| bit | meaning |
|-----|---------|
| 0x1 | Type is constant — one DWORD follows the mask, entries omit the field |
| 0x2 | Group is constant — likewise |
| 0x4 | Instance-high is constant — likewise |

Then per entry, only the non-constant fields, in this order:

```
DWORD type            (omitted if bit 0)
DWORD group           (omitted if bit 1)
DWORD instanceHi      (omitted if bit 2)
DWORD instanceLo
DWORD position
DWORD size            high bit set = the two extra fields below are present
DWORD sizeDecompressed
WORD  compressionType   only if size high bit set
WORD  committed         only if size high bit set
```

**Entries are 28 or 32 bytes, not fixed 32.** The sample happens to have the
high bit set on every entry (even uncompressed ones, which carry
compressionType 0x0000/0xFFFF), which is why a fixed-32 reader worked on it.
A package mixing the two would desync a fixed-stride reader from that point on.

**Instance order is high-then-low.** Proven by the sample's STBL instances:
`0x00D142BB2BC59A1C`, `0x01D142BB…`, `0x02D142BB…` — the Sims 4 locale byte
is the most significant byte of the instance ID, and it lands there only with
hi read first. The pre-existing reader had these swapped; harmless for
CLIP↔ClipHeader matching (both sides swapped identically) but wrong for any
TGI reference that crosses resources, which is exactly what meshes do.

Compression: `0x5A42` = zlib (raw zlib stream, CMF `0x78`), `0x0000`/`0xFFFF`
= stored. RefPack/QFS exists in the format but did not appear in the sample.

## RCOL container

MODL/MLOD/GEOM resources are RCOL-wrapped:

```
DWORD version
DWORD publicChunkCount
DWORD index3              (unused)
DWORD externalCount
DWORD internalCount
{ QWORD instance, DWORD type, DWORD group } × internalCount    -- internal TGI
{ QWORD instance, DWORD type, DWORD group } × externalCount     -- external TGI
{ DWORD position, DWORD size } × internalCount                  -- chunk table
```

Chunk positions are absolute within the resource. Block references are 1-based
with a flag nibble: `0x0xxxxxxx` public internal, `0x1xxxxxxx` private internal
(add publicChunkCount to the index), `0x3xxxxxxx` delayed external.

## MLOD / MODL — object meshes (furniture, decor, clutter)

```
DWORD tag 'MLOD' | 'MODL'
DWORD version            0x00000201
DWORD groupCount
```

Per group:

```
DWORD subsetBytes        bytes following in this iteration
DWORD nameHash           FNV32
DWORD material           MATD/MTST private index
DWORD vertexFormat       VRTF private index
DWORD vertexBuffer       VBUF private index
DWORD indexBuffer        IBUF private index
DWORD flags              primitiveType | (meshFlags << 8)
DWORD streamOffset       byte offset into the VBUF
DWORD startVertex        always 0
DWORD startIndex
DWORD minVertexIndex     always 0
DWORD vertexCount
DWORD primitiveCount
FLOAT[6] boundingBox     min xyz, max xyz
DWORD skinController     SKIN private index
DWORD boneCount
  { DWORD boneHash, DWORD matdIndex, DWORD geoStateCount
    { DWORD nameHash, DWORD vbufStart, DWORD ibufStart×3, DWORD vbufCount, DWORD ibufCount } }
```

Version > 0x201 appends `DWORD parentNameHash` + `FLOAT[3] mirrorPlaneNormal`
+ `FLOAT mirrorPlaneOffset`; version > 0x203 appends a reserved zero DWORD.

Primitive type is the low byte of `flags`: 3 = TriangleList (the only one worth
supporting). Mesh flags in the upper bits include ShadowCaster (0x10) and
DropShadow (0x08) — shadow-only groups should be skipped, not converted.

### VRTF — vertex declaration

```
DWORD tag 'VRTF'
DWORD version        0x00000002
DWORD stride         bytes per vertex
DWORD count          element count
DWORD isExtended     always false; if true the BYTEs below become DWORDs
{ BYTE usage, BYTE usageIndex, BYTE format, BYTE offset } × count
```

Usage: 0 Position, 1 Normal, 2 UV, 3 BlendIndex, 4 BlendWeight, 5 Tangent,
6 Colour. Format: 0 Float(4B), 1 Float2(8B), 2 Float3(12B), 3 Float4(16B),
4 UByte4(4B), 5 ColorUByte4(4B), 6 Short2(4B), 7 Short4(8B) … 0x10 Float16_4(8B).

### IBUF — index buffer

```
DWORD tag 'IBUF'
DWORD version
DWORD flags
DWORD (always zero — pipeline bug, ignore)
BYTE[] face data       count and offset come from the MLOD group
```

Flags: `0x1` **differenced indices — delta-encoded**, accumulate from zero
across the whole buffer before use; `0x2` 32-bit indices; `0x4` display list.
The differencing is easy to miss and produces geometry that looks like noise.

### VBUF — vertex buffer

Not documented on the pages consulted. Structurally the mirror of IBUF (tag,
version, flags, a reserved/swizzle DWORD, then raw bytes read via the VRTF
stride starting at the group's `streamOffset`). **Unconfirmed — verify against
a real object package before trusting it.**

## GEOM — CAS part meshes (accessories, bags, hats, glasses, jewellery)

Self-contained: the vertex declaration and the vertex data live in the same
resource, so no VRTF/VBUF/IBUF indirection. This makes it both the simpler
target and, for FiveM, often the more useful one — accessories are props.

```
DWORD tag 'GEOM'
DWORD version           0x05 or 0x0C
DWORD tgiOffset, DWORD tgiSize
DWORD embeddedId        0 if none; else FNV32 of SimSkin / SimSkinCloth / SimEyes
  DWORD chunkSize + MTNF chunk     (only when embeddedId != 0)
DWORD mergeGroup
DWORD sortOrder
DWORD numVerts
DWORD fCount
  { DWORD dataType, DWORD subType, BYTE bytesPerElement } × fCount
[ vertex data × numVerts ]        fields in declared order
DWORD itemCount         usually 1
  BYTE bytesPerFacePoint          usually 2
  DWORD numFacePoints
  [ index data ]                  every 3 = one triangle
```

GEOM dataType: 1 Position (3f), 2 Normal (3f), 3 UV (2f), 4 BoneAssignment (4B),
5 Weights (4B), 6 TangentNormal (3f), 7 TagVal (4 packed bytes), 10 VertexID (4B).
Note this is a **different enumeration from VRTF's** — do not share the table.

Version 0x05 then has a DWORD skin-controller index; 0x0C has two counted
lists (extra UV sets, and transforms) before the bone list. Then
`DWORD boneCount` + `DWORD[] boneNameHash` (FNV32) + the TGI block.

## Resource type IDs

| Type | Name | Note |
|------|------|------|
| 0x01661233 | MODL | object model |
| 0x01D10F34 | MLOD | object model LODs |
| 0x015A1849 | GEOM | CAS part geometry |
| 0x8EAF13DE | RIG  | skeleton |
| 0x6B20C4F3 | CLIP | animation (already supported) |
| 0xBC4A5044 | CLIP_HEADER | |
| 0x00B2D882 | _IMG | DDS — **already DDS, which is what .ytd wants** |
| 0x3453CF95 | DDS  | DXT5 RLE (needs RLE decode) |
| 0xBA856C78 | _IMG | DXT5 RLES |
| 0xB6C8B6A0 | _IMG | overlay images |
| 0xC0DB5AE7 | OBJD | object definition (name/price/catalog) |
| 0x220557DA | STBL | string table (catalog names) |

Textures being plain DDS is a real win — the GTA `.ytd` path wants DDS, so a
Sims texture can pass through without a re-encode. The RLE variants
(0x3453CF95, 0xBA856C78) are the exception and need unpacking first.

## Scale and axes

Sims 4 is metric and roughly 1 unit = 1 metre, so object sizes land close to
GTA's without a unit conversion — but this is **assumed, not measured**. Sims
uses Y-up; the prop pipeline's existing importers already normalise that, so
route through the same glTF path rather than special-casing it.

## Sources

- <https://simswiki.info/wiki.php?title=Sims_4:RCOL>
- <https://simswiki.info/wiki.php?title=Sims_4:0x01D10F34> (MLOD/MODL)
- <https://simswiki.info/wiki.php?title=Sims_4:0x015A1849> (GEOM)
- <https://simswiki.info/wiki.php?title=Sims_3:0x01D0E723> (VRTF)
- <https://simswiki.info/wiki.php?title=Sims_3:0x01D0E70F> (IBUF)
- <https://github.com/Kuree/Sims4Tools/wiki/Sims-4---Packed-File-Types> (type IDs)

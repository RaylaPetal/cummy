"""Builds Cummy/Assets/drip/both.avfx: the creampie drip from both holes at once, from one ring.

The shipped drip effects (from Dripping Pussy VFX by Tirda Frye) are the same effect with the emitter placed at the
pussy or the anus: they differ only in one AVFX/Emit/Pos keyframe (and a binder label). This copies pussy.avfx, appends
ass.avfx's emitter as emitter 1, and has timeline 0 spawn it too (same binder), fixing the counts and sizes.

    python tools/build-drip-both.py Cummy/Assets/drip/pussy.avfx Cummy/Assets/drip/ass.avfx Cummy/Assets/drip/both.avfx

It asserts the layout it expects (one emitter, four timelines, one item on timeline 0) and stops if the sources differ.
"""
import struct, sys
src, ass_src, out = sys.argv[1], sys.argv[2], sys.argv[3]
b = bytearray(open(src,'rb').read())
a = open(ass_src,'rb').read()
assert len(a) == len(b)

def tag(buf, o): return bytes(buf[o:o+4][::-1]).decode('latin1')
def size(buf, o): return struct.unpack('<I', buf[o+4:o+8])[0]
def nxt(buf, o): return (o + 8 + size(buf, o) + 3) & ~3
def children(buf, start, end):
    o = start
    while o + 8 <= end:
        yield o
        o = nxt(buf, o)
def find(buf, start, end, t):
    return [o for o in children(buf, start, end) if tag(buf, o) == t]

root_end = 8 + size(b, 0)
emits = find(b, 8, root_end, 'Emit'); tmlns = find(b, 8, root_end, 'TmLn')
assert len(emits) == 1 and len(tmlns) == 4, (emits, tmlns)
emit = emits[0]; tl0 = tmlns[0]

# 1. Emitter copy with the ass file's bytes (it differs from pussy's only in the Pos keyframe inside Emit).
emit_end = nxt(b, emit)
emit_copy = bytes(a[emit:emit_end])
assert emit_copy != bytes(b[emit:emit_end]), "ass emitter should differ"

# 2. Second timeline item: copy of TmLn0's item with EmNo 0 -> 1.
item = find(b, tl0 + 8, tl0 + 8 + size(b, tl0), 'Item')
assert len(item) == 1
it = item[0]; it_end = nxt(b, it)
item_copy = bytearray(b[it:it_end])
fields = {tag(item_copy, o): o for o in children(item_copy, 8, 8 + size(item_copy, 0))}
emno = fields['EmNo']; assert struct.unpack('<i', item_copy[emno+8:emno+12])[0] == 0
item_copy[emno+8:emno+12] = struct.pack('<i', 1)

def set_int(buf, start, end, t, value):
    hits = find(buf, start, end, t); assert len(hits) == 1, t
    o = hits[0]; old = struct.unpack('<i', buf[o+8:o+12])[0]
    buf[o+8:o+12] = struct.pack('<i', value); return old

# Insert the emitter copy right after the original emitter (later offsets shift; do it before touching TmLn,
# which comes earlier in the file).
b[emit_end:emit_end] = emit_copy
# Insert the timeline item after the existing one, then grow TmLn0.
b[it_end:it_end] = item_copy
tl0_size = size(b, tl0)
struct.pack_into('<I', b, tl0 + 4, tl0_size + len(item_copy))
assert set_int(b, tl0 + 8, tl0 + 8 + size(b, tl0), 'TICn', 2) == 1
# Root: counts and size.
struct.pack_into('<I', b, 4, size(b, 0) + len(emit_copy) + len(item_copy))
root_end = 8 + size(b, 0)
assert set_int(b, 8, root_end, 'EmCn', 2) == 1
assert root_end == len(b), (root_end, len(b))
open(out, 'wb').write(b)
print("wrote", out, len(b), "bytes")

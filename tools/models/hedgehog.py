"""
The plush hedgehog on all fours, facing you (2026-10-07; the first one sat up: hedgehog_sitting.py).
After the classic plush: a long, low dome of prickles running back from the face; the prickles come
down over the forehead like a hood; a cream face coming forward into a pointed snout with a big
button nose (no ruff round it, as the toy has none); little round ears where the prickles meet the face; four short stubby legs, the front
paws poking out under its chin. Built in the sitting one's units (the runtime scales it up 1.15 and
seats it on the ground, its feet at y -1.09), so the runtime's numbers still hold.
"""

import math

from blendkit import blob, ellipsoid, fur_mask, node, parent_to, place, role, shaped, surface

# Previewed in its own units: from the feet (-1.09) to the top of the dome (0.36).
FRAME = (-0.4, 1.3)

DOME = ((0, -0.38, -0.5), (0.9, 0.74, 1.08))
FACE = ((0, -0.42, 0.42), (0.41, 0.37, 0.36))
SNOUT = ((0, -0.58, 0.84), (0.2, 0.17, 0.25))
EARS = [(-0.38, -0.04, 0.36), (0.38, -0.04, 0.36)]
GROUND_Y = -1.09


def inside(point, centre, radii, grow=1.0):
    """How far into an ellipsoid a point is: under 1 inside, scaled by `grow`."""
    return math.sqrt(sum(((p - c) / (r * grow)) ** 2 for p, c, r in zip(point, centre, radii)))


def build():
    head = node("head")

    # The prickles: one long dome. None underneath, none where the face and the ears are.
    prickles = blob("ball", [ellipsoid(*DOME)], lumps=0.02, faces=6500)
    role(parent_to(prickles, head), "fur", "wool", 0.16)

    def mask(x, y, z, nx, ny, nz):
        if ny < -0.35 or y < -0.95:
            return 0.0
        near = min(inside((x, y, z), FACE[0], FACE[1], 1.18), *(inside((x, y, z), at, (0.16, 0.15, 0.15)) for at in EARS))
        t = max(0.0, min(1.0, (near - 1.0) / 0.35))
        return t * t * (3 - 2 * t)
    fur_mask(prickles, mask)

    # The cream underneath, between the legs.
    tummy = blob("tummy", [ellipsoid((0, -0.86, -0.45), (0.64, 0.26, 0.92))], lumps=0.01, faces=1800)
    role(parent_to(tummy, head), "fur", "face", 0.05)

    # The face, coming forward into a pointed snout: one surface.
    face = blob("face", [ellipsoid(*FACE), ellipsoid(*SNOUT, stiffness=2.6)], faces=3200, voxel=0.012)
    role(parent_to(face, head), "face")

    # The big button nose on the end of the snout.
    at, _ = surface(face, 0, -0.58)
    nose = shaped("nose", (0.1, 0.085, 0.07), lambda x, y, z: (x, y, z), segments=(24, 16), faces=500)
    place(nose, (at[0], at[1], at[2] - 0.025), head)
    role(nose, "button")

    # Little round ears, where the prickles meet the face.
    for (x, y, z), suffix in zip(EARS, ("L", "R")):
        ear = node(f"ear_{suffix}", (x, y, z), head)
        outer = blob(f"ear_{suffix}_fur", [ellipsoid((x, y, z), (0.13, 0.12, 0.055), rotation=(0, math.copysign(0.35, x), 0))], faces=600)
        role(parent_to(outer, ear), "fur", "ear", 0.03)
        inner = shaped(f"ear_{suffix}_inner", (0.07, 0.065, 0.02), lambda x, y, z: (x, y, z), segments=(20, 12), faces=300)
        place(inner, (x, y, z + 0.045), ear)
        role(inner, "earInner")

    # Four short stubby legs, mostly hidden: the front paws poke out under its chin (the right one waves); the
    # hind feet are further back, mostly hidden from the front.
    for side, suffix in ((-1, "L"), (1, "R")):
        at = (side * 0.3, -0.97, 0.5)
        paw = node(f"paw_{suffix}", at, head)
        mesh = blob(f"paw_{suffix}_fur", [
            ellipsoid(at, (0.14, 0.11, 0.2)),
            ellipsoid((at[0], at[1] + 0.1, at[2] - 0.22), (0.13, 0.13, 0.14)),
        ], faces=700)
        role(parent_to(mesh, paw), "fur", "ear", 0.03)

        at = (side * 0.48, -0.97, -1.0)
        foot = node(f"foot_{suffix}", at, head)
        mesh = blob(f"foot_{suffix}_paw", [
            ellipsoid(at, (0.15, 0.12, 0.22)),
            ellipsoid((at[0], at[1] + 0.15, at[2] - 0.1), (0.14, 0.17, 0.14)),
        ], faces=700)
        role(parent_to(mesh, foot), "fur", "ear", 0.03)

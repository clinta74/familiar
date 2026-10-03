"""
The plush hedgehog, after the classic round toy: the ball is the whole toy, in long prickles; a cream
face in a fluffy ruff, coming forward into a short snout with a big button nose; little round ears;
a cream tummy with its paws held in front; two little feet. Built in its own units (the ball centred
on (0, -0.08)), as client/src/avatar/PlushHedgehog3D.ts is: the runtime scales it up 1.15 and seats
it on the ground.
"""

import math

from blendkit import (ball, blob, chain, ellipsoid, fur_mask, node, parent_to, place, role, shaped,
                      surface, P)

# Previewed in its own units: the ball spans about -1.04 to 0.88.
FRAME = (-0.1, 1.25)

BALL = ((0, -0.08, -0.2), (0.92, 0.96, 0.8))
# Where something sits on the ball and the prickles mustn't grow: face, tummy, ears.
CLEAR = [(0, 0.04, 0.5, 0.48), (0, -0.66, 0.46, 0.44), (-0.42, 0.5, 0.17, 0.16), (0.42, 0.5, 0.17, 0.16)]


def build():
    head = node("head")

    prickles = blob("ball", [ellipsoid(*BALL)], lumps=0.02, faces=5500)
    role(parent_to(prickles, head), "fur", "wool", 0.16)

    def mask(x, y, z, nx, ny, nz):
        if nz <= 0:
            return 1.0
        near = min(math.hypot((x - cx) / rx, (y - cy) / ry) for cx, cy, rx, ry in CLEAR)
        t = max(0.0, min(1.0, (near - 0.95) / 0.4))
        return t * t * (3 - 2 * t)
    fur_mask(prickles, mask)

    # Little round ears, set into the prickles above the face.
    for side, suffix in ((-1, "L"), (1, "R")):
        at = (side * 0.42, 0.5, 0.3)
        ear = node(f"ear_{suffix}", at, head)
        outer = blob(f"ear_{suffix}_fur", [ellipsoid(at, (0.13, 0.12, 0.055))], faces=600)
        role(parent_to(outer, ear), "fur", "ear", 0.03)
        inner = shaped(f"ear_{suffix}_inner", (0.07, 0.065, 0.02), lambda x, y, z: (x, y, z), segments=(20, 12), faces=300)
        place(inner, (at[0], at[1], at[2] + 0.045), ear)
        role(inner, "earInner")

    # The tummy, low on the ball.
    tummy = blob("tummy", [ellipsoid((0, -0.66, 0.3), (0.4, 0.38, 0.2))], lumps=0.01, faces=1800)
    role(parent_to(tummy, head), "fur", "face", 0.05)

    # The ruff: a fluffy cream frame round the top and sides of the face.
    arc = [(math.cos(a) * 0.46, -(-6 + math.sin(a) * 42) * 0.01, 0.44)
           for a in (math.pi * (0.88 + 1.24 * i / 13) for i in range(14))]
    ruff = blob("ruff", [
        *chain(arc, [0.1, 0.12, 0.12, 0.1], 30),
        ball((-0.43, -0.12, 0.4), 0.1), ball((0.43, -0.12, 0.4), 0.1),
        ball((-0.35, -0.26, 0.42), 0.09), ball((0.35, -0.26, 0.42), 0.09),
    ], lumps=0.025, faces=3000)
    role(parent_to(ruff, head), "fur", "face", 0.1)

    # The face, coming forward into a short snout: one surface.
    face = blob("face", [
        ellipsoid((0, 0.04, 0.49), (0.39, 0.36, 0.26)),
        ellipsoid((0, -0.12, 0.68), (0.18, 0.16, 0.16), stiffness=2.6),
    ], faces=3000, voxel=0.012)
    role(parent_to(face, head), "face")

    at, _ = surface(face, 0, -0.07)
    nose = shaped("nose", (0.095, 0.08, 0.06), lambda x, y, z: (x, y, z), segments=(24, 16), faces=500)
    place(nose, (at[0], at[1], at[2] - 0.02), head)
    role(nose, "button")

    # Feet poking out under the ball.
    for side, suffix in ((-1, "L"), (1, "R")):
        at = (side * 0.3, -1.0, 0.35)
        foot = node(f"foot_{suffix}", at, head)
        pad = blob(f"foot_{suffix}_paw", [ellipsoid(at, (0.14, 0.09, 0.2))], faces=600)
        role(parent_to(pad, foot), "fur", "ear", 0.03)

    # Its paws, held in front of its tummy; the right one comes up to wave.
    for side, suffix in ((-1, "L"), (1, "R")):
        at = (side * 0.22, -0.6, 0.48)
        paw = node(f"paw_{suffix}", at, head)
        mesh = blob(f"paw_{suffix}_fur", [ellipsoid(at, (0.13, 0.09, 0.09), rotation=(0, 0, side * math.radians(18)))], faces=600)
        role(parent_to(mesh, paw), "fur", "ear", 0.03)

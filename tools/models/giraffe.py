"""
The plush giraffe, sitting: a long neck rising out of its body, all in a patchwork coat (patches
painted by the runtime, in the ear colour); a long head coming forward into a pale muzzle; two
ossicones with dark knobs and a tuft of mane; leaf ears that rest out to the sides; hooves; and a rope
tail with a dark tuft. Proportions as the 2D giraffe (client/src/avatar/PlushGiraffeAvatar.ts): its
head is high, centred about y 0.8.
"""

import math

from blendkit import (ball, blob, capsule, chain, ellipsoid, fur_mask, node, parent_to, place, role,
                      shaped, surface)

from blendkit import foreleg, hind_foot  # noqa: E402

FRAME = (-0.3, 2.05)
FACE = (0, 0.74, 0.36, 0.46)


def ear_shape(rx, ry, rz, cup):
    """A giraffe ear: a long leaf, built pointing up (the runtime turns it out to the side)."""
    def deform(x, y, z):
        t = (y / ry + 1) / 2
        narrow = 1 - 0.6 * t ** 1.4
        x *= narrow
        across = 1 - (x / (rx * narrow + 1e-6)) ** 2
        z = z * (1 - 0.3 * t)
        if z > 0:
            z -= cup * max(0.0, across) * math.sin(math.pi * min(1.0, t))
        return x, y, z
    return deform


def build():
    head = node("head")

    # The head behind the face, in the patterned coat; none over the face itself.
    fur = blob("head_fur", [ellipsoid((0, 0.84, -0.12), (0.38, 0.44, 0.36))], lumps=0.008, faces=2500)
    role(parent_to(fur, head), "fur", "wool", 0.04)
    fur_mask(fur, lambda x, y, z, nx, ny, nz: 1.0 if nz <= 0 else min(1.0, max(0.0, (math.hypot((x - FACE[0]) / FACE[2], (y - FACE[1]) / FACE[3]) - 0.9) / 0.3)))

    # The long face, coming forward and down into a pale muzzle: one surface.
    face = blob("face", [
        ellipsoid((0, 0.8, 0.06), (0.34, 0.42, 0.3)),
        ellipsoid((0, 0.48, 0.24), (0.3, 0.22, 0.26)),
    ], faces=3500, voxel=0.012)
    role(parent_to(face, head), "face")

    # Ossicones: short stems with dark knobs; a tuft of mane between them.
    for side in (-1, 1):
        stem = blob(f"ossicone_{'L' if side < 0 else 'R'}", [capsule((side * 0.16, 1.12, -0.04), (side * 0.15, 1.3, -0.04), 0.045)], faces=500)
        role(parent_to(stem, head), "fur", "wool", 0.02)
        knob = blob(f"knob_{'L' if side < 0 else 'R'}", [ball((side * 0.15, 1.34, -0.04), 0.07)], faces=400)
        role(parent_to(knob, head), "fur", "ear", 0.03)
    mane_top = blob("mane_top", [ball((0, 1.2, -0.12), 0.08), ball((0, 1.14, -0.24), 0.08)], faces=500)
    role(parent_to(mane_top, head), "fur", "ear", 0.06)

    # Leaf ears on pivots, built pointing up: the runtime rests them out to the sides.
    for side, suffix in ((-1, "L"), (1, "R")):
        at = (side * 0.36, 1.0, -0.04)
        ear = node(f"ear_{suffix}", at, head)
        outer = shaped(f"ear_{suffix}_outer", (0.08, 0.17, 0.05), ear_shape(0.08, 0.17, 0.05, 0.025), faces=700)
        place(outer, (at[0], at[1] + 0.15, at[2]), ear)
        role(outer, "fur", "wool", 0.03)
        fur_mask(outer, lambda x, y, z, nx, ny, nz: 1.0 if nz < 0.2 else max(0.0, 1.0 - (nz - 0.2) * 4))
        inner = shaped(f"ear_{suffix}_inner", (0.045, 0.12, 0.015), ear_shape(0.045, 0.12, 0.015, 0.0), faces=300)
        place(inner, (at[0], at[1] + 0.14, at[2] + 0.025), ear)
        role(inner, "earInner")

    body = node("body")

    # The neck rising out of the body, thickening as it goes down; the body sitting.
    torso = blob("torso", [
        *chain([(0, 0.42, -0.14), (0, -0.1, -0.12), (0, -0.55, -0.08), (0, -0.85, -0.04)], [0.17, 0.2, 0.25, 0.32], 12),
        ellipsoid((0, -1.12, -0.02), (0.5, 0.46, 0.42)),
        ellipsoid((0, -1.66, -0.12), (0.72, 0.54, 0.58)),
    ], lumps=0.008, faces=5500)
    role(parent_to(torso, body), "fur", "wool", 0.045)
    # A short mane down the back of the neck.
    mane = blob("mane", chain([(0, 0.3, -0.3), (0, -0.2, -0.3), (0, -0.62, -0.3)], [0.05, 0.06, 0.05], 10), faces=1200)
    role(parent_to(mane, body), "fur", "ear", 0.05)
    belly = blob("belly", [ellipsoid((0, -1.66, 0.36), (0.36, 0.36, 0.16))], faces=1200)
    role(parent_to(belly, body), "fur", "face", 0.04)

    # Long front legs standing to the ground in front, with hooves; hind feet at the sides.
    for side, suffix in ((-1, "L"), (1, "R")):
        foreleg(body, suffix, (side * 0.25, -1.05, 0.28), 0.085, "hoof", "wool", 0.035, lumps=0.004)
        hind_foot(body, suffix, (side * 0.58, -2.08, 0.1), (0.18, 0.13, 0.27), "wool", 0.035, "hoof")

    # The tail: a rope with a dark tuft.
    hip = (0.5, -1.95, -0.3)
    tail = node("tail", hip, body)
    rope = blob("tail_rope", chain([hip, (0.86, -1.9, -0.2), (1.05, -1.66, -0.1)], [0.045, 0.04, 0.035], 10), faces=600)
    role(parent_to(rope, tail), "fur", "wool", 0.015)
    tip = blob("tail_tuft", [ellipsoid((1.08, -1.56, -0.08), (0.07, 0.11, 0.07))], faces=500)
    role(parent_to(tip, tail), "fur", "ear", 0.06)

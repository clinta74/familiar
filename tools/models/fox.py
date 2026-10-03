"""
The plush fox, sitting up: a face that comes forward to a pointed muzzle, pointed cream cheek fur,
big cupped ears with dark linings, a cream bib, dark paws and a bushy white-tipped tail curled round
on its right. Proportions as client/src/avatar/PlushFox3D.ts and the 2D fox.
"""

import math

from blendkit import (ball, blob, capsule, chain, ellipsoid, fur_mask, node, parent_to, place, role,
                      shaped, surface, tuft, P)

from blendkit import foreleg, hind_leg, limb_masses  # noqa: E402

FRAME = (-0.35, 2.0)


def ear_shape(rx, ry, rz, cup):
    """A big fox ear: a broad base narrowing to a sharp point, cupped in front."""
    def deform(x, y, z):
        t = (y / ry + 1) / 2
        narrow = 1 - 0.85 * t ** 1.3
        x *= narrow
        across = 1 - (x / (rx * narrow + 1e-6)) ** 2
        z = z * (1 - 0.4 * t) + 0.06 * t * t
        if z > 0.06 * t * t:
            z -= cup * max(0.0, across) * math.sin(math.pi * min(1.0, t * 1.05))
        return x, y, z
    return deform


def build():
    head = node("head")

    # The head's fur: round behind, wide at the cheeks.
    fur = blob("head_fur", [
        ellipsoid((0, -0.02, -0.16), (0.56, 0.52, 0.46)),
        ellipsoid((-0.3, -0.18, 0.0), (0.26, 0.24, 0.24)),
        ellipsoid((0.3, -0.18, 0.0), (0.26, 0.24, 0.24)),
    ], lumps=0.02, faces=3500)
    role(parent_to(fur, head), "fur", "wool", 0.1)

    # The face and its muzzle as one surface, narrowing forward to a point.
    face = blob("face", [
        ellipsoid((0, -0.02, 0.1), (0.44, 0.44, 0.27)),
        *chain([(0, -0.2, 0.3), (0, -0.23, 0.48), (0, -0.25, 0.64)], [0.19, 0.13, 0.075], 7, stiffness=2.4),
    ], faces=3500, voxel=0.012)
    role(parent_to(face, head), "face")

    at, normal = surface(face, 0, -0.235)
    nose = shaped("nose", (0.062, 0.046, 0.045), lambda x, y, z: (x, y, z), segments=(24, 16), faces=500)
    place(nose, (at[0], at[1], at[2] + 0.01), head)
    role(nose, "button")

    # Cheek fur: pointed cream tufts fanning out from the face, their roots in the head's fur.
    for side in (-1, 1):
        for i, (y, angle, length) in enumerate(((-0.1, 74, 0.31), (-0.2, 96, 0.35), (-0.3, 118, 0.3))):
            cheek = shaped(f"cheek_{'L' if side < 0 else 'R'}{i}", (0.12, length / 2, 0.12),
                           tuft(length, 0.12, (side * 0.34, y, 0.1), -side * math.radians(angle), side * -0.3), faces=500)
            role(parent_to(cheek, head), "fur", "face", 0.06)

    # Big pointed ears, cupped, with dark linings.
    for side, suffix in ((-1, "L"), (1, "R")):
        ear = node(f"ear_{suffix}", (side * 0.36, 0.42, -0.1), head)
        outer = shaped(f"ear_{suffix}_outer", (0.24, 0.34, 0.09), ear_shape(0.24, 0.34, 0.09, 0.045))
        place(outer, (side * 0.36, 0.72, -0.1), ear)
        role(outer, "fur", "wool", 0.05)
        fur_mask(outer, lambda x, y, z, nx, ny, nz: 1.0 if nz < 0.2 else max(0.0, 1.0 - (nz - 0.2) * 4))
        inner = shaped(f"ear_{suffix}_inner", (0.14, 0.25, 0.02), ear_shape(0.14, 0.25, 0.02, 0.0), faces=700)
        place(inner, (side * 0.36, 0.69, -0.07), ear)
        role(inner, "earInner")

    body = node("body")

    torso = blob("torso", [
        capsule((0, -0.45, -0.12), (0, -0.85, -0.08), 0.28),
        ellipsoid((0, -1.08, -0.04), (0.52, 0.5, 0.44)),
        ellipsoid((0, -1.64, -0.16), (0.46, 0.52, 0.5)),
        *limb_masses((0.27, -0.96, 0.3), 0.095, (0.5, -2.1, 0.1), (0.17, 0.12, 0.28), haunches=False),
    ], lumps=0.02, faces=5000)
    role(parent_to(torso, body), "fur", "wool", 0.11)

    # The fluffy cream bib down its chest.
    bib = blob("bib", [
        ellipsoid((0, -1.02, 0.3), (0.28, 0.46, 0.17), rotation=(-0.12, 0, 0)),
        ellipsoid((0, -1.48, 0.36), (0.22, 0.24, 0.15)),
        ellipsoid((0, -0.72, 0.2), (0.22, 0.2, 0.14)),
    ], lumps=0.015, faces=2500)
    role(parent_to(bib, body), "fur", "face", 0.09)

    # Front legs standing to the ground in front, as a sitting dog's, in dark socks; hind feet at the sides.
    for side, suffix in ((-1, "L"), (1, "R")):
        foreleg(body, suffix, (side * 0.27, -0.96, 0.3), 0.095, "paw", "wool", 0.07, "ear", reach=0.2)
        hind_leg(body, suffix, (side * 0.5, -2.1, 0.1), (0.17, 0.12, 0.28), 0.23, "wool", 0.08, "ear", 0.05, lumps=0.015)

    # The tail: a bushy brush from the hip curling up on its right, white at the tip.
    hip = (0.34, -1.98, -0.32)
    tail = node("tail", hip, body)
    path = [hip, (1.08, -2.04, -0.26), (1.38, -1.5, -0.2), (1.26, -0.88, -0.14), (1.04, -0.62, -0.1)]
    brush = blob("tail_fur", chain(path[:4], [0.1, 0.2, 0.25, 0.21], 14), lumps=0.025, faces=3000)
    role(parent_to(brush, tail), "fur", "wool", 0.13)
    tip = blob("tail_tip", chain(path[2:], [0.2, 0.24, 0.14], 8), lumps=0.02, faces=1800)
    role(parent_to(tip, tail), "fur", "face", 0.14)

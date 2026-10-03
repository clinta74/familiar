"""
The plush otter, sitting up: sleek, short fur; a broad round head with little ears set low; a face
that comes forward into two big whisker pads and a chin, under a big dark nose, with whiskers; a cream
chest; dark paws and feet; and a thick tapering tail curled round on the ground to its right.
Proportions as the 2D otter (client/src/avatar/PlushOtterAvatar.ts).
"""

import math

from blendkit import (ball, blob, capsule, chain, ellipsoid, fur_mask, node, parent_to, place, role,
                      shaped, surface, tuft)

from blendkit import foreleg, hind_foot, limb_masses  # noqa: E402

FRAME = (-0.35, 1.95)
FACE = (0, -0.08, 0.55, 0.52)


def build():
    head = node("head")

    # The broad head, in short sleek fur, thinning onto the face.
    fur = blob("head_fur", [ellipsoid((0, 0.0, -0.12), (0.62, 0.54, 0.5))], lumps=0.008, faces=3500)
    role(parent_to(fur, head), "fur", "wool", 0.05)
    fur_mask(fur, lambda x, y, z, nx, ny, nz: 1.0 if nz <= 0 else min(1.0, max(0.0, (math.hypot((x - FACE[0]) / FACE[2], (y - FACE[1]) / FACE[3]) - 0.75) / 0.45)))

    # The face: the front of the head, curving back into its fur at the sides (not a plate on it),
    # coming forward into two whisker pads and a chin, one surface.
    face = blob("face", [
        ellipsoid((0, 0.0, -0.02), (0.52, 0.48, 0.46)),
        ball((-0.14, -0.3, 0.46), 0.17), ball((0.14, -0.3, 0.46), 0.17),
        ball((0, -0.44, 0.4), 0.12),
    ], faces=3500, voxel=0.012)
    role(parent_to(face, head), "face")

    # A big dark nose on top of the pads.
    at, _ = surface(face, 0, -0.18)
    def nose_shape(x, y, z):
        # Wider at the top: a rounded triangle.
        return x * (0.8 + 0.4 * max(0.0, y / 0.075)), y, z
    nose = shaped("nose", (0.12, 0.075, 0.07), nose_shape, segments=(24, 16), faces=600)
    place(nose, (at[0], at[1], at[2] - 0.02), head)
    role(nose, "earInner")

    # Whiskers from the pads, on their own pivots so they twitch and droop.
    for side, suffix in ((-1, "L"), (1, "R")):
        root = (side * 0.26, -0.3, 0.52)
        whiskers = node(f"whiskers_{suffix}", root, head)
        for i, (dy, angle) in enumerate(((0.02, 80), (-0.01, 92), (-0.04, 104))):
            whisker = shaped(f"whiskers_{suffix}{i}", (0.008, 0.17, 0.008),
                             tuft(0.34, 0.008, (root[0], root[1] + dy, root[2]), -side * math.radians(angle), side * -0.25, flat=1.0, curl=0.0),
                             segments=(8, 10), faces=160)
            role(parent_to(whisker, whiskers), "thread")

    # Little round ears set low on the sides.
    for side, suffix in ((-1, "L"), (1, "R")):
        at = (side * 0.52, 0.34, -0.06)
        ear = node(f"ear_{suffix}", at, head)
        outer = blob(f"ear_{suffix}_fur", [ellipsoid(at, (0.12, 0.11, 0.06))], faces=500)
        role(parent_to(outer, ear), "fur", "wool", 0.03)
        inner = shaped(f"ear_{suffix}_inner", (0.065, 0.06, 0.02), lambda x, y, z: (x, y, z), segments=(20, 12), faces=250)
        place(inner, (at[0], at[1], at[2] + 0.05), ear)
        role(inner, "earInner")

    body = node("body")
    torso = blob("torso", [
        capsule((0, -0.45, -0.12), (0, -0.8, -0.08), 0.32),
        ellipsoid((0, -1.1, -0.04), (0.54, 0.52, 0.44)),
        ellipsoid((0, -1.66, -0.12), (0.74, 0.56, 0.58)),
        *limb_masses((0.27, -0.98, 0.3), 0.1, (0.62, -2.09, 0.1), (0.22, 0.11, 0.31)),
    ], lumps=0.008, faces=5000)
    role(parent_to(torso, body), "fur", "wool", 0.05)
    chest = blob("chest", [ellipsoid((0, -1.22, 0.3), (0.36, 0.62, 0.18))], faces=2000)
    role(parent_to(chest, body), "fur", "face", 0.04)

    # Front legs standing to the ground in front, with dark paws; dark webbed hind feet at the sides.
    for side, suffix in ((-1, "L"), (1, "R")):
        foreleg(body, suffix, (side * 0.27, -0.98, 0.3), 0.1, "paw", "wool", 0.04, "ear", lumps=0.004, reach=0.2)
        hind_foot(body, suffix, (side * 0.62, -2.09, 0.1), (0.22, 0.11, 0.31), "ear", 0.025)

    # The thick tail, tapering, curled round on the ground to the right.
    hip = (0.4, -1.98, -0.28)
    tail = node("tail", hip, body)
    path = [hip, (1.0, -2.1, -0.02), (1.34, -2.0, 0.3), (1.3, -1.76, 0.44), (1.16, -1.66, 0.36)]
    curl = blob("tail_fur", chain(path, [0.2, 0.17, 0.13, 0.09, 0.06], 18), lumps=0.006, faces=2500)
    role(parent_to(curl, tail), "fur", "wool", 0.04)

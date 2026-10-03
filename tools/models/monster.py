"""
The fuzzy monster, sitting on its round furry bottom: a big shaggy head (its fur stands on end when
it's startled or cross), a face of shorter fur swelling a little round its wide mouth, stubby ridged
felt horns, a cowlick, a lighter belly and clawed paws. Proportions as
client/src/avatar/PlushMonster3D.ts and the 2D monster.
"""

import math

from blendkit import (ball, blob, capsule, ellipsoid, fur_mask, node, parent_to, place, ring, role,
                      shaped, tuft)

FRAME = (-0.35, 1.95)

FACE = (0, -0.04, 0.5, 0.48)


def horn_shape(rx, ry, rz):
    """A stubby rounded horn, curving outward a little."""
    def deform(x, y, z):
        t = (y / ry + 1) / 2
        narrow = 1 - 0.72 * t ** 1.4
        return x * narrow + 0.03 * t * t, y, z * narrow + 0.04 * t * t
    return deform


def claws(name, at, spread, parent, angle_x):
    """Three little white claws in a row at `at`, pointing out and down."""
    for i, k in enumerate((-1, 0, 1)):
        claw = shaped(f"{name}{i}", (0.022, 0.035, 0.022),
                      tuft(0.07, 0.022, (at[0] + k * spread, at[1], at[2]), 0.0, flat=1.0, curl=0.0, angle_x=angle_x),
                      segments=(12, 8), faces=120)
        role(parent_to(claw, parent), "claw")


def build():
    head = node("head")

    # The shaggy head: long fur everywhere but over the face, where it's short (the face's own).
    fur = blob("head_fur", [ellipsoid((0, -0.06, -0.1), (0.66, 0.64, 0.58))], lumps=0.03, faces=5000)
    role(parent_to(fur, head), "fur", "wool", 0.15)
    fur_mask(fur, lambda x, y, z, nx, ny, nz: 1.0 if nz <= 0 else min(1.0, max(0.0, (math.hypot((x - FACE[0]) / FACE[2], (y - FACE[1]) / FACE[3]) - 0.9) / 0.35)))

    # The face: round, swelling a little round the mouth.
    face = blob("face", [
        ellipsoid((0, -0.04, 0.18), (0.52, 0.5, 0.36)),
        ellipsoid((0, -0.26, 0.3), (0.38, 0.2, 0.24)),
    ], faces=3500, voxel=0.012)
    role(parent_to(face, head), "face")

    # A cowlick on top.
    lick = shaped("cowlick", (0.08, 0.15, 0.08), tuft(0.3, 0.08, (0.02, 0.5, 0.06), -0.25, curl=0.06), faces=500)
    role(parent_to(lick, head), "fur", "wool", 0.06)

    # Stubby felt horns with ridges, on pivots at their base.
    for side, suffix in ((-1, "L"), (1, "R")):
        base = (side * 0.32, 0.52, 0.02)
        horn = node(f"horn_{suffix}", base, head)
        body = shaped(f"horn_{suffix}_felt", (0.1, 0.17, 0.1), horn_shape(0.1, 0.17, 0.1), faces=900)
        place(body, (base[0], base[1] + 0.15, base[2]), horn)
        body.scale.x = side
        role(body, "horn")
        for i, (y, r) in enumerate(((0.1, 0.094), (0.19, 0.074))):
            ridge = ring(f"horn_{suffix}_ridge{i}", r, 0.013, (base[0], base[1] + y, base[2]))
            role(parent_to(ridge, horn), "hoof")

    body = node("body")
    torso = blob("torso", [
        ellipsoid((0, -0.8, -0.08), (0.4, 0.3, 0.36)),
        ellipsoid((0, -1.15, -0.05), (0.56, 0.5, 0.46)),
        ellipsoid((0, -1.62, -0.12), (0.78, 0.6, 0.62)),
    ], lumps=0.03, faces=5500)
    role(parent_to(torso, body), "fur", "wool", 0.14)
    belly = blob("belly", [ellipsoid((0, -1.48, 0.36), (0.42, 0.5, 0.2))], lumps=0.015, faces=1800)
    role(parent_to(belly, body), "fur", "face", 0.1)

    # Arms bending at the elbow, ending in clawed paws.
    for side, suffix in ((-1, "L"), (1, "R")):
        sx, sy, sz = side * 0.52, -1.0, 0.3
        arm = node(f"arm_{suffix}", (sx, sy, sz), body)
        upper = blob(f"arm_{suffix}_upper", [capsule((sx, sy, sz), (sx, sy - 0.43, sz), 0.12), ball((sx, sy - 0.04, sz - 0.02), 0.15)], lumps=0.015, faces=1000)
        role(parent_to(upper, arm), "fur", "wool", 0.09)
        forearm = node(f"forearm_{suffix}", (sx, sy - 0.44, sz), arm)
        lower = blob(f"forearm_{suffix}_fur", [capsule((sx, sy - 0.44, sz), (sx, sy - 0.76, sz), 0.11)], lumps=0.015, faces=900)
        role(parent_to(lower, forearm), "fur", "wool", 0.09)
        paw = blob(f"forearm_{suffix}_paw", [ellipsoid((sx, sy - 0.86, sz + 0.02), (0.15, 0.12, 0.14))], faces=700)
        role(parent_to(paw, forearm), "fur", "ear", 0.04)
        claws(f"forearm_{suffix}_claw", (sx, sy - 0.94, sz + 0.1), 0.06, forearm, math.radians(150))

    # Clawed feet in front.
    for side, suffix in ((-1, "L"), (1, "R")):
        fx, fy, fz = side * 0.56, -2.06, 0.3
        foot = node(f"foot_{suffix}", (fx, fy, fz), body)
        pad = blob(f"foot_{suffix}_paw", [ellipsoid((fx, fy, fz), (0.2, 0.15, 0.3))], lumps=0.01, faces=900)
        role(parent_to(pad, foot), "fur", "ear", 0.05)
        claws(f"foot_{suffix}_claw", (fx, fy - 0.02, fz + 0.3), 0.07, foot, math.radians(95))

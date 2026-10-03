"""
The fuzzy monster, sitting on its round furry bottom: a big shaggy head (its fur stands on end when
it's startled or cross), a face of shorter fur swelling a little round its wide mouth, stubby ridged
felt horns, a cowlick, a lighter belly and clawed paws. Proportions as
client/src/avatar/PlushMonster3D.ts and the 2D monster.
"""

import math

from blendkit import (ball, blob, capsule, ellipsoid, fur_mask, node, parent_to, place, ring, role,
                      shaped, tuft)

from blendkit import foreleg, hind_foot, limb_masses  # noqa: E402

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
        *limb_masses((0.3, -1.02, 0.34), 0.12, (0.64, -2.07, 0.12), (0.21, 0.14, 0.3), reach=0.28, end="claw"),
    ], lumps=0.03, faces=5500)
    role(parent_to(torso, body), "fur", "wool", 0.14)
    belly = blob("belly", [ellipsoid((0, -1.48, 0.36), (0.42, 0.5, 0.2))], lumps=0.015, faces=1800)
    role(parent_to(belly, body), "fur", "face", 0.1)

    # Front legs standing to the ground in front, with clawed paws; clawed hind feet at the sides.
    for side, suffix in ((-1, "L"), (1, "R")):
        foreleg(body, suffix, (side * 0.3, -1.02, 0.34), 0.12, "claw", "wool", 0.09, "ear", lumps=0.015, reach=0.28)
        hind_foot(body, suffix, (side * 0.64, -2.07, 0.12), (0.21, 0.14, 0.3), "ear", 0.05, "claw")

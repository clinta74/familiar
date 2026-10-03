"""
The plush llama, sitting up (docs/BACKLOG.md item 6). Same proportions as the procedural 3D llama
(client/src/avatar/PlushLlama3D.ts) and the 2D one before it, so they compare like for like.
"""

import math

from blendkit import (ball, blob, capsule, ellipsoid, fur_mask, node, parent_to, place, role, shaped,
                      surface, P)

# What the preview camera looks at: centre height and half height, web units.
FRAME = (-0.35, 1.95)


def ear_shape(rx, ry, rz, cup):
    """A llama ear: narrowing to a rounded point, curving forward at the tip, cupped in front."""
    def deform(x, y, z):
        t = (y / ry + 1) / 2
        narrow = 1 - 0.55 * t ** 1.6
        x *= narrow
        across = 1 - (x / (rx * narrow + 1e-6)) ** 2
        z = z * (1 - 0.35 * t) + 0.11 * t * t
        if z > 0.11 * t * t:
            # The front is hollowed: deepest in the middle, along most of the ear's length.
            z -= cup * max(0.0, across) * math.sin(math.pi * min(1.0, t * 1.1))
        return x, y, z
    return deform


def build():
    head = node("head")

    # The head's fleece: a full ball, filling out round the jaw, which frames the face like a ruff.
    fleece = blob("head_fleece", [
        ellipsoid((0, -0.06, -0.12), (0.54, 0.56, 0.46)),
        ellipsoid((-0.34, -0.34, 0.0), (0.24, 0.24, 0.22)),
        ellipsoid((0.34, -0.34, 0.0), (0.24, 0.24, 0.22)),
    ], lumps=0.035, faces=3500)
    role(parent_to(fleece, head), "fur", "wool", 0.13)

    # The face and muzzle as one surface: the muzzle grows out of the face rather than sitting on it.
    face = blob("face", [
        ellipsoid((0, -0.12, 0.18), (0.44, 0.5, 0.3)),
        ellipsoid((0, -0.37, 0.42), (0.27, 0.21, 0.22)),
    ], faces=3500, voxel=0.014)
    role(parent_to(face, head), "face")

    # The nose, on the muzzle where it is, facing the way the muzzle does there.
    at, normal = surface(face, 0, -0.27)
    nose = shaped("nose", (0.1, 0.055, 0.05), lambda x, y, z: (x, y, z), segments=(24, 16), faces=600)
    place(nose, (at[0], at[1], at[2] - 0.005), head)
    nose.rotation_euler = P(0, 0, 1).rotation_difference(P(*normal)).to_euler()
    role(nose, "nose")

    # The topknot: a fluffy fringe falling to just above the eyes; it bounces when the llama hops.
    tuft = node("tuft", parent=head)
    fringe = blob("tuft_fleece", [
        ball((-0.24, 0.44, 0.16), 0.19), ball((0, 0.53, 0.14), 0.21), ball((0.24, 0.44, 0.16), 0.19),
        ball((-0.12, 0.37, 0.3), 0.15), ball((0.13, 0.37, 0.3), 0.15),
    ], lumps=0.025, faces=2200)
    role(parent_to(fringe, tuft), "fur", "wool", 0.12)

    # Tall cupped ears on pivots at their base.
    for side, suffix in ((-1, "L"), (1, "R")):
        ear = node(f"ear_{suffix}", (side * 0.4, 0.5, -0.05), head)
        outer = shaped(f"ear_{suffix}_outer", (0.14, 0.36, 0.09), ear_shape(0.14, 0.36, 0.09, 0.05))
        place(outer, (side * 0.4, 0.82, -0.05), ear)
        role(outer, "fur", "wool", 0.05)
        # No fur in the cup, where the lining is; full fur round the rim and behind.
        fur_mask(outer, lambda x, y, z, nx, ny, nz: 1.0 if nz < 0.2 else max(0.0, 1.0 - (nz - 0.2) * 4))
        inner = shaped(f"ear_{suffix}_inner", (0.078, 0.26, 0.02), ear_shape(0.078, 0.26, 0.02, 0.0), faces=800)
        place(inner, (side * 0.4, 0.8, -0.02), ear)
        role(inner, "earInner")

    body = node("body")

    # Neck, chest and seat melted into one plump body.
    torso = blob("torso", [
        capsule((0, -0.4, -0.12), (0, -0.95, -0.06), 0.3),
        ellipsoid((0, -1.18, -0.02), (0.5, 0.48, 0.42)),
        ellipsoid((0, -1.66, -0.12), (0.62, 0.5, 0.56)),
    ], lumps=0.03, faces=5000)
    role(parent_to(torso, body), "fur", "wool", 0.12)

    # Arms in two parts, bending at the elbow, with hooves on the end.
    for side, suffix in ((-1, "L"), (1, "R")):
        sx, sy, sz = side * 0.46, -1.02, 0.22
        arm = node(f"arm_{suffix}", (sx, sy, sz), body)
        # Full at the shoulder, narrowing to the wrist.
        upper = blob(f"arm_{suffix}_upper", [
            capsule((sx, sy, sz), (sx, sy - 0.46, sz), 0.105),
            ball((sx, sy - 0.04, sz - 0.02), 0.14),
        ], lumps=0.012, faces=1000)
        role(parent_to(upper, arm), "fur", "wool", 0.07)
        forearm = node(f"forearm_{suffix}", (sx, sy - 0.47, sz), arm)
        lower = blob(f"forearm_{suffix}_fleece", [capsule((sx, sy - 0.47, sz), (sx, sy - 0.84, sz), 0.095)], lumps=0.012, faces=900)
        role(parent_to(lower, forearm), "fur", "wool", 0.07)
        # The hoof stands out below the fleece, so the fur doesn't grow over it.
        hoof = shaped(f"forearm_{suffix}_hoof", (0.112, 0.07, 0.112), lambda x, y, z: (x, max(y, -0.045), z), segments=(24, 14), faces=500)
        place(hoof, (sx, sy - 0.99, sz), forearm)
        role(hoof, "hoof")

    # Feet resting in front, fleecy, with a hoof at the toe.
    for side, suffix in ((-1, "L"), (1, "R")):
        fx, fy, fz = side * 0.54, -2.06, 0.3
        foot = node(f"foot_{suffix}", (fx, fy, fz), body)
        pad = blob(f"foot_{suffix}_fleece", [ellipsoid((fx, fy, fz), (0.2, 0.15, 0.3))], lumps=0.012, faces=900)
        role(parent_to(pad, foot), "fur", "wool", 0.05)
        hoof = shaped(f"foot_{suffix}_hoof", (0.12, 0.075, 0.09), lambda x, y, z: (x, y, z), segments=(24, 14), faces=500)
        place(hoof, (fx, fy - 0.03, fz + 0.27), foot)
        role(hoof, "hoof")

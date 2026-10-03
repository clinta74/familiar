"""
Helpers for building the plush characters in Blender, headless (docs/BACKLOG.md item 6).

A character script builds named parts out of these, in the web client's coordinates - three.js's:
x right, y up, z towards the viewer, in the units the 3D characters use (1 = 100 units of the 2D
drawing). `P` turns those into Blender's (z up, -y towards the viewer); the glTF export turns them
back. Organic shapes are metaballs - ellipsoids and capsules that melt into one another - remeshed
into even, smooth meshes, with soft lumps where the fur is fleecy.

What the runtime needs (client/src/avatar/plush3d/ModelPlush3D.ts):
- nodes named for what moves: head, tuft, ear_L/ear_R, arm_L/arm_R (at the shoulder) with
  forearm_L/forearm_R (at the elbow), foot_L/foot_R, tail; pivots are where the node sits, and no
  node is rotated (the runtime sets rotations each frame);
- on each mesh, custom properties saying what it is: role (fur, face, nose, hoof, earInner, plain),
  and for fur its coat (wool, face, ear) and fur length.

Dots aren't used in names: three.js strips them from node names.
"""

import math
import os

import bmesh
import bpy
from mathutils import Matrix, Vector

# A lone metaball's surface sits at this fraction of its radius (stiffness 2, threshold 0.6),
# measured; sizes here are the surface's, so elements are made bigger by this much.
BALL = 0.575


def P(x, y, z=0.0):
    """A point in the web client's coordinates, in Blender's."""
    return Vector((x, -z, y))


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def _link(ob, parent=None):
    bpy.context.scene.collection.objects.link(ob)
    if parent is not None:
        ob.parent = parent
        ob.matrix_parent_inverse = parent.matrix_world.inverted()
    return ob


def node(name, at=(0, 0, 0), parent=None):
    """An empty: a pivot that a group of parts moves around."""
    ob = bpy.data.objects.new(name, None)
    ob.empty_display_size = 0.05
    ob.location = P(*at)
    _link(ob)
    if parent is not None:
        bpy.context.view_layer.update()
        ob.parent = parent
        ob.matrix_parent_inverse = parent.matrix_world.inverted()
    bpy.context.view_layer.update()
    return ob


def ellipsoid(at, radii, stiffness=2.0, rotation=(0, 0, 0)):
    """A metaball element: an ellipsoid at `at` with surface radii (x, y, z) in web coordinates."""
    return ("ELLIPSOID", at, radii, stiffness, rotation)


def ball(at, radius, stiffness=2.0):
    return ("BALL", at, (radius, radius, radius), stiffness, (0, 0, 0))


def capsule(a, b, radius, stiffness=2.0):
    """A capsule from a to b."""
    return ("CAPSULE", a, b, radius, stiffness)


def chain(points, radii, count, stiffness=2.0):
    """
    Balls along a smooth curve through `points` (Catmull-Rom), their radii running through `radii`:
    a tail, a neck, a horn. Metaball elements, for `blob`.
    """
    def at(u):
        n = len(points) - 1
        f = min(n - 1e-6, u * n)
        i = int(f)
        t = f - i
        p0 = Vector(points[max(0, i - 1)])
        p1 = Vector(points[i])
        p2 = Vector(points[i + 1])
        p3 = Vector(points[min(n, i + 2)])
        return 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t)

    def radius(u):
        f = u * (len(radii) - 1)
        k = min(len(radii) - 2, int(f))
        return radii[k] + (radii[k + 1] - radii[k]) * (f - k)

    return [ball(tuple(at(i / (count - 1))), radius(i / (count - 1)), stiffness) for i in range(count)]


def tuft(length, radius, at, angle_z, angle_y=0.0, flat=0.55, curl=0.02, angle_x=0.0):
    """
    A deform for `shaped`: a pointed tuft `length` long from a base `radius` wide, built pointing up,
    then tipped `angle_x` towards the viewer, turned `angle_z` about the view axis and `angle_y` about
    the vertical, and moved to `at`. Claws and horns too.
    """
    ry = length / 2
    cz, sz = math.cos(angle_z), math.sin(angle_z)
    cy, sy = math.cos(angle_y), math.sin(angle_y)
    cx, sx = math.cos(angle_x), math.sin(angle_x)

    def deform(x, y, z):
        t = (y / ry + 1) / 2
        narrow = 1 - 0.92 * t ** 1.6
        x, y, z = x * narrow, y + ry, z * flat * narrow + curl * t * t
        y, z = y * cx - z * sx, y * sx + z * cx
        x, y = x * cz - y * sz, x * sz + y * cz
        x, z = x * cy + z * sy, -x * sy + z * cy
        return x + at[0], y + at[1], z + at[2]
    return deform


def blob(name, elements, resolution=0.02, voxel=0.018, lumps=0.0, lump_size=0.14, faces=3000):
    """
    Melts metaball elements into one smooth mesh: converted, remeshed evenly, lumped if asked,
    decimated to about `faces` triangles, smoothed, and UV-unwrapped (fur needs UVs).
    """
    mb = bpy.data.metaballs.new(f"{name}_mb")
    mb.resolution = resolution
    mb.render_resolution = resolution
    mb.threshold = 0.6
    ob = bpy.data.objects.new(f"{name}_mb", mb)
    _link(ob)
    for element in elements:
        e = mb.elements.new()
        if element[0] == "CAPSULE":
            _, a, b, radius, stiffness = element
            pa, pb = P(*a), P(*b)
            e.type = "CAPSULE"
            e.co = (pa + pb) / 2
            length = (pb - pa).length
            e.radius = radius / BALL
            e.size_x = length / 2
            # A capsule lies along its local x; turn x onto a->b.
            e.rotation = Vector((1, 0, 0)).rotation_difference((pb - pa).normalized())
            e.stiffness = stiffness
            continue
        kind, at, radii, stiffness, rotation = element
        e.type = kind
        e.co = P(*at)
        r = max(radii)
        e.radius = r / BALL
        if kind == "ELLIPSOID":
            # Blender's axes: x, y (depth: web z), z (height: web y).
            e.size_x = radii[0] / r
            e.size_y = radii[2] / r
            e.size_z = radii[1] / r
            rx, ry, rz = rotation
            e.rotation = (Matrix.Rotation(rx, 4, "X") @ Matrix.Rotation(-rz, 4, "Y") @ Matrix.Rotation(ry, 4, "Z")).to_quaternion()
        e.stiffness = stiffness

    bpy.context.view_layer.objects.active = ob
    for other in bpy.context.selected_objects:
        other.select_set(False)
    ob.select_set(True)
    bpy.ops.object.convert(target="MESH")
    mesh = bpy.context.active_object
    mesh.name = name
    mesh.data.name = name
    return _finish(mesh, voxel, lumps, lump_size, faces)


def _finish(mesh, voxel, lumps, lump_size, faces):
    bpy.context.view_layer.objects.active = mesh
    mesh.select_set(True)
    if voxel:
        _apply(mesh, "REMESH", mode="VOXEL", voxel_size=voxel, use_smooth_shade=True)
    if lumps:
        tex = bpy.data.textures.new(f"{mesh.name}_lumps", "CLOUDS")
        tex.noise_scale = lump_size
        tex.noise_depth = 1
        tex.noise_basis = "BLENDER_ORIGINAL"
        _apply(mesh, "DISPLACE", texture=tex, strength=lumps, mid_level=0.5, texture_coords="GLOBAL")
    _apply(mesh, "SMOOTH", factor=0.5, iterations=2)
    triangles = sum(len(p.vertices) - 2 for p in mesh.data.polygons)
    if triangles > faces:
        _apply(mesh, "DECIMATE", ratio=faces / triangles)
    bpy.ops.object.shade_smooth()
    _unwrap(mesh)
    return mesh


def _apply(mesh, kind, **settings):
    modifier = mesh.modifiers.new(kind.lower(), kind)
    for key, value in settings.items():
        setattr(modifier, key, value)
    bpy.context.view_layer.objects.active = mesh
    bpy.ops.object.modifier_apply(modifier=modifier.name)


def _unwrap(mesh):
    bpy.context.view_layer.objects.active = mesh
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.02)
    bpy.ops.object.mode_set(mode="OBJECT")


def ring(name, radius, tube, at):
    """A flat ring (a torus) round the vertical, centred at `at`: ridges on a horn."""
    bpy.ops.mesh.primitive_torus_add(major_radius=radius, minor_radius=tube, major_segments=32, minor_segments=8, location=P(*at))
    ob = bpy.context.active_object
    ob.name = name
    bpy.ops.object.shade_smooth()
    _unwrap(ob)
    return ob


def shaped(name, radii, deform, segments=(40, 28), faces=1500):
    """
    A UV sphere of these radii (web coordinates), each vertex passed through `deform(x, y, z)` (web
    coordinates, returning the new point): ears, horns and other shapes metaballs don't make.
    """
    data = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segments[0], v_segments=segments[1], radius=1.0)
    for v in bm.verts:
        # Blender (x, y, z) on the unit sphere, as web (x, y, z) scaled to the radii.
        x, y, z = v.co.x * radii[0], v.co.z * radii[1], -v.co.y * radii[2]
        nx, ny, nz = deform(x, y, z)
        v.co = P(nx, ny, nz)
    bm.to_mesh(data)
    bm.free()
    ob = bpy.data.objects.new(name, data)
    _link(ob)
    return _finish(ob, None, 0, 0, faces)


def place(mesh, at=(0, 0, 0), parent=None):
    """Moves a part built at the world origin so its origin is `at`, and parents it."""
    mesh.location = P(*at)
    bpy.context.view_layer.update()
    if parent is not None:
        mesh.parent = parent
        mesh.matrix_parent_inverse = parent.matrix_world.inverted()
    return mesh


def parent_to(mesh, parent):
    """Parents a part built in place (world coordinates) without moving it."""
    bpy.context.view_layer.update()
    mesh.parent = parent
    mesh.matrix_parent_inverse = parent.matrix_world.inverted()
    return mesh


def role(mesh, kind, coat=None, fur=None):
    """What the runtime does with a mesh: grow fur on it (coat, length), paint it, or colour it."""
    mesh["role"] = kind
    if coat:
        mesh["coat"] = coat
    if fur is not None:
        mesh["fur"] = fur
    # Preview colours, by role, so a render shows the parts.
    colours = {"fur": (0.95, 0.93, 0.9, 1), "face": (0.89, 0.82, 0.78, 1), "nose": (0.45, 0.36, 0.33, 1), "button": (0.1, 0.08, 0.07, 1),
               "hoof": (0.55, 0.47, 0.42, 1), "earInner": (0.75, 0.62, 0.55, 1), "plain": (0.8, 0.8, 0.8, 1)}
    mesh.color = colours.get(kind, (0.8, 0.8, 0.8, 1))
    if kind == "fur" and coat == "ear":
        mesh.color = (0.7, 0.6, 0.52, 1)
    return mesh


def fur_mask(mesh, length):
    """
    How long the fur grows over a mesh, 0-1 per vertex, from `length(x, y, z, nx, ny, nz)` in web
    coordinates (world): none inside an ear's cup, say. Exported as the vertex colour's red; the
    runtime turns it into the fur shells' furLength.
    """
    bpy.context.view_layer.update()
    data = mesh.data
    colours = data.color_attributes.new("FurMask", "FLOAT_COLOR", "POINT")
    world = mesh.matrix_world
    rotation = world.to_3x3()
    for i, v in enumerate(data.vertices):
        p = world @ v.co
        n = (rotation @ v.normal).normalized()
        value = max(0.0, min(1.0, length(p.x, p.z, -p.y, n.x, n.z, -n.y)))
        colours.data[i].color = (value, value, value, 1.0)
    data.color_attributes.active_color = colours
    return mesh


def surface(mesh, x, y):
    """
    Where a ray from in front, at (x, y) in web coordinates, meets the mesh: a point on its front
    surface and the normal there (web coordinates), for placing a nose on a muzzle, say.
    """
    bpy.context.view_layer.update()
    inverse = mesh.matrix_world.inverted()
    origin = inverse @ P(x, y, 10)
    direction = (inverse.to_3x3() @ P(0, 0, -1)).normalized()
    hit, location, normal, _ = mesh.ray_cast(origin, direction)
    if not hit:
        raise ValueError(f"nothing of {mesh.name} at ({x}, {y})")
    world = mesh.matrix_world @ location
    n = (mesh.matrix_world.to_3x3() @ normal).normalized()
    return (world.x, world.z, -world.y), (n.x, n.z, -n.y)


def export(path):
    """Everything, as a compressed GLB: nodes, meshes, UVs and the custom properties."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    # The metaball objects were converted; drop any leftovers so they don't export.
    for ob in list(bpy.data.objects):
        if ob.type == "META":
            bpy.data.objects.remove(ob)
    bpy.ops.export_scene.gltf(
        filepath=path,
        export_format="GLB",
        export_extras=True,
        export_yup=True,
        export_apply=True,
        export_materials="NONE",
        export_vertex_color="ACTIVE",
        export_normals=True,
        export_texcoords=True,
        export_meshopt_compression_enable=True,
    )


def previews(directory, name, frame):
    """
    Front, three-quarter and side renders of the parts in their preview colours: for reviewing the
    shapes without opening Blender. `frame` is (centre y, half height) in web units.
    """
    os.makedirs(directory, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "OBJECT"
    scene.display.shading.show_cavity = True
    scene.display.shading.cavity_type = "BOTH"
    scene.display.shading.show_shadows = False
    scene.render.resolution_x = 640
    scene.render.resolution_y = 800
    scene.render.film_transparent = False
    world = bpy.data.worlds.new("preview")
    world.color = (0.96, 0.95, 0.98)
    scene.world = world

    camera = bpy.data.objects.new("preview_camera", bpy.data.cameras.new("preview_camera"))
    _link(camera)
    camera.data.type = "ORTHO"
    centre_y, half = frame
    camera.data.ortho_scale = half * 2.2
    scene.camera = camera
    for view, angle in (("front", 0), ("three-quarter", 35), ("side", 90)):
        a = math.radians(angle)
        target = P(0, centre_y, 0)
        camera.location = target + P(math.sin(a) * 10, 0.6, math.cos(a) * 10) - P(0, 0, 0)
        direction = target - camera.location
        camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = os.path.join(directory, f"{name}-{view}.png")
        bpy.ops.render.render(write_still=True)


GROUND = -2.22


def foreleg(body, suffix, shoulder, radius, end, coat="wool", fur=0.07, paw_coat="ear", lumps=0.012, reach=0.0):
    """
    A front leg standing from the shoulder down to the ground, as a sitting dog's do: an upper part
    and a lower part (it bends at the elbow to wave), ending in a hoof, a paw or a clawed paw on
    the floor `reach` in front of the shoulder - leaning forward, so the leg stands clear of the body.
    Nodes arm_<suffix> at the shoulder and forearm_<suffix> at the elbow.
    """
    sx, sy, sz = shoulder
    bottom = GROUND + (0.035 if end == "hoof" else 0.07)
    length = sy - bottom
    half = length * 0.5
    # The leg's line, from the shoulder to where it meets the floor.
    def along(t):
        return (sx, sy - length * t, sz + reach * t)
    ex, ey, ez = along(0.5)
    fx, fy, fz = along(1.0 - radius * 0.5 / length)
    gz = sz + reach
    arm = node(f"arm_{suffix}", shoulder, body)
    upper = blob(f"arm_{suffix}_upper", [
        capsule((sx, sy, sz), (ex, ey, ez), radius * 1.05),
        ball((sx, sy - 0.03, sz - 0.04), radius * 1.4),
    ], lumps=lumps, faces=1100)
    role(parent_to(upper, arm), "fur", coat, fur)
    forearm = node(f"forearm_{suffix}", (ex, ey, ez), arm)
    lower = blob(f"forearm_{suffix}_fur", [capsule((ex, ey, ez), (fx, fy, fz), radius * 0.92)], lumps=lumps, faces=900)
    role(parent_to(lower, forearm), "fur", coat, fur)
    if end == "hoof":
        hoof = shaped(f"forearm_{suffix}_hoof", (radius * 1.08, 0.06, radius * 1.15), lambda x, y, z: (x, max(y, -0.035), z), segments=(24, 14), faces=500)
        place(hoof, (sx, GROUND + 0.04, gz + 0.02), forearm)
        role(hoof, "hoof")
    else:
        sz = gz
        paw = blob(f"forearm_{suffix}_paw", [ellipsoid((sx, GROUND + 0.07, sz + 0.07), (radius * 1.3, 0.075, radius * 1.7))], faces=700)
        role(parent_to(paw, forearm), "fur", paw_coat, 0.03)
        if end == "claw":
            for i, k in enumerate((-1, 0, 1)):
                claw = shaped(f"forearm_{suffix}_claw{i}", (0.02, 0.03, 0.02),
                              tuft(0.06, 0.02, (sx + k * radius * 0.5, GROUND + 0.05, sz + 0.07 + radius * 1.6), 0.0, flat=1.0, curl=0.0, angle_x=math.radians(100)),
                              segments=(12, 8), faces=120)
                role(parent_to(claw, forearm), "claw")
    return arm


def hind_foot(body, suffix, at, radii, coat="wool", fur=0.05, end=None):
    """A hind foot resting at the side, toes forward, on a pivot (it kicks up with a hop)."""
    foot = node(f"foot_{suffix}", at, body)
    pad = blob(f"foot_{suffix}_pad", [ellipsoid(at, radii)], lumps=0.01, faces=900)
    role(parent_to(pad, foot), "fur", coat, fur)
    if end == "hoof":
        hoof = shaped(f"foot_{suffix}_hoof", (radii[0] * 0.6, radii[1] * 0.5, radii[2] * 0.3), lambda x, y, z: (x, y, z), segments=(24, 14), faces=500)
        place(hoof, (at[0], at[1] - radii[1] * 0.2, at[2] + radii[2] * 0.9), foot)
        role(hoof, "hoof")
    elif end == "claw":
        for i, k in enumerate((-1, 0, 1)):
            claw = shaped(f"foot_{suffix}_claw{i}", (0.02, 0.03, 0.02),
                          tuft(0.06, 0.02, (at[0] + k * radii[0] * 0.35, at[1] - 0.02, at[2] + radii[2]), 0.0, flat=1.0, curl=0.0, angle_x=math.radians(95)),
                          segments=(12, 8), faces=120)
            role(parent_to(claw, foot), "claw")
    return foot

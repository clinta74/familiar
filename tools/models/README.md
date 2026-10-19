# Character models

The 3D characters' shapes are built by these scripts in Blender, headless, and exported as GLBs the
web client (and so the phone apps) load. Fur, colours, the painted face, eyes and lids, and all the
movement are the runtime's (`client/src/avatar/plush3d/`); a model is just shapes, named parts
and pivots.

## Building

Blender 4.5 or newer (5.2 is what they're made with):

```bash
blender -b --factory-startup -P tools/models/build.py -- llama fox hedgehog monster otter giraffe
```

(`hedgehog_sitting` is the first hedgehog, sitting up, kept when it went on all fours: its spec is
`ModelHedgehogSitting3D.ts`, unused - point `createAvatar`'s hedgehog at it to have it back.)

Each writes `client/src/avatar/models/<name>.glb` (commit it: nothing builds models in CI) and
front, three-quarter and side preview renders to `tools/models/previews/` (not committed). A model
takes a second or two.

To see them as they'll look, with fur, run the client (`npm run dev`) and open
`http://localhost:5273/gallery.html` — every character across moods, gestures, colours and
accessories (`?models=fox&style=3d&moments=neutral,wave&size=360` to narrow it).

## How a character is made

`blendkit.py` has the pieces; `<name>.py` uses them, in the web client's coordinates (x right, y
up, z towards the viewer; 1 unit = 100 units of the 2D drawing, ground at y −2.22):

- `blob(name, elements)` melts metaball `ellipsoid`s, `ball`s, `capsule`s and `chain`s (balls along
  a curve) into one smooth remeshed surface, optionally lumpy (fleece).
- `shaped(name, radii, deform)` bends a sphere into anything metaballs don't make: ears, horns,
  claws, pointed tufts (`tuft`).
- `node(name, at, parent)` is a pivot; `parent_to` / `place` put parts on it.
- `role(mesh, kind, coat, fur)` says what the runtime does with a part: `fur` (in the `wool`, `face`
  or `ear` colour, this long), `face` (painted), `nose`, `button`, `hoof`, `earInner`, `horn`,
  `claw`, `thread`.
- `fur_mask(mesh, fn)` sets how long the fur grows over a part, 0–1 per vertex (none inside an ear's
  cup, none over a face set into a fur ball).
- `foreleg` and `hind_foot` make the legs most sitting characters share; `held_arm` a short arm held
  up off the ground instead (the otter's, paws at its chest).

The runtime finds parts by name: `head`, `body`, `tuft`, `ear_L`/`ear_R`, `arm_L`/`arm_R` with
`forearm_L`/`forearm_R`, `foot_L`/`foot_R`, `tail`, and whatever a character's spec animates
(`horn_L`, `whiskers_L`, `paw_R` …). Nodes aren't rotated in the model: the runtime sets their
rotations every frame. Each character's spec (`client/src/avatar/Model<Name>3D.ts`) says the rest:
where its face is for painting, how its ears swing, what else moves.

import * as THREE from 'three';
import { classicColours } from './appearance';
import modelUrl from './models/llama.glb?url';
import type { PlushLook } from './plush/engine';
import type { FaceLayout } from './plush/parts';
import { ModelPlush3D, type ModelSpec } from './plush3d/ModelPlush3D';
import { v } from './plush3d/Plush3D';

/** As PlushLlamaAvatar's. */
const LAYOUT: FaceLayout = {
  eyeX: 23, eyeY: -6, eyeR: 12.5,
  browY: -24, browInnerX: 12, browOuterX: 31,
  mouthY: 42, mouthW: 12, mouthDrop: 16,
  cheekX: 35, cheekY: 24,
  eyeStyle: 'button', lashes: true,
};

/** The plush llama, modelled in Blender (tools/models/llama.py). */
export const llamaModel: ModelSpec = {
  url: modelUrl,
  label: 'Your familiar, a plush llama',
  frame: { cx: 0, cy: -0.3, halfWidth: 1.15, halfHeight: 1.95 },
  neckPivotY: 60,
  face: {
    layout: LAYOUT,
    // Where the 2D llama's face is, for painting the model's face (which includes its muzzle).
    dome: { centre: v(0, 12, 20), radii: new THREE.Vector3(0.46, 0.52, 0.33), svg: [0, 12, 47, 52] },
    philtrum: [31, 40],
    // The muzzle a shade darker than the face, as the flat llama's is.
    paintDome: (p, palette) => {
      p.ctx.fillStyle = palette['--plush-snout']!;
      p.ctx.beginPath();
      p.ctx.ellipse(p.x(0), p.y(37), 27 * p.unit, 22 * p.unit, 0, 0, Math.PI * 2);
      p.ctx.fill();
    },
  },
  defaults: { colours: classicColours, accessory: 'none' },
  ears: { rest: 14, droop: 62 },
  // Front legs standing to the ground, leaning forward as modelled.
  arms: { lean: 0, bend: 0.03, out: 1 },
  splay: 10,
  anchors: (node) => ({
    bow: { parent: node('ear_L'), at: new THREE.Vector3(0, 0.16, 0.1) },
    flower: v(32, -58, 48),
    scarf: { y: -0.66, radius: 0.43, tail: new THREE.Vector3(0.2, -0.92, 0.38) },
  }),
};

export const createLlama = (container: HTMLElement, look?: PlushLook) => ModelPlush3D.create(container, look, llamaModel);

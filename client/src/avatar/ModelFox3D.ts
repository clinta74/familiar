import * as THREE from 'three';
import { characterInfo } from './appearance';
import modelUrl from './models/fox.glb?url';
import type { PlushLook } from './plush/engine';
import type { FaceLayout } from './plush/parts';
import { ModelPlush3D, type ModelSpec } from './plush3d/ModelPlush3D';
import type { Painter } from './plush3d/Plush3D';

/** As PlushFoxAvatar's. */
const LAYOUT: FaceLayout = {
  eyeX: 24, eyeY: -8, eyeR: 10.5,
  browY: -25, browInnerX: 13, browOuterX: 31,
  mouthY: 41, mouthW: 10, mouthDrop: 14,
  cheekX: 36, cheekY: 16,
  eyeStyle: 'button', lashes: false,
};

/**
 * The fox's face: the coat, with the cream of its cheeks and chin coming up softly from below -
 * fur shading into fur, not a mask with an edge.
 */
function paintFace(p: Painter, palette: Record<string, string>) {
  const { ctx } = p;
  ctx.fillStyle = palette['--plush-coat-face']!;
  ctx.fillRect(0, 0, ctx.canvas.width, ctx.canvas.height);
  const cream = palette['--plush-skin']!;
  for (const [x, y, r] of [[-36, 34, 30], [36, 34, 30], [0, 52, 30], [0, 38, 22]] as const) {
    const g = ctx.createRadialGradient(p.x(x), p.y(y), 0, p.x(x), p.y(y), r * p.unit);
    g.addColorStop(0, cream);
    g.addColorStop(0.55, cream);
    g.addColorStop(1, `${cream}00`);
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, ctx.canvas.width, ctx.canvas.height);
  }
}

/** The plush fox, modelled in Blender (tools/models/fox.py). */
export const foxModel: ModelSpec = {
  url: modelUrl,
  label: 'Your familiar, a plush fox',
  neckPivotY: 56,
  bubbles: [62, -80],
  face: {
    layout: LAYOUT,
    dome: { centre: new THREE.Vector3(0, -0.02, 0.1), radii: new THREE.Vector3(0.48, 0.48, 0.3), svg: [0, 2, 48, 48] },
    philtrum: [31, 38],
    paintDome: paintFace,
    fur: 0.015,
    // Lids in the coat: the face is painted in it.
    lids: 'wool',
  },
  defaults: { colours: characterInfo('fox').presets[0]!.colours, accessory: 'none' },
  ears: { rest: 12, droop: 70, turn: 0.2 },
  // Front legs standing to the ground, leaning forward as modelled.
  arms: { lean: 0, bend: 0.03, out: 1 },
  splay: 12,
  anchors: (node) => ({
    bow: { parent: node('ear_L'), at: new THREE.Vector3(0.02, 0.14, 0.1), tilt: 0.17 },
    flower: new THREE.Vector3(0.3, 0.46, 0.4),
    scarf: { y: -0.56, radius: 0.42, tail: new THREE.Vector3(0.18, -0.82, 0.38) },
  }),
  // The tail swishes when happy or excited, droops when sad, and is still otherwise.
  animate: (pose, node) => {
    const tail = node('tail');
    if (!tail) return;
    const happy = pose.mood.joy + (pose.gesture?.name === 'excited' ? 1 : 0);
    const swish = Math.sin(pose.t * Math.PI * 2 * 1.6) * 10 * Math.min(1, happy) * pose.motion;
    const droop = pose.mood.sad * 18;
    tail.rotation.z = -(swish + droop) * (Math.PI / 180);
  },
};

export const createFox = (container: HTMLElement, look?: PlushLook) => ModelPlush3D.create(container, look, foxModel);

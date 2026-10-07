import * as THREE from 'three';
import { characterInfo } from './appearance';
import modelUrl from './models/giraffe.glb?url';
import type { PlushLook } from './plush/engine';
import { GIRAFFE_LAYOUT } from './PlushGiraffeAvatar';
import { ModelPlush3D, type ModelSpec } from './plush3d/ModelPlush3D';

const deg = Math.PI / 180;

/** The plush giraffe, modelled in Blender (tools/models/giraffe.py). */
export const giraffeModel: ModelSpec = {
  url: modelUrl,
  label: 'Your familiar, a plush giraffe',
  // The head tilts on the top of the neck.
  neckPivotY: -24,
  bubbles: [72, -126],
  face: {
    // The mouth a little higher than the flat giraffe's: on the front of the muzzle, not under it.
    layout: { ...GIRAFFE_LAYOUT, mouthY: -42 },
    dome: { centre: new THREE.Vector3(0, 0.8, 0.06), radii: new THREE.Vector3(0.36, 0.44, 0.3), svg: [0, -80, 36, 44] },
    // The coat with a few patches, and the pale muzzle with its nostrils.
    paintDome: (p, palette) => {
      const { ctx } = p;
      ctx.fillStyle = palette['--plush-coat-face']!;
      ctx.fillRect(0, 0, ctx.canvas.width, ctx.canvas.height);
      ctx.fillStyle = palette['--plush-ear']!;
      for (const [x, y, r] of [[-24, -110, 7], [26, -104, 6], [-30, -64, 5], [31, -70, 5]] as const) {
        ctx.beginPath();
        ctx.ellipse(p.x(x), p.y(y), r * p.unit, r * 0.85 * p.unit, 0.4, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.fillStyle = palette['--plush-skin']!;
      ctx.beginPath();
      ctx.ellipse(p.x(0), p.y(-46), 31 * p.unit, 22 * p.unit, 0, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = palette['--plush-ear']!;
      for (const side of [-1, 1]) {
        ctx.beginPath();
        ctx.ellipse(p.x(side * 10), p.y(-52), 3.4 * p.unit, 2.2 * p.unit, 0, 0, Math.PI * 2);
        ctx.fill();
      }
    },
    fur: 0.01,
    // Lids in the coat: the face is painted in it.
    lids: 'wool',
  },
  defaults: { colours: characterInfo('giraffe').presets[0]!.colours, accessory: 'none' },
  // Ears rest out to the sides: up a little with interest, down when sad.
  ears: { rest: 78, droop: 30, turn: 0.1 },
  // Front legs standing to the ground, leaning forward as modelled.
  arms: { lean: 0, bend: 0.03, out: 1 },
  splay: 10,
  // The coat's patches, on the neck, body, head and limbs.
  fur: Object.fromEntries(
    ['torso', 'head_fur', 'arm_L_upper', 'arm_R_upper', 'forearm_L_fur', 'forearm_R_fur', 'foot_L_pad', 'foot_R_pad'].map((name) => [name, { pattern: true }]),
  ),
  anchors: (node) => ({
    bow: { parent: node('ear_L'), at: new THREE.Vector3(0, 0.1, 0.06), tilt: 0.3 },
    flower: new THREE.Vector3(0.26, 1.16, 0.18),
    scarf: { y: 0.16, radius: 0.27, tail: new THREE.Vector3(0.12, -0.04, 0.28) },
  }),
  // The tail flicks when happy and hangs when sad.
  animate: (pose, node) => {
    const tail = node('tail');
    if (!tail) return;
    const happy = pose.mood.joy + (pose.gesture?.name === 'excited' ? 1 : 0);
    const flick = Math.sin(pose.t * Math.PI * 2 * 1.8) * 9 * Math.min(1, happy) * pose.motion;
    tail.rotation.z = -(flick + pose.mood.sad * 14) * deg;
  },
};

export const createGiraffe = (container: HTMLElement, look?: PlushLook) => ModelPlush3D.create(container, look, giraffeModel);

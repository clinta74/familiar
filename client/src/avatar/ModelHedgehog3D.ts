import * as THREE from 'three';
import { characterInfo, mix } from './appearance';
import modelUrl from './models/hedgehog.glb?url';
import type { PlushLook } from './plush/engine';
import type { FaceLayout } from './plush/parts';
import { ModelPlush3D, type ModelSpec } from './plush3d/ModelPlush3D';
import { GROUND, S } from './plush3d/Plush3D';

/** As PlushHedgehogAvatar's. */
const LAYOUT: FaceLayout = {
  eyeX: 17, eyeY: -17, eyeR: 9,
  browY: -32, browInnerX: 8, browOuterX: 23,
  mouthY: 24, mouthW: 6, mouthDrop: 9,
  cheekX: 29, cheekY: 2,
  eyeStyle: 'button', lashes: false,
};

// As the flat hedgehog: the ball is the whole toy, a little bigger than the others' heads, sitting
// on the ground; it rocks on its bottom (PIVOT_Y).
const SCALE = 1.15;
const PIVOT_Y = 100;
const SHIFT = -GROUND / S - 6 - (8 + 96) * SCALE;
const deg = Math.PI / 180;

/** The plush hedgehog, modelled in Blender (tools/models/hedgehog.py). */
export const hedgehogModel: ModelSpec = {
  url: modelUrl,
  label: 'Your familiar, a plush hedgehog',
  frame: { cx: 0, cy: -0.62, halfWidth: 1.4, halfHeight: 1.62 },
  neckPivotY: SHIFT + PIVOT_Y * SCALE,
  bubbles: [72, -36],
  head: { y: PIVOT_Y * S * SCALE, scale: SCALE, rock: true },
  face: {
    layout: LAYOUT,
    dome: { centre: new THREE.Vector3(0, 0.04, 0.49), radii: new THREE.Vector3(0.39, 0.36, 0.26), svg: [0, -4, 39, 36] },
    philtrum: [15, 21],
    // The snout a shade darker than the face.
    paintDome: (p, palette) => {
      p.ctx.fillStyle = palette['--plush-snout']!;
      p.ctx.beginPath();
      p.ctx.ellipse(p.x(0), p.y(12), 18 * p.unit, 16 * p.unit, 0, 0, Math.PI * 2);
      p.ctx.fill();
    },
    fur: 0.008,
  },
  defaults: { colours: characterInfo('hedgehog').presets[0]!.colours, accessory: 'none' },
  splay: 14,
  // The prickles bristle, and grow finer over the big ball.
  fur: { ball: { bristle: true, density: 14 } },
  // Pale at the roots and dark at the tips: the mottling of a plush hedgehog.
  furShade: (coat, colour) =>
    coat === 'wool' ? { root: new THREE.Color(mix(colour, '#ffffff', 0.12)), tip: new THREE.Color(mix(colour, '#2a1d17', 0.42)) } : undefined,
  anchors: (node) => ({
    bow: { parent: node('ear_L'), at: new THREE.Vector3(-0.02, 0.1, 0.06), tilt: 0.26 },
    flower: new THREE.Vector3(0.46, 0.66, 0.3),
    scarf: { y: -0.32, radius: 0.98, tail: new THREE.Vector3(0.28, -0.5, 0.74), arc: Math.PI * 1.6, z: -0.2, depth: 0.87 },
  }),
  animate: (pose, node, hedgehog) => {
    const { face } = pose;
    // Prickles: lying flat when sad, standing up when startled or cross.
    hedgehog.bristle.value = Math.max(0.45, (12 + face.earLift * 8 + pose.mood.angry * 6) / 12);

    // The little ears tip up with interest and droop back when it's down.
    for (const [name, side] of [['ear_L', -1], ['ear_R', 1]] as const) {
      const ear = node(name);
      if (!ear) continue;
      ear.position.y = (ear.userData.rest as THREE.Vector3).y + face.earLift * 0.03;
      ear.rotation.set(-0.35, side * -0.2, -side * (8 - face.earLift * 14) * deg);
    }

    // The right paw comes up beside its face to wave.
    const paw = node('paw_R');
    if (paw) {
      const lift = pose.pawLift ?? 0;
      const rest = paw.userData.rest as THREE.Vector3;
      paw.position.set(rest.x + lift * 0.26, rest.y + lift * 0.72, rest.z + lift * 0.06);
      paw.rotation.z = -(pose.pawAngle * 0.6 - lift * 30) * deg;
    }
  },
};

export const createHedgehog = (container: HTMLElement, look?: PlushLook) => ModelPlush3D.create(container, look, hedgehogModel);

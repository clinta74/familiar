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

// On all fours, facing you (2026-10-07; the sitting one is ModelHedgehogSitting3D): the whole toy
// moves as one, a little bigger than the others' heads, standing on the ground; it rocks on its feet
// (PIVOT_Y). Modelled in the sitting one's units, so these still hold.
const SCALE = 1.15;
const PIVOT_Y = 100;
const SHIFT = -GROUND / S - 6 - (8 + 96) * SCALE;
const deg = Math.PI / 180;

/** The plush hedgehog on all fours, modelled in Blender (tools/models/hedgehog.py). */
export const hedgehogModel: ModelSpec = {
  url: modelUrl,
  label: 'Your familiar, a plush hedgehog',
  neckPivotY: SHIFT + PIVOT_Y * SCALE,
  bubbles: [72, -36],
  head: { y: PIVOT_Y * S * SCALE, scale: SCALE, rock: true },
  face: {
    layout: LAYOUT,
    dome: { centre: new THREE.Vector3(0, -0.42, 0.42), radii: new THREE.Vector3(0.41, 0.37, 0.36), svg: [0, -4, 39, 36] },
    // The face is low, under the prickles: the sitting one's was at (0, 0.04).
    layoutAt: [0, -0.46],
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
    flower: new THREE.Vector3(0.42, 0.1, 0.3),
    // A collar under the chin, round the front of the face.
    scarf: { y: -0.74, radius: 0.42, tail: new THREE.Vector3(0.2, -0.86, 0.78), arc: Math.PI * 1.3, z: 0.38, depth: 0.95 },
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

    // On all fours, it waves by lifting its right front paw up and forward off the ground.
    const paw = node('paw_R');
    if (paw) {
      const lift = pose.pawLift ?? 0;
      const rest = paw.userData.rest as THREE.Vector3;
      paw.position.set(rest.x + lift * 0.06, rest.y + lift * 0.34, rest.z + lift * 0.16);
      paw.rotation.x = -lift * 0.7;
      paw.rotation.z = -pose.pawAngle * 0.6 * deg;
    }
  },
};

export const createHedgehog = (container: HTMLElement, look?: PlushLook) => ModelPlush3D.create(container, look, hedgehogModel);

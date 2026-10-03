import * as THREE from 'three';
import { characterInfo } from './appearance';
import modelUrl from './models/monster.glb?url';
import type { PlushLook } from './plush/engine';
import type { FaceLayout } from './plush/parts';
import { ModelPlush3D, type ModelSpec } from './plush3d/ModelPlush3D';

/** As PlushMonsterAvatar's. */
const LAYOUT: FaceLayout = {
  eyeX: 26, eyeY: -14, eyeR: 16,
  browY: -38, browInnerX: 12, browOuterX: 38,
  mouthY: 24, mouthW: 26, mouthDrop: 30,
  cheekX: 46, cheekY: 12,
  eyeStyle: 'googly', lashes: false,
};

/** The fuzzy monster, modelled in Blender (tools/models/monster.py). */
export const monsterModel: ModelSpec = {
  url: modelUrl,
  label: 'Your familiar, a fuzzy monster',
  frame: { cx: 0, cy: -0.3, halfWidth: 1.2, halfHeight: 1.95 },
  neckPivotY: 70,
  bubbles: [66, -96],
  face: {
    layout: LAYOUT,
    dome: { centre: new THREE.Vector3(0, -0.04, 0.18), radii: new THREE.Vector3(0.52, 0.5, 0.36), svg: [0, 4, 52, 50] },
    teeth: true,
    // The face is the fur's own colour: the monster has no muzzle or mask, only fur.
    paintDome: (p, palette) => {
      p.ctx.fillStyle = palette['--plush-wool']!;
      p.ctx.fillRect(0, 0, p.ctx.canvas.width, p.ctx.canvas.height);
    },
    fur: 0.045,
    lids: 'wool',
    browDrop: 0.02,
  },
  defaults: { colours: characterInfo('monster').presets[0]!.colours, accessory: 'none' },
  splay: 14,
  // Its head's fur stands on end, and grows finer over the big head.
  fur: { head_fur: { bristle: true, density: 10 }, cowlick: { bristle: true } },
  anchors: (node) => ({
    bow: { parent: node('horn_L'), at: new THREE.Vector3(0, 0.08, 0.08), tilt: 0.17 },
    flower: new THREE.Vector3(0.46, 0.46, 0.4),
    scarf: { y: -0.7, radius: 0.5, tail: new THREE.Vector3(0.22, -0.96, 0.42) },
  }),
  animate: (pose, node, monster) => {
    const { face } = pose;
    // Fur stands up when startled or cross and lies flat when sad; the twitch ruffles it.
    const bristle = Math.max(-1, Math.min(1.6, face.earLift + pose.mood.angry * 0.6 + Math.abs(pose.earWiggle) / 25));
    monster.bristle.value = Math.min(1.6, Math.max(0.55, 1 + bristle * 0.35));
    // Horns lean out when it's down and stand straight when it's perky.
    const lean = 12 - face.earLift * 10 + pose.earWiggle * 0.4;
    for (const [name, side] of [['horn_L', -1], ['horn_R', 1]] as const) {
      const horn = node(name);
      if (horn) horn.rotation.z = -side * lean * (Math.PI / 180);
    }
  },
};

export const createMonster = (container: HTMLElement, look?: PlushLook) => ModelPlush3D.create(container, look, monsterModel);

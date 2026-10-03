import * as THREE from 'three';
import { characterInfo } from './appearance';
import modelUrl from './models/otter.glb?url';
import type { PlushLook } from './plush/engine';
import { OTTER_LAYOUT } from './PlushOtterAvatar';
import { ModelPlush3D, type ModelSpec } from './plush3d/ModelPlush3D';

const deg = Math.PI / 180;

/** The plush otter, modelled in Blender (tools/models/otter.py). */
export const otterModel: ModelSpec = {
  url: modelUrl,
  label: 'Your familiar, a plush otter',
  frame: { cx: 0.12, cy: -0.3, halfWidth: 1.3, halfHeight: 1.95 },
  neckPivotY: 58,
  bubbles: [64, -84],
  face: {
    layout: OTTER_LAYOUT,
    dome: { centre: new THREE.Vector3(0, 0, 0.14), radii: new THREE.Vector3(0.52, 0.48, 0.3), svg: [0, 0, 52, 48] },
    philtrum: [25, 33],
    // The coat, with the pale whisker pads and chin, and the spots the whiskers grow from.
    paintDome: (p, palette) => {
      const { ctx } = p;
      ctx.fillStyle = palette['--plush-coat-face']!;
      ctx.fillRect(0, 0, ctx.canvas.width, ctx.canvas.height);
      ctx.fillStyle = palette['--plush-skin']!;
      for (const [x, y, rx, ry] of [[-15, 30, 21, 16], [15, 30, 21, 16], [0, 44, 15, 10]] as const) {
        ctx.beginPath();
        ctx.ellipse(p.x(x), p.y(y), rx * p.unit, ry * p.unit, 0, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.fillStyle = palette['--plush-ear']!;
      for (const [x, y] of [[-24, 26], [-18, 34], [-28, 34], [24, 26], [18, 34], [28, 34]] as const) {
        ctx.beginPath();
        ctx.arc(p.x(x), p.y(y), 1.4 * p.unit, 0, Math.PI * 2);
        ctx.fill();
      }
    },
    fur: 0.025,
  },
  defaults: { colours: characterInfo('otter').presets[0]!.colours, accessory: 'none' },
  ears: { rest: 6, droop: 20, turn: 0.4 },
  splay: 14,
  anchors: (node) => ({
    bow: { parent: node('ear_L'), at: new THREE.Vector3(-0.02, 0.08, 0.06), tilt: 0.26 },
    flower: new THREE.Vector3(0.36, 0.46, 0.36),
    scarf: { y: -0.56, radius: 0.45, tail: new THREE.Vector3(0.2, -0.82, 0.4) },
  }),
  animate: (pose, node) => {
    // Whiskers twitch with the ears and droop when it's sad.
    const droop = pose.mood.sad * 12 - pose.mood.joy * 4;
    for (const [name, side] of [['whiskers_L', -1], ['whiskers_R', 1]] as const) {
      const whiskers = node(name);
      if (whiskers) whiskers.rotation.z = side * (droop + pose.earWiggle * 0.6) * deg;
    }
    // The tail swishes when happy or excited, droops when sad.
    const tail = node('tail');
    if (tail) {
      const happy = pose.mood.joy + (pose.gesture?.name === 'excited' ? 1 : 0);
      const swish = Math.sin(pose.t * Math.PI * 2 * 1.4) * 8 * Math.min(1, happy) * pose.motion;
      tail.rotation.y = -(swish + pose.mood.sad * 8) * deg;
    }
  },
};

export const createOtter = (container: HTMLElement, look?: PlushLook) => ModelPlush3D.create(container, look, otterModel);

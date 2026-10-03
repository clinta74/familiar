import * as THREE from 'three';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { characterInfo } from './appearance';
import type { PlushLook, Pose } from './plush/engine';
import type { FaceLayout } from './plush/parts';
import { Plush3D, earShape, ellipsoid, lumpy, sweep, type Painter } from './plush3d/Plush3D';

/** As PlushFoxAvatar's. */
const LAYOUT: FaceLayout = {
  eyeX: 24, eyeY: -8, eyeR: 10.5,
  browY: -25, browInnerX: 13, browOuterX: 31,
  mouthY: 41, mouthW: 10, mouthDrop: 14,
  cheekX: 36, cheekY: 16,
  eyeStyle: 'button', lashes: false,
};

/** Where the tail joins the body; it swishes from here. */
const HIP = new THREE.Vector3(0.34, -1.98, -0.32);

/** The fox's mask: the coat above, the cream cheeks and muzzle below, as the flat fox's. */
function paintMask(p: Painter, palette: Record<string, string>) {
  const { ctx } = p;
  ctx.fillStyle = palette['--plush-wool']!;
  ctx.fillRect(0, 0, ctx.canvas.width, ctx.canvas.height);
  ctx.fillStyle = palette['--plush-skin']!;
  ctx.beginPath();
  ctx.moveTo(p.x(-62), p.y(10));
  ctx.bezierCurveTo(p.x(-44), p.y(2), p.x(-20), p.y(8), p.x(0), p.y(14));
  ctx.bezierCurveTo(p.x(20), p.y(8), p.x(44), p.y(2), p.x(62), p.y(10));
  ctx.bezierCurveTo(p.x(50), p.y(32), p.x(30), p.y(54), p.x(0), p.y(62));
  ctx.bezierCurveTo(p.x(-30), p.y(54), p.x(-50), p.y(32), p.x(-62), p.y(10));
  ctx.fill();
}

/**
 * The plush fox in 3D, sitting up: big pointed ears with dark insides that swivel with its mood, a
 * cream mask and bib, dark paws, and a bushy white-tipped tail curled round beside it that swishes
 * when it's happy.
 */
export class PlushFox3D extends Plush3D {
  private readonly ears: [THREE.Group, THREE.Group];
  private readonly tail = new THREE.Group();

  constructor(container: HTMLElement, look?: PlushLook) {
    super(container, 'Your familiar, a plush fox', { cx: 0.12, cy: -0.3, halfWidth: 1.32, halfHeight: 1.95 }, 56, [62, -80]);

    this.buildBody({
      torso: [
        ellipsoid(0.3, 0.42, 0.28, new THREE.Vector3(0, -0.66, -0.1)),
        ellipsoid(0.52, 0.5, 0.44, new THREE.Vector3(0, -1.08, -0.04)),
        ellipsoid(0.74, 0.54, 0.6, new THREE.Vector3(0, -1.66, -0.14)),
      ],
      // The fluffy cream bib down its chest.
      belly: [
        ellipsoid(0.3, 0.5, 0.18, new THREE.Vector3(0, -1.08, 0.3), new THREE.Euler(-0.12, 0, 0)),
        ellipsoid(0.2, 0.2, 0.14, new THREE.Vector3(0, -1.52, 0.38)),
      ],
      fur: 0.11,
      neckPivotY: 56,
      shoulder: [44, 94],
      arm: { length: 98, width: 20, kind: 'paw' },
      foot: [56, 206],
      feet: { rx: 26, ry: 15, kind: 'paw', splay: 12 },
      armZ: 0.26,
    });

    // The tail: a bushy brush from the hip, curling up on the right, white at the tip.
    const path = [HIP, new THREE.Vector3(1.08, -2.04, -0.26), new THREE.Vector3(1.38, -1.5, -0.2), new THREE.Vector3(1.26, -0.88, -0.14), new THREE.Vector3(1.04, -0.62, -0.1)]
      .map((p) => p.clone().sub(HIP));
    this.tail.position.copy(HIP);
    this.tail.add(this.furry(lumpy(sweep(path.slice(0, 4), [0.1, 0.2, 0.25, 0.21], 18), 0.02, 37), 0.14));
    this.tail.add(this.furry(lumpy(sweep(path.slice(2), [0.2, 0.23, 0.14], 10), 0.015, 39), 0.15, 'face'));
    this.body.add(this.tail);

    // The head: wide at the cheeks, with fluffy tufts sticking out at the sides.
    const head = mergeGeometries([
      ellipsoid(0.56, 0.52, 0.46, new THREE.Vector3(0, -0.02, -0.14)),
      ellipsoid(0.2, 0.085, 0.12, new THREE.Vector3(-0.58, -0.2, -0.02), new THREE.Euler(0, 0, 0.22)),
      ellipsoid(0.2, 0.085, 0.12, new THREE.Vector3(0.58, -0.2, -0.02), new THREE.Euler(0, 0, -0.22)),
    ])!;
    this.head.add(this.furry(lumpy(head, 0.025, 33), 0.11));

    this.ears = ([-1, 1] as const).map((side) =>
      this.ear(new THREE.Vector3(side * 0.36, 0.42, -0.1), earShape(0.24, 0.34, 0.09, 0.85, 0.06), earShape(0.13, 0.24, 0.04, 0.85, 0.05), 0.3),
    ) as [THREE.Group, THREE.Group];

    this.buildFace({
      layout: LAYOUT,
      dome: { centre: new THREE.Vector3(0, -0.04, 0.16), radii: new THREE.Vector3(0.5, 0.52, 0.34), svg: [0, 4, 50, 52] },
      snout: { centre: new THREE.Vector3(0, -0.32, 0.4), radii: new THREE.Vector3(0.2, 0.16, 0.24), svg: [0, 30, 20, 16] },
      nose: { at: new THREE.Vector3(0, -0.24, 0.615), radii: new THREE.Vector3(0.085, 0.05, 0.05), tilt: -0.3 },
      philtrum: [31, 38],
      paintDome: paintMask,
      fur: 0.035,
    });

    this.dress({
      bow: { parent: this.ears[0], at: new THREE.Vector3(0.02, 0.14, 0.1), tilt: 0.17 },
      flower: new THREE.Vector3(0.3, 0.46, 0.4),
      scarf: { y: -0.56, radius: 0.42, tail: new THREE.Vector3(0.18, -0.82, 0.38) },
    });

    this.finish(look, { colours: characterInfo('fox').presets[0]!.colours, accessory: 'none' });
  }

  protected drawParts(pose: Pose): void {
    // Fox ears swivel and flatten more than a llama's: perked forward when happy or curious, flat
    // out to the sides when sad or cross.
    this.drawEars(this.ears, pose, 12, 70, 0.2);

    // The tail swishes when happy or excited, droops when sad, and is still otherwise.
    const happy = pose.mood.joy + (pose.gesture?.name === 'excited' ? 1 : 0);
    const swish = Math.sin(pose.t * Math.PI * 2 * 1.6) * 10 * Math.min(1, happy) * pose.motion;
    const droop = pose.mood.sad * 18;
    this.tail.rotation.z = -(swish + droop) * (Math.PI / 180);
  }
}

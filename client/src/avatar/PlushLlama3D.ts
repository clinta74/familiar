import * as THREE from 'three';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { classicColours } from './appearance';
import type { PlushLook, Pose } from './plush/engine';
import type { FaceLayout } from './plush/parts';
import { Plush3D, S, earShape, ellipsoid, lumpy, v } from './plush3d/Plush3D';

/** As PlushLlamaAvatar's. */
const LAYOUT: FaceLayout = {
  eyeX: 23, eyeY: -6, eyeR: 12.5,
  browY: -24, browInnerX: 12, browOuterX: 31,
  mouthY: 42, mouthW: 12, mouthDrop: 16,
  cheekX: 35, cheekY: 24,
  eyeStyle: 'button', lashes: true,
};

/**
 * The plush llama in 3D, sitting up in thick fleece: big ears that do most of its feeling, lashed
 * button eyes, a fluffy topknot that bounces when it hops, and its hooves held in front.
 */
export class PlushLlama3D extends Plush3D {
  private readonly ears: [THREE.Group, THREE.Group];
  private readonly tuft = new THREE.Group();

  constructor(container: HTMLElement, look?: PlushLook) {
    super(container, 'Your familiar, a plush llama', { cx: 0, cy: -0.3, halfWidth: 1.15, halfHeight: 1.95 }, 60);

    this.buildBody({
      torso: [
        ellipsoid(0.32, 0.5, 0.3, new THREE.Vector3(0, -0.72, -0.1)),
        ellipsoid(0.5, 0.48, 0.42, new THREE.Vector3(0, -1.18, -0.02)),
        ellipsoid(0.62, 0.5, 0.56, new THREE.Vector3(0, -1.66, -0.12)),
      ],
      fur: 0.12,
      neckPivotY: 60,
      shoulder: [46, 102],
      arm: { length: 94, width: 22, kind: 'hoof' },
      foot: [54, 206],
      feet: { rx: 25, ry: 15, kind: 'hoof', splay: 10 },
      armZ: 0.22,
    });

    // The head's fleece, a full ball around the face which it frames like a ruff, and the topknot.
    this.head.add(this.furry(lumpy(ellipsoid(0.54, 0.56, 0.44, new THREE.Vector3(0, -0.06, -0.1)), 0.035, 27), 0.13));
    const fringe = mergeGeometries([
      ellipsoid(0.2, 0.17, 0.16, new THREE.Vector3(-0.24, 0.44, 0.16)),
      ellipsoid(0.22, 0.19, 0.17, new THREE.Vector3(0, 0.53, 0.14)),
      ellipsoid(0.2, 0.17, 0.16, new THREE.Vector3(0.24, 0.44, 0.16)),
      ellipsoid(0.16, 0.13, 0.13, new THREE.Vector3(-0.12, 0.37, 0.3)),
      ellipsoid(0.16, 0.13, 0.13, new THREE.Vector3(0.13, 0.37, 0.3)),
    ])!;
    this.tuft.add(this.furry(lumpy(fringe, 0.02, 29), 0.12));
    this.head.add(this.tuft);

    this.ears = ([-1, 1] as const).map((side) =>
      this.ear(new THREE.Vector3(side * 0.4, 0.5, -0.05), earShape(0.14, 0.36, 0.09), earShape(0.075, 0.25, 0.045), 0.32),
    ) as [THREE.Group, THREE.Group];

    this.buildFace({
      layout: LAYOUT,
      dome: { centre: v(0, 12, 20), radii: new THREE.Vector3(0.46, 0.52, 0.33), svg: [0, 12, 47, 52] },
      snout: { centre: v(0, 38, 43), radii: new THREE.Vector3(0.29, 0.23, 0.24), svg: [0, 36, 27, 22] },
      nose: { at: new THREE.Vector3(0, -0.265, 0.625), radii: new THREE.Vector3(0.1, 0.055, 0.05), tilt: -0.35 },
      philtrum: [31, 40],
    });

    this.dress({
      bow: { parent: this.ears[0], at: new THREE.Vector3(0, 0.16, 0.12) },
      flower: v(32, -58, 48),
      scarf: { y: -0.66, radius: 0.43, tail: new THREE.Vector3(0.2, -0.92, 0.38) },
    });

    this.finish(look, { colours: classicColours, accessory: 'none' });
  }

  protected drawParts(pose: Pose): void {
    this.drawEars(this.ears, pose, 14, 62);
    this.tuft.position.y = Math.max(0, -pose.dy) * 0.4 * S;
  }
}

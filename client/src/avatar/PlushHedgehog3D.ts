import * as THREE from 'three';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { characterInfo, mix } from './appearance';
import type { PlushLook, Pose } from './plush/engine';
import type { FaceLayout } from './plush/parts';
import { GROUND, Plush3D, S, ellipsoid, furLength, lumpy, sweep, v, type Bristle, type Coat } from './plush3d/Plush3D';

/** As PlushHedgehogAvatar's. */
const LAYOUT: FaceLayout = {
  eyeX: 17, eyeY: -17, eyeR: 9,
  browY: -32, browInnerX: 8, browOuterX: 23,
  mouthY: 24, mouthW: 6, mouthDrop: 9,
  cheekX: 29, cheekY: 2,
  eyeStyle: 'button', lashes: false,
};

// As the flat hedgehog: the ball is the whole toy, drawn a little bigger than the others' heads and
// sitting on the ground where their feet rest. Everything here is in its own units (the ball centred
// on (0, 8) in the 2D drawing), scaled up and moved down onto the ground by the head's transform.
const SCALE = 1.15;
const PIVOT_Y = 100;
const SHIFT = -GROUND / S - 6 - (8 + 96) * SCALE;
const BALL = { centre: new THREE.Vector3(0, -0.08, -0.2), radii: new THREE.Vector3(0.92, 0.96, 0.8) };

/**
 * The plush hedgehog in 3D, after the classic round toy: a ball of soft, mottled prickles sitting on
 * two little feet, a cream face in a fluffy ruff, little round ears, a short snout with a big button
 * nose, and its paws held in front of its tummy. The prickles bristle when it's startled or cross
 * and lie flat when it's sad; the whole ball rocks on its bottom.
 */
export class PlushHedgehog3D extends Plush3D {
  private readonly bristle: Bristle = { value: 1 };
  private readonly ears: [THREE.Group, THREE.Group];
  private readonly feetPads: [THREE.Group, THREE.Group];
  private readonly wavePaw = new THREE.Group();

  constructor(container: HTMLElement, look?: PlushLook) {
    super(container, 'Your familiar, a plush hedgehog', { cx: 0, cy: -0.62, halfWidth: 1.4, halfHeight: 1.62 }, SHIFT + PIVOT_Y * SCALE, [72, -36]);
    // The head is the whole ball: its pivot is the ball's bottom, where it rocks.
    this.head.position.y = PIVOT_Y * S * SCALE;
    this.head.scale.setScalar(SCALE);

    // The prickles: long fur over the ball, dark at the tips, that can bristle - but not over the
    // face, the tummy or the ears, which sit on the ball and would be lost under them.
    const ball = lumpy(ellipsoid(BALL.radii.x, BALL.radii.y, BALL.radii.z, BALL.centre), 0.02, 43);
    const clear: [x: number, y: number, rx: number, ry: number][] = [[0, 0.04, 0.5, 0.48], [0, -0.66, 0.46, 0.44], [-0.42, 0.5, 0.17, 0.16], [0.42, 0.5, 0.17, 0.16]];
    furLength(ball, (p, n) => {
      if (n.z <= 0) return 1;
      const near = Math.min(...clear.map(([x, y, rx, ry]) => Math.hypot((p.x - x) / rx, (p.y - y) / ry)));
      return THREE.MathUtils.smoothstep(near, 0.95, 1.35);
    });
    this.head.add(this.furry(ball, 0.16, 'wool', { bristle: this.bristle, varying: true, density: 14 }));

    // Little round ears, set into the prickles above the face.
    this.ears = ([-1, 1] as const).map((side) => {
      const ear = new THREE.Group();
      ear.position.set(side * 0.42, 0.5, 0.3);
      ear.add(this.furry(ellipsoid(0.13, 0.12, 0.05, new THREE.Vector3()), 0.03, 'ear'));
      const inner = new THREE.Mesh(new THREE.SphereGeometry(1, 20, 14), this.earInner);
      inner.scale.set(0.07, 0.065, 0.025);
      inner.position.z = 0.045;
      ear.add(inner);
      this.head.add(ear);
      return ear;
    }) as [THREE.Group, THREE.Group];

    // The tummy, low on the ball, and two little feet poking out under it.
    this.head.add(this.furry(ellipsoid(0.4, 0.38, 0.2, new THREE.Vector3(0, -0.66, 0.3)), 0.05, 'face'));
    this.feetPads = ([-1, 1] as const).map((side) => {
      const foot = new THREE.Group();
      foot.position.set(side * 0.3, -1.0, 0.35);
      foot.rotation.y = side * 14 * (Math.PI / 180);
      foot.add(this.furry(ellipsoid(0.14, 0.09, 0.2, new THREE.Vector3()), 0.03, 'ear'));
      this.head.add(foot);
      return foot;
    }) as [THREE.Group, THREE.Group];

    // The ruff: a fluffy cream frame round the top and sides of the face.
    const arc: THREE.Vector3[] = [];
    for (let i = 0; i <= 13; i++) {
      const a = Math.PI * (0.88 + (1.24 * i) / 13);
      arc.push(v(Math.cos(a) * 46, -6 + Math.sin(a) * 42, 44));
    }
    const ruff = mergeGeometries([
      sweep(arc, [0.1, 0.12, 0.12, 0.1], 44),
      ...([-1, 1] as const).flatMap((side) => [
        ellipsoid(0.1, 0.1, 0.09, v(side * 43, 12, 40)),
        ellipsoid(0.09, 0.09, 0.08, v(side * 35, 26, 42)),
      ]),
    ])!;
    this.head.add(this.furry(lumpy(ruff, 0.03, 47), 0.1, 'face'));

    this.buildFace({
      layout: LAYOUT,
      dome: { centre: new THREE.Vector3(0, 0.04, 0.49), radii: new THREE.Vector3(0.39, 0.36, 0.26), svg: [0, -4, 39, 36] },
      snout: { centre: new THREE.Vector3(0, -0.12, 0.68), radii: new THREE.Vector3(0.18, 0.16, 0.16), svg: [0, 12, 18, 16] },
      nose: { at: new THREE.Vector3(0, -0.07, 0.82), radii: new THREE.Vector3(0.095, 0.08, 0.06), tilt: 0 },
      buttonNose: true,
      philtrum: [15, 21],
      fur: 0.02,
    });

    // Its paws, held in front of its tummy. The right one comes up beside its face to wave.
    const paw = (side: -1 | 1) => {
      const group = new THREE.Group();
      group.add(this.furry(ellipsoid(0.13, 0.09, 0.09, new THREE.Vector3()), 0.03, 'ear'));
      group.position.set(side * 0.22, -0.6, 0.48);
      group.rotation.z = side * 18 * (Math.PI / 180);
      this.head.add(group);
      return group;
    };
    paw(-1);
    this.wavePaw = paw(1);

    this.dress({
      bow: { parent: this.ears[0], at: new THREE.Vector3(-0.02, 0.1, 0.06), tilt: 0.26 },
      flower: new THREE.Vector3(0.46, 0.66, 0.3),
      scarf: { y: -0.32, radius: 0.98, tail: new THREE.Vector3(0.28, -0.5, 0.74), arc: Math.PI * 1.6, z: -0.2, depth: 0.87 },
    });

    this.finish(look, { colours: characterInfo('hedgehog').presets[0]!.colours, accessory: 'none' });
  }

  /** The prickles are pale at the roots and dark at the tips: the mottling of a plush hedgehog. */
  protected override furShade(coat: Coat, colour: string): { root: THREE.Color; tip: THREE.Color } {
    if (coat !== 'wool') return super.furShade(coat, colour);
    return { root: new THREE.Color(mix(colour, '#ffffff', 0.12)), tip: new THREE.Color(mix(colour, '#2a1d17', 0.42)) };
  }

  protected drawParts(pose: Pose): void {
    const { face } = pose;
    const deg = Math.PI / 180;

    // The whole ball rocks on its bottom and squidges: wider and lower as it squashes.
    this.head.scale.setScalar(SCALE);
    this.pivot.scale.set(1 + pose.squash * 0.6, 1 - pose.squash * 0.6, 1 + pose.squash * 0.6);
    // It turns to look about less than a head on a neck can.
    this.pivot.rotation.x *= 0.4;
    this.pivot.rotation.y *= 0.6;

    // Prickles: lying flat when sad, standing up when startled or cross.
    this.bristle.value = Math.max(0.45, (12 + face.earLift * 8 + pose.mood.angry * 6) / 12);

    // The little ears tip up with interest and droop back when it's down.
    this.ears.forEach((ear, i) => {
      const side = i === 0 ? -1 : 1;
      ear.position.y = 0.5 + face.earLift * 0.03;
      ear.rotation.set(-0.35, side * -0.2, -side * (8 - face.earLift * 14) * deg);
    });

    // Feet kick up with each hop of joy.
    this.feetPads.forEach((foot) => (foot.rotation.x = -pose.hop * 0.4));

    // The right paw comes up beside its face to wave.
    const lift = pose.pawLift ?? 0;
    this.wavePaw.position.set(0.22 + lift * 0.26, -0.6 + lift * 0.72, 0.48 + lift * 0.06);
    this.wavePaw.rotation.z = -(-18 + pose.pawAngle * 0.6 - lift * 30) * deg;
  }
}

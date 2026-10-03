import * as THREE from 'three';
import { characterInfo } from './appearance';
import type { PlushLook, Pose } from './plush/engine';
import type { FaceLayout } from './plush/parts';
import { Plush3D, earShape, ellipsoid, furLength, lumpy, tuftShape, type Bristle, type Painter } from './plush3d/Plush3D';

/** As PlushMonsterAvatar's. */
const LAYOUT: FaceLayout = {
  eyeX: 26, eyeY: -14, eyeR: 16,
  browY: -38, browInnerX: 12, browOuterX: 38,
  mouthY: 24, mouthW: 26, mouthDrop: 30,
  cheekX: 46, cheekY: 12,
  eyeStyle: 'googly', lashes: false,
};

const HEAD = { centre: new THREE.Vector3(0, -0.06, -0.1), radii: new THREE.Vector3(0.66, 0.64, 0.58) };
const FACE = { x: 0, y: -0.04, rx: 0.5, ry: 0.48 };

/** The face is the fur's own colour: the monster has no muzzle or mask, only fur. */
function paintFur(p: Painter, palette: Record<string, string>) {
  p.ctx.fillStyle = palette['--plush-wool']!;
  p.ctx.fillRect(0, 0, p.ctx.canvas.width, p.ctx.canvas.height);
}

/**
 * The fuzzy monster in 3D, sitting on its round furry bottom: a big shaggy head whose fur stands
 * on end when it's startled or cross and goes flat when it's sad, big googly eyes that look about,
 * stubby horns, a wide mouth with two little fangs, a lighter belly and clawed paws.
 */
export class PlushMonster3D extends Plush3D {
  private readonly bristle: Bristle = { value: 1 };
  private readonly horns: [THREE.Group, THREE.Group];

  constructor(container: HTMLElement, look?: PlushLook) {
    super(container, 'Your familiar, a fuzzy monster', { cx: 0, cy: -0.3, halfWidth: 1.2, halfHeight: 1.95 }, 70, [66, -96]);

    this.buildBody({
      torso: [
        ellipsoid(0.4, 0.3, 0.36, new THREE.Vector3(0, -0.8, -0.08)),
        ellipsoid(0.56, 0.5, 0.46, new THREE.Vector3(0, -1.15, -0.05)),
        ellipsoid(0.78, 0.6, 0.62, new THREE.Vector3(0, -1.62, -0.12)),
      ],
      belly: [ellipsoid(0.42, 0.5, 0.2, new THREE.Vector3(0, -1.48, 0.36))],
      fur: 0.14,
      neckPivotY: 70,
      shoulder: [52, 100],
      arm: { length: 90, width: 24, kind: 'claw' },
      foot: [56, 206],
      feet: { rx: 25, ry: 16, kind: 'claw', splay: 14 },
      armZ: 0.3,
    });

    // The head: a big ball of shaggy fur, shorter over the face so the eyes and mouth show.
    const head = lumpy(ellipsoid(HEAD.radii.x, HEAD.radii.y, HEAD.radii.z, HEAD.centre), 0.03, 51);
    furLength(head, (p, n) => {
      if (n.z <= 0) return 1;
      return THREE.MathUtils.smoothstep(Math.hypot((p.x - FACE.x) / FACE.rx, (p.y - FACE.y) / FACE.ry), 0.9, 1.25);
    });
    this.head.add(this.furry(head, 0.15, 'wool', { bristle: this.bristle, varying: true, density: 10 }));

    // A cowlick on top.
    const cowlick = tuftShape(0.22, 0.08, 0.06);
    cowlick.rotateZ(-0.25);
    cowlick.translate(0.02, 0.5, 0.06);
    this.head.add(this.furry(cowlick, 0.06, 'wool', { bristle: this.bristle }));

    // Stubby, rounded felt horns with a couple of ridges.
    this.horns = ([-1, 1] as const).map((side) => {
      const horn = new THREE.Group();
      horn.position.set(side * 0.32, 0.52, 0.02);
      const body = new THREE.Mesh(earShape(0.1, 0.17, 0.1, 0.72, 0.04), this.earInner);
      body.position.y = 0.15;
      horn.add(body);
      for (const [y, r] of [[0.1, 0.092], [0.19, 0.072]] as const) {
        const ridge = new THREE.Mesh(new THREE.TorusGeometry(r, 0.012, 8, 28), this.hoof);
        ridge.rotation.x = Math.PI / 2;
        ridge.position.y = y;
        horn.add(ridge);
      }
      this.head.add(horn);
      return horn;
    }) as [THREE.Group, THREE.Group];

    this.buildFace({
      layout: LAYOUT,
      dome: { centre: new THREE.Vector3(0, -0.04, 0.18), radii: new THREE.Vector3(0.52, 0.5, 0.36), svg: [0, 4, 52, 50] },
      teeth: true,
      paintDome: paintFur,
      fur: 0.045,
      lids: 'wool',
      browDrop: 0.02,
    });

    this.dress({
      bow: { parent: this.horns[0], at: new THREE.Vector3(0, 0.08, 0.08), tilt: 0.17 },
      flower: new THREE.Vector3(0.46, 0.46, 0.4),
      scarf: { y: -0.7, radius: 0.5, tail: new THREE.Vector3(0.22, -0.96, 0.42) },
    });

    this.finish(look, { colours: characterInfo('monster').presets[0]!.colours, accessory: 'none' });
  }

  protected drawParts(pose: Pose): void {
    const { face } = pose;

    // Fur: stands up when startled or cross, lies flat when sad; the wiggle that twitches the
    // llama's ears ruffles it.
    const bristle = Math.max(-1, Math.min(1.6, face.earLift + pose.mood.angry * 0.6 + Math.abs(pose.earWiggle) / 25));
    this.bristle.value = Math.min(1.6, Math.max(0.55, 1 + bristle * 0.35));

    // Horns lean out when it's down, stand straight when it's perky.
    const lean = 12 - face.earLift * 10 + pose.earWiggle * 0.4;
    this.horns.forEach((horn, i) => {
      const side = i === 0 ? -1 : 1;
      horn.rotation.z = -side * lean * (Math.PI / 180);
    });
  }
}

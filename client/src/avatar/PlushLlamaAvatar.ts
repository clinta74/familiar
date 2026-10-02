import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildFace, buildOverlay, buildPaw, curls, drawFace, drawOverlay, drawPaw, el, f, plushRoot, showAccessory, wool,
  type AccessoryParts, type FaceLayout, type FaceParts, type Overlay, type Puff,
} from './plush/parts';

const LAYOUT: FaceLayout = {
  eyeX: 23, eyeY: -6, eyeR: 12.5,
  browY: -24, browInnerX: 12, browOuterX: 31,
  mouthY: 42, mouthW: 12, mouthDrop: 16,
  cheekX: 35, cheekY: 24,
  eyeStyle: 'button', lashes: true,
};

const EAR_X = 42;
const EAR_Y = -56;
/** Where the head pivots on the neck for tilts and shakes. */
const NECK_PIVOT_Y = 60;

/** A tall llama ear, base at the origin, pointing up. */
function ear(parent: SVGGElement): SVGGElement {
  const g = el('g', {}, parent);
  el('path', { class: 'plush-wool plush-ear-outer plush-fuzzy', d: 'M-15,8 C-24,-26 -17,-76 0,-98 C17,-76 24,-26 15,8 Z' }, g);
  el('path', { class: 'plush-ear-inner', d: 'M-8,2 C-13,-24 -10,-60 0,-78 C10,-60 13,-24 8,2 Z' }, g);
  return g;
}

/**
 * The default familiar: a plush llama head, drawn in SVG. No assets, so it works offline, costs
 * nothing to load and carries no model licence. Its ears do a lot of the emotional work: perked for
 * joy and surprise, drooping when sad, pinned back when cross.
 */
export class PlushLlamaAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly head: SVGGElement;
  private readonly ears: [SVGGElement, SVGGElement];
  private readonly tuft: SVGGElement;
  private readonly faceParts: FaceParts;
  private readonly overlay: Overlay;
  private readonly hoof: SVGGElement;
  private readonly accessories: AccessoryParts;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const { root, id } = plushRoot('llama', 'Your familiar, a plush llama');
    this.root = root;

    // The long, fleecy neck: starts behind the head and runs down past the bottom of the stage,
    // which fades it out.
    const neck = el('g', {}, root);
    const neckPuffs: Puff[] = [];
    for (let y = 70; y <= 640; y += 26) neckPuffs.push([-50, y, 15], [50, y + 13, 15]);
    wool(neck, neckPuffs);
    el('rect', { x: -52, y: 50, width: 104, height: 600, class: 'plush-wool plush-fuzzy' }, neck);
    curls(neck, [[-24, 120], [20, 150, true], [-6, 196], [26, 236, true], [-28, 262], [6, 300]]);

    this.head = el('g', {}, root);
    this.ears = [ear(this.head), ear(this.head)];

    // The mane: a ring of fleece framing the face, open at the bottom where it becomes the neck.
    const mane: Puff[] = [];
    for (let i = 0; i < 26; i++) {
      const a = (i / 26) * Math.PI * 2;
      mane.push([Math.cos(a) * 60, 8 + Math.sin(a) * 66, i % 2 === 0 ? 18 : 16]);
    }
    wool(this.head, mane);
    curls(this.head, [[-66, 0], [62, 18, true], [-58, 46], [56, 56, true]]);

    // The face: bare, soft taupe, set inside the fleece.
    el('ellipse', { cx: 0, cy: 12, rx: 47, ry: 52, class: 'plush-skin plush-fuzzy' }, this.head);

    // The fringe: a big puffy topknot falling to just above the eyes.
    this.tuft = wool(this.head, [
      [-38, -44, 15], [-24, -60, 18], [-5, -68, 20], [15, -66, 19], [33, -54, 17], [42, -38, 13],
      [-28, -36, 14], [-9, -42, 16], [11, -40, 16], [28, -34, 13],
    ]);
    curls(this.tuft, [[-26, -58], [9, -66, true], [30, -50], [-7, -44], [20, -38, true]]);

    this.faceParts = buildFace(this.head, LAYOUT, id, {
      // The snout: a rounded muzzle with a soft nose and a split upper lip running down to the mouth.
      beforeMouth: (head) => {
        el('ellipse', { cx: 0, cy: 36, rx: 27, ry: 22, class: 'plush-snout' }, head);
        el('path', { class: 'plush-nose', d: 'M-10,22 C-10,16 10,16 10,22 C10,28 4,32 0,32 C-4,32 -10,28 -10,22 Z' }, head);
        el('path', { class: 'plush-nose-shine', d: 'M-6,21 Q-4,19 -2,21 M2,21 Q4,19 6,21' }, head);
        el('path', { class: 'plush-thread', d: 'M0,32 L0,40' }, head);
      },
    });

    this.accessories = buildAccessories({
      bow: { parent: this.ears[0], transform: 'translate(0 -16) rotate(-12)' },
      glasses: { parent: this.head, layout: LAYOUT },
      flower: { parent: this.head, x: 30, y: -60 },
      scarf: { parent: neck, y: 86, halfWidth: 62 },
    });

    // A fleecy hoof that pops up from below the stage to wave.
    this.hoof = buildPaw(root, 'hoof');
    this.overlay = buildOverlay(root);

    container.appendChild(root);
    if (look) this.setLook(look);
    this.start();
  }

  /** Recolours and redresses it in place, for the studio's live preview. */
  setLook(look: PlushLook): void {
    for (const [name, value] of Object.entries(llamaPalette(look.colours))) this.root.style.setProperty(name, value);
    showAccessory(this.accessories, look.accessory);
  }

  protected draw(pose: Pose): void {
    const { face } = pose;

    // Only the head moves; the neck stays put and the head pivots on top of it.
    this.head.setAttribute('transform',
      `translate(0 ${f(pose.dy)}) rotate(${f(pose.rotate)} 0 ${NECK_PIVOT_Y}) scale(${f(1 - pose.squash)} ${f(1 + pose.squash)})`);

    // Ears: perk towards upright, droop outward and down. Mirrored for the right ear.
    const lift = face.earLift;
    const earAngle = lift >= 0 ? 14 - lift * 10 : 14 - lift * 62;
    this.ears.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      const angle = side * (earAngle + pose.earWiggle * (i === 0 ? 1 : 0.6));
      e.setAttribute('transform', `translate(${side * EAR_X} ${EAR_Y}) rotate(${f(angle)})`);
    });

    this.tuft.setAttribute('transform', `translate(0 ${f(-Math.max(0, -pose.dy) * 0.4)})`);

    drawFace(this.faceParts, face, pose);
    drawPaw(this.hoof, pose);
    drawOverlay(this.overlay, pose);
  }
}

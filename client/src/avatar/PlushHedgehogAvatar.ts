import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildFace, buildOverlay, buildPaw, drawFace, drawOverlay, drawPaw, el, f, plushRoot, showAccessory,
  type AccessoryParts, type FaceLayout, type FaceParts, type Overlay,
} from './plush/parts';

const LAYOUT: FaceLayout = {
  eyeX: 20, eyeY: -4, eyeR: 9,
  browY: -19, browInnerX: 10, browOuterX: 27,
  mouthY: 50, mouthW: 8, mouthDrop: 11,
  cheekX: 32, cheekY: 20,
  eyeStyle: 'button', lashes: false,
};

const NECK_PIVOT_Y = 60;

/** How many prickles crown the head, front layer; the back layer has as many again, between them. */
const HEAD_SPINES = 30;

/** The crown runs from low on the left, over the top, to low on the right (SVG angles: down is π/2). */
const CROWN_FROM = Math.PI * 0.72;
const CROWN_TO = Math.PI * 2.28;

/**
 * The prickles as one path: soft, rounded spikes around an arc. `length` is how far they stand up
 * (they bristle when it's startled or cross, lie flat when it's sad); `ripple` sends a wave along them.
 */
export function spinePath(
  cx: number, cy: number, radius: number, from: number, to: number, count: number, length: number, ripple: number, t: number, width = 0.62,
): string {
  let d = '';
  for (let i = 0; i < count; i++) {
    const a = from + ((to - from) * (i + 0.5)) / count;
    const half = ((to - from) / count) * width;
    const len = length * (1 + 0.18 * Math.sin(i * 1.7) + ripple * Math.sin(t * 9 - i * 0.8));
    const base1 = [cx + Math.cos(a - half) * radius, cy + Math.sin(a - half) * radius];
    const base2 = [cx + Math.cos(a + half) * radius, cy + Math.sin(a + half) * radius];
    // The tip leans back a little, as fur does.
    const tipA = a + 0.12;
    const tip = [cx + Math.cos(tipA) * (radius + len), cy + Math.sin(tipA) * (radius + len)];
    const ctrl = [cx + Math.cos(a) * (radius + len * 0.55), cy + Math.sin(a) * (radius + len * 0.55)];
    d += `M${f(base1[0]!)},${f(base1[1]!)} Q${f(ctrl[0]! - 3)},${f(ctrl[1]!)} ${f(tip[0]!)},${f(tip[1]!)} Q${f(ctrl[0]! + 3)},${f(ctrl[1]!)} ${f(base2[0]!)},${f(base2[1]!)} Z `;
  }
  return d;
}

/**
 * A plush hedgehog: a round, soft face in a crown of prickles. The prickles are its ears, emotionally
 * speaking - they bristle up when it's surprised or cross and lie flat when it's sad or sleepy.
 */
export class PlushHedgehogAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly head: SVGGElement;
  /** Back and front layers of prickles, on the head and down the sides of the body. */
  private readonly spines: [SVGPathElement, SVGPathElement];
  private readonly bodySpines: [SVGPathElement, SVGPathElement];
  private readonly ears: [SVGGElement, SVGGElement];
  private readonly faceParts: FaceParts;
  private readonly overlay: Overlay;
  private readonly paw: SVGGElement;
  private readonly accessories: AccessoryParts;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const { root, id } = plushRoot('hedgehog', 'Your familiar, a plush hedgehog');
    this.root = root;

    // The body, a prickly back running down past the stage.
    const body = el('g', {}, root);
    this.bodySpines = [el('path', { class: 'plush-spines plush-spines-back plush-fuzzy' }, body), el('path', { class: 'plush-spines plush-fuzzy' }, body)];
    el('ellipse', { cx: 0, cy: 170, rx: 70, ry: 130, class: 'plush-wool plush-fuzzy plush-outlined' }, body);
    el('ellipse', { cx: 0, cy: 175, rx: 34, ry: 100, class: 'plush-skin plush-fuzzy' }, body);

    this.head = el('g', {}, root);
    this.spines = [el('path', { class: 'plush-spines plush-spines-back plush-fuzzy' }, this.head), el('path', { class: 'plush-spines plush-fuzzy' }, this.head)];
    el('circle', { cx: 0, cy: 0, r: 62, class: 'plush-wool plush-fuzzy plush-outlined' }, this.head);

    // Little round ears, peeking out where the face meets the prickles.
    const roundEar = () => {
      const g = el('g', {}, this.head);
      el('circle', { cx: 0, cy: 0, r: 11, class: 'plush-skin plush-fuzzy plush-outlined' }, g);
      el('circle', { cx: 0, cy: 1, r: 6, class: 'plush-ear-inner' }, g);
      return g;
    };
    this.ears = [roundEar(), roundEar()];

    // The face: a soft round patch, coming to a pointed snout.
    el('path', {
      class: 'plush-skin plush-fuzzy',
      d: 'M-50,-6 C-50,-40 -26,-48 0,-48 C26,-48 50,-40 50,-6 C50,22 34,40 16,48 C10,52 6,56 0,56 C-6,56 -10,52 -16,48 C-34,40 -50,22 -50,-6 Z',
    }, this.head);

    this.faceParts = buildFace(this.head, LAYOUT, id, {
      beforeMouth: (head) => {
        el('ellipse', { cx: 0, cy: 32, rx: 15, ry: 14, class: 'plush-snout' }, head);
        el('circle', { cx: 0, cy: 40, r: 7, class: 'plush-button' }, head);
        el('circle', { cx: 2.5, cy: 37.5, r: 2, class: 'plush-button-shine' }, head);
      },
    });

    this.accessories = buildAccessories({
      bow: { parent: this.head, transform: 'translate(-38 -56) rotate(-20) scale(0.9)' },
      glasses: { parent: this.head, layout: LAYOUT },
      flower: { parent: this.head, x: 34, y: -62 },
      scarf: { parent: body, y: 60, halfWidth: 60 },
    });

    this.paw = buildPaw(root, 'paw');
    this.overlay = buildOverlay(root);

    container.appendChild(root);
    if (look) this.setLook(look);
    this.start();
  }

  setLook(look: PlushLook): void {
    for (const [name, value] of Object.entries(llamaPalette(look.colours))) this.root.style.setProperty(name, value);
    showAccessory(this.accessories, look.accessory);
  }

  protected draw(pose: Pose): void {
    const { face } = pose;
    this.head.setAttribute('transform',
      `translate(0 ${f(pose.dy)}) rotate(${f(pose.rotate)} 0 ${NECK_PIVOT_Y}) scale(${f(1 - pose.squash)} ${f(1 + pose.squash)})`);

    // Prickles: lying flat (~16 out) when sad, bristling (~48) when startled or cross; any wiggle
    // runs a ripple along them. Two layers, the back one darker and offset, so they read as a coat.
    const length = 34 + face.earLift * 15 + pose.mood.angry * 8;
    const ripple = Math.min(0.5, Math.abs(pose.earWiggle) / 20);
    const step = (CROWN_TO - CROWN_FROM) / HEAD_SPINES;
    this.spines[0].setAttribute('d', spinePath(0, 0, 50, CROWN_FROM + step / 2, CROWN_TO + step / 2, HEAD_SPINES, length * 1.12, ripple, pose.t + 0.3, 0.9));
    this.spines[1].setAttribute('d', spinePath(0, 0, 54, CROWN_FROM, CROWN_TO, HEAD_SPINES, length, ripple, pose.t, 0.85));
    const side = (from: number, to: number, r: number, len: number, offset: number) =>
      spinePath(0, 170, r, from + offset, to + offset, 10, len, 0, pose.t, 0.85);
    const bodyLength = length * 0.85;
    this.bodySpines[0].setAttribute('d', side(Math.PI * 0.62, Math.PI * 1.3, 62, bodyLength * 1.1, 0.03) + side(Math.PI * 1.7, Math.PI * 2.38, 62, bodyLength * 1.1, 0.03));
    this.bodySpines[1].setAttribute('d', side(Math.PI * 0.62, Math.PI * 1.3, 66, bodyLength, 0) + side(Math.PI * 1.7, Math.PI * 2.38, 66, bodyLength, 0));

    // The little ears tip forward with interest and back when it's down.
    this.ears.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      e.setAttribute('transform', `translate(${side * 40} ${f(-40 - face.earLift * 3)}) rotate(${f(side * (10 - face.earLift * 12))})`);
    });

    drawFace(this.faceParts, face, pose);
    drawPaw(this.paw, pose);
    drawOverlay(this.overlay, pose);
  }
}

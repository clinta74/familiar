import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildFace, buildOverlay, drawFace, drawOverlay, el, f, foot, GROUND_Y, plushRoot, showAccessory, wool,
  type AccessoryParts, type BodySpec, type FaceLayout, type FaceParts, type Overlay, type Puff,
} from './plush/parts';

const LAYOUT: FaceLayout = {
  eyeX: 17, eyeY: -17, eyeR: 9,
  browY: -32, browInnerX: 8, browOuterX: 23,
  mouthY: 24, mouthW: 6, mouthDrop: 9,
  cheekX: 29, cheekY: 2,
  eyeStyle: 'button', lashes: false,
};

/** The ball: everything hangs off it and it moves as one, rocking on its bottom. */
const BALL = { cx: 0, cy: 8, rx: 92, ry: 96 };
const PIVOT_Y = 100;
/**
 * The ball is the whole toy, so it's drawn a little bigger than the others' heads and sits on
 * the ground where their feet rest.
 */
const SCALE = 1.15;
const SHIFT = GROUND_Y - 6 - (BALL.cy + BALL.ry) * SCALE;
const FEET: BodySpec['feet'] = { rx: 17, ry: 9, kind: 'paw', splay: 14 };
const FOOT = { x: 30, y: 100 };

/**
 * The fur's edge as one path of soft tufts around an ellipse: rounded, plush prickles rather than
 * spines. `length` is how far they stand out - they bristle when it's startled or cross and lie
 * flat when it's sad; `ripple` runs a wave round them.
 */
export function tuftPath(rx: number, ry: number, cx: number, cy: number, count: number, length: number, ripple: number, t: number): string {
  let d = '';
  const at = (angle: number, out: number) => [cx + Math.cos(angle) * (rx + out), cy + Math.sin(angle) * (ry + out)] as const;
  for (let i = 0; i < count; i++) {
    const a = (i / count) * Math.PI * 2;
    const half = (Math.PI / count) * 0.95;
    const len = length * (0.75 + 0.35 * Math.abs(Math.sin(i * 2.3))) * (1 + ripple * Math.sin(t * 9 - i * 0.7));
    const [x1, y1] = at(a - half, -2);
    const [x2, y2] = at(a + half, -2);
    const [tx, ty] = at(a + 0.04, len);
    const [c1x, c1y] = at(a - half * 0.55, len * 0.7);
    const [c2x, c2y] = at(a + half * 0.55, len * 0.7);
    d += `M${f(x1)},${f(y1)} Q${f(c1x)},${f(c1y)} ${f(tx)},${f(ty)} Q${f(c2x)},${f(c2y)} ${f(x2)},${f(y2)} Z `;
  }
  return d;
}

/** Short dark-tipped strokes in the fur, pointing outwards: the mottling of a plush hedgehog. */
function streaks(parent: SVGGElement) {
  let d = '';
  for (let ring = 0; ring < 4; ring++) {
    const count = 26 + ring * 6;
    for (let i = 0; i < count; i++) {
      const a = (i / count) * Math.PI * 2 + ring * 0.37;
      const r = 0.62 + ring * 0.1 + 0.03 * Math.sin(i * 3.1);
      const x = BALL.cx + Math.cos(a) * BALL.rx * r;
      const y = BALL.cy + Math.sin(a) * BALL.ry * r;
      // Not over the face and its ruff, nor the tummy.
      if (Math.hypot(x / 58, (y + 6) / 54) < 1 || Math.hypot(x / 44, (y - 66) / 40) < 1) continue;
      const len = 7 + 4 * Math.abs(Math.sin(i * 1.9 + ring));
      d += `M${f(x)},${f(y)} l${f(Math.cos(a) * len)},${f(Math.sin(a) * len)} `;
    }
  }
  el('path', { class: 'plush-streak', d }, parent);
}

/**
 * A plush hedgehog, after the classic round toy: a ball of soft, mottled fur sitting on two little
 * feet, with a cream face in a fluffy ruff, little round ears, a short snout with a big button
 * nose, and its paws held in front. Its prickles - soft tufts round its edge - bristle when it's
 * startled or cross and lie flat when it's sad.
 */
export class PlushHedgehogAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly head: SVGGElement;
  private readonly tufts: [SVGPathElement, SVGPathElement];
  private readonly ears: [SVGGElement, SVGGElement];
  private readonly feet: [SVGGElement, SVGGElement];
  private readonly wavePaw: SVGGElement;
  private readonly faceParts: FaceParts;
  private readonly overlay: Overlay;
  private readonly accessories: AccessoryParts;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const { root, id } = plushRoot('hedgehog', 'Your familiar, a plush hedgehog');
    this.root = root;

    // All one piece: a hedgehog toy has no neck to speak of.
    const stage = el('g', { transform: `translate(0 ${f(SHIFT)}) scale(${SCALE})` }, root);
    this.head = el('g', {}, stage);

    // The fur's edge, two layers of tufts: the back one darker, so the edge reads as fur.
    this.tufts = [
      el('path', { class: 'plush-spines plush-spines-back plush-fuzzy' }, this.head),
      el('path', { class: 'plush-spines plush-fuzzy' }, this.head),
    ];
    el('ellipse', { ...BALL, class: 'plush-wool plush-fuzzy' }, this.head);
    streaks(this.head);

    // Little round ears, set into the fur above the ruff.
    const roundEar = () => {
      const g = el('g', {}, this.head);
      el('ellipse', { cx: 0, cy: 0, rx: 13, ry: 12, class: 'plush-ear-outer-tan plush-fuzzy' }, g);
      el('ellipse', { cx: 0, cy: 1, rx: 7, ry: 6.5, class: 'plush-ear-inner' }, g);
      return g;
    };
    this.ears = [roundEar(), roundEar()];

    // The tummy, low on the ball, and two little feet poking out under it.
    el('ellipse', { cx: 0, cy: 66, rx: 40, ry: 38, class: 'plush-skin plush-fuzzy' }, this.head);
    this.feet = [foot(this.head, FEET, -1), foot(this.head, FEET, 1)];

    // The ruff: a fluffy cream frame round the face.
    const ruff: Puff[] = [];
    for (let i = 0; i < 18; i++) {
      const a = Math.PI * (0.92 + (1.16 * i) / 17);
      ruff.push([Math.cos(a) * 45, -6 + Math.sin(a) * 41, i % 2 === 0 ? 13 : 11]);
    }
    for (const x of [-43, 43]) ruff.push([x, 12, 12], [x * 0.82, 26, 11]);
    wool(this.head, ruff, 'plush-cream');

    // The face: a soft oval, coming forward into a short snout.
    el('ellipse', { cx: 0, cy: -4, rx: 39, ry: 36, class: 'plush-skin plush-fuzzy' }, this.head);

    this.faceParts = buildFace(this.head, LAYOUT, id, {
      beforeMouth: (head) => {
        el('ellipse', { cx: 0, cy: 6, rx: 17, ry: 15, class: 'plush-snout' }, head);
        el('ellipse', { cx: 0, cy: 7, rx: 9.5, ry: 8, class: 'plush-button' }, head);
        el('circle', { cx: 3, cy: 4, r: 2.4, class: 'plush-button-shine' }, head);
        el('path', { class: 'plush-thread', d: 'M0,15 L0,21' }, head);
      },
    });

    this.accessories = buildAccessories({
      bow: { parent: this.ears[0], transform: 'translate(-2 -10) rotate(-15) scale(0.75)' },
      glasses: { parent: this.head, layout: LAYOUT },
      flower: { parent: this.head, x: 46, y: -66 },
      scarf: { parent: this.head, y: 32, halfWidth: 46 },
    });

    // Its paws, held in front of its tummy (drawn last, so they're over a scarf). The right one
    // comes up to wave.
    el('ellipse', { cx: -22, cy: 60, rx: 13, ry: 9, transform: 'rotate(18 -22 60)', class: 'plush-paw plush-fuzzy' }, this.head);
    this.wavePaw = el('g', {}, this.head);
    el('ellipse', { cx: 22, cy: 60, rx: 13, ry: 9, transform: 'rotate(-18 22 60)', class: 'plush-paw plush-fuzzy' }, this.wavePaw);

    this.overlay = buildOverlay(root, -36);

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

    // The whole ball rocks and bobs; squash makes it squidge.
    this.head.setAttribute('transform',
      `translate(0 ${f(pose.dy)}) rotate(${f(pose.rotate)} 0 ${PIVOT_Y}) translate(0 ${PIVOT_Y}) scale(${f(1 + pose.squash * 0.6)} ${f(1 - pose.squash * 0.6)}) translate(0 ${-PIVOT_Y})`);

    // Tufts: ~6 out when flat and sad, ~22 bristling when startled or cross.
    const length = 12 + face.earLift * 8 + pose.mood.angry * 6;
    const ripple = Math.min(0.4, Math.abs(pose.earWiggle) / 25);
    this.tufts[0].setAttribute('d', tuftPath(BALL.rx - 4, BALL.ry - 4, BALL.cx, BALL.cy, 34, length * 1.15, ripple, pose.t + 0.4));
    this.tufts[1].setAttribute('d', tuftPath(BALL.rx, BALL.ry, BALL.cx, BALL.cy, 30, length, ripple, pose.t));

    // The little ears tip up with interest and droop back when it's down.
    this.ears.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      e.setAttribute('transform', `translate(${side * 42} ${f(-50 - face.earLift * 3)}) rotate(${f(side * (8 - face.earLift * 14))})`);
    });

    // Feet turn out a little, and kick up with each hop of joy.
    this.feet.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      e.setAttribute('transform', `translate(${side * FOOT.x} ${FOOT.y}) rotate(${f(-side * ((FEET.splay ?? 0) + pose.hop * 8))})`);
    });

    // The right paw comes up beside its face to wave.
    const lift = pose.pawLift ?? 0;
    this.wavePaw.setAttribute('transform', `translate(${f(lift * 26)} ${f(-lift * 72)}) rotate(${f(pose.pawAngle * 0.6 - lift * 30)} 22 60)`);

    drawFace(this.faceParts, face, pose);
    drawOverlay(this.overlay, pose);
  }
}

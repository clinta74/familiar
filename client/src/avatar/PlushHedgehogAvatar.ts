import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildFace, buildOverlay, drawFace, drawOverlay, el, f, foot, GROUND_Y, plushRoot, showAccessory,
  type AccessoryParts, type BodySpec, type FaceLayout, type FaceParts, type Overlay,
} from './plush/parts';

/** The face, low in front of the prickles: the sitting one's, moved down. */
const FACE = { cx: 0, cy: 44, rx: 42, ry: 38 };
const LAYOUT: FaceLayout = {
  eyeX: 17, eyeY: FACE.cy - 13, eyeR: 9,
  browY: FACE.cy - 28, browInnerX: 8, browOuterX: 23,
  mouthY: FACE.cy + 28, mouthW: 6, mouthDrop: 9,
  cheekX: 29, cheekY: FACE.cy + 6,
  eyeStyle: 'button', lashes: false,
};

/**
 * On all fours, facing you (2026-10-07, as the 3D one): the prickles a wide, low dome; everything
 * hangs off it and it moves as one, rocking on its feet.
 */
const BALL = { cx: 0, cy: 36, rx: 92, ry: 68 };
const PIVOT_Y = 100;
/** Where its feet rest: the bottom of the drawing. */
const FEET_Y = 108;
/**
 * The hedgehog is the whole toy, so it's drawn a little bigger than the others' heads and stands
 * on the ground where their feet rest.
 */
const SCALE = 1.15;
const SHIFT = GROUND_Y - 4 - FEET_Y * SCALE;
const FEET: BodySpec['feet'] = { rx: 15, ry: 11, kind: 'paw', splay: 0 };
const PAW = { x: 30, y: 98 };
const HIND = { x: 58, y: 96 };

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
      // Not over the face, nor low down where the tummy and paws are.
      if (Math.hypot((x - FACE.cx) / (FACE.rx + 14), (y - FACE.cy) / (FACE.ry + 12)) < 1 || y > 84) continue;
      const len = 7 + 4 * Math.abs(Math.sin(i * 1.9 + ring));
      d += `M${f(x)},${f(y)} l${f(Math.cos(a) * len)},${f(Math.sin(a) * len)} `;
    }
  }
  el('path', { class: 'plush-streak', d }, parent);
}

/**
 * A plush hedgehog on all fours, facing you, after the classic toy: a wide, low dome of soft,
 * mottled fur coming down over its forehead, a cream face set low in front with a short snout and a
 * big button nose, little round ears where the prickles meet the face, and four stubby legs - the
 * front paws poking out under its chin, the hind feet just showing at the sides. Its prickles - soft
 * tufts round its edge - bristle when it's startled or cross and lie flat when it's sad. (Until
 * 2026-10-07 it sat up, a round ball with its paws held in front: in git history.)
 */
export class PlushHedgehogAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly head: SVGGElement;
  private readonly tufts: [SVGPathElement, SVGPathElement];
  private readonly ears: [SVGGElement, SVGGElement];
  private readonly paws: [SVGGElement, SVGGElement];
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

    // The hind feet, just showing at the sides behind it.
    for (const side of [-1, 1]) el('ellipse', { cx: side * HIND.x, cy: HIND.y, rx: 14, ry: 10, class: 'plush-paw plush-fuzzy' }, this.head);

    // The fur's edge, two layers of tufts: the back one darker, so the edge reads as fur.
    this.tufts = [
      el('path', { class: 'plush-spines plush-spines-back plush-fuzzy' }, this.head),
      el('path', { class: 'plush-spines plush-fuzzy' }, this.head),
    ];
    el('ellipse', { ...BALL, class: 'plush-wool plush-fuzzy' }, this.head);
    streaks(this.head);

    // Little round ears, where the prickles meet the face.
    const roundEar = () => {
      const g = el('g', {}, this.head);
      el('ellipse', { cx: 0, cy: 0, rx: 13, ry: 12, class: 'plush-ear-outer-tan plush-fuzzy' }, g);
      el('ellipse', { cx: 0, cy: 1, rx: 7, ry: 6.5, class: 'plush-ear-inner' }, g);
      return g;
    };
    this.ears = [roundEar(), roundEar()];

    // The cream underneath, showing as a band between its legs.
    el('ellipse', { cx: 0, cy: 97, rx: 48, ry: 10, class: 'plush-skin plush-fuzzy' }, this.head);

    // The face: a soft oval set low in front of the prickles, coming forward into a short snout.
    el('ellipse', { ...FACE, class: 'plush-skin plush-fuzzy' }, this.head);

    this.faceParts = buildFace(this.head, LAYOUT, id, {
      beforeMouth: (head) => {
        const y = FACE.cy;
        el('ellipse', { cx: 0, cy: y + 10, rx: 17, ry: 15, class: 'plush-snout' }, head);
        el('ellipse', { cx: 0, cy: y + 11, rx: 9.5, ry: 8, class: 'plush-button' }, head);
        el('circle', { cx: 3, cy: y + 8, r: 2.4, class: 'plush-button-shine' }, head);
        el('path', { class: 'plush-thread', d: `M0,${y + 19} L0,${y + 25}` }, head);
      },
    });

    this.accessories = buildAccessories({
      bow: { parent: this.ears[0], transform: 'translate(-2 -10) rotate(-15) scale(0.75)' },
      glasses: { parent: this.head, layout: LAYOUT },
      flower: { parent: this.head, x: 50, y: -8 },
      // A collar under its chin.
      scarf: { parent: this.head, y: FACE.cy + 36, halfWidth: 38 },
    });

    // Its front paws, poking out under its chin (drawn last, so they're over a scarf); the right one
    // lifts to wave.
    this.paws = [foot(this.head, FEET, -1), foot(this.head, FEET, 1)];

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
      e.setAttribute('transform', `translate(${side * 40} ${f(FACE.cy - 40 - face.earLift * 3)}) rotate(${f(side * (8 - face.earLift * 14))})`);
    });

    // The front paws kick up with each hop of joy; the right one lifts up beside its cheek to wave.
    const lift = pose.pawLift ?? 0;
    this.paws.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      const up = side === 1 ? lift : 0;
      const turn = -side * pose.hop * 8 + (side === 1 ? pose.pawAngle * 0.6 - up * 20 : 0);
      e.setAttribute('transform', `translate(${f(side * PAW.x + up * 10)} ${f(PAW.y - up * 40)}) rotate(${f(turn)})`);
    });

    drawFace(this.faceParts, face, pose);
    drawOverlay(this.overlay, pose);
  }
}

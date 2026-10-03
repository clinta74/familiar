import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildFace, buildOverlay, buildPaw, drawFace, drawOverlay, drawPaw, el, f, plushRoot, showAccessory, wool,
  type AccessoryParts, type FaceLayout, type FaceParts, type Overlay, type Puff,
} from './plush/parts';

const LAYOUT: FaceLayout = {
  eyeX: 24, eyeY: -8, eyeR: 10.5,
  browY: -25, browInnerX: 13, browOuterX: 31,
  mouthY: 41, mouthW: 10, mouthDrop: 14,
  cheekX: 36, cheekY: 16,
  eyeStyle: 'button', lashes: false,
};

const EAR_X = 38;
const EAR_Y = -44;
const NECK_PIVOT_Y = 56;

/** A big pointed fox ear, base at the origin, pointing up, with a dark inside. */
function ear(parent: SVGGElement): SVGGElement {
  const g = el('g', {}, parent);
  el('path', { class: 'plush-wool plush-ear-outer plush-fuzzy', d: 'M-24,10 C-20,-18 -9,-48 0,-66 C9,-48 20,-18 24,10 Z' }, g);
  el('path', { class: 'plush-ear-inner', d: 'M-12,4 C-10,-16 -5,-36 0,-48 C5,-36 10,-16 12,4 Z' }, g);
  el('path', { class: 'plush-ear-tuft', d: 'M-8,4 L-5,-8 L-2,2 L1,-12 L4,2 L7,-6 L9,4 Z' }, g);
  return g;
}

/**
 * A plush fox: big pointed ears that carry its mood the way the llama's do, a cream muzzle and
 * chest, and a bushy tail that swishes when it's happy.
 */
export class PlushFoxAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly head: SVGGElement;
  private readonly ears: [SVGGElement, SVGGElement];
  private readonly tail: SVGGElement;
  private readonly faceParts: FaceParts;
  private readonly overlay: Overlay;
  private readonly paw: SVGGElement;
  private readonly accessories: AccessoryParts;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const { root, id } = plushRoot('fox', 'Your familiar, a plush fox');
    this.root = root;

    // The tail, behind everything: a bushy brush curling up on the right, white at the tip.
    this.tail = el('g', {}, root);
    el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: 'M40,150 C110,140 128,70 104,10 C96,-10 80,-14 76,2 C92,40 84,92 30,104 Z' }, this.tail);
    el('path', { class: 'plush-skin plush-fuzzy', d: 'M104,10 C96,-10 80,-14 76,2 C80,12 84,22 86,30 C96,26 104,18 104,10 Z' }, this.tail);

    // Neck and chest: coat at the sides, a cream bib down the middle, off the bottom of the stage.
    const neck = el('g', {}, root);
    const sides: Puff[] = [];
    for (let y = 60; y <= 600; y += 24) sides.push([-48, y, 14], [48, y + 12, 14]);
    wool(neck, sides);
    el('rect', { x: -50, y: 40, width: 100, height: 600, class: 'plush-wool plush-fuzzy' }, neck);
    const bib: Puff[] = [];
    for (let y = 60; y <= 300; y += 22) bib.push([-14, y, 20], [14, y + 11, 20]);
    wool(neck, bib, 'plush-cream');

    this.head = el('g', {}, root);
    this.ears = [ear(this.head), ear(this.head)];

    // The head: wide at the cheeks, with fluffy tufts sticking out, narrowing to the snout.
    el('path', {
      class: 'plush-wool plush-fuzzy plush-outlined',
      d: 'M-60,-14 C-62,-48 -30,-62 0,-62 C30,-62 62,-48 60,-14 L80,8 L60,12 L70,26 L48,30 C34,48 18,58 0,62 C-18,58 -34,48 -48,30 L-70,26 L-60,12 L-80,8 Z',
    }, this.head);

    // The cream mask: cheeks and muzzle.
    el('path', {
      class: 'plush-skin plush-fuzzy',
      d: 'M-62,10 C-44,2 -20,8 0,14 C20,8 44,2 62,10 C50,32 30,54 0,62 C-30,54 -50,32 -62,10 Z',
    }, this.head);

    this.faceParts = buildFace(this.head, LAYOUT, id, {
      beforeMouth: (head) => {
        el('ellipse', { cx: 0, cy: 30, rx: 20, ry: 16, class: 'plush-snout' }, head);
        el('path', { class: 'plush-nose', d: 'M-9,22 C-9,17 9,17 9,22 C9,28 3,31 0,31 C-3,31 -9,28 -9,22 Z' }, head);
        el('path', { class: 'plush-nose-shine', d: 'M-5,21 Q-3,19 -1,21' }, head);
        el('path', { class: 'plush-thread', d: 'M0,31 L0,38' }, head);
      },
    });

    this.accessories = buildAccessories({
      bow: { parent: this.ears[0], transform: 'translate(2 -14) rotate(-10) scale(0.85)' },
      glasses: { parent: this.head, layout: LAYOUT },
      flower: { parent: this.head, x: 30, y: -50 },
      scarf: { parent: this.head, y: 50, halfWidth: 40 },
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

    // Fox ears swivel and flatten more than a llama's: perked forward when happy or curious, flat
    // out to the sides when sad or cross.
    const lift = face.earLift;
    const earAngle = lift >= 0 ? 12 - lift * 10 : 12 - lift * 70;
    this.ears.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      const angle = side * (earAngle + pose.earWiggle * (i === 0 ? 1 : 0.6));
      e.setAttribute('transform', `translate(${side * EAR_X} ${EAR_Y}) rotate(${f(angle)})`);
    });

    // The tail: swishes when happy or excited, droops when sad, still otherwise.
    const happy = pose.mood.joy + (pose.gesture?.name === 'excited' ? 1 : 0);
    const swish = Math.sin(pose.t * Math.PI * 2 * 1.6) * 10 * Math.min(1, happy) * pose.motion;
    const droop = pose.mood.sad * 18;
    this.tail.setAttribute('transform', `rotate(${f(swish + droop)} 40 140)`);

    drawFace(this.faceParts, face, pose);
    drawPaw(this.paw, pose);
    drawOverlay(this.overlay, pose);
  }
}

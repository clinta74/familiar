import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildBody, buildFace, buildOverlay, drawBody, drawFace, drawOverlay, el, f, plushRoot, showAccessory, wool,
  type AccessoryParts, type BodyParts, type FaceLayout, type FaceParts, type Overlay,
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
/** Where the tail joins the body; it swishes from here. */
const HIP = { x: 36, y: 200 };

/** A big pointed fox ear, base at the origin, pointing up, with a dark inside. */
function ear(parent: SVGGElement): SVGGElement {
  const g = el('g', {}, parent);
  el('path', { class: 'plush-wool plush-ear-outer plush-fuzzy', d: 'M-24,10 C-20,-18 -9,-48 0,-66 C9,-48 20,-18 24,10 Z' }, g);
  el('path', { class: 'plush-ear-inner', d: 'M-12,4 C-10,-16 -5,-36 0,-48 C5,-36 10,-16 12,4 Z' }, g);
  el('path', { class: 'plush-ear-tuft', d: 'M-8,4 L-5,-8 L-2,2 L1,-12 L4,2 L7,-6 L9,4 Z' }, g);
  return g;
}

/**
 * A plush fox, sitting up: big pointed ears that carry its mood the way the llama's do, a cream
 * muzzle and a big cream bib down its chest, dark paws and feet, and a bushy tail curled round
 * beside it that swishes when it's happy.
 */
export class PlushFoxAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly body: BodyParts;
  private readonly head: SVGGElement;
  private readonly ears: [SVGGElement, SVGGElement];
  private readonly tail: SVGGElement;
  private readonly faceParts: FaceParts;
  private readonly overlay: Overlay;
  private readonly accessories: AccessoryParts;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const { root, id } = plushRoot('fox', 'Your familiar, a plush fox');
    this.root = root;

    // The tail, behind everything: a bushy brush from the hip, curling up on the right, white at the tip.
    this.tail = el('g', {}, root);
    el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: 'M26,206 C116,222 160,150 132,76 C121,44 84,48 82,76 C104,112 98,168 22,190 Z' }, this.tail);
    el('path', { class: 'plush-skin plush-fuzzy', d: 'M132,76 C121,44 84,48 82,76 C86,88 91,98 94,108 C110,104 130,98 132,76 Z' }, this.tail);

    this.body = buildBody(root, {
      torso: (body) => {
        // Sitting upright: a soft, slightly pear-shaped torso with a fluffy cream bib down the chest.
        el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: 'M-58,66 C-76,108 -84,170 -74,214 L74,214 C84,170 76,108 58,66 C40,50 -40,50 -58,66 Z' }, body);
        const bib = el('g', {}, body);
        wool(bib, [[-36, 72, 14], [36, 72, 14], [-30, 96, 13], [30, 96, 13], [-23, 120, 12], [23, 120, 12], [-15, 144, 11], [15, 144, 11], [0, 164, 11]], 'plush-cream');
        el('path', { class: 'plush-skin plush-fuzzy', d: 'M-40,64 C-32,96 -24,140 0,170 C24,140 32,96 40,64 C22,56 -22,56 -40,64 Z' }, bib);
      },
      neckPivotY: NECK_PIVOT_Y,
      shoulder: [44, 94],
      arm: { length: 98, width: 20, kind: 'paw' },
      foot: [56, 206],
      feet: { rx: 26, ry: 15, kind: 'paw', splay: 12 },
    });

    this.head = this.body.head;
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
    drawBody(this.body, pose);

    // Fox ears swivel and flatten more than a llama's: perked forward when happy or curious, flat
    // out to the sides when sad or cross.
    const lift = face.earLift;
    const earAngle = lift >= 0 ? 12 - lift * 10 : 12 - lift * 70;
    this.ears.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      const angle = side * (earAngle + pose.earWiggle * (i === 0 ? 1 : 0.6));
      e.setAttribute('transform', `translate(${side * EAR_X} ${EAR_Y}) rotate(${f(angle)})`);
    });

    // The tail: swishes when happy or excited, droops when sad, still otherwise. It moves with the body.
    const happy = pose.mood.joy + (pose.gesture?.name === 'excited' ? 1 : 0);
    const swish = Math.sin(pose.t * Math.PI * 2 * 1.6) * 10 * Math.min(1, happy) * pose.motion;
    const droop = pose.mood.sad * 18;
    this.tail.setAttribute('transform', `translate(0 ${f(pose.dy * 0.6)}) rotate(${f(swish + droop)} ${HIP.x} ${HIP.y})`);

    drawFace(this.faceParts, face, pose);
    drawOverlay(this.overlay, pose);
  }
}

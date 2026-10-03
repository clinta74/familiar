import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildBody, buildFace, buildOverlay, drawBody, drawFace, drawOverlay, el, f, plushRoot, showAccessory,
  type AccessoryParts, type BodyParts, type FaceLayout, type FaceParts, type Overlay,
} from './plush/parts';

export const OTTER_LAYOUT: FaceLayout = {
  eyeX: 26, eyeY: -10, eyeR: 9.5,
  browY: -27, browInnerX: 16, browOuterX: 33,
  mouthY: 41, mouthW: 9, mouthDrop: 12,
  cheekX: 42, cheekY: 16,
  eyeStyle: 'button', lashes: false,
};

const EAR_X = 52;
const EAR_Y = -34;
const NECK_PIVOT_Y = 58;
/** Where the tail joins the body; it swishes from here. */
const HIP = { x: 48, y: 200 };

/**
 * A plush otter, sitting up: sleek and round, a broad head with little round ears set low, a big
 * pale muzzle of two whisker pads under a big dark nose, whiskers that twitch and droop, a cream
 * chest, and a thick tapering tail curled round on the ground beside it that swishes when it's happy.
 */
export class PlushOtterAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly body: BodyParts;
  private readonly head: SVGGElement;
  private readonly ears: [SVGGElement, SVGGElement];
  private readonly whiskers: [SVGGElement, SVGGElement];
  private readonly tail: SVGGElement;
  private readonly faceParts: FaceParts;
  private readonly overlay: Overlay;
  private readonly accessories: AccessoryParts;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const { root, id } = plushRoot('otter', 'Your familiar, a plush otter');
    this.root = root;

    // The tail, behind: thick at the root, tapering, curled round on the ground to the right.
    this.tail = el('g', {}, root);
    el('path', {
      class: 'plush-wool plush-fuzzy plush-outlined',
      d: 'M30,196 C96,214 150,206 152,170 C153,150 136,140 124,150 C116,158 124,170 130,170 C128,186 98,192 34,176 Z',
    }, this.tail);

    this.body = buildBody(root, {
      torso: (body) => {
        // A sleek, round-bellied body with a cream chest.
        el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: 'M-56,64 C-80,104 -88,170 -76,214 L76,214 C88,170 80,104 56,64 C38,50 -38,50 -56,64 Z' }, body);
        el('path', { class: 'plush-skin plush-fuzzy', d: 'M-38,66 C-46,110 -44,160 -26,196 C-10,206 10,206 26,196 C44,160 46,110 38,66 C20,56 -20,56 -38,66 Z' }, body);
      },
      neckPivotY: NECK_PIVOT_Y,
      shoulder: [44, 96],
      arm: { length: 88, width: 22, kind: 'paw' },
      foot: [54, 206],
      feet: { rx: 26, ry: 14, kind: 'paw', splay: 14 },
    });

    this.head = this.body.head;

    // Little round ears, set low on the sides of the broad head.
    const ear = () => {
      const g = el('g', {}, this.head);
      el('ellipse', { cx: 0, cy: 0, rx: 13, ry: 12, class: 'plush-wool plush-fuzzy plush-outlined' }, g);
      el('ellipse', { cx: 0, cy: 1, rx: 7, ry: 6.5, class: 'plush-ear-inner' }, g);
      return g;
    };
    this.ears = [ear(), ear()];

    // The head: broad and round, a little flat on top.
    el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: 'M-64,6 C-66,-34 -36,-56 0,-56 C36,-56 66,-34 64,6 C62,40 34,62 0,62 C-34,62 -62,40 -64,6 Z' }, this.head);

    // Whiskers, from the muzzle pads: they twitch and droop.
    this.whiskers = [-1, 1].map((side) => {
      const g = el('g', {}, this.head);
      const d = [[-2, 10], [0, 18], [2, 26]].map(([dy, len]) => `M${side * 28},${32 + dy!} q${side * 20},${-4 + dy! * 0.6} ${side * (36 + len! * 0.4)},${-2 + dy! * 1.4}`).join(' ');
      el('path', { class: 'plush-whisker', d }, g);
      return g;
    }) as [SVGGElement, SVGGElement];

    this.faceParts = buildFace(this.head, OTTER_LAYOUT, id, {
      beforeMouth: (head) => {
        // The muzzle: two big pale whisker pads and a chin, under a big dark nose.
        el('ellipse', { cx: -15, cy: 30, rx: 21, ry: 16, class: 'plush-skin plush-fuzzy' }, head);
        el('ellipse', { cx: 15, cy: 30, rx: 21, ry: 16, class: 'plush-skin plush-fuzzy' }, head);
        el('ellipse', { cx: 0, cy: 44, rx: 15, ry: 10, class: 'plush-skin plush-fuzzy' }, head);
        for (const [x, y] of [[-24, 26], [-18, 34], [-28, 34], [24, 26], [18, 34], [28, 34]] as const) {
          el('circle', { cx: x, cy: y, r: 1.4, class: 'plush-patch' }, head);
        }
        el('path', { class: 'plush-patch', d: 'M-12,14 C-12,8 12,8 12,14 C12,21 4,25 0,25 C-4,25 -12,21 -12,14 Z' }, head);
        el('path', { class: 'plush-nose-shine', d: 'M-6,13 Q-3,11 0,13' }, head);
        el('path', { class: 'plush-thread', d: 'M0,25 L0,33' }, head);
      },
    });

    this.accessories = buildAccessories({
      bow: { parent: this.ears[0], transform: 'translate(-2 -8) rotate(-15) scale(0.75)' },
      glasses: { parent: this.head, layout: OTTER_LAYOUT },
      flower: { parent: this.head, x: 36, y: -46 },
      scarf: { parent: this.head, y: 56, halfWidth: 50 },
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

    // Small ears: they tip up a little with interest and fold down when it's low.
    this.ears.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      const angle = side * (6 - face.earLift * 12 + pose.earWiggle * 0.5);
      e.setAttribute('transform', `translate(${side * EAR_X} ${f(EAR_Y - face.earLift * 3)}) rotate(${f(angle)})`);
    });

    // Whiskers twitch with the ears and droop when it's sad.
    const droop = pose.mood.sad * 12 - pose.mood.joy * 4;
    this.whiskers.forEach((w, i) => {
      const side = i === 0 ? -1 : 1;
      w.setAttribute('transform', `rotate(${f(side * (droop + pose.earWiggle * 0.6))} ${side * 28} 32)`);
    });

    // The tail swishes when happy or excited, droops when sad.
    const happy = pose.mood.joy + (pose.gesture?.name === 'excited' ? 1 : 0);
    const swish = Math.sin(pose.t * Math.PI * 2 * 1.4) * 8 * Math.min(1, happy) * pose.motion;
    this.tail.setAttribute('transform', `translate(0 ${f(pose.dy * 0.6)}) rotate(${f(swish + pose.mood.sad * 8)} ${HIP.x} ${HIP.y})`);

    drawFace(this.faceParts, face, pose);
    drawOverlay(this.overlay, pose);
  }
}

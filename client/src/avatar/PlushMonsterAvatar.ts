import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildBody, buildFace, buildOverlay, drawBody, drawFace, drawOverlay, el, f, plushRoot, showAccessory, wool,
  type AccessoryParts, type BodyParts, type FaceLayout, type FaceParts, type Overlay, type Puff,
} from './plush/parts';

const LAYOUT: FaceLayout = {
  eyeX: 26, eyeY: -14, eyeR: 16,
  browY: -38, browInnerX: 12, browOuterX: 38,
  mouthY: 24, mouthW: 26, mouthDrop: 30,
  cheekX: 46, cheekY: 12,
  eyeStyle: 'googly', lashes: false,
};

const NECK_PIVOT_Y = 70;
const HORN_X = 32;
const HORN_Y = -62;

/** A stubby, rounded horn, base at the origin, pointing up. */
function horn(parent: SVGGElement): SVGGElement {
  const g = el('g', {}, parent);
  el('path', { class: 'plush-horn', d: 'M-10,6 C-11,-10 -6,-24 2,-32 C4,-22 9,-10 10,6 Z' }, g);
  el('path', { class: 'plush-horn-ridge', d: 'M-8,-4 Q0,-1 9,-4 M-6,-14 Q1,-11 8,-15' }, g);
  return g;
}

/** Where the fur tufts sit around the head; drawn again each frame as they bristle or settle. */
function furRing(bristle: number, t: number): Puff[] {
  const puffs: Puff[] = [];
  for (let i = 0; i < 30; i++) {
    const a = (i / 30) * Math.PI * 2;
    const r = 66 + bristle * 6 * (i % 2 === 0 ? 1 : 0.4) + Math.sin(t * 7 + i) * bristle * 1.5;
    puffs.push([Math.cos(a) * r, 6 + Math.sin(a) * r * 0.96, i % 2 === 0 ? 15 : 12]);
  }
  return puffs;
}

/**
 * A fuzzy, friendly monster, sitting on its round furry bottom: big googly eyes, stubby horns, a
 * wide mouth with two little fangs, a lighter belly and clawed paws. Its fur stands on end when
 * it's startled or cross, and goes flat when sad.
 */
export class PlushMonsterAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly body: BodyParts;
  private readonly head: SVGGElement;
  /** The fur tufts: outline circles and fur circles, moved each frame rather than rebuilt. */
  private readonly fur: { outline: SVGCircleElement[]; tuft: SVGCircleElement[] };
  private readonly horns: [SVGGElement, SVGGElement];
  private readonly faceParts: FaceParts;
  private readonly overlay: Overlay;
  private readonly accessories: AccessoryParts;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const { root, id } = plushRoot('monster', 'Your familiar, a fuzzy monster');
    this.root = root;

    this.body = buildBody(root, {
      torso: (body) => {
        // A furry, round-bottomed body with tufts round its edge and a lighter belly.
        const edge: Puff[] = [];
        for (let i = 0; i < 24; i++) {
          const a = Math.PI * (-0.14 + (1.28 * i) / 23);
          edge.push([Math.cos(a) * 78, 150 + Math.sin(a) * 66, i % 2 === 0 ? 15 : 12]);
        }
        wool(body, edge);
        el('path', { class: 'plush-wool plush-fuzzy', d: 'M-52,70 C-70,90 -78,120 -78,150 C-78,186 -40,216 0,216 C40,216 78,186 78,150 C78,120 70,90 52,70 Z' }, body);
        el('ellipse', { cx: 0, cy: 152, rx: 42, ry: 50, class: 'plush-skin plush-fuzzy' }, body);
      },
      neckPivotY: NECK_PIVOT_Y,
      shoulder: [52, 100],
      arm: { length: 90, width: 24, kind: 'claw', fluffy: true },
      foot: [56, 206],
      feet: { rx: 25, ry: 16, kind: 'claw', splay: 14 },
    });

    this.head = this.body.head;
    this.horns = [horn(this.head), horn(this.head)];
    const furGroup = el('g', {}, this.head);
    const ring = furRing(0, 0);
    const outline = ring.map(([, , r]) => el('circle', { r: r + 2.6, class: 'plush-wool-outline' }, furGroup));
    const tuftGroup = el('g', { class: 'plush-fuzzy' }, furGroup);
    const tuft = ring.map(([, , r]) => el('circle', { r, class: 'plush-wool' }, tuftGroup));
    this.fur = { outline, tuft };
    el('circle', { cx: 0, cy: 6, r: 66, class: 'plush-wool plush-fuzzy' }, this.head);

    // A tuft on top, like a cowlick.
    el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: 'M-10,-54 C-14,-74 -2,-82 4,-76 C0,-70 6,-66 12,-72 C14,-62 8,-56 -10,-54 Z' }, this.head);

    this.faceParts = buildFace(this.head, LAYOUT, id, { teeth: true });

    this.accessories = buildAccessories({
      bow: { parent: this.horns[0], transform: 'translate(0 -6) rotate(-10) scale(0.8)' },
      glasses: { parent: this.head, layout: LAYOUT },
      flower: { parent: this.head, x: 46, y: -46 },
      scarf: { parent: this.head, y: 64, halfWidth: 52 },
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

    // Fur: stands up (bristle 1) when startled or cross, lies flat (-1) when sad; the wiggle
    // that twitches the llama's ears ruffles it.
    const bristle = Math.max(-1, Math.min(1.6, face.earLift + pose.mood.angry * 0.6 + Math.abs(pose.earWiggle) / 25));
    furRing(bristle, pose.t).forEach(([cx, cy], i) => {
      for (const c of [this.fur.outline[i]!, this.fur.tuft[i]!]) {
        c.setAttribute('cx', f(cx));
        c.setAttribute('cy', f(cy));
      }
    });

    // Horns lean out when it's down, stand straight when it's perky.
    const lean = 12 - face.earLift * 10 + pose.earWiggle * 0.4;
    this.horns.forEach((h, i) => {
      const side = i === 0 ? -1 : 1;
      h.setAttribute('transform', `translate(${side * HORN_X} ${HORN_Y}) rotate(${f(side * lean)})`);
    });

    drawFace(this.faceParts, face, pose);
    drawOverlay(this.overlay, pose);
  }
}

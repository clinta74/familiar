import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildFace, buildOverlay, buildPaw, drawFace, drawOverlay, drawPaw, el, f, plushRoot, showAccessory, wool,
  type AccessoryParts, type FaceLayout, type FaceParts, type Overlay, type Puff,
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
 * A fuzzy, friendly monster: a round ball of fur with big googly eyes, stubby horns and a wide mouth
 * with two little fangs. Its fur stands on end when it's startled or cross, and goes flat when sad.
 */
export class PlushMonsterAvatar extends PlushCharacter {
  protected readonly root: SVGSVGElement;
  private readonly head: SVGGElement;
  /** The fur tufts: outline circles and fur circles, moved each frame rather than rebuilt. */
  private readonly fur: { outline: SVGCircleElement[]; tuft: SVGCircleElement[] };
  private readonly horns: [SVGGElement, SVGGElement];
  private readonly faceParts: FaceParts;
  private readonly overlay: Overlay;
  private readonly paw: SVGGElement;
  private readonly accessories: AccessoryParts;

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    const { root, id } = plushRoot('monster', 'Your familiar, a fuzzy monster');
    this.root = root;

    // The body below, a furry trunk running off the stage, with a lighter belly.
    const body = el('g', {}, root);
    const sides: Puff[] = [];
    for (let y = 80; y <= 600; y += 24) sides.push([-58, y, 16], [58, y + 12, 16]);
    wool(body, sides);
    el('rect', { x: -60, y: 60, width: 120, height: 600, class: 'plush-wool plush-fuzzy' }, body);
    el('ellipse', { cx: 0, cy: 190, rx: 42, ry: 110, class: 'plush-skin plush-fuzzy' }, body);

    this.head = el('g', {}, root);
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
      scarf: { parent: body, y: 74, halfWidth: 64 },
    });

    this.paw = buildPaw(root, 'claw');
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
    drawPaw(this.paw, pose);
    drawOverlay(this.overlay, pose);
  }
}

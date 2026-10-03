import { llamaPalette } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import {
  buildAccessories, buildBody, buildFace, buildOverlay, drawBody, drawFace, drawOverlay, el, f, plushRoot, showAccessory,
  type AccessoryParts, type BodyParts, type FaceLayout, type FaceParts, type Overlay,
} from './plush/parts';

export const GIRAFFE_LAYOUT: FaceLayout = {
  eyeX: 19, eyeY: -82, eyeR: 9.5,
  browY: -98, browInnerX: 10, browOuterX: 26,
  mouthY: -36, mouthW: 11, mouthDrop: 12,
  cheekX: 30, cheekY: -62,
  eyeStyle: 'button', lashes: true,
};

/** The head tilts on the top of the neck. */
const NECK_PIVOT_Y = -24;
const EAR_X = 42;
const EAR_Y = -100;
const HIP = { x: 58, y: 196 };

/** The neck and body as one outline: a long neck rising out of a sitting body. */
const BODY_PATH = 'M-22,-30 C-26,10 -32,48 -52,74 C-78,110 -84,172 -74,214 L74,214 C84,172 78,110 52,74 C32,48 26,10 22,-30 Z';

/** Where the patches sit on the neck and body (x, y, size), in the coat's colour gaps between them. */
const PATCHES: [number, number, number][] = [
  [-8, -14, 9], [10, 2, 10], [-12, 24, 11], [12, 40, 11], [-16, 58, 12], [20, 70, 12],
  [-44, 96, 14], [-6, 92, 13], [34, 104, 14], [-58, 136, 14], [-20, 128, 15], [18, 140, 15], [56, 140, 13],
  [-44, 174, 15], [-2, 168, 15], [40, 176, 15], [-64, 200, 10], [62, 200, 10],
];

/** A soft, irregular patch: a rounded polygon. */
function patch(x: number, y: number, r: number, seed: number): string {
  const corners = 6;
  const points = Array.from({ length: corners }, (_, i) => {
    const a = (i / corners) * Math.PI * 2 + seed;
    const k = 0.75 + 0.25 * Math.abs(Math.sin(seed * 3.1 + i * 1.7));
    return [x + Math.cos(a) * r * k, y + Math.sin(a) * r * k * 0.9] as const;
  });
  const mid = (a: readonly [number, number], b: readonly [number, number]) => [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2] as const;
  let d = '';
  points.forEach((p, i) => {
    const next = points[(i + 1) % corners]!;
    const m = mid(p, next);
    d += i === 0 ? `M${f(mid(points[corners - 1]!, p)[0])},${f(mid(points[corners - 1]!, p)[1])} ` : '';
    d += `Q${f(p[0])},${f(p[1])} ${f(m[0])},${f(m[1])} `;
  });
  return `${d}Z`;
}

/**
 * A plush giraffe, sitting: a long neck rising out of its body, a patchwork coat, a long face with a
 * pale muzzle, two little horns with dark knobs, sideways ears that carry its mood, the longest
 * lashes of all, and a thin tail with a dark tuft.
 */
export class PlushGiraffeAvatar extends PlushCharacter {
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
    const { root, defs, id } = plushRoot('giraffe', 'Your familiar, a plush giraffe');
    this.root = root;

    const clip = el('clipPath', { id: `${id}-coat` }, defs);
    el('path', { d: BODY_PATH }, clip);

    // The tail, behind: a thin rope with a dark tuft at the end.
    this.tail = el('g', {}, root);
    el('path', { class: 'plush-thread', d: 'M54,192 C84,196 104,186 110,160', style: 'stroke: var(--plush-wool-shadow); stroke-width: 7' }, this.tail);
    el('path', { class: 'plush-patch plush-fuzzy', d: 'M104,166 C100,150 110,136 118,140 C124,146 120,162 110,170 Z' }, this.tail);

    this.body = buildBody(root, {
      torso: (body) => {
        el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: BODY_PATH }, body);
        // The tummy, paler.
        el('ellipse', { cx: 0, cy: 168, rx: 40, ry: 40, class: 'plush-skin plush-fuzzy' }, body);
        const coat = el('g', { 'clip-path': `url(#${id}-coat)` }, body);
        PATCHES.forEach(([x, y, r], i) => {
          if (Math.hypot(x / 40, (y - 168) / 40) < 1) return;
          el('path', { class: 'plush-patch plush-fuzzy', d: patch(x, y, r, i + 1) }, coat);
        });
      },
      neckPivotY: NECK_PIVOT_Y,
      shoulder: [40, 104],
      arm: { length: 90, width: 20, kind: 'hoof' },
      foot: [52, 206],
      feet: { rx: 24, ry: 14, kind: 'hoof', splay: 10 },
    });

    this.head = this.body.head;

    // Two little horns (ossicones) with dark knobs, and a tuft of mane between them.
    for (const side of [-1, 1]) {
      el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: `M${side * 16 - 5},-114 L${side * 15 - 4},-132 L${side * 15 + 4},-132 L${side * 16 + 5},-114 Z` }, this.head);
      el('circle', { cx: side * 15, cy: -134, r: 7, class: 'plush-patch plush-fuzzy' }, this.head);
    }

    // Ears out to the sides, like leaves.
    const ear = (side: number) => {
      const g = el('g', {}, this.head);
      el('ellipse', { cx: side * 14, cy: 0, rx: 18, ry: 8, class: 'plush-wool plush-fuzzy plush-outlined' }, g);
      el('ellipse', { cx: side * 15, cy: 1, rx: 11, ry: 4, class: 'plush-ear-inner' }, g);
      return g;
    };
    this.ears = [ear(-1), ear(1)];

    // The head: long, with a pale muzzle and a couple of patches.
    el('path', { class: 'plush-wool plush-fuzzy plush-outlined', d: 'M-36,-82 C-38,-112 -20,-124 0,-124 C20,-124 38,-112 36,-82 C36,-62 34,-44 30,-34 C24,-24 -24,-24 -30,-34 C-34,-44 -36,-62 -36,-82 Z' }, this.head);
    el('path', { class: 'plush-patch plush-fuzzy', d: patch(-22, -108, 6, 2) }, this.head);
    el('path', { class: 'plush-patch plush-fuzzy', d: patch(24, -100, 5, 5) }, this.head);
    el('path', { class: 'plush-patch plush-fuzzy', d: 'M-6,-124 C-6,-132 6,-132 6,-124 C4,-118 -4,-118 -6,-124 Z' }, this.head);

    this.faceParts = buildFace(this.head, GIRAFFE_LAYOUT, id, {
      beforeMouth: (head) => {
        el('ellipse', { cx: 0, cy: -46, rx: 31, ry: 22, class: 'plush-skin plush-fuzzy' }, head);
        for (const side of [-1, 1]) el('ellipse', { cx: side * 10, cy: -52, rx: 3.4, ry: 2.2, class: 'plush-patch' }, head);
      },
    });

    this.accessories = buildAccessories({
      bow: { parent: this.ears[0], transform: 'translate(-16 -6) rotate(-10) scale(0.7)' },
      glasses: { parent: this.head, layout: GIRAFFE_LAYOUT },
      flower: { parent: this.head, x: 26, y: -116 },
      scarf: { parent: this.head, y: -18, halfWidth: 30 },
    });

    this.overlay = buildOverlay(root, -126);

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

    // Ears out to the sides: lifting with interest, drooping when sad, back when cross.
    const lift = face.earLift;
    this.ears.forEach((e, i) => {
      const side = i === 0 ? -1 : 1;
      const angle = side * (-lift * (lift > 0 ? 18 : 30) + pose.earWiggle * (i === 0 ? 1 : 0.6));
      e.setAttribute('transform', `translate(${side * EAR_X} ${EAR_Y}) rotate(${f(-angle)})`);
    });

    // The tail flicks when happy and hangs when sad.
    const happy = pose.mood.joy + (pose.gesture?.name === 'excited' ? 1 : 0);
    const flick = Math.sin(pose.t * Math.PI * 2 * 1.8) * 9 * Math.min(1, happy) * pose.motion;
    this.tail.setAttribute('transform', `translate(0 ${f(pose.dy * 0.6)}) rotate(${f(flick + pose.mood.sad * 14)} ${HIP.x} ${HIP.y})`);

    drawFace(this.faceParts, face, pose);
    drawOverlay(this.overlay, pose);
  }
}

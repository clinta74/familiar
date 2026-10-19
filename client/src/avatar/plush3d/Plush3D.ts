import * as THREE from 'three';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { fromOklch, llamaPalette, mix, toOklch } from '../appearance';
import type { FaceParams } from '../faceExpressions';
import { PlushCharacter, type PlushLook, type Pose } from '../plush/engine';
import type { FaceLayout } from '../plush/parts';

// The plush characters in 3D (PLAN.md §5.0): what they share. Each is the 2D character modelled -
// the same PlushCharacter life (every frame's face and movement), drawn with three.js as a soft toy
// sitting on the stage in shell fur (layers of strands over each furry shape), with button or
// googly eyes whose lids blink and squint, thread brows, and the mouth and cheeks embroidered on as
// textures painted from the 2D drawing. Laid out in the 2D drawing's units (its viewBox, y down)
// scaled by S, so a character's numbers carry over from its flat version.

export const S = 0.01;
/** A point in the 2D drawing's units: x right, y down, z towards the viewer. */
export const v = (x: number, y: number, z = 0) => new THREE.Vector3(x * S, -y * S, z * S);
/** Where they sit: the 2D drawings' GROUND_Y. */
export const GROUND = -2.22;

const FOV = 22;
/** How far the thought bubbles may rise above the top of the character. */
const BUBBLE_HEADROOM = 0.3;
/** Texture size for the embroidered face and snout. Front-projected, so this is a front view. */
const TEX = 256;
/** Layers of fur over each furry shape: more is softer and costs more. */
const SHELLS = 12;
/** A googly eye's size, from the 2D eye radius: big, as they should be. */
const GOOGLY = 1.1;

export type Side = -1 | 1;
/** Which of the five colours a part takes: coat (wool), face, or ear (inner ears and paws). */
export type Coat = 'wool' | 'face' | 'ear';
/**
 * Fur's colour: one of the coats; the face's painted texture (so a mask shows through its fur); or
 * the coat's pattern (a giraffe's patches), painted in the coat and ear colours.
 */
type FurColour = Coat | 'painted' | 'pattern';

/**
 * How much of a pose's bob is the whole body's: its breathing and its hops. Nods, sighs and the
 * rest of the head's movement are the head's alone (the head gets pose.dy less this).
 */
export function bodyBob(pose: Pose): number {
  return pose.breath * 1.5 - pose.hop * (pose.gesture?.name === 'excited' ? 16 : 5);
}

// --- geometry ----------------------------------------------------------------------------------

/** A unit sphere whose UVs are a projection from the front, so a texture on it reads like a picture of its face. */
function frontSphere(widthSegments = 48, heightSegments = 32): THREE.SphereGeometry {
  const geometry = new THREE.SphereGeometry(1, widthSegments, heightSegments);
  const position = geometry.getAttribute('position');
  const uv = geometry.getAttribute('uv');
  for (let i = 0; i < position.count; i++) uv.setXY(i, position.getX(i) * 0.5 + 0.5, position.getY(i) * 0.5 + 0.5);
  uv.needsUpdate = true;
  return geometry;
}

/** An ellipsoid with its size baked in, so fur grows the same length all over it. */
export function ellipsoid(rx: number, ry: number, rz: number, at: THREE.Vector3, rotation?: THREE.Euler): THREE.BufferGeometry {
  const geometry = new THREE.SphereGeometry(1, 36, 24);
  geometry.scale(rx, ry, rz);
  if (rotation) geometry.applyMatrix4(new THREE.Matrix4().makeRotationFromEuler(rotation));
  geometry.translate(at.x, at.y, at.z);
  return geometry;
}

/** A limb: a capsule hanging down from the origin. */
export function limb(radius: number, length: number): THREE.BufferGeometry {
  const geometry = new THREE.CapsuleGeometry(radius, length, 8, 20);
  geometry.translate(0, -length / 2, 0);
  return geometry;
}

/**
 * An ear: an ellipsoid of these radii that narrows towards the tip (`taper` 0 not at all, 1 to a
 * point) and curves forward there by `curl`, centred on the origin.
 */
export function earShape(rx: number, ry: number, rz: number, taper = 0.55, curl = 0.11): THREE.BufferGeometry {
  const geometry = new THREE.SphereGeometry(1, 32, 24);
  const position = geometry.getAttribute('position');
  for (let i = 0; i < position.count; i++) {
    const x = position.getX(i);
    const y = position.getY(i);
    const z = position.getZ(i);
    const t = (y + 1) / 2;
    const narrow = 1 - taper * t ** 1.6;
    position.setXYZ(i, x * rx * narrow, y * ry, z * rz * (1 - 0.35 * t) + curl * t * t);
  }
  geometry.computeVertexNormals();
  return geometry;
}

/** Spheres placed along a curve, growing and shrinking, merged: a tail, a horn, a spine. */
export function sweep(points: THREE.Vector3[], radii: number[], count: number): THREE.BufferGeometry {
  const curve = new THREE.CatmullRomCurve3(points);
  const parts: THREE.BufferGeometry[] = [];
  for (let i = 0; i < count; i++) {
    const u = i / (count - 1);
    const scaled = u * (radii.length - 1);
    const k = Math.min(radii.length - 2, Math.floor(scaled));
    const r = radii[k]! + (radii[k + 1]! - radii[k]!) * (scaled - k);
    parts.push(ellipsoid(r, r, r, curve.getPoint(u)));
  }
  return mergeGeometries(parts)!;
}

/** A seeded random, so the fur and the lumps are the same every time. */
export function seeded(seed: number): () => number {
  let s = seed;
  return () => {
    s = (s * 16807) % 2147483647;
    return (s - 1) / 2147483646;
  };
}

/**
 * Pushes a shape's surface in and out a little, smoothly, so its outline is lumpy fleece rather
 * than a perfect curve. Deterministic: the same lumps every time.
 */
export function lumpy(geometry: THREE.BufferGeometry, amount: number, seed: number): THREE.BufferGeometry {
  geometry.computeVertexNormals();
  const position = geometry.getAttribute('position');
  const normal = geometry.getAttribute('normal');
  const random = seeded(seed);
  const waves = Array.from({ length: 5 }, () => [random() * 9 + 5, random() * 9 + 5, random() * 9 + 5, random() * 6.28] as const);
  for (let i = 0; i < position.count; i++) {
    const x = position.getX(i);
    const y = position.getY(i);
    const z = position.getZ(i);
    let d = 0;
    for (const [a, b, c, phase] of waves) d += Math.sin(x * a + y * b + z * c + phase);
    d = (d / waves.length) * amount;
    position.setXYZ(i, x + normal.getX(i) * d, y + normal.getY(i) * d, z + normal.getZ(i) * d);
  }
  geometry.computeVertexNormals();
  return geometry;
}

// --- textures ----------------------------------------------------------------------------------

/** Fine noise for the fabric's bump: felt, not plastic. */
function fuzzTexture(): THREE.CanvasTexture {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 128;
  const ctx = canvas.getContext('2d')!;
  const image = ctx.createImageData(128, 128);
  const random = seeded(7);
  for (let i = 0; i < image.data.length; i += 4) {
    const n = 110 + random() * 145;
    image.data[i] = image.data[i + 1] = image.data[i + 2] = n;
    image.data[i + 3] = 255;
  }
  ctx.putImageData(image, 0, 0);
  const texture = new THREE.CanvasTexture(canvas);
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.repeat.set(3, 3);
  return texture;
}

/**
 * Fur strands for the shells' alpha: fine strands bundled into tufts (each cell of the grid has a
 * clump value the strands in it share), so the fur parts and clusters as a plush's pile does.
 */
function strandTexture(): THREE.CanvasTexture {
  const size = 128;
  const cell = 4;
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext('2d')!;
  const image = ctx.createImageData(size, size);
  const random = seeded(19);
  const clumps = Array.from({ length: (size / cell) ** 2 }, () => random());
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const clump = clumps[Math.floor(y / cell) * (size / cell) + Math.floor(x / cell)]!;
      const n = Math.min(1, clump * 0.65 + random() * 0.45) * 255;
      const i = (y * size + x) * 4;
      image.data[i] = image.data[i + 1] = image.data[i + 2] = n;
      image.data[i + 3] = 255;
    }
  }
  ctx.putImageData(image, 0, 0);
  const texture = new THREE.CanvasTexture(canvas);
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.repeat.set(7, 7);
  texture.magFilter = THREE.NearestFilter;
  return texture;
}

/** Something to paint a front-projected texture with, in the 2D drawing's units. */
export interface Painter {
  ctx: CanvasRenderingContext2D;
  /** Canvas x and y for a point in the 2D drawing. */
  x(svgX: number): number;
  y(svgY: number): number;
  /** Canvas pixels per unit of the 2D drawing. */
  unit: number;
}

/** A shape in 3D, and the same oval in the 2D drawing (centre and radii), which its texture shows. */
export interface Shape {
  centre: THREE.Vector3;
  radii: THREE.Vector3;
  svg: [cx: number, cy: number, rx: number, ry: number];
  /** Narrowing towards the front, 0 not at all to 1 to a point: a fox's muzzle. */
  taper?: number;
}

/**
 * A pointed tuft of fur, `length` long from a base `radius` wide at the origin, pointing along +y
 * and curving a little at the tip.
 */
export function tuftShape(length: number, radius: number, curl = 0.02): THREE.BufferGeometry {
  const geometry = earShape(radius, length / 2, radius * 0.6, 0.92, curl);
  geometry.translate(0, length / 2, 0);
  return geometry;
}

class Paint {
  readonly canvas = document.createElement('canvas');
  readonly ctx: CanvasRenderingContext2D;
  readonly texture: THREE.CanvasTexture;

  readonly shape: Shape;

  constructor(shape: Shape) {
    this.shape = shape;
    this.canvas.width = this.canvas.height = TEX;
    this.ctx = this.canvas.getContext('2d')!;
    this.texture = new THREE.CanvasTexture(this.canvas);
    this.texture.colorSpace = THREE.SRGBColorSpace;
    this.texture.anisotropy = 4;
  }

  painter(): Painter {
    const [cx, cy, rx, ry] = this.shape.svg;
    return {
      ctx: this.ctx,
      x: (x) => (((x - cx) / rx) * 0.5 + 0.5) * TEX,
      y: (y) => (0.5 + ((y - cy) / ry) * 0.5) * TEX,
      unit: TEX / (2 * rx),
    };
  }
}

// --- the character ------------------------------------------------------------------------------

/** The face: how the 2D face is laid out, and the shapes it's made on in 3D. */
export interface FaceSpec {
  layout: FaceLayout;
  /** The face the eyes sit on, painted (its colour and the cheeks; and the mouth, without a snout). */
  dome: Shape;
  /** A muzzle in front, painted with the mouth. */
  snout?: Shape;
  nose?: { at: THREE.Vector3; radii: THREE.Vector3; tilt: number };
  /** The stitch from the nose down to the mouth, as 2D y from and to. */
  philtrum?: [number, number];
  /** Two little fangs in the mouth (the monster's). */
  teeth?: boolean;
  /** Paints the dome under the cheeks; by default it's all the face colour. */
  paintDome?: (paint: Painter, palette: Record<string, string>) => void;
  /** How much lower than the 2D brows to put them, out from under a fringe. */
  browDrop?: number;
  /** The lids' colour: the face's, or (googly eyes in fur) the coat's. */
  lids?: Coat;
  /** Short fur over the face, coloured by its paint (a fox's mask), this long; none by default. */
  fur?: number;
  /**
   * Where the layout's centre is on this head, if not at (0, 0): the eyes, brows, tear and glasses
   * are placed from the layout's numbers about it. The hedgehog on all fours has its face low and
   * forward, below its prickles.
   */
  layoutAt?: [x: number, y: number];
  /** A big shiny black button for a nose, rather than a stitched felt one. */
  buttonNose?: boolean;
  /**
   * The face's own surface, from a model (in head coordinates), instead of the dome: painted through
   * the dome's projection (which still says where the 2D drawing's face is), and what the eyes,
   * brows and tear are placed on.
   */
  surface?: THREE.BufferGeometry;
}

/** The sitting body, from the 2D character's BodySpec numbers (its units), plus shapes in 3D. */
export interface BodySpec {
  /** Furry torso shapes, in 3D. */
  torso: THREE.BufferGeometry[];
  /** Shapes over the torso in the face colour's fur: a bib, a belly. */
  belly?: THREE.BufferGeometry[];
  /** Fur length on the torso. */
  fur: number;
  neckPivotY: number;
  shoulder: [x: number, y: number];
  arm: { length: number; width: number; kind: 'hoof' | 'paw' | 'claw' };
  foot: [x: number, y: number];
  feet: { rx: number; ry: number; kind: 'hoof' | 'paw' | 'claw'; splay: number };
  /** How far in front of the body the arms hang and the feet rest. */
  armZ?: number;
  footZ?: number;
}

/** What the camera keeps in view, in 3D. */
export interface Frame {
  cx: number;
  cy: number;
  halfWidth: number;
  halfHeight: number;
}

/** Where each accessory goes on this character. */
export interface Anchors {
  /** The bow, on an ear (usually), at this point on it. */
  bow: { parent: THREE.Object3D; at: THREE.Vector3; tilt?: number };
  flower: THREE.Vector3;
  /**
   * The scarf: the wrap's height and radius under the chin, and where its tail hangs. Round a body
   * too big to wrap (the hedgehog), only `arc` of it shows, centred at `z`, `depth` as deep as wide.
   */
  scarf: { y: number; radius: number; tail: THREE.Vector3; arc?: number; z?: number; depth?: number };
}

interface Eye {
  group: THREE.Group;
  /** The button (or googly eye), its shine and its lids: sinks away as the eye becomes a smile. */
  ball: THREE.Group;
  upper: THREE.Group;
  lower: THREE.Mesh;
  /** A stitched arch, for a big smile: ^ ^. */
  arc: THREE.Mesh;
  /** A googly eye's pupil, which looks about. */
  pupil?: THREE.Mesh;
}

/** One layer of fur: how far out it is (0-1), its coat, and the material drawing it. */
interface Shell {
  f: number;
  coat: FurColour;
  material: THREE.MeshStandardMaterial;
}

/** A live multiplier on a furry shape's fur length: prickles that bristle and lie flat. */
export type Bristle = { value: number };

/** How a furry shape's fur grows, beyond its length and colour. */
export interface FurOptions {
  bristle?: Bristle;
  /**
   * The geometry has a `furLength` attribute, 0-1 per vertex, scaling the fur there: none over the
   * parts of a big shape that something else sits on (the hedgehog's face and tummy).
   */
  varying?: boolean;
  /** Strands per unit of the texture, for a shape much bigger than most: 7 is usual. */
  density?: number;
  /**
   * Coloured by the coat's pattern (patches in the ear colour), not plainly. A number is how many
   * times the pattern repeats across the shape's UVs: under 1 for a part small for its UV space (a
   * leg), so its patches come out as big as everyone else's.
   */
  pattern?: boolean | number;
}

/** How often a shape's coat pattern repeats (FurOptions.pattern), or 0 for a plain coat. */
function patternRepeat(options?: FurOptions): number {
  if (!options?.pattern) return 0;
  return options.pattern === true ? 1 : options.pattern;
}

/**
 * How big a shape is for its UV space: the square root of its surface area over the area its UVs
 * cover. Parts unwrapped separately each fill their own UV square, so a small part gets small
 * texture features unless its texture repeats less (FurOptions.pattern).
 */
export function uvScale(geometry: THREE.BufferGeometry): number {
  const position = geometry.getAttribute('position');
  const uv = geometry.getAttribute('uv');
  if (!uv) return 1;
  const index = geometry.getIndex();
  const count = index ? index.count : position.count;
  const at = (i: number) => (index ? index.getX(i) : i);
  const a = new THREE.Vector3();
  const b = new THREE.Vector3();
  const c = new THREE.Vector3();
  let world = 0;
  let flat = 0;
  for (let i = 0; i + 2 < count; i += 3) {
    const i0 = at(i);
    const i1 = at(i + 1);
    const i2 = at(i + 2);
    a.fromBufferAttribute(position, i0);
    b.fromBufferAttribute(position, i1).sub(a);
    c.fromBufferAttribute(position, i2).sub(a);
    world += b.cross(c).length() / 2;
    const du1 = uv.getX(i1) - uv.getX(i0);
    const dv1 = uv.getY(i1) - uv.getY(i0);
    const du2 = uv.getX(i2) - uv.getX(i0);
    const dv2 = uv.getY(i2) - uv.getY(i0);
    flat += Math.abs(du1 * dv2 - du2 * dv1) / 2;
  }
  return flat > 0 ? Math.sqrt(world / flat) : 1;
}

/**
 * Sets a shape's `furLength` attribute from `length(position, normal)`, 0-1 per vertex, for
 * FurOptions.varying.
 */
export function furLength(geometry: THREE.BufferGeometry, length: (position: THREE.Vector3, normal: THREE.Vector3) => number): THREE.BufferGeometry {
  geometry.computeVertexNormals();
  const position = geometry.getAttribute('position');
  const normal = geometry.getAttribute('normal');
  const values = new Float32Array(position.count);
  const p = new THREE.Vector3();
  const n = new THREE.Vector3();
  for (let i = 0; i < position.count; i++) {
    p.fromBufferAttribute(position, i);
    n.fromBufferAttribute(normal, i);
    values[i] = Math.min(1, Math.max(0, length(p, n)));
  }
  geometry.setAttribute('furLength', new THREE.BufferAttribute(values, 1));
  return geometry;
}

export abstract class Plush3D extends PlushCharacter {
  protected readonly root: HTMLCanvasElement;
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(FOV, 1, 0.1, 50);
  private readonly resize: ResizeObserver;
  private readonly container: HTMLElement;
  /** What the camera fits into the stage: measured once the character is built. */
  private frameSpec: Frame = { cx: 0, cy: 0, halfWidth: 1, halfHeight: 1 };

  private readonly fuzz = fuzzTexture();
  private readonly strands = strandTexture();
  private readonly furBase: Record<Coat, THREE.MeshPhysicalMaterial>;
  private readonly shells = new Map<string, Shell>();
  protected readonly skin: THREE.MeshPhysicalMaterial;
  private readonly snoutMaterial: THREE.MeshPhysicalMaterial;
  private readonly lidMaterial: THREE.MeshPhysicalMaterial;
  protected readonly earInner: THREE.MeshPhysicalMaterial;
  protected readonly nose: THREE.MeshPhysicalMaterial;
  protected readonly thread: THREE.MeshStandardMaterial;
  protected readonly hoof: THREE.MeshPhysicalMaterial;
  protected readonly claw: THREE.MeshPhysicalMaterial;
  /** A shiny black button: a hedgehog's or a fox's nose. */
  protected readonly button = new THREE.MeshPhysicalMaterial({ color: '#181210', roughness: 0.18, clearcoat: 1, clearcoatRoughness: 0.05 });
  protected readonly accent: THREE.MeshPhysicalMaterial;
  private readonly bubble: THREE.MeshStandardMaterial;

  /** Everything but the shadow, pivoting where it sits so it can breathe, slump and lean. */
  private readonly toy = new THREE.Group();
  /** The body's parts that don't move on their own. */
  protected readonly body = new THREE.Group();
  private readonly bob = new THREE.Group();
  /** Where the head pivots on the neck (the hedgehog's ball, on its bottom). */
  protected readonly pivot = new THREE.Group();
  /** The head: moves on the neck. */
  protected readonly head = new THREE.Group();
  private readonly floorShadow: THREE.Mesh;
  private readonly thinking = new THREE.Group();
  private readonly sleep: THREE.Sprite;

  private faceSpec?: FaceSpec;
  /** The face surface, unmoved, for finding points on it (FaceSpec.surface). */
  private faceProbe?: THREE.Mesh;
  private readonly ray = new THREE.Raycaster();
  private domePaint?: Paint;
  private mouthPaint?: Paint;
  private eyes: Eye[] = [];
  private brows: THREE.Mesh[] = [];
  private tear?: THREE.Mesh;
  /**
   * How the arms rest: leaning forward from the shoulder (radians), bent forward at the elbow
   * (radians), and out from the body (degrees). Paws on the tummy by default; standing front legs
   * (ModelSpec.arms) rest nearly straight.
   */
  /** `wave`: the elbow's turn (degrees) with the paw up, for an arm that doesn't hang straight down. */
  protected armRest: { lean: number; bend: number; out: number; wave?: number } = { lean: 0.3, bend: 0.55, out: -6 };

  /** Each arm: the shoulder (the upper arm swings from it) and the elbow (the forearm bends at it). */
  private arms: { shoulder: THREE.Group; elbow: THREE.Group }[] = [];
  private feet: THREE.Group[] = [];
  private accessories: Partial<Record<'bow' | 'scarf' | 'glasses' | 'flower', THREE.Object3D>> = {};

  protected palette: Record<string, string> = {};
  /** How far it's turned round (the studio lets you look at it from the side). */
  private turn = 0;

  setTurn(radians: number): void {
    this.turn = radians;
  }
  private painted = { blush: -1, mouth: '' };

  protected constructor(container: HTMLElement, label: string, neckPivotY: number, bubbles: [number, number] = [60, -84]) {
    super();
    this.container = container;

    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(2, window.devicePixelRatio));
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.renderer.toneMapping = THREE.NeutralToneMapping;
    this.root = this.renderer.domElement;
    this.root.className = 'plush-3d';
    this.root.setAttribute('role', 'img');
    this.root.setAttribute('aria-label', label);

    // Soft studio light for the fur and the glossy eyes, a warm key from the upper left and a cool
    // rim behind to lift the fur off the background.
    const environment = new THREE.PMREMGenerator(this.renderer);
    this.scene.environment = environment.fromScene(new RoomEnvironment(), 0.04).texture;
    this.scene.environmentIntensity = 0.55;
    environment.dispose();
    const key = new THREE.DirectionalLight(0xfff2e2, 2.1);
    key.position.set(-2.5, 3, 4);
    const rim = new THREE.DirectionalLight(0xdfe8ff, 1.6);
    rim.position.set(2, 2.5, -4);
    this.scene.add(key, rim, new THREE.HemisphereLight(0xfffaf3, 0x8c7c70, 0.7));

    const fabric = { roughness: 1, sheen: 1, sheenRoughness: 0.45, bumpMap: this.fuzz, bumpScale: 1.2 };
    this.furBase = {
      wool: new THREE.MeshPhysicalMaterial(fabric),
      face: new THREE.MeshPhysicalMaterial(fabric),
      ear: new THREE.MeshPhysicalMaterial(fabric),
    };
    this.skin = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.6, bumpScale: 0.5 });
    this.snoutMaterial = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.6, bumpScale: 0.4 });
    this.lidMaterial = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.6, bumpScale: 0.5 });
    this.earInner = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.7, bumpScale: 0.5 });
    this.nose = new THREE.MeshPhysicalMaterial({ roughness: 0.45, clearcoat: 0.4, clearcoatRoughness: 0.3 });
    this.thread = new THREE.MeshStandardMaterial({ roughness: 0.85 });
    this.hoof = new THREE.MeshPhysicalMaterial({ roughness: 0.6, clearcoat: 0.2 });
    this.claw = new THREE.MeshPhysicalMaterial({ color: '#fffdf8', roughness: 0.5, clearcoat: 0.3 });
    this.accent = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.5, bumpScale: 0.6 });
    this.bubble = new THREE.MeshStandardMaterial({ roughness: 0.4, transparent: true });

    this.floorShadow = this.shadow();
    this.scene.add(this.floorShadow, this.toy);
    this.toy.position.y = GROUND;
    const seated = new THREE.Group();
    seated.position.y = -GROUND;
    seated.add(this.body, this.bob);
    this.toy.add(seated);
    this.bob.add(this.pivot);
    this.pivot.position.y = -neckPivotY * S;
    this.pivot.add(this.head);
    this.head.position.y = neckPivotY * S;

    this.buildThinking(bubbles);
    this.sleep = this.buildSleep(bubbles);

    container.appendChild(this.root);
    this.resize = new ResizeObserver(() => this.fit());
    this.resize.observe(container);
  }

  /** Call last in the subclass's constructor, once everything is built. */
  protected finish(look: PlushLook | undefined, defaults: PlushLook) {
    this.frameSpec = this.measure();
    this.fit();
    this.setLook(look ?? defaults);
    // Its shaders compiled in the background first (parallel where the browser can), then the
    // first frame: compiled by that frame instead, they held the page up while the placeholder
    // glow should have been showing.
    this.ready = this.renderer.compileAsync(this.scene, this.camera).catch(() => undefined).then(() => this.start());
  }

  /** Built and its shaders compiled: ready to show without a stall. */
  ready: Promise<void> = Promise.resolve();

  /** Recolours and redresses it in place, for the studio's live preview. */
  setLook(look: PlushLook): void {
    const p = llamaPalette(look.colours);
    // The coat as its fur reads, lit and lightened at the tips, and a little greyer (fur is shaded
    // towards a dusky brown at the roots): for painting a face in the coat's colour so it matches
    // the fur round it rather than reading as a brighter mask.
    const [l, c, h] = toOklch(look.colours.wool);
    p['--plush-coat-face'] = mix(fromOklch(l, c * 0.7, h), '#ffffff', 0.16);
    this.palette = p;
    const white = new THREE.Color('#ffffff');

    // Fur is darker at the roots and lighter at the tips, which is most of what makes it look deep.
    const shades = {} as Record<Coat, { root: THREE.Color; tip: THREE.Color }>;
    for (const [coat, colour] of [['wool', look.colours.wool], ['face', look.colours.face], ['ear', look.colours.ear]] as const) {
      const shade = this.furShade(coat, colour);
      shades[coat] = shade;
      this.furBase[coat].color.copy(shade.root);
      // A soft sheen in the coat's own colour: a whiter one washes a dark coat out.
      this.furBase[coat].sheenColor.set(colour).lerp(white, 0.18);
    }
    for (const shell of this.shells.values()) {
      if (shell.coat === 'painted' || shell.coat === 'pattern') {
        // The paint gives the colour; shaded root to tip as the coats are (roots ~36% darker, tips a
        // little lighter), so painted fur matches the fur round it.
        shell.material.color.setScalar(0.66 + 0.44 * shell.f ** 0.7);
        continue;
      }
      const { root, tip } = shades[shell.coat];
      shell.material.color.copy(root).lerp(tip, 0.25 + 0.75 * shell.f ** 0.7);
    }

    const lids = this.faceSpec?.lids === 'wool' ? look.colours.wool : p['--plush-skin']!;
    this.lidMaterial.color.set(lids);
    this.lidMaterial.sheenColor.set(lids).lerp(white, 0.4);
    // A face painted in the coat shines in the coat's colour, as the fur round it does; a cream
    // sheen would make it glow lighter, like a mask.
    const coatFace = this.faceSpec?.lids === 'wool';
    this.skin.sheenColor.set(coatFace ? look.colours.wool : p['--plush-skin']!).lerp(white, coatFace ? 0.18 : 0.4);
    this.snoutMaterial.sheenColor.set(p['--plush-skin']!).lerp(white, 0.4);
    this.earInner.color.set(p['--plush-ear']!);
    this.earInner.sheenColor.set(p['--plush-ear']!).lerp(white, 0.3);
    this.nose.color.set(p['--plush-nose']!);
    this.thread.color.set(p['--plush-thread']!);
    this.hoof.color.set(p['--plush-hoof']!);
    this.accent.color.set(p['--plush-accent']!);
    this.accent.sheenColor.set(p['--plush-accent']!).lerp(white, 0.4);
    this.bubble.color.set(p['--plush-accent']!);

    for (const [name, object] of Object.entries(this.accessories)) object.visible = name === look.accessory;
    if (this.pattern) this.paintPattern(look.colours.wool, look.colours.ear);

    // Repaint the face in the new colours.
    this.painted = { blush: -1, mouth: '' };
  }

  /**
   * The coat's pattern: one painting of patches, and for each repeat a texture of it and the
   * material for the skin under the fur.
   */
  private pattern?: { canvas: HTMLCanvasElement; sheets: Map<number, { texture: THREE.CanvasTexture; base: THREE.MeshPhysicalMaterial }> };

  private coatPattern(repeat = 1) {
    if (!this.pattern) {
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = 512;
      this.pattern = { canvas, sheets: new Map() };
    }
    let sheet = this.pattern.sheets.get(repeat);
    if (!sheet) {
      const texture = new THREE.CanvasTexture(this.pattern.canvas);
      texture.colorSpace = THREE.SRGBColorSpace;
      texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
      texture.repeat.set(repeat, repeat);
      const base = new THREE.MeshPhysicalMaterial({ roughness: 1, sheen: 1, sheenRoughness: 0.45, bumpMap: this.fuzz, bumpScale: 1.2, map: texture });
      base.color.setScalar(0.66);
      sheet = { texture, base };
      this.pattern.sheets.set(repeat, sheet);
    }
    return sheet;
  }

  /**
   * Paints the coat's pattern: patches in the ear colour with the coat between them, irregular
   * rounded shapes on a jittered grid, the same every time. In each shape's UV space, so a patch can
   * break at a seam - which giraffe patches do anyway.
   */
  private paintPattern(coat: string, patches: string) {
    this.coatPattern();
    const { canvas, sheets } = this.pattern!;
    const ctx = canvas.getContext('2d')!;
    ctx.fillStyle = coat;
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = patches;
    const random = seeded(61);
    const cells = 11;
    const size = canvas.width / cells;
    for (let row = 0; row < cells; row++) {
      for (let column = 0; column < cells; column++) {
        const cx = (column + 0.5 + (random() - 0.5) * 0.35) * size;
        const cy = (row + 0.5 + (random() - 0.5) * 0.35) * size;
        const corners = 6;
        ctx.beginPath();
        for (let i = 0; i <= corners; i++) {
          const a = ((i % corners) / corners) * Math.PI * 2 + random() * 0.3;
          const k = (0.32 + random() * 0.1) * size;
          const x = cx + Math.cos(a) * k;
          const y = cy + Math.sin(a) * k;
          if (i === 0) ctx.moveTo(x, y);
          else ctx.quadraticCurveTo(cx + Math.cos(a - 0.5) * k * 1.15, cy + Math.sin(a - 0.5) * k * 1.15, x, y);
        }
        ctx.fill();
      }
    }
    for (const { texture } of sheets.values()) texture.needsUpdate = true;
  }

  /**
   * A coat's fur colour at its roots and its tips: darker at the roots and lighter at the tips,
   * which is most of what makes fur look deep. A character can change it (dark-tipped prickles).
   */
  protected furShade(_coat: Coat, colour: string): { root: THREE.Color; tip: THREE.Color } {
    return { root: new THREE.Color(mix(colour, '#3a2a22', 0.36)), tip: new THREE.Color(colour).lerp(new THREE.Color('#ffffff'), 0.14) };
  }

  override dispose(): void {
    super.dispose();
    this.resize.disconnect();
    const materials = new Set<THREE.Material>();
    this.scene.traverse((object) => {
      if (object instanceof THREE.Mesh || object instanceof THREE.Sprite) {
        object.geometry.dispose();
        for (const material of Array.isArray(object.material) ? object.material : [object.material]) materials.add(material);
      }
    });
    for (const material of materials) {
      material.dispose();
      // Denser strands are clones of the shared texture.
      if (material instanceof THREE.MeshStandardMaterial && material.alphaMap && material.alphaMap !== this.strands) material.alphaMap.dispose();
    }
    for (const texture of [this.fuzz, this.strands, this.domePaint?.texture, this.mouthPaint?.texture, this.sleep.material.map]) texture?.dispose();
    for (const { texture } of this.pattern?.sheets.values() ?? []) texture.dispose();
    this.scene.environment?.dispose();
    this.renderer.dispose();
  }

  /** The character's own moving parts: ears, a tail, prickles. */
  protected abstract drawParts(pose: Pose): void;

  protected draw(pose: Pose): void {
    const { face } = pose;
    const deg = Math.PI / 180;

    // As the flat characters' bodies (drawBody): the toy breathes - the chest filling as the head
    // rises - slumps when sad and leans a little into a tilt, with its seat on the floor; the head
    // does the rest of the bob and tilt on the neck, and turns to look where the eyes would.
    // The body moves only with its breathing, a sad slump and hops; nods, shakes and tilts are the
    // head's alone.
    const sad = pose.mood.sad;
    const sx = 1 - pose.breath * 0.006 + sad * 0.03;
    const sy = 1 + pose.breath * 0.008 - sad * 0.05;
    const bodyDy = bodyBob(pose);
    this.toy.position.y = GROUND - bodyDy * S;
    this.toy.scale.set(sx, sy, sx);
    this.toy.rotation.y = this.turn;
    this.floorShadow.scale.setScalar(1 - pose.hop * 0.15);
    this.bob.position.y = -(pose.dy - bodyDy) * S;
    this.pivot.rotation.set(face.gazeY * 0.16, face.gazeX * 0.32, -pose.rotate * deg, 'YXZ');
    this.head.scale.set(1 - pose.squash, 1 + pose.squash, 1 - pose.squash * 0.5);

    // Arms, bent at the elbow: resting with the paws forward on the tummy; out with the forearms
    // turned up for a shrug; and the right one lifting out to the side, its forearm bending up at
    // the elbow, to wave from there. Feet kick up with each hop of joy.
    const shrug = pose.gesture?.name === 'shrug' ? Math.sin(Math.PI * pose.gesture.u) : 0;
    const paw = pose.pawLift ?? 0;
    const rest = this.armRest;
    this.arms.forEach(({ shoulder, elbow }, i) => {
      const side = i === 0 ? -1 : 1;
      const lift = side === 1 ? paw : 0;
      // Out from the body (degrees), mirrored: positive takes the paw away from the tummy.
      const out = rest.out + shrug * 32 + lift * 78 + pose.breath;
      shoulder.rotation.set(-rest.lean * (1 - lift) - shrug * 0.2, 0, side * out * deg);
      const bend = rest.bend + shrug * 0.6;
      elbow.rotation.set(-bend * (1 - lift), 0, side * (lift * ((rest.wave ?? 102) - pose.pawAngle)) * deg);
    });
    this.feet.forEach((foot, i) => {
      const side = i === 0 ? -1 : 1;
      foot.rotation.x = -pose.hop * 0.4;
      foot.rotation.z = side * pose.hop * 8 * deg;
    });

    if (this.faceSpec) this.drawFace(face, pose);
    this.drawParts(pose);

    // Thinking: three bubbles pulsing in turn. Resting or asleep: a drifting z.
    this.thinking.visible = pose.state === 'thinking';
    if (this.thinking.visible) {
      this.thinking.children.forEach((dot, i) => dot.scale.setScalar(0.85 + 0.25 * Math.max(0, Math.sin(pose.t * 4 - i * 0.9))));
      this.bubble.opacity = 0.9;
    }
    const resting = pose.state === 'resting' || pose.drowsy >= 1;
    this.sleep.visible = resting;
    if (resting) {
      this.sleep.material.opacity = 0.5 + 0.5 * Math.sin(pose.t * 2);
      this.sleep.position.y = this.sleep.userData.y + ((pose.t * 0.06) % 0.12);
    }

    this.renderer.render(this.scene, this.camera);
  }

  // --- fur -----------------------------------------------------------------------------------

  /**
   * One layer of fur `length` long at `f` (0 at the skin, 1 at the tips): the shape pushed out along
   * its normals, drooping a little, with fewer strands the further out it is so they taper.
   */
  private shell(coat: FurColour, f: number, length: number, options: FurOptions = {}): Shell {
    const { bristle, varying, density } = options;
    const repeat = patternRepeat(options);
    const variant = `${bristle ? 'b' : ''}${varying ? 'v' : ''}${density ?? ''}${repeat ? `p${repeat}` : ''}`;
    if (repeat) coat = 'pattern';
    const key = `${coat}:${f}:${length}:${variant}`;
    const existing = this.shells.get(key);
    if (existing) return existing;

    let strands = this.strands;
    if (density) {
      // The same strands, finer: a clone shares the image.
      strands = this.strands.clone();
      strands.repeat.set(density, density);
    }
    const material = new THREE.MeshStandardMaterial({ roughness: 1, alphaMap: strands, alphaTest: 0.25 + 0.7 * f });
    if (coat === 'painted') material.map = this.domePaint!.texture;
    if (coat === 'pattern') material.map = this.coatPattern(repeat).texture;
    // How far out this layer sits and how much it droops, as uniforms: written into the shader as
    // numbers, every layer of every fur length was its own program - 57 to 153 of them to compile
    // before a character's first frame (0.3-0.8 s on a desktop graphics card, more on a phone). As
    // uniforms, there's one program per kind of fur.
    const out = { value: length * f };
    const droop = { value: length * f * f * 0.45 };
    material.onBeforeCompile = (shader) => {
      shader.uniforms.furOut = out;
      shader.uniforms.furDroop = droop;
      let declarations = 'uniform float furOut;\nuniform float furDroop;\n';
      let scale = '';
      if (bristle) {
        shader.uniforms.furScale = bristle;
        declarations += 'uniform float furScale;\n';
        scale += ' * furScale';
      }
      if (varying) {
        declarations += 'attribute float furLength;\n';
        scale += ' * furLength';
      }
      shader.vertexShader = declarations + shader.vertexShader.replace(
        '#include <begin_vertex>',
        `#include <begin_vertex>\n  transformed += objectNormal * furOut${scale};\n  transformed.y -= furDroop${scale};`,
      );
    };
    material.customProgramCacheKey = () => `fur-${bristle ? 'b' : ''}${varying ? 'v' : ''}`;
    const shell = { f, coat, material };
    this.shells.set(key, shell);
    return shell;
  }

  /** A furry shape: the base in the coat's root colour and layers of fur over it. */
  protected furry(geometry: THREE.BufferGeometry, length: number, coat: Coat = 'wool', options?: FurOptions): THREE.Group {
    const group = new THREE.Group();
    const repeat = patternRepeat(options);
    group.add(new THREE.Mesh(geometry, repeat ? this.coatPattern(repeat).base : this.furBase[coat]));
    this.shellsOver(group, geometry, length, coat, options);
    return group;
  }

  /** The layers of fur over a shape. */
  private shellsOver(group: THREE.Object3D, geometry: THREE.BufferGeometry, length: number, coat: FurColour, options?: FurOptions) {
    for (let i = 1; i <= SHELLS; i++) group.add(new THREE.Mesh(geometry, this.shell(coat, i / SHELLS, length, options).material));
  }

  // --- building ------------------------------------------------------------------------------

  /**
   * The frame from the character itself, once it's built: all of it, the feet and the shadow under
   * them included, a little headroom, and the thought bubbles and the "z" - brought down to sit
   * beside the head if they'd stand far above it, since in a short, wide stage (the phone's chat)
   * headroom is what there's least of. Hand-set frames left the feet off there and a band of
   * empty space above.
   */
  private measure(): Frame {
    const body = new THREE.Box3().setFromObject(this.toy);
    const above = new THREE.Box3().setFromObject(this.thinking).max.y - (body.max.y + BUBBLE_HEADROOM);
    if (above > 0) {
      this.thinking.position.y -= above;
      this.sleep.position.y -= above;
      this.sleep.userData.y -= above;
    }
    const all = body.union(new THREE.Box3().setFromObject(this.thinking)).union(new THREE.Box3().setFromObject(this.sleep));
    // More below than above: the feet are nearest the camera, which looks down a little, so they
    // reach further down the picture than their height says; and the shadow is under them.
    const [top, bottom, side] = [0.15, 0.32, 0.15];
    return {
      cx: (all.min.x + all.max.x) / 2,
      cy: (all.min.y - bottom + all.max.y + top) / 2,
      halfWidth: (all.max.x - all.min.x) / 2 + side,
      halfHeight: (all.max.y + top - (all.min.y - bottom)) / 2,
    };
  }

  /** Fits the frame into the stage, whatever its shape. */
  private fit() {
    const width = Math.max(1, this.container.clientWidth);
    const height = Math.max(1, this.container.clientHeight);
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
    const { cx, cy, halfWidth, halfHeight } = this.frameSpec;
    const tan = Math.tan((FOV / 2) * (Math.PI / 180));
    const distance = Math.max(halfHeight / tan, halfWidth / (tan * this.camera.aspect));
    this.camera.position.set(cx, cy + 0.35, distance);
    this.camera.lookAt(cx, cy, 0);
    this.camera.updateProjectionMatrix();
  }

  /** A soft contact shadow where it sits. */
  private shadow(): THREE.Mesh {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 128;
    const ctx = canvas.getContext('2d')!;
    const g = ctx.createRadialGradient(64, 64, 0, 64, 64, 64);
    g.addColorStop(0, 'rgba(40,28,24,0.32)');
    g.addColorStop(1, 'rgba(40,28,24,0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, 128, 128);
    const shadow = new THREE.Mesh(
      new THREE.PlaneGeometry(2.4, 1.5),
      new THREE.MeshBasicMaterial({ map: new THREE.CanvasTexture(canvas), transparent: true, depthWrite: false }),
    );
    shadow.rotation.x = -Math.PI / 2;
    shadow.position.set(0, GROUND + 0.01, 0.05);
    return shadow;
  }

  /** The sitting body: the furry torso, an optional belly or bib, arms hanging in front, hind feet. */
  protected buildBody(spec: BodySpec) {
    this.body.add(this.furry(lumpy(mergeGeometries(spec.torso)!, 0.03, 21), spec.fur));
    if (spec.belly) this.body.add(this.furry(lumpy(mergeGeometries(spec.belly)!, 0.02, 31), spec.fur * 0.8, 'face'));

    const armZ = spec.armZ ?? 0.32;
    const footZ = spec.footZ ?? 0.3;
    const { length, width, kind } = spec.arm;
    const r = (width / 2) * S;
    const half = (length * S) / 2;
    for (const side of [-1, 1] as const) {
      // Upper arm and forearm, each a soft capsule; their rounded ends make the elbow.
      const shoulder = new THREE.Group();
      shoulder.position.copy(v(side * spec.shoulder[0], spec.shoulder[1], armZ * 100));
      shoulder.add(this.furry(lumpy(limb(r, half), 0.01, 23 + side), 0.07));
      const elbow = new THREE.Group();
      elbow.position.y = -half;
      elbow.add(this.furry(lumpy(limb(r * 0.94, half - r * 0.5), 0.01, 27 + side), 0.07));
      elbow.add(this.extremity(kind, r, -half, side));
      shoulder.add(elbow);
      this.body.add(shoulder);
      this.arms.push({ shoulder, elbow });
    }

    const { rx, ry, splay } = spec.feet;
    for (const side of [-1, 1] as const) {
      const foot = new THREE.Group();
      foot.position.set(side * spec.foot[0] * S, -spec.foot[1] * S, footZ);
      foot.rotation.y = side * splay * (Math.PI / 180);
      const pad = spec.feet.kind === 'hoof' ? 'wool' : 'ear';
      foot.add(this.furry(ellipsoid(rx * S * 0.8, ry * S, rx * S * 1.2, new THREE.Vector3()), 0.05, pad));
      if (spec.feet.kind === 'hoof') {
        const hoof = new THREE.Mesh(new THREE.SphereGeometry(1, 20, 14), this.hoof);
        hoof.scale.set(rx * S * 0.55, ry * S * 0.7, rx * S * 0.4);
        hoof.position.set(0, -ry * S * 0.15, rx * S * 1.05);
        foot.add(hoof);
      } else if (spec.feet.kind === 'claw') {
        foot.add(this.claws(rx * S * 0.6, new THREE.Vector3(0, 0, rx * S * 1.25), new THREE.Euler(Math.PI / 2, 0, 0)));
      }
      this.body.add(foot);
      this.feet.push(foot);
    }
  }

  /**
   * Arms and feet a model brought (rather than buildBody made): moved by the pose the same way. The
   * feet's toes turn out by `splay` degrees.
   */
  protected useLimbs(arms: { shoulder: THREE.Object3D; elbow: THREE.Object3D }[], feet: THREE.Object3D[], splay: number) {
    this.arms = arms as { shoulder: THREE.Group; elbow: THREE.Group }[];
    this.feet = feet as THREE.Group[];
    feet.forEach((foot, i) => (foot.rotation.y = (i === 0 ? -1 : 1) * splay * (Math.PI / 180)));
  }

  /** The materials a model's plain parts take, by the role it gives them. */
  protected materialFor(role: string): THREE.Material | undefined {
    switch (role) {
      case 'nose':
        return this.nose;
      case 'button':
        return this.button;
      case 'hoof':
        return this.hoof;
      case 'earInner':
      case 'horn':
        return this.earInner;
      case 'claw':
        return this.claw;
      case 'thread':
        return this.thread;
      case 'accent':
        return this.accent;
      default:
        return undefined;
    }
  }

  /** The end of an arm: a hoof, or a round paw (with claws, for the monster). */
  private extremity(kind: 'hoof' | 'paw' | 'claw', r: number, y: number, side: Side): THREE.Object3D {
    const group = new THREE.Group();
    group.position.y = y;
    if (kind === 'hoof') {
      const hoof = new THREE.Mesh(new THREE.CylinderGeometry(r * 0.9, r * 1.05, r * 0.8, 24), this.hoof);
      hoof.position.y = -r * 0.1;
      group.add(hoof);
      return group;
    }
    group.add(this.furry(ellipsoid(r * 1.3, r * 1.05, r * 1.15, new THREE.Vector3(0, r * 0.2, 0)), 0.04, 'ear'));
    if (kind === 'claw') group.add(this.claws(r * 0.7, new THREE.Vector3(side * 0.01, -r * 0.7, r * 0.6), new THREE.Euler(0.6, 0, 0)));
    return group;
  }

  /** Three little white claws in a row, pointing along the group's -y. */
  private claws(spread: number, at: THREE.Vector3, rotation: THREE.Euler): THREE.Group {
    const group = new THREE.Group();
    for (const k of [-1, 0, 1]) {
      const claw = new THREE.Mesh(new THREE.ConeGeometry(0.014, 0.06, 10), this.claw);
      claw.position.set(k * spread * 0.6, 0, 0);
      claw.rotation.z = Math.PI;
      group.add(claw);
    }
    group.position.copy(at);
    group.rotation.copy(rotation);
    return group;
  }

  /** An ear on a pivot at its base, furry outside with a soft inside. */
  protected ear(at: THREE.Vector3, outer: THREE.BufferGeometry, inner: THREE.BufferGeometry, height: number, fur = 0.05): THREE.Group {
    const ear = new THREE.Group();
    ear.position.copy(at);
    const fluff = this.furry(outer, fur);
    fluff.position.y = height;
    const lining = new THREE.Mesh(inner, this.earInner);
    lining.position.set(0, height * 0.97, 0.068);
    ear.add(fluff, lining);
    this.head.add(ear);
    return ear;
  }

  /** Swings ears with the face: perked towards upright, drooping outward, pinned back when cross or sad. */
  protected drawEars(ears: THREE.Group[], pose: Pose, rest: number, droop: number, turn = 0.3) {
    const lift = pose.face.earLift;
    const angle = lift >= 0 ? rest - lift * 10 : rest - lift * droop;
    ears.forEach((ear, i) => {
      const side = i === 0 ? -1 : 1;
      const a = side * (angle + pose.earWiggle * (i === 0 ? 1 : 0.6));
      ear.rotation.set(-0.35 * Math.max(0, -lift) + 0.08, side * -turn, -a * (Math.PI / 180), 'XYZ');
    });
  }

  /** The point on the face dome at (x, y) seen from the front, and the surface's normal there. */
  protected onFace(x: number, y: number): { point: THREE.Vector3; normal: THREE.Vector3 } {
    // The layout's numbers are about the head's centre; a face set elsewhere says where.
    const [dx, dy] = this.faceSpec?.layoutAt ?? [0, 0];
    x += dx;
    y += dy;
    if (this.faceProbe) {
      this.ray.set(new THREE.Vector3(x, y, 5), new THREE.Vector3(0, 0, -1));
      const hit = this.ray.intersectObject(this.faceProbe, false)[0];
      if (hit) return { point: hit.point.clone(), normal: (hit.normal ?? hit.face!.normal).clone().normalize() };
    }
    const { centre, radii } = this.faceSpec!.dome;
    const nx = (x - centre.x) / radii.x;
    const ny = (y - centre.y) / radii.y;
    const nz = Math.sqrt(Math.max(0, 1 - nx * nx - ny * ny));
    const point = new THREE.Vector3(x, y, centre.z + nz * radii.z);
    const normal = new THREE.Vector3(nx / radii.x, ny / radii.y, nz / radii.z).normalize();
    return { point, normal };
  }

  /** The face: the painted dome, a snout and nose, eyes, brows and a tear. */
  protected buildFace(spec: FaceSpec) {
    this.faceSpec = spec;
    this.domePaint = new Paint(spec.dome);
    this.skin.map = this.domePaint.texture;
    let domeShape: THREE.BufferGeometry;
    if (spec.surface) {
      // A model's face: painted through the dome's projection, as if it were the dome.
      domeShape = spec.surface;
      const { centre, radii } = spec.dome;
      const position = domeShape.getAttribute('position');
      const uv = new Float32Array(position.count * 2);
      for (let i = 0; i < position.count; i++) {
        uv[i * 2] = ((position.getX(i) - centre.x) / radii.x) * 0.5 + 0.5;
        uv[i * 2 + 1] = ((position.getY(i) - centre.y) / radii.y) * 0.5 + 0.5;
      }
      domeShape.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
      this.faceProbe = new THREE.Mesh(domeShape);
    } else {
      domeShape = frontSphere();
      domeShape.scale(spec.dome.radii.x, spec.dome.radii.y, spec.dome.radii.z);
      domeShape.translate(spec.dome.centre.x, spec.dome.centre.y, spec.dome.centre.z);
    }
    this.head.add(new THREE.Mesh(domeShape, this.skin));
    if (spec.fur) this.shellsOver(this.head, domeShape, spec.fur, 'painted');

    if (spec.snout) {
      this.mouthPaint = new Paint(spec.snout);
      this.snoutMaterial.map = this.mouthPaint.texture;
      const shape = frontSphere();
      if (spec.snout.taper) {
        // Narrowing towards the front; the paint stays where it was seen from the front.
        const position = shape.getAttribute('position');
        for (let i = 0; i < position.count; i++) {
          const k = 1 - spec.snout.taper * ((position.getZ(i) + 1) / 2) ** 1.5;
          position.setXY(i, position.getX(i) * k, position.getY(i) * k);
        }
        shape.computeVertexNormals();
      }
      const snout = new THREE.Mesh(shape, this.snoutMaterial);
      snout.position.copy(spec.snout.centre);
      snout.scale.copy(spec.snout.radii);
      this.head.add(snout);
    } else {
      this.mouthPaint = this.domePaint;
    }
    if (spec.nose) {
      const button = new THREE.MeshPhysicalMaterial({ color: '#181210', roughness: 0.18, clearcoat: 1, clearcoatRoughness: 0.05 });
      const nose = new THREE.Mesh(new THREE.SphereGeometry(1, 24, 16), spec.buttonNose ? button : this.nose);
      nose.scale.copy(spec.nose.radii);
      nose.position.copy(spec.nose.at);
      nose.rotation.x = spec.nose.tilt;
      this.head.add(nose);
    }

    const { layout } = spec;
    this.eyes = [this.eye(-1, layout), this.eye(1, layout)];
    for (let i = 0; i < 2; i++) {
      const length = (layout.browOuterX - layout.browInnerX) * S * 0.7;
      const brow = new THREE.Mesh(new THREE.CapsuleGeometry(0.019, length, 4, 8), this.thread);
      this.head.add(brow);
      this.brows.push(brow);
    }

    this.tear = new THREE.Mesh(
      new THREE.SphereGeometry(1, 16, 12),
      new THREE.MeshPhysicalMaterial({ color: '#8fd3ff', roughness: 0.05, clearcoat: 1, transparent: true }),
    );
    this.tear.scale.set(0.028, 0.042, 0.022);
    this.head.add(this.tear);
  }

  /** A button (or googly) eye in a socket on the face, with an upper and a lower lid. */
  private eye(side: Side, layout: FaceLayout): Eye {
    const { point, normal } = this.onFace(side * layout.eyeX * S, -layout.eyeY * S);
    const group = new THREE.Group();
    group.position.copy(point).addScaledVector(normal, -0.04);
    group.lookAt(group.position.clone().add(normal));
    const googly = layout.eyeStyle === 'googly';
    const r = layout.eyeR * S * (googly ? GOOGLY : 0.84);

    const ball = new THREE.Group();
    const button = new THREE.Mesh(
      new THREE.SphereGeometry(r, 32, 24),
      new THREE.MeshPhysicalMaterial({ color: googly ? '#fbfaf7' : '#181210', roughness: googly ? 0.3 : 0.18, clearcoat: 1, clearcoatRoughness: 0.05 }),
    );
    ball.add(button);
    let pupil: THREE.Mesh | undefined;
    if (googly) {
      pupil = new THREE.Mesh(new THREE.SphereGeometry(r * 0.48, 24, 16), new THREE.MeshPhysicalMaterial({ color: '#181210', roughness: 0.2, clearcoat: 1 }));
      pupil.scale.set(1, 1, 0.45);
      ball.add(pupil);
    }
    const shine = new THREE.Mesh(new THREE.SphereGeometry(r * (googly ? 0.1 : 0.17), 12, 8), new THREE.MeshBasicMaterial({ color: '#ffffff' }));
    shine.position.set(r * 0.34, r * 0.42, r * 0.74).normalize().multiplyScalar(r * (googly ? 0.97 : 0.86));
    ball.add(shine);

    // Lids: hemispherical caps a little bigger than the eye. Rotated about x, the upper one swings
    // down over the front to close; the lower one rises a little for a contented look.
    const upper = new THREE.Group();
    upper.add(new THREE.Mesh(new THREE.SphereGeometry(r * 1.08, 32, 12, 0, Math.PI * 2, 0, Math.PI / 2), this.lidMaterial));
    if (layout.lashes) {
      // Lashes at the outer corner, on the lid's rim so they close with it.
      for (const a of [38, 58, 78]) {
        const angle = (a * Math.PI) / 180;
        const lash = new THREE.Mesh(new THREE.CapsuleGeometry(0.0065, 0.045, 4, 8), this.thread);
        const rim = new THREE.Vector3(side * Math.sin(angle), 0, Math.cos(angle)).multiplyScalar(r * 1.08);
        lash.position.copy(rim).addScaledVector(rim.clone().normalize(), 0.022).add(new THREE.Vector3(0, -0.006, 0));
        lash.lookAt(lash.position.clone().add(rim.clone().normalize().add(new THREE.Vector3(0, -0.35, 0))));
        lash.rotateX(Math.PI / 2);
        upper.add(lash);
      }
    }
    const lower = new THREE.Mesh(new THREE.SphereGeometry(r * 1.06, 32, 12, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2), this.lidMaterial);
    ball.add(upper, lower);

    const arc = new THREE.Mesh(new THREE.TorusGeometry(r * 0.9, 0.019, 8, 28, Math.PI), this.thread);
    // Out on top of any fur on the face, or the fur hides it.
    arc.position.set(0, -r * 0.35, r * 0.62 + (this.faceSpec!.fur ?? 0));
    arc.rotation.x = -0.35;
    group.add(ball, arc);
    this.head.add(group);
    return { group, ball, upper, lower, arc, pupil };
  }

  private drawFace(face: FaceParams, pose: Pose) {
    const { layout } = this.faceSpec!;

    // A big smile turns the eyes into stitched arches, as the flat ones' do.
    const arch = THREE.MathUtils.smoothstep(face.eyeSmile, 0.45, 0.85);
    this.eyes.forEach((eye, i) => {
      const side: Side = i === 0 ? -1 : 1;
      let open = Math.max(0, face.eyeOpen) * pose.blink;
      if (side === 1) open *= 1 - pose.wink;
      eye.upper.rotation.set(Math.PI / 2 - (Math.PI / 2 + 1.15) * Math.min(1.25, open), 0, side * face.browTilt * -0.18);
      eye.lower.rotation.x = 1.25 - Math.min(0.6, face.eyeSmile) * 1.1;
      eye.ball.scale.setScalar(Math.max(0.001, 1 - arch));
      eye.arc.visible = arch > 0.01;
      eye.arc.scale.set(arch, arch * (0.6 + 0.4 * pose.blink), arch);
      if (eye.pupil) {
        const r = layout.eyeR * S * GOOGLY;
        eye.pupil.position.set(face.gazeX * r * 0.4, -face.gazeY * r * 0.4, r * 0.82);
      }
    });

    // Brows: thread stitches that lift and tilt (inner ends down when cross, up when worried).
    const drop = this.faceSpec!.browDrop ?? 0.035;
    this.brows.forEach((brow, i) => {
      const side: Side = i === 0 ? -1 : 1;
      const x = side * ((layout.browInnerX + layout.browOuterX) / 2) * S;
      const y = -layout.browY * S - drop + (face.browRaise + pose.browBoost) * 0.045;
      const { point, normal } = this.onFace(x, y);
      brow.position.copy(point).addScaledVector(normal, 0.018 + (this.faceSpec!.fur ?? 0));
      brow.lookAt(point.clone().add(normal));
      brow.rotateZ(Math.PI / 2 + side * face.browTilt * 0.35);
    });

    // A tear running down from the right eye.
    const tear = this.tear!;
    (tear.material as THREE.MeshPhysicalMaterial).opacity = face.tear;
    tear.visible = face.tear > 0.02;
    if (tear.visible) {
      const x = (layout.eyeX + 6) * S;
      const y = -(layout.eyeY + 14) * S - ((pose.t * 0.12) % 0.16) * face.tear;
      tear.position.copy(this.onFace(x, y).point).add(new THREE.Vector3(0, 0, 0.02));
    }

    if (Math.abs(face.blush - this.painted.blush) > 0.02) {
      this.paintDome(face.blush);
      if (this.mouthPaint === this.domePaint) this.painted.mouth = '';
    }
    const mouth = `${face.mouthOpen.toFixed(2)}|${face.mouthCurve.toFixed(2)}|${face.mouthWidth.toFixed(2)}`;
    if (mouth !== this.painted.mouth) {
      if (this.mouthPaint === this.domePaint) this.paintDome(face.blush);
      this.paintMouth(face);
      this.painted.mouth = mouth;
    }
  }

  /** The dome's texture: its colours, and cheeks that are always a little rosy. */
  private paintDome(blush: number) {
    const paint = this.domePaint!;
    const p = paint.painter();
    const { ctx } = p;
    ctx.fillStyle = this.palette['--plush-skin']!;
    ctx.fillRect(0, 0, TEX, TEX);
    this.faceSpec!.paintDome?.(p, this.palette);

    const { cheekX, cheekY } = this.faceSpec!.layout;
    ctx.globalAlpha = 0.3 + blush * 0.5;
    for (const side of [-1, 1]) {
      const x = p.x(side * cheekX);
      const y = p.y(cheekY);
      const g = ctx.createRadialGradient(x, y, 0, x, y, 12 * p.unit);
      g.addColorStop(0, this.palette['--plush-blush']!);
      g.addColorStop(1, 'rgba(0,0,0,0)');
      ctx.fillStyle = g;
      ctx.beginPath();
      ctx.ellipse(x, y, 12 * p.unit, 9 * p.unit, 0, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
    paint.texture.needsUpdate = true;
    this.painted.blush = blush;
  }

  /** The stitched mouth, drawn as in 2D: corners lift with a smile, opening drops the lower lip. */
  private paintMouth(face: FaceParams) {
    const paint = this.mouthPaint!;
    const p = paint.painter();
    const { ctx } = p;
    const { layout, philtrum, teeth } = this.faceSpec!;
    if (paint !== this.domePaint) {
      ctx.fillStyle = this.palette['--plush-snout']!;
      ctx.fillRect(0, 0, TEX, TEX);
    }

    ctx.strokeStyle = this.palette['--plush-thread']!;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.lineWidth = 2.8 * p.unit;
    if (philtrum) {
      ctx.beginPath();
      ctx.moveTo(p.x(0), p.y(philtrum[0]));
      ctx.lineTo(p.x(0), p.y(philtrum[1]));
      ctx.stroke();
    }

    const mw = layout.mouthW * face.mouthWidth;
    let corner = layout.mouthY - face.mouthCurve * 4;
    let upper = layout.mouthY + face.mouthCurve * 6;
    let lower = upper + face.mouthOpen * layout.mouthDrop;

    // Never past the bottom of the surface it's painted on: there the texture wraps under the chin,
    // and a wide yawn showed as a dark strip down it (the giraffe's and otter's mouths sit low). The
    // mouth opens upwards instead, as far as the nose allows, and stops at the edge.
    // The lower lip is a curve towards `lower`: its lowest point is halfway between the corners and it.
    const [, cy, , ry] = paint.shape.svg;
    const floor = cy + ry * 0.95;
    const bottom = (corner + lower) / 2;
    if (bottom > floor) {
      const roof = philtrum ? philtrum[1] + 2 : layout.mouthY - layout.mouthDrop * 0.6;
      const shift = Math.min(bottom - floor, Math.max(0, Math.min(corner, upper) - roof));
      corner -= shift;
      upper -= shift;
      lower = Math.min(lower - shift, 2 * floor - corner);
    }

    const mouth = new Path2D();
    mouth.moveTo(p.x(-mw), p.y(corner));
    mouth.quadraticCurveTo(p.x(0), p.y(upper), p.x(mw), p.y(corner));
    mouth.quadraticCurveTo(p.x(0), p.y(lower), p.x(-mw), p.y(corner));
    mouth.closePath();

    if (face.mouthOpen > 0.04) {
      ctx.fillStyle = '#4a2a28';
      ctx.fill(mouth);
      ctx.save();
      ctx.clip(mouth);
      if (face.mouthOpen > 0.25) {
        // A felt tongue at the bottom of an open mouth.
        ctx.fillStyle = '#e98a95';
        ctx.beginPath();
        ctx.ellipse(p.x(0), p.y(lower - 1), mw * 0.55 * p.unit, (layout.mouthDrop * 0.4) * face.mouthOpen * p.unit, 0, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.restore();
    }
    if (teeth) {
      // Two little fangs at the top of the mouth, as the flat monster has.
      const tx = mw * 0.45;
      const ty = (corner + upper) / 2;
      ctx.fillStyle = '#fffdf8';
      ctx.beginPath();
      for (const x of [-tx, tx]) {
        ctx.moveTo(p.x(x - 4), p.y(ty));
        ctx.lineTo(p.x(x), p.y(ty + 7));
        ctx.lineTo(p.x(x + 4), p.y(ty));
      }
      ctx.fill();
    }
    ctx.stroke(mouth);
    paint.texture.needsUpdate = true;
  }

  private buildThinking([x, y]: [number, number]) {
    ([[x, y, 5], [x + 16, y - 16, 7], [x + 34, y - 34, 9]] as const).forEach(([bx, by, r]) => {
      const dot = new THREE.Mesh(new THREE.SphereGeometry(r * S * 1.2, 20, 14), this.bubble);
      dot.position.copy(v(bx, by, 30));
      this.thinking.add(dot);
    });
    this.scene.add(this.thinking);
  }

  private buildSleep([x, y]: [number, number]): THREE.Sprite {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 64;
    const ctx = canvas.getContext('2d')!;
    ctx.fillStyle = '#6b6880';
    ctx.font = '700 52px system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText('z', 32, 34);
    const map = new THREE.CanvasTexture(canvas);
    map.colorSpace = THREE.SRGBColorSpace;
    const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map, transparent: true }));
    sprite.scale.setScalar(0.22);
    sprite.position.copy(v(x + 6, y + 14, 30));
    sprite.userData.y = sprite.position.y;
    this.scene.add(sprite);
    return sprite;
  }

  /** The accessories, in felt in the accent colour; one is shown at a time. */
  protected dress(anchors: Anchors) {
    // A bow.
    const bow = new THREE.Group();
    for (const side of [-1, 1]) {
      const loop = new THREE.Mesh(new THREE.SphereGeometry(1, 20, 14), this.accent);
      loop.scale.set(0.1, 0.065, 0.04);
      loop.position.set(side * 0.085, 0, 0);
      loop.rotation.z = side * 0.35;
      bow.add(loop);
    }
    const knot = new THREE.Mesh(new THREE.SphereGeometry(0.038, 16, 12), this.accent);
    knot.position.z = 0.02;
    bow.add(knot);
    bow.position.copy(anchors.bow.at);
    bow.rotation.z = anchors.bow.tilt ?? 0.2;
    anchors.bow.parent.add(bow);

    // A scarf wrapped under the chin, with a tail hanging down the chest.
    const scarf = new THREE.Group();
    const arc = anchors.scarf.arc ?? Math.PI * 2;
    const wrap = new THREE.Mesh(new THREE.TorusGeometry(anchors.scarf.radius, 0.095, 16, 48, arc), this.accent);
    // Lying flat, the arc centred at the front.
    wrap.rotation.set(Math.PI / 2, 0, Math.PI / 2 - arc / 2);
    wrap.scale.set(1, anchors.scarf.depth ?? 0.88, 1);
    wrap.position.set(0, anchors.scarf.y, anchors.scarf.z ?? -0.06);
    const tail = new THREE.Mesh(new THREE.CapsuleGeometry(0.075, 0.34, 6, 12), this.accent);
    tail.position.copy(anchors.scarf.tail);
    tail.rotation.set(0.35, 0, 0.15);
    scarf.add(wrap, tail);
    this.head.add(scarf);

    // Round glasses over the eyes.
    const glasses = new THREE.Group();
    const layout = this.faceSpec!.layout;
    const rims: THREE.Vector3[] = [];
    for (const side of [-1, 1] as const) {
      const { point, normal } = this.onFace(side * layout.eyeX * S, -layout.eyeY * S);
      const rim = new THREE.Mesh(new THREE.TorusGeometry(layout.eyeR * S * 1.3, 0.012, 10, 40), this.accent);
      rim.position.copy(point).addScaledVector(normal, 0.075);
      rim.lookAt(rim.position.clone().add(normal));
      glasses.add(rim);
      rims.push(rim.position);
    }
    const bridgeLength = Math.max(0.03, rims[1]!.distanceTo(rims[0]!) - layout.eyeR * S * 2.6);
    const bridge = new THREE.Mesh(new THREE.CylinderGeometry(0.011, 0.011, bridgeLength, 8), this.accent);
    bridge.position.copy(rims[0]!).lerp(rims[1]!, 0.5).add(new THREE.Vector3(0, 0.02, 0.02));
    bridge.rotation.z = Math.PI / 2;
    glasses.add(bridge);
    this.head.add(glasses);

    // A flower tucked in by an ear.
    const flower = new THREE.Group();
    for (let i = 0; i < 5; i++) {
      const a = (i / 5) * Math.PI * 2;
      const petal = new THREE.Mesh(new THREE.SphereGeometry(1, 16, 12), this.accent);
      petal.scale.set(0.055, 0.055, 0.025);
      petal.position.set(Math.cos(a) * 0.06, Math.sin(a) * 0.06, 0);
      flower.add(petal);
    }
    const middle = new THREE.Mesh(new THREE.SphereGeometry(0.035, 16, 12), new THREE.MeshPhysicalMaterial({ color: '#f6d55c', roughness: 0.7, sheen: 0.6 }));
    middle.position.z = 0.02;
    flower.add(middle);
    flower.position.copy(anchors.flower);
    flower.rotation.set(-0.3, 0.2, 0.3);
    this.head.add(flower);

    this.accessories = { bow, scarf, glasses, flower };
  }
}

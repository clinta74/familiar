import * as THREE from 'three';
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js';
import { mergeGeometries } from 'three/examples/jsm/utils/BufferGeometryUtils.js';
import { llamaPalette, mix } from './appearance';
import { PlushCharacter, type PlushLook, type Pose } from './plush/engine';
import type { FaceParams } from './faceExpressions';

// The plush llama, modelled: the same character and the same life (PlushCharacter works out every
// frame's face and movement) drawn with three.js as a soft toy sitting on the stage - a furry body
// and head (shell fur: layers of strands over each shape), button eyes with lids that blink and
// squint, ears that pivot at the base, and a mouth and cheeks embroidered on as textures. The face
// is laid out in the 2D llama's units (its viewBox, y down) scaled by S, so it carries over from
// PlushLlamaAvatar.

const S = 0.01;
/** A point in the 2D drawing's units: x right, y down, z towards the viewer. */
const v = (x: number, y: number, z = 0) => new THREE.Vector3(x * S, -y * S, z * S);

/** What the camera keeps in view: the sitting llama, ear tips to hooves, and its thought bubbles. */
const FRAME = { cx: 0, cy: -0.27, halfWidth: 1.05, halfHeight: 1.74 };
const FOV = 22;
const FLOOR = -1.95;

// The face: a soft taupe dome set into the fur, and the snout on it.
const FACE = { centre: v(0, 12, 20), radii: new THREE.Vector3(0.46, 0.52, 0.33) };
const SNOUT = { centre: v(0, 38, 43), radii: new THREE.Vector3(0.29, 0.23, 0.24) };
const EYE = { x: 0.23, y: 0.06, r: 0.105 };
const NECK_PIVOT = -0.6;
/** Texture size for the embroidered face and snout. Front-projected, so this is a front view. */
const TEX = 256;
/** Layers of fur over each furry shape: more is softer and costs more. */
const SHELLS = 12;

type Side = -1 | 1;

/** The point on the face dome at (x, y) seen from the front, and the surface's normal there. */
function onFace(x: number, y: number): { point: THREE.Vector3; normal: THREE.Vector3 } {
  const { centre, radii } = FACE;
  const nx = x / radii.x;
  const ny = (y - centre.y) / radii.y;
  const nz = Math.sqrt(Math.max(0, 1 - nx * nx - ny * ny));
  const point = new THREE.Vector3(x, y, centre.z + nz * radii.z);
  const normal = new THREE.Vector3(nx / radii.x, ny / radii.y, nz / radii.z).normalize();
  return { point, normal };
}

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
function ellipsoid(rx: number, ry: number, rz: number, at: THREE.Vector3, rotation?: THREE.Euler): THREE.BufferGeometry {
  const geometry = new THREE.SphereGeometry(1, 36, 24);
  geometry.scale(rx, ry, rz);
  if (rotation) geometry.applyMatrix4(new THREE.Matrix4().makeRotationFromEuler(rotation));
  geometry.translate(at.x, at.y, at.z);
  return geometry;
}

/** A leg: a capsule hanging down from the origin. */
function leg(radius: number, length: number): THREE.BufferGeometry {
  const geometry = new THREE.CapsuleGeometry(radius, length, 8, 20);
  geometry.translate(0, -length / 2, 0);
  return geometry;
}

/**
 * An ear: an ellipsoid of these radii that narrows to a rounded point and curves forward at the tip,
 * centred on the origin.
 */
function earShape(rx: number, ry: number, rz: number): THREE.BufferGeometry {
  const geometry = new THREE.SphereGeometry(1, 32, 24);
  const position = geometry.getAttribute('position');
  for (let i = 0; i < position.count; i++) {
    const x = position.getX(i);
    const y = position.getY(i);
    const z = position.getZ(i);
    const t = (y + 1) / 2;
    const taper = 1 - 0.55 * t ** 1.6;
    position.setXYZ(i, x * rx * taper, y * ry, z * rz * (1 - 0.35 * t) + 0.11 * t * t);
  }
  geometry.computeVertexNormals();
  return geometry;
}

/** A seeded random, so the fur is the same every time it's drawn. */
function seeded(seed: number): () => number {
  let s = seed;
  return () => {
    s = (s * 16807) % 2147483647;
    return (s - 1) / 2147483646;
  };
}

/** A canvas texture of per-pixel noise, repeated. */
function noiseTexture(size: number, repeat: number, low: number, seed: number): THREE.CanvasTexture {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext('2d')!;
  const image = ctx.createImageData(size, size);
  const random = seeded(seed);
  for (let i = 0; i < image.data.length; i += 4) {
    const n = low + random() * (255 - low);
    image.data[i] = image.data[i + 1] = image.data[i + 2] = n;
    image.data[i + 3] = 255;
  }
  ctx.putImageData(image, 0, 0);
  const texture = new THREE.CanvasTexture(canvas);
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.repeat.set(repeat, repeat);
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

/**
 * Pushes a shape's surface in and out a little, smoothly, so its outline is lumpy fleece rather
 * than a perfect curve. Deterministic: the same lumps every time.
 */
function lumpy(geometry: THREE.BufferGeometry, amount: number, seed: number): THREE.BufferGeometry {
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

/** A canvas-backed texture to paint on. */
function paintable(): { canvas: HTMLCanvasElement; ctx: CanvasRenderingContext2D; texture: THREE.CanvasTexture } {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = TEX;
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.anisotropy = 4;
  return { canvas, ctx: canvas.getContext('2d')!, texture };
}

interface Eye {
  group: THREE.Group;
  /** The button, its shine and its lids: sinks away as the eye becomes a smile. */
  ball: THREE.Group;
  upper: THREE.Group;
  lower: THREE.Mesh;
  /** A stitched arch, for a big smile: ^ ^. */
  arc: THREE.Mesh;
}

/** One layer of fur: how far out it is (0-1) and the material drawing it. */
interface Shell {
  f: number;
  material: THREE.MeshStandardMaterial;
}

/**
 * The default familiar in 3D: a plush llama sitting up, all soft fur. Same feelings as the flat one -
 * the ears still do most of the work - with the head turning to look where its eyes would.
 */
export class PlushLlama3D extends PlushCharacter {
  protected readonly root: HTMLCanvasElement;
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(FOV, 1, 0.1, 50);
  private readonly resize: ResizeObserver;
  private readonly container: HTMLElement;
  private readonly containerMask: string;

  private readonly fuzz = noiseTexture(128, 3, 110, 7);
  private readonly strands = strandTexture();
  private readonly wool: THREE.MeshPhysicalMaterial;
  private readonly skin: THREE.MeshPhysicalMaterial;
  private readonly lid: THREE.MeshPhysicalMaterial;
  private readonly snoutMaterial: THREE.MeshPhysicalMaterial;
  private readonly earInner: THREE.MeshPhysicalMaterial;
  private readonly nose: THREE.MeshPhysicalMaterial;
  private readonly thread: THREE.MeshStandardMaterial;
  private readonly hoofMaterial: THREE.MeshPhysicalMaterial;
  private readonly accent: THREE.MeshPhysicalMaterial;
  private readonly bubble: THREE.MeshStandardMaterial;
  private readonly shells = new Map<string, Shell>();

  private readonly facePaint = paintable();
  private readonly snoutPaint = paintable();

  private readonly body = new THREE.Group();
  private readonly bob = new THREE.Group();
  private readonly pivot = new THREE.Group();
  private readonly head = new THREE.Group();
  private readonly tuft = new THREE.Group();
  private readonly ears: [THREE.Group, THREE.Group];
  private readonly forelegs: [THREE.Group, THREE.Group];
  private readonly eyes: [Eye, Eye];
  private readonly brows: [THREE.Mesh, THREE.Mesh];
  private readonly tear: THREE.Mesh;
  private readonly thinking = new THREE.Group();
  private readonly sleep: THREE.Sprite;
  private readonly accessories: Record<'bow' | 'scarf' | 'glasses' | 'flower', THREE.Object3D>;

  private palette: Record<string, string> = {};
  private painted = { blush: -1, mouth: '' };

  constructor(container: HTMLElement, look?: PlushLook) {
    super();
    this.container = container;

    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(2, window.devicePixelRatio));
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.renderer.toneMapping = THREE.NeutralToneMapping;
    this.root = this.renderer.domElement;
    this.root.className = 'plush-3d';
    this.root.setAttribute('role', 'img');
    this.root.setAttribute('aria-label', 'Your familiar, a plush llama');

    // It sits on the stage's floor, so the fade the flat llama's neck needs would only hide its hooves.
    this.containerMask = container.style.maskImage;
    container.style.maskImage = 'none';

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
    this.wool = new THREE.MeshPhysicalMaterial(fabric);
    this.skin = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.6, bumpScale: 0.5, map: this.facePaint.texture });
    this.lid = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.6, bumpScale: 0.5 });
    this.snoutMaterial = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.6, bumpScale: 0.4, map: this.snoutPaint.texture });
    this.earInner = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.7, bumpScale: 0.5 });
    this.nose = new THREE.MeshPhysicalMaterial({ roughness: 0.45, clearcoat: 0.4, clearcoatRoughness: 0.3 });
    this.thread = new THREE.MeshStandardMaterial({ roughness: 0.85 });
    this.hoofMaterial = new THREE.MeshPhysicalMaterial({ roughness: 0.6, clearcoat: 0.2 });
    this.accent = new THREE.MeshPhysicalMaterial({ ...fabric, sheenRoughness: 0.5, bumpScale: 0.6 });
    this.bubble = new THREE.MeshStandardMaterial({ roughness: 0.4, transparent: true });

    this.scene.add(this.shadow(), this.body, this.bob);
    this.bob.add(this.pivot);
    this.pivot.position.y = NECK_PIVOT;
    this.pivot.add(this.head);
    this.head.position.y = -NECK_PIVOT;

    this.forelegs = this.buildBody();
    this.headFur();
    this.ears = [this.ear(-1), this.ear(1)];
    this.faceAndSnout();
    this.eyes = [this.eye(-1), this.eye(1)];
    this.brows = [this.brow(), this.brow()];
    this.tear = this.tearDrop();
    this.accessories = this.dressing();
    this.buildThinking();
    this.sleep = this.buildSleep();

    container.appendChild(this.root);
    this.resize = new ResizeObserver(() => this.fit());
    this.resize.observe(container);
    this.fit();

    this.setLook(look ?? { colours: { wool: '#fdfaf5', face: '#e4d6cc', ear: '#b3a093', blush: '#f4aab5', accent: '#7c5cff' }, accessory: 'none' });
    this.start();
  }

  /** Recolours and redresses it in place, for the studio's live preview. */
  setLook(look: PlushLook): void {
    const p = llamaPalette(look.colours);
    this.palette = p;
    const white = new THREE.Color('#ffffff');

    // Fur is darker at the roots and lighter at the tips, which is most of what makes it look deep.
    const root = new THREE.Color(mix(look.colours.wool, '#3a2a22', 0.36));
    const tip = new THREE.Color(look.colours.wool).lerp(white, 0.14);
    this.wool.color.copy(root);
    this.wool.sheenColor.set(look.colours.wool).lerp(white, 0.4);
    for (const shell of this.shells.values()) shell.material.color.copy(root).lerp(tip, 0.25 + 0.75 * shell.f ** 0.7);

    this.lid.color.set(p['--plush-skin']!);
    this.lid.sheenColor.set(p['--plush-skin']!).lerp(white, 0.4);
    this.skin.sheenColor.copy(this.lid.sheenColor);
    this.snoutMaterial.sheenColor.copy(this.lid.sheenColor);
    this.earInner.color.set(p['--plush-ear']!);
    this.earInner.sheenColor.set(p['--plush-ear']!).lerp(white, 0.3);
    this.nose.color.set(p['--plush-nose']!);
    this.thread.color.set(p['--plush-thread']!);
    this.hoofMaterial.color.set(p['--plush-hoof']!);
    this.accent.color.set(p['--plush-accent']!);
    this.accent.sheenColor.set(p['--plush-accent']!).lerp(white, 0.4);
    this.bubble.color.set(p['--plush-accent']!);

    for (const [name, object] of Object.entries(this.accessories)) object.visible = name === look.accessory;

    // Repaint the face and snout in the new colours.
    this.painted = { blush: -1, mouth: '' };
  }

  override dispose(): void {
    super.dispose();
    this.resize.disconnect();
    this.container.style.maskImage = this.containerMask;
    const materials = new Set<THREE.Material>();
    this.scene.traverse((object) => {
      if (object instanceof THREE.Mesh || object instanceof THREE.Sprite) {
        object.geometry.dispose();
        for (const material of Array.isArray(object.material) ? object.material : [object.material]) materials.add(material);
      }
    });
    for (const material of materials) material.dispose();
    for (const texture of [this.fuzz, this.strands, this.facePaint.texture, this.snoutPaint.texture, this.sleep.material.map]) texture?.dispose();
    this.scene.environment?.dispose();
    this.renderer.dispose();
  }

  protected draw(pose: Pose): void {
    const { face } = pose;
    const deg = Math.PI / 180;

    // The head bobs, rolls on the neck, turns to look where the eyes would, and squashes; the body
    // goes along a little with the bigger movements, so a hop is the whole toy.
    this.bob.position.y = -pose.dy * S;
    this.body.position.y = -pose.dy * S * 0.35;
    this.body.scale.set(1 - pose.squash * 0.5, 1 + pose.squash * 0.5, 1);
    this.pivot.rotation.set(face.gazeY * 0.16, face.gazeX * 0.32, -pose.rotate * deg, 'YXZ');
    this.head.scale.set(1 - pose.squash, 1 + pose.squash, 1 - pose.squash * 0.5);
    this.tuft.position.y = Math.max(0, -pose.dy) * 0.4 * S;

    // Ears: perk towards upright, droop outward; pinned back a little when cross or sad.
    const lift = face.earLift;
    const earAngle = lift >= 0 ? 14 - lift * 10 : 14 - lift * 62;
    this.ears.forEach((ear, i) => {
      const side = i === 0 ? -1 : 1;
      const angle = side * (earAngle + pose.earWiggle * (i === 0 ? 1 : 0.6));
      ear.rotation.set(-0.35 * Math.max(0, -lift) + 0.08, side * -0.3, -angle * deg, 'XYZ');
    });

    // A big smile turns the buttons into stitched arches, as the flat llama's eyes do.
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
    });

    // Brows: thread stitches that lift and tilt (inner ends down when cross, up when worried).
    this.brows.forEach((brow, i) => {
      const side: Side = i === 0 ? -1 : 1;
      const x = side * 0.215;
      const y = 0.205 + (face.browRaise + pose.browBoost) * 0.045;
      const { point, normal } = onFace(x, y);
      brow.position.copy(point).addScaledVector(normal, 0.018);
      brow.lookAt(point.clone().add(normal));
      brow.rotateZ(Math.PI / 2 + side * face.browTilt * 0.35);
    });

    // A tear running down from the right eye.
    const material = this.tear.material as THREE.MeshPhysicalMaterial;
    material.opacity = face.tear;
    this.tear.visible = face.tear > 0.02;
    if (this.tear.visible) {
      const { point } = onFace(EYE.x + 0.06, EYE.y - 0.14 - ((pose.t * 0.12) % 0.16) * face.tear);
      this.tear.position.copy(point).add(new THREE.Vector3(0, 0, 0.02));
    }

    this.paint(face);

    // Waving: the right foreleg comes up from the shoulder and swings.
    const wave = this.forelegs[1];
    wave.rotation.set(-(pose.pawLift ?? 0) * 2.3, 0, (pose.pawLift ?? 0) * 0.35 - pose.pawAngle * deg * 0.9);

    // Thinking: three bubbles pulsing in turn. Resting: a drifting z.
    this.thinking.visible = pose.state === 'thinking';
    if (this.thinking.visible) {
      this.thinking.children.forEach((dot, i) => {
        const pulse = Math.max(0, Math.sin(pose.t * 4 - i * 0.9));
        dot.scale.setScalar(0.85 + 0.25 * pulse);
      });
      this.bubble.opacity = 0.9;
    }
    const resting = pose.state === 'resting';
    this.sleep.visible = resting;
    if (resting) {
      this.sleep.material.opacity = 0.5 + 0.5 * Math.sin(pose.t * 2);
      this.sleep.position.set(0.66, 0.98 + ((pose.t * 0.06) % 0.12), 0.3);
    }

    this.renderer.render(this.scene, this.camera);
  }

  // --- building ---------------------------------------------------------------------------------

  /** Fits the sitting llama into the stage, whatever its shape. */
  private fit() {
    const width = Math.max(1, this.container.clientWidth);
    const height = Math.max(1, this.container.clientHeight);
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
    const tan = Math.tan((FOV / 2) * (Math.PI / 180));
    const distance = Math.max(FRAME.halfHeight / tan, FRAME.halfWidth / (tan * this.camera.aspect));
    this.camera.position.set(FRAME.cx, FRAME.cy + 0.35, distance);
    this.camera.lookAt(FRAME.cx, FRAME.cy, 0);
    this.camera.updateProjectionMatrix();
  }

  /**
   * One layer of fur `length` long at `f` (0 at the skin, 1 at the tips): the shape pushed out along
   * its normals, drooping a little, with fewer strands the further out it is so they taper.
   */
  private shell(f: number, length: number): Shell {
    const key = `${f}:${length}`;
    const existing = this.shells.get(key);
    if (existing) return existing;

    const material = new THREE.MeshStandardMaterial({ roughness: 1, alphaMap: this.strands, alphaTest: 0.25 + 0.7 * f });
    const out = (length * f).toFixed(4);
    const droop = (length * f * f * 0.45).toFixed(4);
    material.onBeforeCompile = (shader) => {
      shader.vertexShader = shader.vertexShader.replace(
        '#include <begin_vertex>',
        `#include <begin_vertex>\n  transformed += objectNormal * ${out};\n  transformed.y -= ${droop};`,
      );
    };
    material.customProgramCacheKey = () => `fur-${key}`;
    const shell = { f, material };
    this.shells.set(key, shell);
    return shell;
  }

  /** A furry shape: the base in the wool's root colour and SHELLS layers of fur over it. */
  private furry(geometry: THREE.BufferGeometry, length: number): THREE.Group {
    const group = new THREE.Group();
    group.add(new THREE.Mesh(geometry, this.wool));
    for (let i = 1; i <= SHELLS; i++) group.add(new THREE.Mesh(geometry, this.shell(i / SHELLS, length).material));
    return group;
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
      new THREE.PlaneGeometry(2.2, 1.4),
      new THREE.MeshBasicMaterial({ map: new THREE.CanvasTexture(canvas), transparent: true, depthWrite: false }),
    );
    shadow.rotation.x = -Math.PI / 2;
    shadow.position.set(0, FLOOR + 0.01, 0.05);
    return shadow;
  }

  /**
   * The body, sitting: a neck rising into the head, a chest, a round bottom, the hind legs folded at
   * its sides with their hooves forward, and the forelegs standing in front. All one furry mesh but
   * the forelegs, which can wave.
   */
  private buildBody(): [THREE.Group, THREE.Group] {
    const parts = [
      ellipsoid(0.32, 0.5, 0.3, new THREE.Vector3(0, -0.72, -0.1)),
      ellipsoid(0.44, 0.42, 0.4, new THREE.Vector3(0, -1.12, 0)),
      ellipsoid(0.55, 0.45, 0.54, new THREE.Vector3(0, -1.48, -0.14)),
    ];
    for (const side of [-1, 1] as const) {
      parts.push(ellipsoid(0.21, 0.22, 0.38, new THREE.Vector3(side * 0.46, -1.7, 0.02), new THREE.Euler(0.1, side * -0.15, 0)));
    }
    this.body.add(this.furry(lumpy(mergeGeometries(parts)!, 0.03, 21), 0.12));

    // The hind hooves, just peeking out from under the folded legs.
    for (const side of [-1, 1] as const) {
      const hoof = new THREE.Mesh(new THREE.SphereGeometry(1, 20, 14), this.hoofMaterial);
      hoof.scale.set(0.085, 0.06, 0.07);
      hoof.position.set(side * 0.5, FLOOR + 0.06, 0.4);
      this.body.add(hoof);
    }

    return ([-1, 1] as const).map((side) => {
      const shoulder = new THREE.Group();
      shoulder.position.set(side * 0.21, -1.22, 0.28);
      shoulder.add(this.furry(lumpy(leg(0.12, 0.5), 0.015, 23 + side), 0.08));
      const hoof = new THREE.Mesh(new THREE.CylinderGeometry(0.115, 0.13, 0.1, 24), this.hoofMaterial);
      hoof.position.y = FLOOR + 0.05 - shoulder.position.y;
      shoulder.add(hoof);
      this.body.add(shoulder);
      return shoulder;
    }) as [THREE.Group, THREE.Group];
  }

  /** The head's fur, a full ball around the face which it frames like a ruff, and a fluffy topknot. */
  private headFur() {
    this.head.add(this.furry(lumpy(ellipsoid(0.54, 0.56, 0.44, new THREE.Vector3(0, -0.06, -0.1)), 0.035, 27), 0.13));
    const fringe = mergeGeometries([
      ellipsoid(0.2, 0.17, 0.16, new THREE.Vector3(-0.24, 0.44, 0.16)),
      ellipsoid(0.22, 0.19, 0.17, new THREE.Vector3(0, 0.53, 0.14)),
      ellipsoid(0.2, 0.17, 0.16, new THREE.Vector3(0.24, 0.44, 0.16)),
      ellipsoid(0.16, 0.13, 0.13, new THREE.Vector3(-0.12, 0.37, 0.3)),
      ellipsoid(0.16, 0.13, 0.13, new THREE.Vector3(0.13, 0.37, 0.3)),
    ])!;
    this.tuft.add(this.furry(lumpy(fringe, 0.02, 29), 0.12));
    this.head.add(this.tuft);
  }

  /** A tall ear on a pivot at its base, furry outside and soft colour within. */
  private ear(side: Side): THREE.Group {
    const ear = new THREE.Group();
    ear.position.set(side * 0.4, 0.5, -0.05);
    const outer = this.furry(earShape(0.14, 0.36, 0.09), 0.05);
    outer.position.set(0, 0.32, 0);
    const inner = new THREE.Mesh(earShape(0.075, 0.25, 0.045), this.earInner);
    inner.position.set(0, 0.31, 0.068);
    ear.add(outer, inner);
    this.head.add(ear);
    return ear;
  }

  private faceAndSnout() {
    const face = new THREE.Mesh(frontSphere(), this.skin);
    face.position.copy(FACE.centre);
    face.scale.copy(FACE.radii);
    const snout = new THREE.Mesh(frontSphere(), this.snoutMaterial);
    snout.position.copy(SNOUT.centre);
    snout.scale.copy(SNOUT.radii);
    const nose = new THREE.Mesh(new THREE.SphereGeometry(1, 24, 16), this.nose);
    nose.scale.set(0.1, 0.055, 0.05);
    nose.position.set(0, -0.265, 0.625);
    nose.rotation.x = -0.35;
    this.head.add(face, snout, nose);
  }

  /** A button eye in a socket on the face, with an upper and a lower lid. */
  private eye(side: Side): Eye {
    const { point, normal } = onFace(side * EYE.x, EYE.y);
    const group = new THREE.Group();
    group.position.copy(point).addScaledVector(normal, -0.04);
    group.lookAt(group.position.clone().add(normal));
    const r = EYE.r;

    const button = new THREE.Mesh(
      new THREE.SphereGeometry(r, 32, 24),
      new THREE.MeshPhysicalMaterial({ color: '#181210', roughness: 0.18, clearcoat: 1, clearcoatRoughness: 0.05 }),
    );
    const shine = new THREE.Mesh(new THREE.SphereGeometry(r * 0.17, 12, 8), new THREE.MeshBasicMaterial({ color: '#ffffff' }));
    shine.position.set(r * 0.34, r * 0.42, r * 0.74).normalize().multiplyScalar(r * 0.86);

    // Lids: hemispherical caps a little bigger than the button. Rotated about x, the upper one
    // swings down over the front to close; the lower one rises a little for a contented look.
    const upper = new THREE.Group();
    upper.add(new THREE.Mesh(new THREE.SphereGeometry(r * 1.08, 32, 12, 0, Math.PI * 2, 0, Math.PI / 2), this.lid));
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
    const lower = new THREE.Mesh(new THREE.SphereGeometry(r * 1.06, 32, 12, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2), this.lid);

    const ball = new THREE.Group();
    ball.add(button, shine, upper, lower);
    const arc = new THREE.Mesh(new THREE.TorusGeometry(r * 0.9, 0.019, 8, 28, Math.PI), this.thread);
    arc.position.set(0, -r * 0.35, r * 0.62);
    arc.rotation.x = -0.35;
    group.add(ball, arc);
    this.head.add(group);
    return { group, ball, upper, lower, arc };
  }

  private brow(): THREE.Mesh {
    const brow = new THREE.Mesh(new THREE.CapsuleGeometry(0.019, 0.1, 4, 8), this.thread);
    this.head.add(brow);
    return brow;
  }

  private tearDrop(): THREE.Mesh {
    const tear = new THREE.Mesh(
      new THREE.SphereGeometry(1, 16, 12),
      new THREE.MeshPhysicalMaterial({ color: '#8fd3ff', roughness: 0.05, clearcoat: 1, transparent: true }),
    );
    tear.scale.set(0.028, 0.042, 0.022);
    this.head.add(tear);
    return tear;
  }

  private buildThinking() {
    // Close beside the head, where the camera already looks.
    ([[60, -84, 5], [76, -100, 7], [94, -118, 9]] as const).forEach(([x, y, r]) => {
      const dot = new THREE.Mesh(new THREE.SphereGeometry(r * S * 1.2, 20, 14), this.bubble);
      dot.position.copy(v(x, y, 30));
      this.thinking.add(dot);
    });
    this.scene.add(this.thinking);
  }

  private buildSleep(): THREE.Sprite {
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
    this.scene.add(sprite);
    return sprite;
  }

  /** The accessories, in felt in the accent colour; one is shown at a time. */
  private dressing(): Record<'bow' | 'scarf' | 'glasses' | 'flower', THREE.Object3D> {
    // A bow on the left ear.
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
    bow.position.set(0, 0.16, 0.12);
    bow.rotation.z = 0.2;
    this.ears[0].add(bow);

    // A scarf wrapped under the chin, with a tail hanging down the chest.
    const scarf = new THREE.Group();
    const wrap = new THREE.Mesh(new THREE.TorusGeometry(0.43, 0.095, 16, 48), this.accent);
    wrap.rotation.x = Math.PI / 2;
    wrap.scale.set(1, 0.88, 1);
    wrap.position.set(0, -0.66, -0.06);
    const tail = new THREE.Mesh(new THREE.CapsuleGeometry(0.075, 0.34, 6, 12), this.accent);
    tail.position.set(0.2, -0.92, 0.38);
    tail.rotation.set(0.35, 0, 0.15);
    scarf.add(wrap, tail);
    this.head.add(scarf);

    // Round glasses over the eyes.
    const glasses = new THREE.Group();
    const rims: THREE.Vector3[] = [];
    for (const side of [-1, 1] as const) {
      const { point, normal } = onFace(side * EYE.x, EYE.y);
      const rim = new THREE.Mesh(new THREE.TorusGeometry(EYE.r * 1.55, 0.012, 10, 40), this.accent);
      rim.position.copy(point).addScaledVector(normal, 0.075);
      rim.lookAt(rim.position.clone().add(normal));
      glasses.add(rim);
      rims.push(rim.position);
    }
    const bridge = new THREE.Mesh(new THREE.CylinderGeometry(0.011, 0.011, 0.11, 8), this.accent);
    bridge.position.copy(rims[0]!).lerp(rims[1]!, 0.5).add(new THREE.Vector3(0, 0.02, 0.02));
    bridge.rotation.z = Math.PI / 2;
    glasses.add(bridge);
    this.head.add(glasses);

    // A flower tucked in the fur by the right ear.
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
    flower.position.copy(v(32, -58, 48));
    flower.rotation.set(-0.3, 0.2, 0.3);
    this.head.add(flower);

    return { bow, scarf, glasses, flower };
  }

  // --- painting ---------------------------------------------------------------------------------

  /** Repaints the cheeks and the embroidered mouth when they've changed enough to see. */
  private paint(face: FaceParams) {
    if (Math.abs(face.blush - this.painted.blush) > 0.02) this.paintFace(face.blush);
    const mouth = `${face.mouthOpen.toFixed(2)}|${face.mouthCurve.toFixed(2)}|${face.mouthWidth.toFixed(2)}`;
    if (mouth !== this.painted.mouth) {
      this.paintSnout(face);
      this.painted.mouth = mouth;
    }
  }

  /** The face's texture: its colour, and cheeks that are always a little rosy. */
  private paintFace(blush: number) {
    const { ctx, texture } = this.facePaint;
    ctx.fillStyle = this.palette['--plush-skin']!;
    ctx.fillRect(0, 0, TEX, TEX);
    ctx.globalAlpha = 0.3 + blush * 0.5;
    for (const side of [-1, 1]) {
      // Cheeks at (±35, 24) on a face centred at (0, 12) with radii 47 x 52, seen from the front.
      const x = (side * (35 / 47) * 0.5 + 0.5) * TEX;
      const y = (0.5 + ((24 - 12) / 52) * 0.5) * TEX;
      const g = ctx.createRadialGradient(x, y, 0, x, y, TEX * 0.13);
      g.addColorStop(0, this.palette['--plush-blush']!);
      g.addColorStop(1, 'rgba(0,0,0,0)');
      ctx.fillStyle = g;
      ctx.beginPath();
      ctx.ellipse(x, y, TEX * 0.13, TEX * 0.1, 0, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
    texture.needsUpdate = true;
    this.painted.blush = blush;
  }

  /**
   * The snout's texture: the split upper lip and the stitched mouth, drawn as in the 2D llama (its
   * snout is centred at (0, 36) with radii 27 x 22) - corners lift with a smile, opening drops the lip.
   */
  private paintSnout(face: FaceParams) {
    const { ctx, texture } = this.snoutPaint;
    const px = (x: number) => ((x / 27) * 0.5 + 0.5) * TEX;
    const py = (y: number) => (0.5 + ((y - 36) / 22) * 0.5) * TEX;
    const unit = TEX / 54;

    ctx.fillStyle = this.palette['--plush-snout']!;
    ctx.fillRect(0, 0, TEX, TEX);

    ctx.strokeStyle = this.palette['--plush-thread']!;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.lineWidth = 2.8 * unit;
    ctx.beginPath();
    ctx.moveTo(px(0), py(31));
    ctx.lineTo(px(0), py(40));
    ctx.stroke();

    const mw = 12 * face.mouthWidth;
    const corner = 42 - face.mouthCurve * 4;
    const upper = 42 + face.mouthCurve * 6;
    const lower = upper + face.mouthOpen * 16;
    const mouth = new Path2D();
    mouth.moveTo(px(-mw), py(corner));
    mouth.quadraticCurveTo(px(0), py(upper), px(mw), py(corner));
    mouth.quadraticCurveTo(px(0), py(lower), px(-mw), py(corner));
    mouth.closePath();

    if (face.mouthOpen > 0.04) {
      ctx.fillStyle = '#4a2a28';
      ctx.fill(mouth);
      if (face.mouthOpen > 0.25) {
        // A felt tongue at the bottom of an open mouth.
        ctx.save();
        ctx.clip(mouth);
        ctx.fillStyle = '#e98a95';
        ctx.beginPath();
        ctx.ellipse(px(0), py(lower - 1), mw * 0.55 * unit, 6 * face.mouthOpen * unit, 0, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();
      }
    }
    ctx.stroke(mouth);
    texture.needsUpdate = true;
  }
}

import * as THREE from 'three';
import { GLTFLoader, type GLTF } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { MeshoptDecoder } from 'three/examples/jsm/libs/meshopt_decoder.module.js';
import type { PlushLook, Pose } from '../plush/engine';
import { Plush3D, S, type Anchors, type Coat, type FaceSpec, type Frame, type FurOptions } from './Plush3D';

/**
 * A 3D character modelled in Blender (tools/models): its parts come from a GLB, everything else -
 * fur, colour, the painted face, eyes and lids, the pose - is Plush3D's, as for the characters built
 * in code. Each mesh says what it is in its custom properties (tools/models/blendkit.py): `role`
 * fur (with `coat` and `fur` length), face, nose, hoof, earInner, horn, claw or accent. Nodes are
 * named for what moves: head, tuft, ear_L/ear_R, arm_L/arm_R and forearm_L/forearm_R, foot_L/foot_R,
 * tail.
 */
export interface ModelSpec {
  url: string;
  label: string;
  frame: Frame;
  /** Where the head pivots on the neck, in the 2D drawing's units (as Plush3D takes it). */
  neckPivotY: number;
  bubbles?: [number, number];
  /** The face, as for the characters built in code; its surface comes from the model's `face` mesh. */
  face: Omit<FaceSpec, 'surface'>;
  defaults: PlushLook;
  /** How the ears swing (Plush3D.drawEars): resting angle, how far they droop, and their turn. */
  ears?: { rest: number; droop: number; turn?: number };
  /** How far the feet's toes turn out, in degrees. */
  splay?: number;
  /** Where the accessories go; `node` finds a part of the model by name. */
  anchors: (node: (name: string) => THREE.Object3D) => Anchors;
  /** Fur options for particular meshes, by name: prickles that bristle, say. */
  fur?: Record<string, FurOptions>;
  /** Anything else that moves: a tail, bristling fur. */
  animate?: (pose: Pose, node: (name: string) => THREE.Object3D | undefined, character: ModelPlush3D) => void;
}

const loaded = new Map<string, Promise<GLTF>>();

/** Loads a model once, however many avatars use it. */
function load(url: string): Promise<GLTF> {
  let gltf = loaded.get(url);
  if (!gltf) {
    const loader = new GLTFLoader();
    loader.setMeshoptDecoder(MeshoptDecoder);
    gltf = loader.loadAsync(url);
    // A failed load shouldn't stick: the next try should try again.
    gltf.catch(() => loaded.delete(url));
    loaded.set(url, gltf);
  }
  return gltf;
}

export class ModelPlush3D extends Plush3D {
  /** A model's parts by name. */
  private readonly nodes = new Map<string, THREE.Object3D>();
  private readonly spec: ModelSpec;
  /** A live length multiplier for fur that bristles (see ModelSpec.fur). */
  readonly bristle = { value: 1 };

  static async create(container: HTMLElement, look: PlushLook | undefined, spec: ModelSpec): Promise<ModelPlush3D> {
    const gltf = await load(spec.url);
    return new ModelPlush3D(container, look, spec, gltf.scene);
  }

  private constructor(container: HTMLElement, look: PlushLook | undefined, spec: ModelSpec, scene: THREE.Object3D) {
    super(container, spec.label, spec.frame, spec.neckPivotY, spec.bubbles);
    this.spec = spec;

    const model = scene.clone(true);
    const meshes: THREE.Mesh[] = [];
    model.traverse((object) => {
      if (object instanceof THREE.Mesh) meshes.push(object);
    });

    // Each mesh becomes what it says it is.
    let face: THREE.Mesh | undefined;
    for (const mesh of meshes) {
      const { role, coat = 'wool', fur = 0.08 } = mesh.userData as { role?: string; coat?: Coat; fur?: number };
      if (role === 'fur') {
        // A fur mask painted in Blender (blendkit.fur_mask) arrives as the vertex colour's red.
        const mask = mesh.geometry.getAttribute('color');
        const options = { ...spec.fur?.[mesh.name] };
        if (mask) {
          const lengths = new Float32Array(mask.count);
          for (let i = 0; i < mask.count; i++) lengths[i] = mask.getX(i);
          mesh.geometry.setAttribute('furLength', new THREE.BufferAttribute(lengths, 1));
          options.varying = true;
        }
        const furry = this.furry(mesh.geometry, fur, coat, options);
        furry.name = mesh.name;
        furry.position.copy(mesh.position);
        furry.quaternion.copy(mesh.quaternion);
        furry.scale.copy(mesh.scale);
        mesh.parent!.add(furry);
        mesh.removeFromParent();
      } else if (role === 'face') {
        face = mesh;
      } else {
        mesh.material = this.materialFor(role ?? '') ?? this.claw;
      }
    }
    model.traverse((object) => this.nodes.set(object.name, object));

    // The model's head and body become the character's: its head moves on the neck.
    const head = this.nodes.get('head');
    const body = this.nodes.get('body');
    if (face) {
      // In head coordinates: the face is the head's child.
      const surface = face.geometry.clone();
      surface.applyMatrix4(face.matrix);
      face.removeFromParent();
      this.buildFace({ ...spec.face, surface });
    }
    for (const child of [...(head?.children ?? [])]) this.head.add(child);
    for (const child of [...(body?.children ?? [])]) this.body.add(child);

    const named = (name: string) => this.nodes.get(name);
    const arms = (['L', 'R'] as const)
      .map((side) => ({ shoulder: named(`arm_${side}`), elbow: named(`forearm_${side}`) }))
      .filter((arm): arm is { shoulder: THREE.Object3D; elbow: THREE.Object3D } => !!arm.shoulder && !!arm.elbow);
    const feet = (['L', 'R'] as const).map((side) => named(`foot_${side}`)).filter((foot): foot is THREE.Object3D => !!foot);
    this.useLimbs(arms, feet, spec.splay ?? 10);

    this.dress(spec.anchors((name) => {
      const found = this.nodes.get(name);
      if (!found) throw new Error(`The ${spec.label} model has no ${name}.`);
      return found;
    }));

    this.finish(look, spec.defaults);
  }

  protected drawParts(pose: Pose): void {
    const ears = ['ear_L', 'ear_R'].map((name) => this.nodes.get(name)).filter((ear): ear is THREE.Object3D => !!ear);
    if (this.spec.ears && ears.length === 2) this.drawEars(ears as THREE.Group[], pose, this.spec.ears.rest, this.spec.ears.droop, this.spec.ears.turn);

    // A topknot bounces up as it hops.
    const tuft = this.nodes.get('tuft');
    if (tuft) tuft.position.y = Math.max(0, -pose.dy) * 0.4 * S;

    this.spec.animate?.(pose, (name) => this.nodes.get(name), this);
  }
}

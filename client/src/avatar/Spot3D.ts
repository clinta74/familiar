import * as THREE from 'three';
import { characterInfo, mix } from './appearance';
import type { PlushLook, Pose } from './plush/engine';
import { Plush3D } from './plush3d/Plush3D';
import { spotFrame, spotSplit } from './SpotAvatar';

/** The spot's radius, as the flat one's (72 units). */
const RADIUS = 0.72;
/** Where it floats: about where the plush characters' heads are, over its shadow. */
const CENTRE_Y = -1.0;

/**
 * The surface, in the vertex shader: a sphere pushed in and out by the same shape the flat spot's
 * outline follows (spotPath), in 3D - slow lobes, spikes, a fast shiver, a reaching lobe, sagging -
 * with the normals worked out from the moved surface so the light follows its shape.
 */
const SHAPE_GLSL = /* glsl */ `
uniform float uSize;
uniform float uSx;
uniform float uSy;
uniform float uWobble;
uniform float uPhase;
uniform float uSwirl;
uniform float uSpikes;
uniform float uSag;
uniform float uLobe;
uniform vec3 uLobeDir;
uniform float uJiggle;
uniform float uRough;
uniform float uTime;

float spotRadius(vec3 d) {
  float az = atan(d.z, d.x);
  float el = asin(clamp(d.y, -1.0, 1.0));
  float around = cos(el);
  float r = 1.0
    + uWobble * (sin(2.0 * az + uPhase + uSwirl) * 0.6 * around + sin(3.0 * az - 2.0 * el - uPhase * 0.7 + 1.3 + uSwirl) * 0.4)
    + uSpikes * pow(max(0.0, sin(13.0 * az + uTime * 0.8) * sin(9.0 * el + 0.6)), 3.0) * around
    + uJiggle * sin(7.0 * az + 5.0 * el + uTime * 31.0)
    + uLobe * pow(max(0.0, dot(d, uLobeDir)), 6.0)
    + uRough * 0.0035 * sin(12.0 * d.x + uTime * 2.0) * sin(10.0 * d.y + 1.0) * sin(11.0 * d.z);
  // Heavier at the bottom, flatter on top.
  r += uSag * (d.y < 0.0 ? -d.y * 0.6 : -d.y * 0.9);
  return r;
}

vec3 spotPoint(vec3 d) {
  vec3 p = d * spotRadius(d) * ${RADIUS.toFixed(2)} * uSize;
  p.x *= uSx;
  p.z *= uSx;
  p.y = p.y * uSy - uSag * ${(RADIUS * 0.5).toFixed(2)};
  return p;
}
`;

/** A soft round glow, white, to be tinted. */
function glowTexture(): THREE.CanvasTexture {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 128;
  const ctx = canvas.getContext('2d')!;
  const g = ctx.createRadialGradient(64, 64, 0, 64, 64, 64);
  g.addColorStop(0, 'rgba(255,255,255,0.9)');
  g.addColorStop(0.45, 'rgba(255,255,255,0.45)');
  g.addColorStop(1, 'rgba(255,255,255,0)');
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, 128, 128);
  return new THREE.CanvasTexture(canvas);
}

/** A four-pointed sparkle, white, to be tinted. */
function sparkleTexture(): THREE.CanvasTexture {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 64;
  const ctx = canvas.getContext('2d')!;
  ctx.fillStyle = '#ffffff';
  ctx.beginPath();
  ctx.moveTo(32, 2);
  for (const [x, y] of [[38, 26], [62, 32], [38, 38], [32, 62], [26, 38], [2, 32], [26, 26]] as const) ctx.lineTo(x, y);
  ctx.closePath();
  ctx.fill();
  return new THREE.CanvasTexture(canvas);
}

/**
 * The spot in 3D: a glossy blob of colour with no face, floating over its shadow. It shows how it
 * feels exactly as the flat spot does (spotFrame) - its shape, its surface and its colour - with
 * light to work with: glossy and lit warm from within when happy, matte and dim when sad, frayed
 * and spiky when cross, a soft halo in its glow colour, and sparkles when delighted.
 */
export class Spot3D extends Plush3D {
  private readonly blob: THREE.Mesh;
  private readonly material: THREE.MeshPhysicalMaterial;
  private readonly uniforms = {
    uSize: { value: 1 },
    uSx: { value: 1 },
    uSy: { value: 1 },
    uWobble: { value: 0 },
    uPhase: { value: 0 },
    uSwirl: { value: 0 },
    uSpikes: { value: 0 },
    uSag: { value: 0 },
    uLobe: { value: 0 },
    uLobeDir: { value: new THREE.Vector3(1, 0, 0) },
    uJiggle: { value: 0 },
    uRough: { value: 0 },
    uTime: { value: 0 },
  };
  private readonly halo: THREE.Sprite;
  private readonly sparkles: THREE.Sprite[] = [];
  /** Pieces that bud off for a gesture and are taken back in: it splits and reforms. */
  private readonly pieces: THREE.Mesh[] = [];
  private readonly pieceMaterial: THREE.MeshPhysicalMaterial;
  private base = '#7c5cff';
  private glowColour = '#c4b5ff';
  private phase = 0;
  private swirl = 0;
  private lastT = 0;

  constructor(container: HTMLElement, look?: PlushLook) {
    super(container, 'Your familiar, a spot of colour', 100, [72, 2]);

    this.material = new THREE.MeshPhysicalMaterial({ roughness: 0.35, clearcoat: 0.6, clearcoatRoughness: 0.08, sheen: 0.4 });
    this.material.onBeforeCompile = (shader) => {
      Object.assign(shader.uniforms, this.uniforms);
      shader.vertexShader = `${SHAPE_GLSL}\n${shader.vertexShader}`
        .replace('#include <beginnormal_vertex>', `
          vec3 spotDir = normalize(position);
          vec3 spotT1 = normalize(cross(spotDir, abs(spotDir.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
          vec3 spotT2 = cross(spotDir, spotT1);
          vec3 spotP0 = spotPoint(spotDir);
          vec3 spotP1 = spotPoint(normalize(spotDir + spotT1 * 0.01));
          vec3 spotP2 = spotPoint(normalize(spotDir + spotT2 * 0.01));
          vec3 objectNormal = normalize(cross(spotP1 - spotP0, spotP2 - spotP0));
          if (dot(objectNormal, spotDir) < 0.0) objectNormal = -objectNormal;`)
        .replace('#include <begin_vertex>', 'vec3 transformed = spotP0;');
    };
    this.material.customProgramCacheKey = () => 'spot3d';
    this.blob = new THREE.Mesh(new THREE.SphereGeometry(1, 128, 96), this.material);
    this.blob.position.y = CENTRE_Y;
    this.head.add(this.blob);

    this.pieceMaterial = new THREE.MeshPhysicalMaterial({ roughness: 0.35, clearcoat: 0.6, clearcoatRoughness: 0.08, sheen: 0.4 });
    for (let i = 0; i < 3; i++) {
      const piece = new THREE.Mesh(new THREE.SphereGeometry(1, 48, 32), this.pieceMaterial);
      piece.visible = false;
      this.head.add(piece);
      this.pieces.push(piece);
    }

    this.halo = new THREE.Sprite(new THREE.SpriteMaterial({ map: glowTexture(), transparent: true, depthWrite: false }));
    this.halo.position.set(0, CENTRE_Y, -0.6);
    this.head.add(this.halo);

    const star = sparkleTexture();
    for (const [x, y] of [[-70, -60], [74, -44], [-56, 62], [66, 58], [0, -96]] as const) {
      const sparkle = new THREE.Sprite(new THREE.SpriteMaterial({ map: star, transparent: true, depthWrite: false }));
      sparkle.position.set(x * 0.012, CENTRE_Y - y * 0.012, 0.5);
      sparkle.scale.setScalar(0.13);
      this.head.add(sparkle);
      this.sparkles.push(sparkle);
    }

    this.finish(look, { colours: characterInfo('spot').presets[0]!.colours, accessory: 'none' });
  }

  /** Splitting and reforming (spotSplit, shared with the flat spot), in this spot's units. */
  private split(pose: Pose): { main: number; pieces: { x: number; y: number; z: number; size: number }[] } {
    const { main, pieces } = spotSplit(pose);
    return { main, pieces: pieces.map((p) => ({ x: p.x * RADIUS, y: p.y * RADIUS, z: p.z * RADIUS, size: p.size * RADIUS })) };
  }

  /** Its colour is the "wool" colour; the accessory colour is its glow. It wears nothing. */
  override setLook(look: PlushLook): void {
    super.setLook(look);
    this.base = look.colours.wool;
    this.glowColour = look.colours.accent;
  }

  protected drawParts(pose: Pose): void {
    // It floats: the plush characters' squash and gaze aren't for it.
    this.head.scale.setScalar(1);
    this.pivot.rotation.set(0, 0, 0);

    const dt = Math.max(0, Math.min(0.1, pose.t - this.lastT));
    this.lastT = pose.t;
    const { shape, flash, colour, centre, glow, haloOpacity, sparkle } = spotFrame(pose, this.base, this.glowColour);
    // Thinking turns the lobes slowly round, like turning something over.
    this.swirl += dt * (0.2 + pose.mood.thinking * 1.2) * pose.motion;
    this.phase += dt * shape.speed;

    const u = this.uniforms;
    const split = this.split(pose);
    u.uSize.value = shape.size * split.main;
    u.uSx.value = shape.sx;
    u.uSy.value = shape.sy;
    u.uWobble.value = shape.wobble;
    u.uPhase.value = this.phase;
    u.uSwirl.value = this.swirl;
    // Spikes only for real anger: the flat spot's faint bristle when surprised reads as dimples in 3D.
    u.uSpikes.value = Math.max(0, (shape.spikes - 0.04) / 0.12) * 0.16;
    u.uSag.value = shape.sag;
    u.uLobe.value = shape.lobe;
    // The flat spot's lobe angle is in its picture, y down; here it reaches out a little forward too.
    u.uLobeDir.value.set(Math.cos(shape.lobeAngle), -Math.sin(shape.lobeAngle), 0.35).normalize();
    // Gentler than the flat spot's: on a whole surface, the light shows every ripple as a crease.
    u.uJiggle.value = shape.jiggle * 0.35;
    // Frayed only beyond a calm spot's roughness (cross, a little when thinking): in 3D the light
    // catches every dimple, so calm moods stay glassy-smooth.
    u.uRough.value = Math.max(0, shape.roughness - 1.2) * (pose.motion < 1 ? 0.5 : 1);
    u.uTime.value = pose.t;

    // Glossy (happy, surprised) to matte (sad); lit warm from within as the flat spot's core is.
    const dim = pose.state === 'resting' ? 1 : pose.drowsy;
    this.material.color.set(dim > 0 ? mix(colour, '#202028', 0.35 * dim) : colour);
    this.material.roughness = 0.72 - shape.gloss * 0.6;
    this.material.clearcoat = shape.gloss;
    this.material.sheenColor.set(centre);
    this.material.emissive.set(centre);
    this.material.emissiveIntensity = (0.12 - 0.1 * dim) + pose.mood.joy * 0.3 + flash * 0.25;

    this.halo.material.color.set(glow);
    this.halo.material.opacity = haloOpacity * 0.75;
    this.halo.scale.setScalar(RADIUS * 3.4 * shape.size);

    // The pieces: the same surface as the body, moving out of it and back.
    this.pieceMaterial.color.copy(this.material.color);
    this.pieceMaterial.roughness = this.material.roughness;
    this.pieceMaterial.clearcoat = this.material.clearcoat;
    this.pieceMaterial.sheenColor.copy(this.material.sheenColor);
    this.pieceMaterial.emissive.copy(this.material.emissive);
    this.pieceMaterial.emissiveIntensity = this.material.emissiveIntensity;
    this.pieces.forEach((piece, i) => {
      const part = split.pieces[i];
      piece.visible = !!part && part.size > 0.01;
      if (!part) return;
      piece.position.set(part.x, CENTRE_Y + part.y, part.z);
      piece.scale.set(part.size, part.size * (1 - 0.12 * Math.sin(pose.t * 9 + i)), part.size);
    });

    this.sparkles.forEach((s, i) => {
      s.material.color.set(mix(this.glowColour, '#ffffff', 0.5));
      s.material.opacity = sparkle * (0.5 + 0.5 * Math.sin(pose.t * 5 + i * 1.7));
      s.visible = sparkle > 0.01;
    });
  }
}

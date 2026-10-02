import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { VRMExpressionPresetName, VRMLoaderPlugin, VRMUtils, type VRM } from '@pixiv/three-vrm';
import type { AvatarDriver, AvatarState, Emotion, Gesture } from './types';

/** Emotions onto VRM's preset expressions (PLAN.md §5.1). Thinking has no preset; it's a look. */
const PRESETS: Partial<Record<Emotion, VRMExpressionPresetName>> = {
  joy: VRMExpressionPresetName.Happy,
  sad: VRMExpressionPresetName.Sad,
  angry: VRMExpressionPresetName.Angry,
  surprised: VRMExpressionPresetName.Surprised,
  relaxed: VRMExpressionPresetName.Relaxed,
};

const GESTURE_MS: Record<Gesture, number> = { nod: 900, shake: 900, tilt: 1600, wave: 1500, shrug: 1000, laugh: 1400, wink: 750, sigh: 1700, yawn: 2200, excited: 1300 };

/**
 * A VRM model (VRoid Studio exports, or any VRM 0.x/1.0) driven through the same interface as the
 * built-in plush llama. Loaded only when a model is present, so the default build never downloads three.js.
 */
export class VrmAvatar implements AvatarDriver {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(28, 1, 0.1, 20);
  private readonly lookTarget = new THREE.Object3D();
  private readonly clock = new THREE.Clock();
  private readonly resize: ResizeObserver;
  private readonly weights = new Map<string, number>();

  private emotion: Emotion = 'neutral';
  private intensity = 0.5;
  private state: AvatarState = 'idle';
  private mouth = 0;
  private gesture: { name: Gesture; start: number } | null = null;
  private blinkAt = 2.5;
  private frame = 0;

  private readonly container: HTMLElement;
  private readonly vrm: VRM;

  private constructor(container: HTMLElement, vrm: VRM) {
    this.container = container;
    this.vrm = vrm;
    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(2, window.devicePixelRatio));
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.renderer.domElement.className = 'vrm-avatar';
    container.appendChild(this.renderer.domElement);

    const light = new THREE.DirectionalLight(0xffffff, Math.PI * 0.9);
    light.position.set(1, 1.5, 2);
    this.scene.add(light, new THREE.AmbientLight(0xffffff, Math.PI * 0.4));
    this.scene.add(vrm.scene);

    // Frame head and shoulders.
    const head = vrm.humanoid.getNormalizedBoneNode('head');
    const headY = head ? head.getWorldPosition(new THREE.Vector3()).y : 1.4;
    this.camera.position.set(0, headY - 0.05, 1.25);
    this.camera.lookAt(0, headY - 0.12, 0);

    this.lookTarget.position.set(0, headY, 1.25);
    this.scene.add(this.lookTarget);
    if (vrm.lookAt) vrm.lookAt.target = this.lookTarget;

    // Arms down from the T-pose (the model faces +Z, so its left arm lies along +X).
    vrm.humanoid.getNormalizedBoneNode('leftUpperArm')?.rotation.set(0, 0, -1.2);
    vrm.humanoid.getNormalizedBoneNode('rightUpperArm')?.rotation.set(0, 0, 1.2);

    this.resize = new ResizeObserver(() => this.fit());
    this.resize.observe(container);
    this.fit();
    this.frame = requestAnimationFrame(this.tick);
  }

  static async load(container: HTMLElement, url: string): Promise<VrmAvatar> {
    const loader = new GLTFLoader();
    loader.register((parser) => new VRMLoaderPlugin(parser));
    const gltf = await loader.loadAsync(url);
    const vrm = gltf.userData.vrm as VRM | undefined;
    if (!vrm) throw new Error(`${url} is not a VRM model`);

    VRMUtils.removeUnnecessaryVertices(gltf.scene);
    VRMUtils.combineSkeletons(gltf.scene);
    VRMUtils.rotateVRM0(vrm);
    return new VrmAvatar(container, vrm);
  }

  setState(state: AvatarState): void {
    this.state = state;
  }

  setExpression(emotion: Emotion, intensity: number): void {
    this.emotion = emotion;
    this.intensity = intensity;
  }

  playGesture(gesture: Gesture): void {
    this.gesture = { name: gesture, start: this.clock.elapsedTime };
  }

  setMouthOpen(level: number): void {
    this.mouth = Math.max(this.mouth, Math.min(1, level));
  }

  dispose(): void {
    cancelAnimationFrame(this.frame);
    this.resize.disconnect();
    VRMUtils.deepDispose(this.vrm.scene);
    this.renderer.dispose();
    this.renderer.domElement.remove();
  }

  private fit() {
    const { clientWidth: w, clientHeight: h } = this.container;
    if (w === 0 || h === 0) return;
    this.renderer.setSize(w, h, false);
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
  }

  private ease(name: string, target: number, dt: number) {
    const current = this.weights.get(name) ?? 0;
    const next = current + (target - current) * (1 - Math.exp(-dt * 8));
    this.weights.set(name, next);
    this.vrm.expressionManager?.setValue(name, next);
  }

  private readonly tick = () => {
    const dt = Math.min(0.1, this.clock.getDelta());
    const t = this.clock.elapsedTime;
    const vrm = this.vrm;

    const emotion: Emotion = this.state === 'thinking' ? 'thinking' : this.emotion;
    for (const [name, preset] of Object.entries(PRESETS)) {
      this.ease(preset, name === emotion ? this.intensity : 0, dt);
    }

    // Blink, unless resting (eyes closed) or a happy preset is already closing them.
    let blink = this.state === 'resting' ? 1 : 0;
    if (t > this.blinkAt) {
      const into = t - this.blinkAt;
      blink = Math.max(blink, into < 0.08 ? into / 0.08 : into < 0.16 ? 1 - (into - 0.08) / 0.08 : 0);
      if (into >= 0.16) this.blinkAt = t + 2.2 + Math.random() * 4;
    }
    vrm.expressionManager?.setValue(VRMExpressionPresetName.Blink, emotion === 'joy' ? 0 : blink);

    this.mouth *= Math.exp(-dt * 9);
    this.ease(VRMExpressionPresetName.Aa, this.mouth * 0.8, dt * 3);

    // Gaze: at the viewer, or up and away while thinking.
    const thinking = emotion === 'thinking';
    this.lookTarget.position.x += ((thinking ? 0.5 : 0) - this.lookTarget.position.x) * (1 - Math.exp(-dt * 4));
    this.lookTarget.position.y += ((this.camera.position.y + (thinking ? 0.4 : 0)) - this.lookTarget.position.y) * (1 - Math.exp(-dt * 4));

    // Breathing and gestures on the spine and head.
    const spine = vrm.humanoid.getNormalizedBoneNode('spine');
    if (spine) spine.rotation.x = Math.sin(t * Math.PI * 2 / 3.6) * 0.02;

    const head = vrm.humanoid.getNormalizedBoneNode('head');
    const rightArm = vrm.humanoid.getNormalizedBoneNode('rightUpperArm');
    if (head) head.rotation.set(0, 0, thinking ? 0.08 : 0);
    if (rightArm) rightArm.rotation.set(0, 0, 1.2);

    if (this.gesture) {
      const u = (t - this.gesture.start) / (GESTURE_MS[this.gesture.name] / 1000);
      if (u >= 1) {
        this.gesture = null;
      } else {
        const env = Math.sin(Math.PI * u);
        switch (this.gesture.name) {
          case 'nod': if (head) head.rotation.x = Math.sin(u * Math.PI * 4) * 0.18; break;
          case 'shake': if (head) head.rotation.y = Math.sin(u * Math.PI * 6) * 0.25 * env; break;
          case 'tilt': if (head) head.rotation.z = 0.22 * env; break;
          case 'wave':
            if (rightArm) rightArm.rotation.set(0, 0, 1.2 - 2.3 * env + Math.sin(u * Math.PI * 8) * 0.25 * env);
            break;
          case 'shrug': if (head) head.rotation.x = -0.1 * env; break;
          case 'wink':
            vrm.expressionManager?.setValue(VRMExpressionPresetName.BlinkRight, env);
            if (head) head.rotation.z = 0.08 * env;
            break;
          case 'sigh':
            if (head) head.rotation.x = 0.15 * env;
            if (spine) spine.rotation.x = 0.05 * env;
            break;
          case 'yawn':
            if (head) head.rotation.x = -0.18 * env;
            vrm.expressionManager?.setValue(VRMExpressionPresetName.Aa, env);
            vrm.expressionManager?.setValue(VRMExpressionPresetName.Blink, 0.8 * env);
            break;
          case 'excited':
            if (head) head.rotation.x = Math.sin(u * Math.PI * 4) * 0.12 * env;
            vrm.expressionManager?.setValue(VRMExpressionPresetName.Happy, Math.max(this.weights.get(VRMExpressionPresetName.Happy) ?? 0, env));
            break;
          case 'laugh':
            if (head) head.rotation.x = -0.08 * env + Math.sin(u * Math.PI * 7) * 0.05 * env;
            vrm.expressionManager?.setValue(VRMExpressionPresetName.Happy, Math.max(this.weights.get(VRMExpressionPresetName.Happy) ?? 0, env));
            vrm.expressionManager?.setValue(VRMExpressionPresetName.Aa, 0.4 * env * Math.abs(Math.sin(u * Math.PI * 7)));
            break;
        }
      }
    }

    vrm.update(dt);
    this.renderer.render(this.scene, this.camera);
    this.frame = requestAnimationFrame(this.tick);
  };
}

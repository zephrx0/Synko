export const motion = Object.freeze({
  spatialFast: { stiffness: 800, damping: .8 },
  spatialDefault: { stiffness: 380, damping: .9 },
  spatialSlow: { stiffness: 200, damping: .8 },
  effectsFast: { stiffness: 3800, damping: 1 },
  effectsDefault: { stiffness: 1600, damping: 1 },
  effectsSlow: { stiffness: 800, damping: 1 },
});

export const reducedMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches || window.synkoEnvironment?.reducedMotion === true;

export class Spring {
  constructor(value = 0, scheme = motion.spatialDefault) { this.value = value; this.target = value; this.velocity = 0; this.scheme = scheme; }
  get settled() { return Math.abs(this.target - this.value) < .0008 && Math.abs(this.velocity) < .0008; }
  snap(value) { this.value = value; this.target = value; this.velocity = 0; }
  step(elapsed) {
    if (reducedMotion() && this.scheme.damping < 1) { this.snap(this.target); return this.value; }
    const { stiffness, damping } = this.scheme;
    let remaining = Math.min(elapsed, .064);
    while (remaining > 0) {
      const dt = Math.min(remaining, 1 / 240);
      this.velocity += (stiffness * (this.target - this.value) - 2 * damping * Math.sqrt(stiffness) * this.velocity) * dt;
      this.value += this.velocity * dt;
      remaining -= dt;
    }
    if (this.settled) this.snap(this.target);
    return this.value;
  }
}

const animations = new Map();
let frame = 0;
let previous = 0;
let visible = true;

function tick(now) {
  frame = 0;
  if (!visible || document.hidden) return;
  const dt = previous ? Math.min((now - previous) / 1000, .064) : 1 / 60;
  previous = now;
  for (const [key, callback] of animations) if (callback(dt, now) === false) animations.delete(key);
  if (animations.size) frame = requestAnimationFrame(tick);
}

export function animate(key, callback) {
  animations.set(key, callback);
  if (!frame && visible && !document.hidden) { previous = 0; frame = requestAnimationFrame(tick); }
}

export function stopAnimation(key) { animations.delete(key); if (!animations.size && frame) { cancelAnimationFrame(frame); frame = 0; } }

export function setAnimationVisibility(value) {
  visible = value;
  if (!value && frame) { cancelAnimationFrame(frame); frame = 0; }
  if (value && animations.size && !frame) { previous = 0; frame = requestAnimationFrame(tick); }
}

document.addEventListener("visibilitychange", () => setAnimationVisibility(!document.hidden));
export function animationCount() { return frame ? animations.size : 0; }

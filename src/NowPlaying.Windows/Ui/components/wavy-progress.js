import { Spring, motion, animate, stopAnimation, reducedMotion } from "../motion.js";

export class WavyProgress {
  constructor(canvas, changed) {
    this.canvas = canvas;
    this.changed = changed;
    this.amplitude = new Spring(0, motion.spatialDefault);
    this.position = new Spring(0, motion.spatialDefault);
    this.phase = 0;
    this.media = null;
    this.active = true;
    this.palette = null;
    this.frames = 0;
    this.resize = new ResizeObserver(() => this.draw());
    this.resize.observe(canvas);
    this.paletteHandler = event => { this.palette = event.detail; this.draw(); };
    document.addEventListener("synko:palette", this.paletteHandler);
    this.visibilityHandler = event => { this.active = event.detail; if (this.active) this.run(); };
    document.addEventListener("synko:visibility", this.visibilityHandler);
  }

  setMedia(media) {
    const changed = this.media?.key !== media?.key;
    this.media = media;
    this.amplitude.target = media?.playing ? 3 : 0;
    const position = this.currentPosition();
    const fraction = media?.duration > 0 ? Math.min(1, position / media.duration) : 0;
    if (changed) this.position.snap(fraction);
    else this.position.target = fraction;
    this.draw();
    this.run();
  }

  currentPosition() {
    if (!this.media) return 0;
    const elapsed = this.media.playing ? Math.max(0, (Date.now() - this.media.observedAt) / 1000) * this.media.rate : 0;
    const value = this.media.position + elapsed;
    return this.media.duration > 0 ? Math.min(this.media.duration, value) : value;
  }

  run() {
    if (!this.active) return;
    animate(this, dt => {
      this.frames++;
      if (this.media?.playing && !reducedMotion()) this.phase += dt * Math.PI * 2 / 1.75;
      this.amplitude.step(dt);
      const elapsed = this.currentPosition();
      this.position.target = this.media?.duration > 0 ? Math.min(1, elapsed / this.media.duration) : 0;
      this.position.step(dt);
      this.changed(elapsed, this.media?.duration ?? 0);
      this.draw();
      return this.media?.playing || !this.amplitude.settled || !this.position.settled;
    });
  }

  draw() {
    if (!this.active || document.hidden) return;
    const width = this.canvas.clientWidth, height = 24, dpr = devicePixelRatio;
    if (!width) return;
    const pixelWidth = Math.round(width * dpr), pixelHeight = Math.round(height * dpr);
    if (this.canvas.width !== pixelWidth || this.canvas.height !== pixelHeight) { this.canvas.width = pixelWidth; this.canvas.height = pixelHeight; }
    const context = this.canvas.getContext("2d");
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
    context.clearRect(0, 0, width, height);
    const forcedColors = window.synkoEnvironment?.highContrast || matchMedia("(forced-colors: active)").matches;
    const color = forcedColors ? getComputedStyle(this.canvas).color
      : this.palette?.onPrimaryContainer ?? getComputedStyle(this.canvas).getPropertyValue("--md-sys-color-on-primary-container").trim();
    context.strokeStyle = color;
    context.lineWidth = 4;
    context.lineCap = "round";
    if (!this.media) {
      context.globalAlpha = .24;
      context.beginPath(); context.moveTo(2, 12); context.lineTo(width - 2, 12); context.stroke();
      context.globalAlpha = 1;
      return;
    }
    const stream = this.media && this.media.duration <= 0;
    const head = 2 + Math.max(0, Math.min(1, this.position.value)) * (width - 4);
    const waveEnd = stream || this.position.value >= 1 ? width - 2 : Math.max(2, head - 4);
    if (stream || waveEnd > 2) {
      context.beginPath();
      for (let x = 2; x <= waveEnd; x += .5) {
        const taper = Math.min(1, (x - 2) / 12, (waveEnd - x) / 12);
        const y = 12 + Math.sin((x - 2) / 40 * Math.PI * 2 - this.phase) * this.amplitude.value * taper;
        if (x === 2) context.moveTo(x, y); else context.lineTo(x, y);
      }
      context.stroke();
    }
    if (!stream) {
      context.globalAlpha = .24;
      if (head + 6 < width - 2) { context.beginPath(); context.moveTo(head + 6, 12); context.lineTo(width - 2, 12); context.stroke(); }
      context.globalAlpha = 1;
    }
  }

  diagnostics() { return { frames: this.frames, amplitude: this.amplitude.value, phase: this.phase, fraction: this.position.value, width: this.canvas.width, height: this.canvas.height }; }
  dispose() { stopAnimation(this); this.resize.disconnect(); document.removeEventListener("synko:palette", this.paletteHandler); document.removeEventListener("synko:visibility", this.visibilityHandler); }
}

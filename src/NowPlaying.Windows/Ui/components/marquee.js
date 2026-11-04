import { Spring, motion, animate, stopAnimation, reducedMotion } from "../motion.js";

export class Marquee {
  constructor(element) {
    this.element = element;
    this.line = document.createElement("span"); this.line.className = "marquee-line";
    this.text = document.createElement("span"); this.mirror = document.createElement("span");
    this.mirror.setAttribute("aria-hidden", "true");
    this.line.append(this.text, this.mirror); element.append(this.line);
    this.speed = new Spring(0, motion.spatialSlow);
    this.offset = 0;
    this.delay = 0;
    this.observer = new ResizeObserver(() => this.measure());
    this.observer.observe(element);
  }
  setText(value) {
    if (this.text.textContent === value) return;
    stopAnimation(this); this.offset = 0; this.delay = 2400; this.speed.snap(0);
    this.text.textContent = value; this.mirror.textContent = value;
    this.element.setAttribute("aria-label", value);
    this.element.title = value;
    this.line.style.transform = "translateX(0)";
    this.measure();
  }
  measure() {
    const overflow = this.text.scrollWidth > this.element.clientWidth;
    this.mirror.hidden = !overflow;
    if (!overflow || reducedMotion()) { stopAnimation(this); this.line.style.transform = "translateX(0)"; return; }
    this.speed.target = 16;
    animate(this, dt => {
      if (this.delay > 0) { this.delay -= dt * 1000; return true; }
      const period = this.text.scrollWidth + 32;
      const next = this.offset + this.speed.step(dt) * dt;
      if (next >= period) { this.offset = 0; this.delay = 1600; this.speed.snap(0); this.speed.target = 16; }
      else this.offset = next;
      this.line.style.transform = `translateX(${-this.offset}px)`;
      return this.element.isConnected;
    });
  }
  dispose() { this.observer.disconnect(); stopAnimation(this); }
}

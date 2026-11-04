import { Spring, motion, animate } from "../motion.js";

export function interactive(element, radius = 24) {
  const shape = new Spring(radius, motion.spatialFast);
  const state = new Spring(0, motion.effectsFast);
  let hovered = false, focused = false, pressed = false;
  const key = Symbol("interaction");
  function update() {
    shape.target = pressed ? 16 : radius;
    state.target = pressed || focused ? .1 : hovered ? .08 : 0;
    animate(key, dt => {
      element.style.setProperty("--press-radius", shape.step(dt) + "px");
      element.style.setProperty("--state-opacity", state.step(dt));
      return !shape.settled || !state.settled;
    });
  }
  element.addEventListener("pointerenter", () => { if (!element.disabled) { hovered = true; update(); } });
  element.addEventListener("pointerleave", () => { hovered = false; update(); });
  element.addEventListener("focus", () => { focused = element.matches(":focus-visible"); update(); });
  element.addEventListener("blur", () => { focused = false; pressed = false; update(); });
  element.addEventListener("pointerdown", event => {
    if (element.disabled || event.button !== 0) return;
    pressed = true; update();
    const bounds = element.getBoundingClientRect();
    const size = Math.max(bounds.width, bounds.height) * 2;
    const ripple = document.createElement("span");
    ripple.className = "ripple";
    ripple.style.cssText = `width:${size}px;height:${size}px;left:${event.clientX - bounds.left - size / 2}px;top:${event.clientY - bounds.top - size / 2}px`;
    const expand = new Spring(0, motion.effectsDefault), fade = new Spring(.1, motion.effectsDefault);
    expand.target = 1;
    element.append(ripple);
    const release = () => {
      pressed = false; update(); fade.target = 0;
      window.removeEventListener("pointerup", release);
      element.removeEventListener("pointercancel", release);
    };
    window.addEventListener("pointerup", release, { once: true });
    element.addEventListener("pointercancel", release, { once: true });
    animate(ripple, dt => {
      ripple.style.transform = `scale(${expand.step(dt)})`;
      ripple.style.opacity = fade.step(dt);
      if (!ripple.isConnected || (fade.target === 0 && fade.settled)) { ripple.remove(); return false; }
      return true;
    });
  });
  element.addEventListener("keydown", event => { if (event.key === " " || event.key === "Enter") { pressed = true; update(); } });
  element.addEventListener("keyup", () => { pressed = false; update(); });
  return element;
}

export function symbol(name, small = false) {
  const icon = document.createElement("span");
  if (name.startsWith("browser:")) {
    const browser = name.slice(8);
    icon.className = "browser-glyph" + (small ? " small" : "");
    icon.setAttribute("aria-hidden", "true");
    const image = document.createElement("img");
    image.src = "./assets/" + browser + (["yandex", "waterfox"].includes(browser) ? ".png" : ".svg"); image.alt = "";
    icon.append(image); return icon;
  }
  const brand = { apple_logo: "applemusic", discord_logo: "discord", youtube_logo: "youtube" }[name];
  if (brand) {
    icon.className = "brand-glyph " + brand + (small ? " small" : "");
    icon.setAttribute("aria-hidden", "true");
    return icon;
  }
  icon.className = "symbol" + (small ? " small" : "");
  icon.setAttribute("aria-hidden", "true");
  icon.textContent = name;
  return icon;
}

const iconSprings = new WeakMap();
export function setIconFill(icon, selected, immediate = false) {
  let spring = iconSprings.get(icon);
  if (!spring) { spring = new Spring(selected ? 1 : 0, motion.effectsFast); iconSprings.set(icon, spring); }
  spring.target = selected ? 1 : 0;
  if (immediate) spring.snap(spring.target);
  icon.style.setProperty("--icon-fill", spring.value);
  if (!spring.settled) animate(icon, dt => {
    icon.style.setProperty("--icon-fill", Math.max(0, Math.min(1, spring.step(dt))));
    return icon.isConnected && !spring.settled;
  });
}

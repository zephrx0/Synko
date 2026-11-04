import { Spring, motion, animate } from "../motion.js";
import { interactive, symbol, setIconFill } from "./interaction.js";

export function toggle(label, checked, changed) {
  const element = document.createElement("button");
  element.type = "button";
  element.className = "switch";
  element.setAttribute("role", "switch");
  element.setAttribute("aria-label", label);
  element.innerHTML = '<span class="switch-track"><span class="switch-thumb"><span class="symbol" aria-hidden="true">check</span></span></span>';
  const value = new Spring(checked ? 1 : 0, motion.spatialFast);
  function paint() {
    element.style.setProperty("--thumb-x", value.value * 20 + "px");
    element.style.setProperty("--thumb-scale", .6667 + value.value / 3);
    element.style.setProperty("--thumb-icon-opacity", Math.max(0, Math.min(1, value.value)));
  }
  element.setValue = (next, immediate = false) => {
    if (immediate && element.getAttribute("aria-checked") === String(next)) return;
    element.setAttribute("aria-checked", String(next));
    if (immediate) value.snap(next ? 1 : 0);
    value.target = next ? 1 : 0;
    paint();
    if (!immediate) animate(element, dt => { value.step(dt); paint(); return !value.settled; });
  };
  element.setValue(checked, true);
  element.addEventListener("click", () => { const next = element.getAttribute("aria-checked") !== "true"; element.setValue(next); changed(next); });
  return interactive(element, 20);
}

export function sourceChip(label, icon, selected, action) {
  const element = document.createElement("button");
  element.type = "button";
  element.className = "source-chip";
  const glyph = symbol(icon, true), caption = document.createElement("span");
  element.append(glyph, caption);
  element.setState = (text, active, immediate = false) => {
    if (caption.textContent !== text) caption.textContent = text;
    element.setAttribute("aria-pressed", String(active));
    setIconFill(glyph, active, immediate);
  };
  element.setState(label, selected, true);
  element.addEventListener("click", action);
  return interactive(element, 24);
}

export function segmentedGroup(label, choices, selected, changed) {
  const group = document.createElement("div");
  group.className = "segmented-group";
  group.setAttribute("role", "radiogroup");
  group.setAttribute("aria-label", label);
  function select(value, notify) {
    for (const child of group.children) {
      const active = child.dataset.value === value;
      child.setAttribute("aria-checked", String(active));
      child.tabIndex = active ? 0 : -1;
      child.querySelector(".symbol").hidden = !active;
      setIconFill(child.querySelector(".symbol"), active);
    }
    if (notify) changed(value);
  }
  for (const choice of choices) {
    const item = document.createElement("button");
    item.type = "button"; item.className = "segment"; item.dataset.value = choice.value;
    item.setAttribute("role", "radio");
    item.append(symbol("check", true), document.createTextNode(choice.label));
    item.addEventListener("click", () => select(choice.value, true));
    item.addEventListener("keydown", event => {
      if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
      event.preventDefault();
      const index = [...group.children].indexOf(item);
      const next = event.key === "Home" ? 0 : event.key === "End" ? choices.length - 1 : (index + (event.key === "ArrowRight" ? 1 : -1) + choices.length) % choices.length;
      select(choices[next].value, true); group.children[next].focus();
    });
    group.append(interactive(item, 20));
  }
  group.setValue = value => select(value, false);
  select(selected, false);
  return group;
}

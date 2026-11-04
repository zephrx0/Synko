import { interactive, symbol } from "./interaction.js";

export function button(label, { icon = null, variant = "text", action = null, disabled = false } = {}) {
  const element = document.createElement("button");
  element.type = "button";
  element.className = "md-button " + variant;
  element.disabled = disabled;
  if (icon) element.append(symbol(icon));
  element.append(document.createTextNode(label));
  if (action) element.addEventListener("click", action);
  return interactive(element, 20);
}

export function iconButton(icon, label, action) {
  const element = document.createElement("button");
  element.type = "button";
  element.className = "icon-button quiet";
  element.setAttribute("aria-label", label);
  element.title = label;
  element.append(symbol(icon));
  element.addEventListener("click", action);
  return interactive(element, 20);
}

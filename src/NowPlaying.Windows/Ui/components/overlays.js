import { button } from "./buttons.js";
import { Spring, motion, animate } from "../motion.js";

export function showDialog(message) {
  const dialog = document.createElement("dialog");
  dialog.className = "md-dialog";
  const text = document.createElement("p"), actions = document.createElement("div");
  text.id = "dialog-message"; text.textContent = message;
  dialog.setAttribute("aria-describedby", text.id);
  actions.className = "dialog-actions";
  actions.append(button("Понятно", { action: () => dialog.close() }));
  dialog.append(text, actions);
  document.body.append(dialog);
  dialog.addEventListener("close", () => dialog.remove(), { once: true });
  dialog.showModal();
  return dialog;
}

export function showSnackbar(message) {
  document.querySelector(".snackbar")?.remove();
  const element = document.createElement("div");
  element.className = "snackbar";
  element.setAttribute("role", "status");
  element.textContent = message;
  document.body.append(element);
  const entrance = new Spring(0, motion.spatialDefault);
  entrance.target = 1;
  animate(element, dt => {
    const progress = entrance.step(dt);
    element.style.transform = `translateY(${16 * (1 - progress)}px)`;
    element.style.opacity = Math.max(0, Math.min(1, progress));
    return !entrance.settled;
  });
  const timeout = setTimeout(() => element.remove(), 4000);
  element.addEventListener("click", () => { clearTimeout(timeout); element.remove(); });
  return element;
}

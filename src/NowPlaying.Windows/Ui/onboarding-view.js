import { logo, sourceIcon } from "./brand.js";
import { button } from "./components/buttons.js";
import { symbol } from "./components/interaction.js";
import { DiscordFields, AppearanceControls, switchRow } from "./settings-view.js";
import { WavyProgress } from "./components/wavy-progress.js";
import { Spring, animate, motion, stopAnimation } from "./motion.js";

export class OnboardingView {
  constructor(root, command, state) {
    this.root = root; this.command = command; this.state = state;
    this.step = Math.max(0, Math.min(4, state?.preferences?.onboardingStep || 0)); this.render();
    this.keys = event => {
      if (document.querySelector('dialog[open]')) return;
      if (event.key === "Escape" && this.step > 0) { event.preventDefault(); this.go(-1); }
      if (event.key === "Enter" && !["BUTTON"].includes(event.target.tagName) && !this.next.disabled) { event.preventDefault(); this.go(1); }
    };
    document.addEventListener("keydown", this.keys);
  }
  change(key, value) { this.command({ type: "preference", key, value }); }
  go(direction) {
    if (direction > 0 && this.step === 4) {
      this.change("onboardingDone", true); this.command({ type: "navigate", view: "main" }); return;
    }
    if (direction > 0 && this.next.disabled) return;
    this.step = Math.max(0, Math.min(4, this.step + direction)); this.render(direction); this.change("onboardingStep", this.step);
  }
  render(direction = 1) {
    this.id?.dispose(); this.id = null; this.wave?.dispose(); this.wave = null; stopAnimation(this);
    this.root.replaceChildren(); this.root.className = "app-shell onboarding-view";
    const progress = document.createElement("div"); progress.className = "step-progress"; progress.setAttribute("aria-label", `${this.step + 1} / 5`);
    for (let i = 0; i < 5; i++) { const pill = document.createElement("span"); pill.className = i === this.step ? "current" : i < this.step ? "done" : ""; progress.append(pill); }
    const content = document.createElement("section"); content.className = "onboarding-content";
    const p = this.state?.preferences || {};
    const heading = text => { const h = document.createElement("h1"); h.textContent = text; content.append(h); };
    if (this.step === 0) {
      content.classList.add("welcome"); const mark = document.createElement("div"); mark.className = "welcome-logo"; mark.innerHTML = logo; content.append(mark);
      heading("Synko"); const text = document.createElement("p"); text.textContent = "Ваша музыка в статусе Discord"; content.append(text);
      this.addWave(content, null);
    }
    if (this.step === 1) {
      heading("Подключите Discord");
      const list = document.createElement("ol"); list.className = "setup-instructions";
      for (const text of ["Создайте приложение в Developer Portal", "Назовите его так, как хотите видеть в статусе", "Скопируйте Application ID"]) { const item = document.createElement("li"); item.textContent = text; list.append(item); }
      content.append(list, button("Открыть Developer Portal", { icon: "open_in_new", action: () => this.command({ type: "portal" }) }));
      this.id = new DiscordFields(content, p, this.command); this.id.changed = () => this.updateNext();
    }
    if (this.step === 2) {
      heading("Откуда брать музыку"); const choices = document.createElement("div"); choices.className = "source-choices";
      for (const [value, name, source] of [[0, "Авто", "auto"], [1, "Apple Music", "apple"], [2, "Браузер", "browser"]]) {
        const item = document.createElement("button"); item.className = "source-choice"; item.type = "button"; item.dataset.mode = String(value);
        const text = document.createElement("span"); const label = document.createElement("span"); label.className = "choice-label"; label.textContent = name;
        const status = document.createElement("span"); status.className = "choice-status"; status.dataset.source = source; text.append(label, status);
        item.append(symbol(source === "auto" ? "headphones" : sourceIcon(source)), text, symbol("check"));
        item.addEventListener("click", () => this.change("sourceMode", value)); choices.append(item);
      }
      content.append(choices);
    }
    if (this.step === 3) {
      heading("Внешний вид"); this.appearance = new AppearanceControls(content, p, (key, value) => this.change(key, value));
      this.apple = switchRow(content, "Обложки Apple Music", p.automaticArtwork ?? true, value => this.change("automaticArtwork", value));
      this.youtube = switchRow(content, "Превью YouTube", p.youTubeThumbnails ?? true, value => this.change("youTubeThumbnails", value));
      this.startup = switchRow(content, "Запускать вместе с Windows", p.startup || false, enabled => this.command({ type: "startup", enabled }));
    }
    if (this.step === 4) {
      heading("Всё готово");
      const mini = document.createElement("div"); mini.className = "setup-player";
      const title = document.createElement("h2"); title.textContent = this.state?.media?.title || "Ничего не играет";
      const artist = document.createElement("p"); artist.textContent = this.state?.media?.artist || ""; mini.append(title, artist);
      this.addWave(mini, this.state?.media); content.append(mini);
      const text = document.createElement("p"); text.className = "tray-hint"; text.textContent = "Закрытие окна сворачивает Synko в трей"; content.append(text);
    }
    const footer = document.createElement("footer"); footer.className = "setup-footer";
    if (this.step > 0) footer.append(button("Назад", { action: () => this.go(-1) }));
    this.next = button(this.step === 0 ? "Начать" : this.step === 4 ? "Открыть Synko" : "Далее", { variant: "filled", action: () => this.go(1) }); footer.append(this.next);
    this.root.append(progress, content, footer); this.update(this.state);
    const spring = new Spring(0, motion.effectsDefault); spring.target = 1;
    animate(this, dt => { const amount = spring.step(dt); content.style.opacity = amount; content.style.transform = `translateX(${direction * 12 * (1 - amount)}px)`; return !spring.settled; });
  }
  addWave(root, media) {
    const canvas = document.createElement("canvas"); canvas.className = "wavy-progress"; canvas.setAttribute("aria-hidden", "true"); root.append(canvas);
    this.wave = new WavyProgress(canvas, () => {}); this.wave.setMedia(media);
  }
  updateNext() { if (this.next) this.next.disabled = this.step === 1 && !this.id?.valid; }
  update(state) {
    this.state = state; const p = state?.preferences || {};
    if (this.step === 2) {
      for (const choice of this.root.querySelectorAll(".source-choice")) choice.setAttribute("aria-pressed", String(Number(choice.dataset.mode) === (p.sourceMode || 0)));
      for (const status of this.root.querySelectorAll(".choice-status")) {
        const found = state?.sources?.some(s => status.dataset.source === "auto" || (status.dataset.source === "browser" ? s.id.startsWith("browser:") : s.id === status.dataset.source));
        status.textContent = found ? "найдено" : "не найдено";
      }
    }
    if (this.step === 3) {
      this.appearance.update(p); this.apple.setValue(p.automaticArtwork ?? true, true); this.youtube.setValue(p.youTubeThumbnails ?? true, true); this.startup.setValue(p.startup || false, true);
    }
    this.updateNext();
  }
  receive(message) { this.id?.receive(message); }
  dispose() { this.id?.dispose(); this.wave?.dispose(); stopAnimation(this); document.removeEventListener("keydown", this.keys); }
}

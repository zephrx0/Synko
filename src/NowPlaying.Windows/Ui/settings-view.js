import { iconButton, button } from "./components/buttons.js";
import { symbol } from "./components/interaction.js";
import { toggle, segmentedGroup } from "./components/selection.js";
import { textField } from "./components/fields.js";
import { showDialog } from "./components/overlays.js";

export function section(root, title, icon) {
  const card = document.createElement("section"); card.className = "settings-section";
  const header = document.createElement("h2"); header.className = "section-heading";
  if (icon) header.append(symbol(icon));
  header.append(document.createTextNode(title)); card.append(header); root.append(card);
  return card;
}

export function switchRow(root, label, value, changed, info = null) {
  const row = document.createElement("div"); row.className = "settings-row";
  const caption = document.createElement("span"); caption.className = "row-label"; caption.textContent = label;
  row.append(caption);
  if (info) row.append(iconButton("info", label, () => showDialog(info)));
  const control = toggle(label, value, changed); row.append(control); root.append(row); return control;
}

export class DiscordFields {
  constructor(root, preferences, command) {
    this.command = command; this.value = preferences.applicationId || ""; this.valid = /^\d{17,20}$/.test(this.value);
    const row = document.createElement("div"); row.className = "id-row";
    this.field = textField("Application ID", this.value, value => this.setValue(value));
    this.field.input.inputMode = "numeric"; this.field.input.maxLength = 20;
    row.append(this.field, button("Вставить", { action: () => command({ type: "paste" }) }));
    this.status = document.createElement("div"); this.status.className = "id-status"; this.status.setAttribute("aria-live", "polite");
    root.append(row, this.status);
  }
  setValue(value) {
    this.value = value.replace(/\D/g, "").slice(0, 20); this.field.input.value = this.value;
    this.valid = false; this.field.setInvalid(this.value.length > 0 && this.value.length < 17);
    this.status.textContent = this.value && this.value.length < 17 ? "Неверный ID" : "";
    this.changed?.(); clearTimeout(this.timer);
    if (this.value.length >= 17) this.timer = setTimeout(() => this.command({ type: "applicationId", value: this.value }), 500);
  }
  receive(message) {
    if (message.type === "clipboard") this.setValue(message.value);
    if (message.type === "validation" && message.id === this.value) {
      this.valid = message.valid;
      this.status.textContent = message.status;
      this.status.classList.toggle("checking", message.status === "Проверяем");
      this.field.setInvalid(message.status === "Неверный ID"); this.changed?.();
      clearTimeout(this.retry);
      if (message.status === "Запустите Discord") this.retry = setTimeout(() => {
        if (!document.hidden) this.command({ type: "applicationId", value: this.value });
      }, 3000);
    }
  }
  dispose() { clearTimeout(this.timer); clearTimeout(this.retry); }
}

export class AppearanceControls {
  constructor(root, preferences, change) {
    this.change = change;
    const themeLabel = document.createElement("div"); themeLabel.className = "control-label"; themeLabel.textContent = "Тема";
    this.theme = segmentedGroup("Тема", [{ value: "system", label: "Системная" }, { value: "light", label: "Светлая" }, { value: "dark", label: "Тёмная" }], preferences.themeMode || "system", value => change("themeMode", value));
    const colorLabel = document.createElement("div"); colorLabel.className = "control-label"; colorLabel.textContent = "Цвет";
    this.color = segmentedGroup("Цвет", [{ value: "artwork", label: "Из обложки" }, { value: "windows", label: "Windows" }], preferences.colorSource || "artwork", value => change("colorSource", value));
    this.swatches = document.createElement("div"); this.swatches.className = "color-swatches";
    for (const seed of ["#8060ad", "#426894", "#347b74", "#697940", "#966b3a", "#a55869"]) {
      const swatch = document.createElement("button"); swatch.type = "button"; swatch.className = "color-swatch";
      swatch.style.setProperty("--swatch", seed); swatch.dataset.seed = seed;
      swatch.setAttribute("aria-label", "Цвет " + seed); swatch.setAttribute("aria-pressed", "false");
      swatch.append(symbol("check", true)); swatch.addEventListener("click", () => change("customSeed", seed)); this.swatches.append(swatch);
    }
    root.append(themeLabel, this.theme, colorLabel, this.color, this.swatches); this.update(preferences);
  }
  update(preferences) {
    this.theme.setValue(preferences.themeMode || "system");
    this.color.setValue(preferences.colorSource || "artwork");
    for (const swatch of this.swatches.children) swatch.setAttribute("aria-pressed", String(preferences.colorSource === "custom" && preferences.customSeed === swatch.dataset.seed));
  }
}

export class SettingsView {
  constructor(root, command, state) {
    this.command = command; this.state = state; const p = state?.preferences || {};
    root.className = "app-shell settings-view";
    const header = document.createElement("header"); header.className = "topbar page-header";
    const title = document.createElement("h1"); title.textContent = "Настройки";
    header.append(iconButton("arrow_back", "Назад", () => command({ type: "navigate", view: "main" })), title); root.append(header);
    const discord = section(root, "Discord", "discord_logo");
    this.id = new DiscordFields(discord, p, command);
    discord.append(button("Developer Portal", { icon: "open_in_new", action: () => command({ type: "portal" }) }));
    this.publication = switchRow(discord, "Показывать в Discord", state?.enabled ?? true, enabled => command({ type: "publication", enabled }));
    const sources = section(root, "Источник", "headphones");
    this.source = segmentedGroup("Источник", [{ value: "0", label: "Авто" }, { value: "1", label: "Apple Music" }, { value: "4", label: "Spotify" }, { value: "2", label: "Браузер" }], String(p.sourceMode || 0), value => this.change("sourceMode", Number(value)));
    this.source.classList.add("music-source-group");
    sources.append(this.source);
    this.allPlayers = switchRow(sources, "Все медиаплееры Windows", p.sourceMode === 3, enabled => this.change("sourceMode", enabled ? 3 : 0));
    const covers = section(root, "Обложки", "album");
    this.apple = switchRow(covers, "Обложки музыки", p.automaticArtwork ?? true, value => this.change("automaticArtwork", value), "Для Apple Music и Spotify картинка берётся из медиапанели Windows. Чтобы найти публичную обложку для Discord, название трека, исполнитель и альбом отправляются в каталог Apple");
    this.youtube = switchRow(covers, "Превью YouTube", p.youTubeThumbnails ?? true, value => this.change("youTubeThumbnails", value), "Ссылка на ролик берётся из адресной строки YouTube и передаётся в Discord как превью. История браузера не читается");
    const appearance = section(root, "Внешний вид", "palette"); this.appearance = new AppearanceControls(appearance, p, (key, value) => this.change(key, value));
    const system = section(root, "Система", "desktop_windows");
    this.startup = switchRow(system, "Запускать вместе с Windows", p.startup || false, enabled => command({ type: "startup", enabled }));
    const updates = section(root, "Обновления", "system_update");
    this.autoUpdates = switchRow(updates, "Проверять при запуске", p.autoCheckUpdates ?? true, enabled => this.change("autoCheckUpdates", enabled));
    this.updateMessage = document.createElement("p"); this.updateMessage.className = "update-message"; this.updateMessage.setAttribute("aria-live", "polite");
    this.updateProgress = document.createElement("progress"); this.updateProgress.className = "update-progress"; this.updateProgress.max = 1; this.updateProgress.setAttribute("aria-label", "Загрузка обновления");
    this.checkUpdate = button("Проверить обновления", { action: () => command({ type: "checkUpdates" }) });
    this.installUpdate = button("Обновить и перезапустить", { variant: "filled", action: () => command({ type: "installUpdate" }) });
    updates.append(this.updateMessage, this.updateProgress, this.checkUpdate, this.installUpdate); this.setUpdate(state?.update);
    const advanced = section(root, "Дополнительно", "tune");
    this.image = textField("Запасная иконка", p.imageKey || "");
    this.image.input.addEventListener("change", () => this.change("imageKey", this.image.input.value)); advanced.append(this.image);
    advanced.append(button("Пройти настройку заново", { action: () => command({ type: "restartOnboarding" }) }));
    const version = document.createElement("p"); version.className = "version"; version.textContent = "Версия " + (p.version || "2.1.0"); advanced.append(version);
  }
  change(key, value) { this.command({ type: "preference", key, value }); }
  update(state) {
    this.state = state; const p = state.preferences || {};
    this.publication.setValue(state.enabled, true); this.source.setValue(String(p.sourceMode || 0));
    this.allPlayers.setValue(p.sourceMode === 3, true); this.apple.setValue(p.automaticArtwork ?? true, true);
    this.youtube.setValue(p.youTubeThumbnails ?? true, true); this.startup.setValue(p.startup || false, true); this.appearance.update(p);
    this.autoUpdates.setValue(p.autoCheckUpdates ?? true, true); this.setUpdate(state.update);
  }
  setUpdate(update) {
    update ||= { status: "idle", message: "" };
    this.updateMessage.textContent = update.message;
    this.updateProgress.hidden = update.status !== "downloading"; this.updateProgress.value = update.progress || 0;
    this.checkUpdate.disabled = ["checking", "downloading", "ready"].includes(update.status);
    this.installUpdate.hidden = !update.available; this.installUpdate.disabled = this.checkUpdate.disabled;
  }
  receive(message) { this.id.receive(message); }
  dispose() { this.id.dispose(); }
}

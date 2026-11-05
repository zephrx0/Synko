import { iconButton } from "./components/buttons.js";
import { symbol } from "./components/interaction.js";
import { toggle } from "./components/selection.js";
import { logo, sourceIcon as iconFor, browserIcon } from "./brand.js";
import { Marquee } from "./components/marquee.js";
import { WavyProgress } from "./components/wavy-progress.js";
import { Spring, motion, animate, stopAnimation, reducedMotion } from "./motion.js";

const formatTime = seconds => {
  const value = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(value / 60), remainder = String(value % 60).padStart(2, "0");
  return value >= 3600 ? `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, "0")}:${remainder}` : `${minutes}:${remainder}`;
};

export class MainView {
  constructor(root, command) {
    this.root = root; this.command = command; this.state = null; this.image = null; this.sourceElements = new Map();
    root.className = "app-shell";
    root.innerHTML = `<header class="topbar"><div class="brand">${logo}<h1 class="brand-name">Synko</h1></div></header>
      <section class="player-card" aria-label="Synko"><div class="media-top"><div class="cover"></div><div class="media-copy"><div class="source-badge"></div><h2 class="media-title"></h2><p class="media-artist"></p><p class="media-album"></p></div></div><div class="progress-zone"><canvas class="wavy-progress" role="progressbar" aria-label="Synko" aria-valuemin="0" aria-valuemax="100"></canvas></div><div class="media-time"><span></span><span></span></div></section>
      <section class="publication-card"><div class="publication-icon"></div><span class="publication-label">Показывать в Discord</span></section><div class="source-row" aria-live="polite"></div>`;
    root.querySelector(".topbar").append(iconButton("settings", "Настройки", () => command({ type: "settings" })));
    root.querySelector(".publication-icon").append(symbol("discord_logo"));
    this.publication = toggle("Показывать в Discord", true, enabled => command({ type: "publication", enabled }));
    root.querySelector(".publication-card").append(this.publication);
    this.title = new Marquee(root.querySelector(".media-title"));
    this.artistLine = new Marquee(root.querySelector(".media-artist"));
    this.albumLine = new Marquee(root.querySelector(".media-album"));
    this.wave = new WavyProgress(root.querySelector("canvas"), (position, duration) => this.setTime(position, duration));
    this.cover = root.querySelector(".cover");
    this.badge = root.querySelector(".source-badge");
    this.artist = root.querySelector(".media-artist");
    this.album = root.querySelector(".media-album");
    this.enter();
  }

  enter() {
    this.entrances = [...this.root.children].map((element, index) => {
      const spring = new Spring(0, reducedMotion() ? motion.effectsFast : motion.spatialDefault); spring.target = 1;
      const start = performance.now() + index * 40;
      element.style.opacity = 0; element.style.transform = "translateY(8px)";
      animate(element, (dt, now) => {
        if (now < start) return true;
        const value = spring.step(dt);
        element.style.transform = reducedMotion() ? "none" : `translateY(${8 * (1 - value)}px)`;
        element.style.opacity = Math.max(0, Math.min(1, value));
        return !spring.settled;
      });
      return element;
    });
  }

  update(state) {
    this.state = state;
    const media = state.media;
    const empty = !media;
    this.root.querySelector(".player-card").classList.toggle("empty", empty);
    const filterLabels = { 1: "Выбран Apple Music", 2: "Выбран браузер", 4: "Выбран Spotify" };
    const blockedByFilter = empty && filterLabels[state.preferences?.sourceMode] && state.sources?.some(source => source.playing);
    this.title.setText(media?.title || (blockedByFilter ? filterLabels[state.preferences.sourceMode] : "Ничего не играет"));
    this.root.querySelector(".player-card").classList.toggle("filtered", !!blockedByFilter);
    this.artistLine.setText(media?.artist || "");
    this.albumLine.setText(media?.album || "");
    this.badge.replaceChildren(symbol(media?.source === "browser" ? browserIcon(media.browser) : iconFor(media?.source), true), document.createTextNode(media?.source === "browser" ? (media.browser || "Браузер").replace("Google ", "").replace("Microsoft ", "") : media?.sourceLabel || "Браузер"));
    this.publication.setValue(state.enabled, true);
    const picture = media?.cover || null;
    if (picture !== this.image || !this.cover.childElementCount) {
      this.image = picture;
      this.cover.replaceChildren();
      if (picture) { const image = document.createElement("img"); image.src = picture; image.alt = media?.album || media?.title || ""; this.cover.append(image); }
      else this.cover.append(symbol(media?.source === "spotify" ? "spotify_logo" : "music_note"));
    }
    this.wave.setMedia(media);
    this.setTime(this.wave.currentPosition(), media?.duration || 0);
    const sources = state.sources || [];
    const row = this.root.querySelector(".source-row");
    row.hidden = sources.length === 0;
    for (const [id, element] of this.sourceElements) {
      if (!sources.some(source => source.id === id)) { element.remove(); this.sourceElements.delete(id); }
    }
    for (const [index, source] of sources.entries()) {
      let element = this.sourceElements.get(source.id);
      if (!element) {
        element = document.createElement("div");
        element.className = "source-status";
        const caption = document.createElement("span"), status = document.createElement("span");
        caption.className = "source-name";
        status.className = "source-playback";
        element.append(symbol(source.id === "apple" || source.id === "spotify" ? iconFor(source.id) : source.id.startsWith("player:") ? "music_note" : browserIcon(source.browser || source.name), true), caption, status);
        element.setState = (name, active) => {
          caption.textContent = name;
          status.textContent = active ? "играет" : "пауза";
          element.dataset.playing = String(active);
          element.setAttribute("aria-label", `${name}, ${status.textContent}`);
        };
        element.setState(source.name, source.playing);
        this.sourceElements.set(source.id, element);
      } else element.setState(source.name, source.playing);
      if (row.children[index] !== element) row.insertBefore(element, row.children[index] || null);
    }
  }

  setTime(position, duration) {
    const time = this.root.querySelector(".media-time");
    const show = duration > 0 && !!this.state?.media;
    time.style.visibility = show ? "visible" : "hidden";
    const first = show ? formatTime(position) : "", last = show ? formatTime(duration) : "";
    if (time.children[0].textContent !== first) time.children[0].textContent = first;
    if (time.children[1].textContent !== last) time.children[1].textContent = last;
    const canvas = this.root.querySelector("canvas");
    if (duration > 0) canvas.setAttribute("aria-valuenow", String(Math.round(position / duration * 100)));
    else canvas.removeAttribute("aria-valuenow");
  }
  diagnostics() { return this.wave.diagnostics(); }
  dispose() { this.wave.dispose(); this.title.dispose(); this.artistLine.dispose(); this.albumLine.dispose(); for (const element of this.entrances) stopAnimation(element); }
}

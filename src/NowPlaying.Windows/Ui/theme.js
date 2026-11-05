import { Hct, SchemeTonalSpot, hexFromArgb, argbFromHex, sourceColorFromImageBytes } from "./vendor/material-color-utilities/index.js";
import { Spring, motion, animate, stopAnimation, reducedMotion } from "./motion.js";

const roles = ["primary", "onPrimary", "primaryContainer", "onPrimaryContainer", "secondary", "onSecondary", "secondaryContainer", "onSecondaryContainer", "tertiary", "onTertiary", "tertiaryContainer", "onTertiaryContainer", "surface", "surfaceDim", "surfaceBright", "surfaceContainerLowest", "surfaceContainerLow", "surfaceContainer", "surfaceContainerHigh", "surfaceContainerHighest", "onSurface", "onSurfaceVariant", "outline", "outlineVariant", "error", "onError", "errorContainer", "onErrorContainer", "inverseSurface", "inverseOnSurface", "scrim"];
const cssName = name => "--md-sys-color-" + name.replace(/[A-Z]/g, letter => "-" + letter.toLowerCase());
let current = null;
let environment = { dark: false, accent: "#6750a4", highContrast: false };
let artworkSeed = null;
let settings = { themeMode: "system", colorSource: "artwork", customSeed: null };
let generation = 0;

export function colors(seed, dark) {
  const scheme = new SchemeTonalSpot(Hct.fromInt(argbFromHex(seed)), dark, 0, "2021");
  // Keep app chrome nearly neutral; only the media card carries the artwork hue.
  scheme.neutralPalette.chroma = 2;
  scheme.neutralVariantPalette.chroma = 4;
  return Object.fromEntries(roles.map(role => [role, hexFromArgb(scheme[role])]));
}

function rgb(hex) { return [1, 3, 5].map(index => parseInt(hex.substring(index, index + 2), 16)); }
function render(palette) {
  for (const [role, value] of Object.entries(palette)) document.documentElement.style.setProperty(cssName(role), value);
  document.dispatchEvent(new CustomEvent("synko:palette", { detail: palette }));
}

export function applyTheme(options = {}, animateChange = true) {
  environment = { ...environment, ...options };
  window.synkoEnvironment = environment;
  const dark = settings.themeMode === "system" ? environment.dark : settings.themeMode === "dark";
  const seed = settings.colorSource === "custom" && settings.customSeed ? settings.customSeed
    : settings.colorSource === "artwork" && artworkSeed ? artworkSeed : environment.accent;
  const next = colors(seed, dark);
  document.documentElement.dataset.dark = String(dark);
  document.documentElement.dataset.highContrast = String(environment.highContrast);
  document.documentElement.dataset.reducedMotion = String(environment.reducedMotion === true);
  window.chrome?.webview?.postMessage({ type: "palette", surface: next.surface, foreground: next.onSurface, dark });
  const start = current;
  current = next;
  stopAnimation("theme");
  if (!start || !animateChange || document.hidden) { render(next); return next; }
  const spring = new Spring(0, reducedMotion() ? motion.effectsFast : motion.effectsSlow);
  spring.target = 1;
  animate("theme", dt => {
    const amount = Math.min(1, Math.max(0, spring.step(dt)));
    render(Object.fromEntries(roles.map(role => {
      const a = rgb(start[role]), b = rgb(next[role]);
      return [role, "#" + a.map((channel, i) => Math.round(channel + (b[i] - channel) * amount).toString(16).padStart(2, "0")).join("")];
    })));
    return !spring.settled;
  });
  return next;
}

export function setThemeSettings(value) { settings = { ...settings, ...value }; return applyTheme(); }
export function themeSnapshot() { return { ...current }; }

export async function setArtworkSeed(source) {
  const request = ++generation;
  if (!source) { artworkSeed = null; applyTheme(); return; }
  const image = new Image();
  image.src = source;
  try {
    await image.decode();
    const canvas = document.createElement("canvas");
    canvas.width = 64; canvas.height = 64;
    const context = canvas.getContext("2d", { willReadFrequently: true });
    context.drawImage(image, 0, 0, 64, 64);
    const pixels = context.getImageData(0, 0, 64, 64).data;
    let colorful = 0, opaque = 0;
    for (let i = 0; i < pixels.length; i += 4) {
      if (pixels[i + 3] < 200) continue;
      opaque++;
      if (Math.max(pixels[i], pixels[i + 1], pixels[i + 2]) - Math.min(pixels[i], pixels[i + 1], pixels[i + 2]) > 24) colorful++;
    }
    const selected = sourceColorFromImageBytes(pixels);
    if (request !== generation) return;
    artworkSeed = opaque && colorful / opaque >= .08 && Hct.fromInt(selected).chroma >= 8 ? hexFromArgb(selected) : null;
  } catch { if (request !== generation) return; artworkSeed = null; }
  applyTheme();
}

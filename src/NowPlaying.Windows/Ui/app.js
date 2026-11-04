import { applyTheme, setThemeSettings, themeSnapshot, setArtworkSeed } from "./theme.js";
import { animationCount, setAnimationVisibility, animate, stopAnimation, Spring, motion } from "./motion.js";
import { mountGallery } from "./component-gallery.js";
import { MainView } from "./main-view.js";
import { SettingsView } from "./settings-view.js";
import { OnboardingView } from "./onboarding-view.js";
import { showSnackbar } from "./components/overlays.js";

let state = null;
let ready = false;
let view = null;
let currentView = "main";
let bootstrapped = false;
const root = document.getElementById("app");
function command(message) {
  if (message.type === "settings") { showView("settings"); return; }
  if (message.type === "navigate") { showView(message.view); return; }
  if (message.type === "restartOnboarding") {
    if (state?.preferences) state.preferences = { ...state.preferences, onboardingStep: 0, onboardingDone: false };
    window.chrome?.webview?.postMessage({ type: "preference", key: "onboardingStep", value: 0 });
    window.chrome?.webview?.postMessage({ type: "preference", key: "onboardingDone", value: false });
    showView("onboarding"); return;
  }
  if (message.type === "preference" && state?.preferences) {
    state.preferences[message.key] = message.value;
    if (message.key === "customSeed") state.preferences.colorSource = "custom";
    if (["themeMode", "colorSource", "customSeed"].includes(message.key)) setThemeSettings(state.preferences);
    view?.update(state);
  }
  window.chrome?.webview?.postMessage(message);
}
function showView(name) {
  stopAnimation("navigation");
  currentView = name;
  view?.dispose(); view = null; root.replaceChildren();
  if (name === "components") mountGallery(root);
  else if (name === "settings") view = new SettingsView(root, command, state);
  else if (name === "onboarding") view = new OnboardingView(root, command, state);
  else { view = new MainView(root, command); if (state) view.update(state); }
  window.scrollTo(0, 0);
  if (name === "settings") {
    const fade = new Spring(0, motion.effectsDefault); fade.target = 1;
    animate("navigation", dt => { const amount = fade.step(dt); root.style.opacity = amount; root.style.transform = `translateY(${8 * (1 - amount)}px)`; return !fade.settled; });
  } else { root.style.opacity = 1; root.style.transform = "none"; }
}
function receive(message) {
  if (message.type === "environment") applyTheme(message.environment, ready);
  if (message.type === "visibility") { setAnimationVisibility(message.visible); document.dispatchEvent(new CustomEvent("synko:visibility", { detail: message.visible })); }
  if (message.type === "state") {
    const previous = state?.preferences; const previousCover = state?.media?.cover; state = message.state;
    if (previousCover !== state.media?.cover) setArtworkSeed(state.media?.cover || null);
    if (state.preferences && (!previous || ["themeMode", "colorSource", "customSeed"].some(key => previous[key] !== state.preferences[key]))) setThemeSettings(state.preferences);
    if (!bootstrapped && state.preferences) { bootstrapped = true; if (!state.preferences.onboardingDone) showView("onboarding"); }
    view?.update(state);
  }
  if (message.type === "view") showView(message.view);
  if (message.type === "error") showSnackbar(message.message);
  if (message.type === "update" && state) { state.update = message.update; view?.setUpdate?.(message.update); }
  view?.receive?.(message);
}

window.chrome?.webview?.addEventListener("message", event => receive(event.data));
window.synko = {
  receive,
  showView,
  setTheme: settings => setThemeSettings(settings),
  diagnostics: () => ({ ready, state, view: currentView, palette: themeSnapshot(), animationCount: animationCount(), wave: view?.diagnostics?.(), width: innerWidth, dpr: devicePixelRatio }),
};
applyTheme(window.synkoEnvironment ?? {}, false);
showView("main");
if (window.synkoInitialMessage) receive(window.synkoInitialMessage);
await Promise.all([document.fonts.load('400 16px "Roboto Flex"'), document.fonts.load('400 24px "Material Symbols Rounded"')]);
await document.fonts.ready;
ready = true;
window.chrome?.webview?.postMessage({ type: "ready", phase: 7 });

// The one global theme provider for V3 and V4. Writes the resolved theme's --theme-* variables on
// <html>; every token in styles.css derives from them. Resolution: Live Preview OFF → the SAVED
// theme; Live Preview ON → the unsaved draft (`state.live`) across every page. Only the saved theme
// is ever persisted; the preview flag and draft live in memory, so a refresh starts from saved.
// The saved theme is stored in this browser; the pre-paint script in __root.tsx reads
// THEME_VARS_KEY so it shows before React loads.
import { useSyncExternalStore } from "react";
import {
  DEFAULT_THEME_STATE,
  applyTheme,
  deleteUserTheme,
  editColor,
  isDirty,
  isLightColor,
  previewTheme,
  resetColor,
  resetToDefault,
  resolveTheme,
  restorePrevious,
  sanitizeThemeState,
  saveCustomTheme,
  themeVars,
  validateThemeDraft,
  type ThemeChoice,
  type ThemeKey,
  type ThemeState,
} from "@/lib/eo/theme";

export const THEME_STATE_KEY = "eo-theme-v2";
export const THEME_VARS_KEY = "eo-theme-vars-v1";

let state: ThemeState = DEFAULT_THEME_STATE;
let hydrated = false;
/** Global Live Preview mode (memory only, never persisted). */
let previewActive = false;
const listeners = new Set<() => void>();

function apply(s: ThemeState, animate: boolean) {
  if (typeof document === "undefined") return;
  const root = document.documentElement;
  const t = resolveTheme(previewActive ? s.live : s.saved, s.userThemes);
  if (animate && root.dataset["motion"] !== "reduced") {
    root.classList.add("eo-theme-anim");
    window.setTimeout(() => root.classList.remove("eo-theme-anim"), 450);
  }
  for (const [k, v] of Object.entries(themeVars(t.colors))) root.style.setProperty(k, v);
  const light = isLightColor(t.colors.background);
  root.dataset["themeScheme"] = light ? "light" : "dark";
  root.style.colorScheme = light ? "light" : "dark";
}

function persist(s: ThemeState) {
  try {
    window.localStorage.setItem(
      THEME_STATE_KEY,
      JSON.stringify({ saved: s.saved, history: s.history, userThemes: s.userThemes }),
    );
    const t = resolveTheme(s.saved, s.userThemes);
    window.localStorage.setItem(
      THEME_VARS_KEY,
      JSON.stringify(themeVars(t.colors)),
    );
  } catch {
    /* storage unavailable: theme still applies for this session */
  }
}

/** Storage and <html> attributes left behind by the removed visual-effects engine. */
const LEGACY_VFX_KEYS = ["eo-vfx-v1"];
function clearLegacyEffects() {
  const root = document.documentElement;
  for (const name of root.getAttributeNames()) if (name.startsWith("data-vfx-")) root.removeAttribute(name);
  try {
    for (const k of LEGACY_VFX_KEYS) window.localStorage.removeItem(k);
  } catch {
    /* storage unavailable */
  }
}

function hydrate() {
  if (hydrated || typeof window === "undefined") return;
  hydrated = true;
  clearLegacyEffects();
  try {
    const raw =
      window.localStorage.getItem(THEME_STATE_KEY) ?? window.localStorage.getItem("eo-theme-v1");
    if (raw) state = sanitizeThemeState(JSON.parse(raw));
  } catch {
    state = DEFAULT_THEME_STATE;
  }
  apply(state, false);
  // Rewrites the saved values so a removed theme or old effect data never comes back.
  persist(state);
}

function commit(next: ThemeState, opts: { animate?: boolean; preview?: boolean } = {}) {
  const savedChanged =
    next.saved !== state.saved || next.history !== state.history || next.userThemes !== state.userThemes;
  const wasPreview = previewActive;
  if (opts.preview !== undefined) previewActive = opts.preview;
  const appliedChanged =
    wasPreview !== previewActive ||
    next.saved !== state.saved ||
    next.userThemes !== state.userThemes ||
    (previewActive && next.live !== state.live);
  state = next;
  if (appliedChanged) apply(next, opts.animate ?? false);
  if (savedChanged) persist(next);
  listeners.forEach((l) => l());
}

/** Applies the saved theme on startup (called once from the shell). */
export function initTheme() {
  hydrate();
}

/** True while global Live Preview mode shows the draft across the whole app. */
export function isLivePreviewActive() {
  return previewActive;
}
/** Turns Live Preview on with a fresh draft copied from the saved theme. */
export function startLivePreview() {
  hydrate();
  if (previewActive) return;
  commit({ ...state, live: state.saved }, { preview: true, animate: true });
}
/** Turns Live Preview off: discards the draft and restores the saved theme everywhere. Never saves. */
export function stopLivePreview() {
  hydrate();
  if (!previewActive && !isDirty(state)) return;
  commit({ ...state, live: state.saved }, { preview: false, animate: true });
}
/** Loads a theme into the draft. Never changes the Live Preview on/off state. */
export function previewThemePreset(id: string) {
  hydrate();
  commit(previewTheme(state, id), { animate: true });
}
/** The Settings LIVE PREVIEW button: turns Live Preview on (if off) showing the given theme. */
export function livePreviewTheme(id: string) {
  hydrate();
  const base = previewActive ? state : { ...state, live: state.saved };
  commit(previewTheme(base, id), { preview: true, animate: true });
}
/** Saves the draft and applies it globally (V3 + V4). */
export function applyCurrentTheme() {
  hydrate();
  commit(applyTheme(state), { animate: true, preview: false });
}

export type SaveDraftResult = { ok: true; name: string } | { ok: false; reason: string };
/** Validates the draft, then saves it and applies it everywhere. Invalid drafts are not saved. */
export function saveThemeDraft(): SaveDraftResult {
  hydrate();
  const check = validateThemeDraft(state);
  if (!check.ok) return check; // draft and Live Preview stay as they are
  commit(applyTheme(state), { animate: true, preview: false });
  return { ok: true, name: resolveTheme(state.saved, state.userThemes).name };
}
/** Throws away unsaved draft changes; the saved theme stays applied. */
export function discardThemeDraft() {
  hydrate();
  stopLivePreview();
}
/** True while the draft differs from the saved theme (read outside React, e.g. navigation guards). */
export function hasThemeDraft() {
  hydrate();
  return isDirty(state);
}
export function setThemeColor(key: ThemeKey, value: string) {
  hydrate();
  commit(editColor(state, key, value));
}
export function resetThemeColor(key: ThemeKey) {
  hydrate();
  commit(resetColor(state, key), { animate: true });
}
export function saveCurrentAsCustomTheme(name: string) {
  hydrate();
  // Adds the draft to the saved-theme library and keeps editing it; the applied (saved) theme and
  // the Live Preview state are untouched. Only SAVE THEME commits the draft.
  const next = saveCustomTheme(state, name);
  commit({ ...next, saved: state.saved, history: state.history });
}
export function removeCustomTheme(id: string) {
  hydrate();
  commit(deleteUserTheme(state, id), { animate: true });
}
export function resetThemeToDefault() {
  hydrate();
  commit(resetToDefault(state), { animate: true });
}
export function restorePreviousTheme() {
  hydrate();
  commit(restorePrevious(state), { animate: true, preview: false });
}

export function useLivePreview(): boolean {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => previewActive,
    () => false,
  );
}

export function useThemeState(): ThemeState {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => {
      hydrate();
      return state;
    },
    () => DEFAULT_THEME_STATE,
  );
}

/** The draft theme (what the live preview shows). */
export const resolveLive = (s: ThemeState) => resolveTheme(s.live, s.userThemes);
/** The saved theme (what the whole app shows). */
export const resolveSaved = (s: ThemeState) => resolveTheme(s.saved, s.userThemes);
export type { ThemeChoice };

/** Inline pre-paint script: applies the applied theme's variables before the first frame. */
export const THEME_PREPAINT_SCRIPT = `(function(){try{var v=JSON.parse(localStorage.getItem(${JSON.stringify(
  THEME_VARS_KEY,
)})||"null");if(!v)return;var s=document.documentElement.style;for(var k in v){if(k.indexOf("--theme-")===0)s.setProperty(k,v[k]);}}catch(e){}})();`;

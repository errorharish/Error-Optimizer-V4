// Saved Settings & Control Center preferences (startup, AI policies, performance, safety, logs,
// motion), shared by every V3 + V4 page. Themes live in themeStore.
import { useSyncExternalStore } from "react";
import { hasNativeBackend } from "@/lib/eo/nativeBridge";
import { isBackendConnected, subscribeConnection } from "@/lib/eo/connectionStore";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import {
  DEFAULT_SETTINGS,
  restoreDefaults,
  sanitizeSettings,
  type AppSettings,
  type SettingsCategory,
  type Suite,
} from "@/lib/eo/settings";

export const SETTINGS_KEY = "eo-settings-v1";
let state: AppSettings = DEFAULT_SETTINGS;
let hydrated = false;
let backendHydrated = false;
let connSubAttached = false;
let webviewSubAttached = false;
const listeners = new Set<() => void>();

function attachWebviewListener() {
  if (webviewSubAttached || typeof window === "undefined") return;
  const webview = (window as unknown as { chrome?: { webview?: { addEventListener: (t: string, cb: (e: { data: unknown }) => void) => void } } })?.chrome?.webview;
  if (webview && typeof webview.addEventListener === "function") {
    webviewSubAttached = true;
    webview.addEventListener("message", (event: { data: unknown }) => {
      try {
        const raw = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
        if (!raw || typeof raw !== "object") return;
        const msg = raw as Record<string, unknown>;
        if (msg["event"] === "global-config:changed" && msg["config"]) {
          state = sanitizeSettings(msg["config"]);
          try {
            window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(state));
          } catch {}
          listeners.forEach((l) => l());
        } else if (msg["event"] === "settings:changed" && msg["settings"]) {
          state = sanitizeSettings(msg["settings"]);
          try {
            window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(state));
          } catch {}
          listeners.forEach((l) => l());
        }
      } catch {}
    });
  }
}

function syncSettingsFromHost() {
  if (backendHydrated || (!hasNativeBackend() && !isBackendConnected())) return;
  backendHydrated = true;
  void sendHostRequest("global-config:get", undefined, 10000)
    .then((res) => {
      if (res && typeof res === "object") {
        const r = res as Record<string, unknown>;
        const rawConfig = r["config"] ?? res;
        state = sanitizeSettings(rawConfig);
        try {
          window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(state));
        } catch {
          /* ignore storage errors */
        }
        listeners.forEach((l) => l());
      }
    })
    .catch(() => {
      void sendHostRequest("settings:get", undefined, 10000)
        .then((res) => {
          if (res && typeof res === "object") {
            state = sanitizeSettings(res);
            try {
              window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(state));
            } catch {}
            listeners.forEach((l) => l());
          }
        })
        .catch(() => {
          backendHydrated = false;
        });
    });
}

function hydrate() {
  if (typeof window === "undefined") return;
  attachWebviewListener();
  if (!connSubAttached) {
    connSubAttached = true;
    subscribeConnection(() => {
      if (isBackendConnected()) syncSettingsFromHost();
    });
  }
  if (!hydrated) {
    hydrated = true;
    try {
      const raw = window.localStorage.getItem(SETTINGS_KEY);
      if (raw) state = sanitizeSettings(JSON.parse(raw));
    } catch {
      state = DEFAULT_SETTINGS;
    }
  }
  if (hasNativeBackend() || isBackendConnected()) {
    syncSettingsFromHost();
  }
}

function commit(next: AppSettings, syncBackend = true) {
  state = next;
  try {
    window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(next));
  } catch {
    /* storage unavailable: keep for this session */
  }
  listeners.forEach((l) => l());
  if (syncBackend && hasNativeBackend()) {
    void sendHostRequest("global-config:save", next, 10000).catch(() => {
      void sendHostRequest("settings:save", next, 10000).catch(() => undefined);
    });
  }
}

export function setSettings(patch: Partial<AppSettings>) {
  hydrate();
  commit({ ...state, ...patch });
}

export async function toggleAutoOptimization(): Promise<boolean> {
  hydrate();
  const next = !state.autoOptimizationEnabled;
  commit({ ...state, autoOptimizationEnabled: next });
  if (hasNativeBackend()) {
    try {
      const res = await sendHostRequest<{ autoOptimizationEnabled?: boolean }>(
        "global-config:toggle-auto-opt",
        undefined,
        10000
      );
      if (res && typeof res.autoOptimizationEnabled === "boolean") {
        if (state.autoOptimizationEnabled !== res.autoOptimizationEnabled) {
          commit({ ...state, autoOptimizationEnabled: res.autoOptimizationEnabled });
        }
      }
    } catch {}
  }
  return state.autoOptimizationEnabled;
}

export async function toggleAiOptimization(): Promise<boolean> {
  hydrate();
  const next = !state.aiOptimizationEnabled;
  commit({ ...state, aiOptimizationEnabled: next, aiEnabled: next });
  if (hasNativeBackend()) {
    try {
      const res = await sendHostRequest<{ aiOptimizationEnabled?: boolean }>(
        "global-config:toggle-ai",
        undefined,
        10000
      );
      if (res && typeof res.aiOptimizationEnabled === "boolean") {
        if (state.aiOptimizationEnabled !== res.aiOptimizationEnabled) {
          commit({ ...state, aiOptimizationEnabled: res.aiOptimizationEnabled, aiEnabled: res.aiOptimizationEnabled });
        }
      }
    } catch {}
  }
  return state.aiOptimizationEnabled;
}

export function setLastSuite(suite: Suite) {
  hydrate();
  if (state.lastSuite !== suite) commit({ ...state, lastSuite: suite });
}

export function restoreSettingsDefaults() {
  hydrate();
  const next = restoreDefaults(state);
  commit(next, false);
  if (hasNativeBackend()) {
    void sendHostRequest("settings:reset-defaults", undefined, 10000)
      .then((res) => {
        if (res && typeof res === "object") commit(sanitizeSettings(res), false);
      })
      .catch(() => undefined);
  }
}

export function getSettingsSnapshot(): AppSettings {
  hydrate();
  return state;
}

export function subscribeSettings(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useSettings(): AppSettings {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => {
      hydrate();
      return state;
    },
    () => DEFAULT_SETTINGS,
  );
}

/* ── Open a Settings category from anywhere (e.g. the Live Preview tray's APPEARANCE shortcut).
 * In-memory only: SettingsCenter takes the request when it mounts, or immediately if it is
 * already open. Nothing about the theme draft or Live Preview is touched. */
let requestedCategory: SettingsCategory | null = null;
const categoryListeners = new Set<(c: SettingsCategory) => void>();

export function requestSettingsCategory(c: SettingsCategory) {
  requestedCategory = c;
  categoryListeners.forEach((l) => l(c));
}

/** Returns and clears the pending category request. */
export function takeSettingsCategoryRequest(): SettingsCategory | null {
  const c = requestedCategory;
  requestedCategory = null;
  return c;
}

export function onSettingsCategoryRequest(l: (c: SettingsCategory) => void): () => void {
  categoryListeners.add(l);
  return () => categoryListeners.delete(l);
}

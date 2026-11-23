// Desktop host window bridge — native backend readiness is strictly gated on confirmed handshake.
import {
  ensureBackendHandshake,
  getConnectionSnapshot,
  isBackendConnected,
  useIsBackendConnected,
} from "./connectionStore";

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (message: any) => void;
      };
    };
  }
}

export function isDesktopApp(): boolean {
  return typeof window !== "undefined" && !!window.chrome?.webview;
}

/**
 * Returns true only when hosted inside WebView2 AND the native host/backend handshake has
 * genuinely completed in CONNECTED state (no unconditional shortcut).
 */
export function hasNativeBackend(): boolean {
  if (!isDesktopApp()) return false;
  const snap = getConnectionSnapshot();
  if (snap.phase === "INITIALIZING") {
    void ensureBackendHandshake();
  }
  return isBackendConnected();
}

export { useIsBackendConnected };

export function minimizeWindow() {
  if (typeof window !== "undefined" && window.chrome?.webview) {
    window.chrome.webview.postMessage(JSON.stringify({ action: "window:minimize" }));
  }
}

export function closeWindow() {
  if (typeof window !== "undefined" && window.chrome?.webview) {
    window.chrome.webview.postMessage(JSON.stringify({ action: "window:close" }));
  }
}

export function dragWindow() {
  if (typeof window !== "undefined" && window.chrome?.webview) {
    window.chrome.webview.postMessage(JSON.stringify({ action: "window:drag" }));
  }
}

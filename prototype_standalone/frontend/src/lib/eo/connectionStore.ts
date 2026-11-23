// Global backend connection lifecycle state machine (100% non-visual).
// Lifecycle:
//   INITIALIZING -> CONNECTING -> CONNECTED      (when native host + backend engine handshake succeeds)
//   INITIALIZING -> CONNECTING -> DISCONNECTED   (when host bridge is missing, times out, or fails)
import { useSyncExternalStore } from "react";

export type ConnectionPhase = "INITIALIZING" | "CONNECTING" | "CONNECTED" | "DISCONNECTED";

export type BackendMode = "windows-service" | "desktop-host-engine" | "none";

export interface FeatureCapability {
  available: boolean;
  mode?: string;
  reason?: string;
}

export interface ConnectionSnapshot {
  phase: ConnectionPhase;
  hostConnected: boolean;
  hostEngineReady: boolean;
  serviceConnected: boolean;
  backendMode: BackendMode;
  isElevated: boolean | null;
  version: string | null;
  pipeName: string | null;
  message: string;
  features: Record<string, FeatureCapability>;
  lastCheckedAt: number | null;
}

interface WebViewPort {
  postMessage: (message: string) => void;
  addEventListener?: (type: "message", handler: (event: { data: unknown }) => void) => void;
  removeEventListener?: (type: "message", handler: (event: { data: unknown }) => void) => void;
}

const SERVER_SNAPSHOT: ConnectionSnapshot = {
  phase: "INITIALIZING",
  hostConnected: false,
  hostEngineReady: false,
  serviceConnected: false,
  backendMode: "none",
  isElevated: null,
  version: null,
  pipeName: null,
  message: "Initializing desktop backend connection…",
  features: {},
  lastCheckedAt: null,
};

let snapshot: ConnectionSnapshot = SERVER_SNAPSHOT;
const listeners = new Set<() => void>();
let handshakePromise: Promise<ConnectionSnapshot> | null = null;
let handshakeSeq = 0;

function getWebViewPort(): WebViewPort | null {
  if (typeof window === "undefined") return null;
  const wv = (window as { chrome?: { webview?: unknown } }).chrome?.webview;
  return wv && typeof wv === "object" ? (wv as WebViewPort) : null;
}

function setSnapshot(next: ConnectionSnapshot) {
  snapshot = next;
  listeners.forEach((l) => l());
}

function parseFeatureMap(raw: unknown): Record<string, FeatureCapability> {
  if (!raw || typeof raw !== "object") return {};
  const out: Record<string, FeatureCapability> = {};
  for (const [k, val] of Object.entries(raw as Record<string, unknown>)) {
    if (!val || typeof val !== "object") continue;
    const obj = val as Record<string, unknown>;
    const cap: FeatureCapability = {
      available: obj["available"] === true,
    };
    if (typeof obj["mode"] === "string") cap.mode = obj["mode"];
    if (typeof obj["reason"] === "string" && obj["reason"]) cap.reason = obj["reason"];
    out[k] = cap;
  }
  return out;
}

export function applyHandshakePayload(raw: unknown): ConnectionSnapshot {
  if (!raw || typeof raw !== "object") {
    const next: ConnectionSnapshot = {
      ...SERVER_SNAPSHOT,
      phase: "DISCONNECTED",
      message: "Backend returned empty health payload.",
      lastCheckedAt: Date.now(),
    };
    setSnapshot(next);
    return next;
  }

  const r = raw as Record<string, unknown>;
  const healthy = r["healthy"] === true;
  const hostConnected = r["hostConnected"] !== undefined ? r["hostConnected"] === true : healthy;
  const hostEngineReady = r["hostEngineReady"] !== undefined ? r["hostEngineReady"] === true : healthy;
  const serviceConnected = r["serviceConnected"] === true;
  const backendModeRaw = r["backendMode"];
  const backendMode: BackendMode =
    backendModeRaw === "windows-service"
      ? "windows-service"
      : backendModeRaw === "desktop-host-engine"
        ? "desktop-host-engine"
        : serviceConnected
          ? "windows-service"
          : healthy
            ? "desktop-host-engine"
            : "none";

  const connected = healthy && hostConnected && (serviceConnected || hostEngineReady);
  const next: ConnectionSnapshot = {
    phase: connected ? "CONNECTED" : "DISCONNECTED",
    hostConnected,
    hostEngineReady,
    serviceConnected,
    backendMode,
    isElevated: typeof r["isElevated"] === "boolean" ? r["isElevated"] : null,
    version: typeof r["version"] === "string" ? r["version"] : null,
    pipeName: typeof r["pipeName"] === "string" ? r["pipeName"] : null,
    message:
      typeof r["message"] === "string" && r["message"]
        ? r["message"]
        : connected
          ? "Backend connected"
          : "Backend offline",
    features: parseFeatureMap(r["features"]),
    lastCheckedAt: Date.now(),
  };

  setSnapshot(next);
  return next;
}

export function markConnectionDisconnected(reason: string): ConnectionSnapshot {
  const next: ConnectionSnapshot = {
    ...snapshot,
    phase: "DISCONNECTED",
    hostConnected: false,
    hostEngineReady: false,
    serviceConnected: false,
    backendMode: "none",
    message: reason,
    lastCheckedAt: Date.now(),
  };
  setSnapshot(next);
  return next;
}

export function ensureBackendHandshake(force = false): Promise<ConnectionSnapshot> {
  if (typeof window === "undefined") {
    return Promise.resolve(snapshot);
  }
  if (!force && snapshot.phase === "CONNECTED") {
    return Promise.resolve(snapshot);
  }
  if (handshakePromise) {
    return handshakePromise;
  }

  const wv = getWebViewPort();
  if (!wv) {
    setSnapshot({
      ...SERVER_SNAPSHOT,
      phase: "CONNECTING",
      message: "Probing Windows desktop host bridge…",
    });
    return Promise.resolve(
      markConnectionDisconnected("Windows desktop WebView2 host bridge is not present."),
    );
  }

  setSnapshot({
    ...snapshot,
    phase: "CONNECTING",
    message: "Performing native host handshake…",
  });

  handshakePromise = new Promise<ConnectionSnapshot>((resolve) => {
    const id = `hs-${++handshakeSeq}-${Date.now()}`;
    let finished = false;

    const cleanup = () => {
      window.clearTimeout(timer);
      wv.removeEventListener?.("message", onMessage);
      handshakePromise = null;
    };

    const fail = (reason: string) => {
      if (finished) return;
      finished = true;
      cleanup();
      resolve(markConnectionDisconnected(reason));
    };

    const onMessage = (event: { data: unknown }) => {
      if (finished) return;
      let parsed: unknown = event.data;
      if (typeof parsed === "string") {
        try {
          parsed = JSON.parse(parsed);
        } catch {
          return;
        }
      }
      if (!parsed || typeof parsed !== "object") return;
      const msg = parsed as Record<string, unknown>;
      if (msg["id"] !== id) return;

      finished = true;
      cleanup();

      if (msg["ok"] === false) {
        const err = typeof msg["error"] === "string" ? msg["error"] : "Host handshake rejected.";
        resolve(markConnectionDisconnected(err));
        return;
      }

      resolve(applyHandshakePayload(msg["result"] ?? msg));
    };

    const timer = window.setTimeout(
      () => fail("Timed out waiting for native desktop host handshake."),
      8000,
    );

    if (!wv.addEventListener) {
      fail("Desktop host message listener API is unavailable.");
      return;
    }

    wv.addEventListener("message", onMessage);
    try {
      wv.postMessage(JSON.stringify({ id, action: "host:handshake" }));
    } catch (err) {
      fail(err instanceof Error ? err.message : "Failed to send handshake request.");
    }
  });

  return handshakePromise;
}

export function getConnectionSnapshot(): ConnectionSnapshot {
  return snapshot;
}

export function isBackendConnected(): boolean {
  return snapshot.phase === "CONNECTED" && snapshot.hostConnected;
}

export function subscribeConnection(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function useConnectionState(): ConnectionSnapshot {
  return useSyncExternalStore(
    subscribeConnection,
    getConnectionSnapshot,
    () => SERVER_SNAPSHOT,
  );
}

export function useIsBackendConnected(): boolean {
  return useSyncExternalStore(
    subscribeConnection,
    isBackendConnected,
    () => false,
  );
}

// Kick off initial host handshake automatically inside desktop WebView2 environments.
if (typeof window !== "undefined" && getWebViewPort()) {
  void ensureBackendHandshake();
}

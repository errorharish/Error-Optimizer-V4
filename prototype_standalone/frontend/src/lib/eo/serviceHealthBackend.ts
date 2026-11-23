// Service health sources. Real and simulated sources are separate classes behind one interface,
// so the .NET 8 Windows backend can be connected later without touching the UI.
//
// Picking order (`pickServiceHealthSource`):
//   1. VITE_EO_SERVICE_HEALTH_URL is set      → HTTP GET that endpoint (e.g. http://localhost:5080/health)
//   2. running inside the WebView2 desktop host → `service:health` request message
//   3. otherwise                               → SimulatedServiceHealth (browser prototype, labelled)
import {
  HEALTH_TIMEOUT_MS,
  interpretHttpHealth,
  type HealthCheckResult,
  type HealthSource,
} from "./serviceHealth";
import { isDesktopApp } from "./nativeBridge";
import { applyHandshakePayload, markConnectionDisconnected } from "./connectionStore";

export interface ServiceHealthSource {
  readonly mode: HealthSource;
  /** Must resolve (never reject) and respect `signal`; timeouts become ok:false results. */
  check(signal: AbortSignal): Promise<HealthCheckResult>;
}

const now = () => Date.now();
const errText = (e: unknown) =>
  e instanceof DOMException && e.name === "AbortError"
    ? `Timed out after ${HEALTH_TIMEOUT_MS / 1000} s`
    : e instanceof Error
      ? e.message
      : "Connection failed";

/* ───── HTTP health endpoint (.NET 8 MapHealthChecks) ───── */
export class HttpServiceHealth implements ServiceHealthSource {
  readonly mode = "http" as const;
  constructor(private url: string) {}
  async check(signal: AbortSignal): Promise<HealthCheckResult> {
    const t0 = now();
    try {
      const res = await fetch(this.url, { signal, cache: "no-store", headers: { Accept: "application/json, text/plain" } });
      const body = await res.text();
      const r = interpretHttpHealth(res.status, body);
      return { ...r, at: now(), latencyMs: now() - t0 };
    } catch (e) {
      return { ok: false, at: now(), latencyMs: now() - t0, detail: errText(e) };
    }
  }
}

/* ───── WebView2 desktop host ───── */
interface WebView {
  postMessage: (m: unknown) => void;
  addEventListener?: (t: "message", h: (e: { data: unknown }) => void) => void;
  removeEventListener?: (t: "message", h: (e: { data: unknown }) => void) => void;
}

export class WebView2ServiceHealth implements ServiceHealthSource {
  readonly mode = "native" as const;
  private seq = 0;
  constructor(private wv: WebView) {}
  check(signal: AbortSignal): Promise<HealthCheckResult> {
    const t0 = now();
    const id = `svc-${++this.seq}`;
    return new Promise((resolve) => {
      const finish = (r: Omit<HealthCheckResult, "at" | "latencyMs">, handshakePayload?: unknown) => {
        this.wv.removeEventListener?.("message", onMsg);
        signal.removeEventListener("abort", onAbort);
        if (r.ok && handshakePayload) {
          applyHandshakePayload(handshakePayload);
        } else if (r.ok) {
          applyHandshakePayload({ healthy: true, hostConnected: true, hostEngineReady: true, message: r.detail, version: r.version });
        } else {
          markConnectionDisconnected(r.detail);
        }
        resolve({ ...r, at: now(), latencyMs: now() - t0 });
      };
      const onAbort = () => finish({ ok: false, detail: errText(new DOMException("", "AbortError")) });
      const onMsg = (e: { data: unknown }) => {
        let msg: unknown = e.data;
        if (typeof msg === "string") {
          try {
            msg = JSON.parse(msg);
          } catch {
            return;
          }
        }
        const m = msg as { id?: string; ok?: boolean; error?: string; result?: { healthy?: boolean; version?: string; message?: string } } | null;
        if (!m || m.id !== id) return;
        const healthy = m.ok !== false && m.result?.healthy !== false;
        finish(
          {
            ok: healthy,
            detail: healthy ? (m.result?.message ?? "Healthy") : (m.error ?? m.result?.message ?? "Service reported unhealthy"),
            version: m.result?.version,
          },
          m.result,
        );
      };
      if (signal.aborted) return onAbort();
      signal.addEventListener("abort", onAbort, { once: true });
      if (!this.wv.addEventListener) return finish({ ok: false, detail: "Desktop host cannot reply to messages" });
      this.wv.addEventListener("message", onMsg);
      try {
        this.wv.postMessage(JSON.stringify({ id, action: "service:health" }));
      } catch (e) {
        finish({ ok: false, detail: errText(e) });
      }
    });
  }
}

/* ───── Browser prototype simulator ─────
 * Answers like a healthy service after a short delay. To demo an outage, set
 * localStorage "eo-sim-service" to "offline" (read on every check). Never used when a real
 * backend is available. */
export const SIM_SERVICE_KEY = "eo-sim-service";

export class SimulatedServiceHealth implements ServiceHealthSource {
  readonly mode = "simulated" as const;
  constructor(private latencyMs = 650) {}
  check(signal: AbortSignal): Promise<HealthCheckResult> {
    const t0 = now();
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        signal.removeEventListener("abort", onAbort);
        let down = false;
        try {
          down = window.localStorage.getItem(SIM_SERVICE_KEY) === "offline";
        } catch {
          down = false;
        }
        resolve(
          down
            ? { ok: false, at: now(), latencyMs: now() - t0, detail: "Simulated outage (demo)" }
            : { ok: true, at: now(), latencyMs: now() - t0, detail: "Simulated healthy response (demo)", version: "demo" },
        );
      }, this.latencyMs);
      const onAbort = () => {
        clearTimeout(timer);
        resolve({ ok: false, at: now(), latencyMs: now() - t0, detail: errText(new DOMException("", "AbortError")) });
      };
      if (signal.aborted) return onAbort();
      signal.addEventListener("abort", onAbort, { once: true });
    });
  }
}

export class DisconnectedServiceHealth implements ServiceHealthSource {
  readonly mode = "simulated" as const;

  async check(): Promise<HealthCheckResult> {
    return {
      ok: false,
      at: Date.now(),
      latencyMs: 0,
      detail: "Backend service not connected",
    };
  }
}

export function pickServiceHealthSource(): ServiceHealthSource {
  const url = import.meta.env["VITE_EO_SERVICE_HEALTH_URL"] as string | undefined;
  if (url) return new HttpServiceHealth(url);
  if (isDesktopApp()) {
    const wv = typeof window !== "undefined" ? (window.chrome?.webview as WebView | undefined) : undefined;
    if (wv) return new WebView2ServiceHealth(wv);
  }
  return new DisconnectedServiceHealth();
}

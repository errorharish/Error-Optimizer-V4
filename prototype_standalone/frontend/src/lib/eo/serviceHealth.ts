// Service health status — pure rules for the V4 header SERVICE STATUS indicator.
// The UI never decides the state itself: it feeds check events into `reduceServiceHealth`.
//
// States
//   connecting — the first check, or a reconnect attempt after the service went offline
//   online     — the latest check got a successful health response
//   offline    — the latest check failed, timed out, or the network went away
// While online, background rechecks keep showing ONLINE (no flicker every poll); a failed
// recheck switches to OFFLINE immediately.

export type ServiceState = "connecting" | "online" | "offline";
/** Origin of the reported service health status. */
export type HealthSource = "native" | "http" | "simulated";

export const HEALTH_POLL_MS = 10_000;
export const HEALTH_TIMEOUT_MS = 8_000;

export interface HealthCheckResult {
  ok: boolean;
  /** When the check finished (epoch ms). */
  at: number;
  latencyMs: number;
  /** Short human-readable result, e.g. "Healthy · v8.0.4" or "Timed out after 4 s". */
  detail: string;
  version?: string | undefined;
}

export interface ServiceHealthSnapshot {
  state: ServiceState;
  checking: boolean;
  last: HealthCheckResult | null;
  /** Consecutive failed checks (0 while online). */
  failures: number;
}

export type ServiceHealthEvent =
  | { type: "check-start" }
  | { type: "check-done"; result: HealthCheckResult }
  | { type: "network-lost"; at: number };

export const INITIAL_SERVICE_HEALTH: ServiceHealthSnapshot = {
  state: "connecting",
  checking: false,
  last: null,
  failures: 0,
};

export function reduceServiceHealth(
  s: ServiceHealthSnapshot,
  e: ServiceHealthEvent,
): ServiceHealthSnapshot {
  switch (e.type) {
    case "check-start":
      // Online stays online during a background recheck; anything else is "connecting".
      return { ...s, checking: true, state: s.state === "online" ? "online" : "connecting" };
    case "check-done":
      return e.result.ok
        ? { state: "online", checking: false, last: e.result, failures: 0 }
        : { state: "offline", checking: false, last: e.result, failures: s.failures + 1 };
    case "network-lost":
      return {
        state: "offline",
        checking: false,
        failures: s.failures + 1,
        last: { ok: false, at: e.at, latencyMs: 0, detail: "Network connection lost" },
      };
  }
}

export const STATE_LABEL: Record<ServiceState, string> = {
  connecting: "CONNECTING...",
  online: "SERVICE ONLINE",
  offline: "NOT CONNECTED",
};

export const SOURCE_LABEL: Record<HealthSource, string> = {
  native: "Windows service (desktop host)",
  http: "Windows service (health endpoint)",
  simulated: "Awaiting ERROR OPTIMIZER background service connection",
};

/**
 * Interprets an HTTP health response. ASP.NET Core `MapHealthChecks` answers 200 "Healthy" /
 * "Degraded" and 503 "Unhealthy"; JSON bodies may carry `status` or `ok`.
 */
export function interpretHttpHealth(
  status: number,
  body: string,
): { ok: boolean; detail: string; version?: string | undefined } {
  let json: Record<string, unknown> | null = null;
  try {
    const parsed: unknown = JSON.parse(body);
    if (parsed && typeof parsed === "object") json = parsed as Record<string, unknown>;
  } catch {
    json = null;
  }
  const word = String(json?.["status"] ?? body ?? "").trim();
  const version = typeof json?.["version"] === "string" ? json["version"] : undefined;
  if (status < 200 || status >= 300) return { ok: false, detail: `HTTP ${status}${word ? ` · ${word.slice(0, 40)}` : ""}`, version };
  if (json && json["ok"] === false) return { ok: false, detail: "Service reported not ok", version };
  if (/^unhealthy$/i.test(word)) return { ok: false, detail: "Unhealthy", version };
  return { ok: true, detail: /^degraded$/i.test(word) ? "Degraded" : "Healthy", version };
}

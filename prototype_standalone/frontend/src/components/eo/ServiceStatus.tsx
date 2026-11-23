/* ── Global header SERVICE STATUS indicator (every V3 + V4 route) ──
 * Rendered once in the shared Shell header, which stays mounted across navigation and V3/V4
 * switching, so a single polling loop and state serve the whole app.
 * Polls the service health source (real .NET host or labelled simulator) every
 * HEALTH_POLL_MS. One check at a time; each check is aborted after HEALTH_TIMEOUT_MS; the
 * next check is scheduled only after the previous one finished. Everything is cleaned up on
 * unmount. */
import { useEffect, useReducer, useRef, useState } from "react";
import {
  HEALTH_POLL_MS,
  HEALTH_TIMEOUT_MS,
  INITIAL_SERVICE_HEALTH,
  SOURCE_LABEL,
  STATE_LABEL,
  reduceServiceHealth,
  type HealthSource,
  type ServiceHealthSnapshot,
  type ServiceState,
} from "@/lib/eo/serviceHealth";
import { StatusIndicator, type StatusTone } from "./StatusIndicator";
import { useConnectionState } from "@/lib/eo/connectionStore";

/** ONLINE only after a successful response; CONNECTING while checking; OFFLINE after failures. */
const SERVICE_TONE: Record<ServiceState, StatusTone> = { online: "pos", connecting: "warn", offline: "neg" };
const SERVICE_RESERVE = Object.values(STATE_LABEL).reduce((a, b) => (b.length > a.length ? b : a), "");
import { pickServiceHealthSource, type ServiceHealthSource } from "@/lib/eo/serviceHealthBackend";

export function useServiceHealth(
  makeSource: () => ServiceHealthSource = pickServiceHealthSource,
  pollMs = HEALTH_POLL_MS,
  timeoutMs = HEALTH_TIMEOUT_MS,
): ServiceHealthSnapshot & { source: HealthSource | null } {
  const [snap, dispatch] = useReducer(reduceServiceHealth, INITIAL_SERVICE_HEALTH);
  const [source, setSource] = useState<HealthSource | null>(null);
  const makeRef = useRef(makeSource);

  useEffect(() => {
    const src = makeRef.current();
    setSource(src.mode);
    let alive = true;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let ctrl: AbortController | null = null;

    const schedule = () => {
      if (timer) clearTimeout(timer);
      if (alive) timer = setTimeout(run, pollMs);
    };
    async function run() {
      if (!alive || inFlight) return; // never overlap requests
      inFlight = true;
      if (timer) clearTimeout(timer);
      ctrl = new AbortController();
      const kill = setTimeout(() => ctrl?.abort(), timeoutMs);
      dispatch({ type: "check-start" });
      let result;
      try {
        result = await src.check(ctrl.signal);
      } catch (e) {
        result = { ok: false, at: Date.now(), latencyMs: 0, detail: e instanceof Error ? e.message : "Check failed" };
      } finally {
        clearTimeout(kill);
        inFlight = false;
        ctrl = null;
      }
      if (!alive) return;
      dispatch({ type: "check-done", result });
      schedule();
    }

    const onOffline = () => {
      ctrl?.abort();
      dispatch({ type: "network-lost", at: Date.now() });
    };
    const onOnline = () => void run();
    window.addEventListener("offline", onOffline);
    window.addEventListener("online", onOnline);
    void run();
    return () => {
      alive = false;
      if (timer) clearTimeout(timer);
      ctrl?.abort();
      window.removeEventListener("offline", onOffline);
      window.removeEventListener("online", onOnline);
    };
  }, [pollMs, timeoutMs]);

  return { ...snap, source };
}

const clock = (t: number) =>
  new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });

export function GlobalServiceStatus() {
  const h = useServiceHealth();
  const conn = useConnectionState();
  const demo = false;
  const last = h.last;
  const srcLabel =
    h.source === "native" && conn.phase === "CONNECTED"
      ? conn.serviceConnected
        ? "Windows Service + Desktop Host"
        : "Desktop Host Backend (In-Process Core/V4)"
      : h.source
        ? SOURCE_LABEL[h.source]
        : "—";
  return (
    <StatusIndicator
      tone={SERVICE_TONE[h.state]}
      className="eo-st-head"
      live
      demo={demo}
      reserve={SERVICE_RESERVE}
      tip={
        <>
          <p className="eo-svc-tip-title">Service status</p>
        <dl>
          <div>
            <dt>Source</dt>
            <dd>{srcLabel}</dd>
          </div>
          <div>
            <dt>Lifecycle</dt>
            <dd>{conn.phase}</dd>
          </div>
          <div>
            <dt>Last check</dt>
            <dd>{last ? clock(last.at) : h.checking ? "Checking…" : "Not checked yet"}</dd>
          </div>
          <div>
            <dt>Result</dt>
            <dd data-ok={last ? String(last.ok) : undefined}>
              {last ? `${last.ok ? "OK" : "Failed"} · ${last.detail}` : "Waiting for first response"}
            </dd>
          </div>
          {last && last.latencyMs > 0 && (
            <div>
              <dt>Response</dt>
              <dd>{last.latencyMs} ms</dd>
            </div>
          )}
          {h.failures > 1 && (
            <div>
              <dt>Failed checks</dt>
              <dd>{h.failures} in a row</dd>
            </div>
          )}
          <div>
            <dt>Rechecks</dt>
            <dd>Every {HEALTH_POLL_MS / 1000} s{h.checking ? " · checking now" : ""}</dd>
          </div>
        </dl>
        {!last?.ok && (
          <p className="eo-svc-tip-note">
            Waiting for the ERROR OPTIMIZER background service connection.
          </p>
        )}
        </>
      }
    >
      {STATE_LABEL[h.state]}
    </StatusIndicator>
  );
}

/** @deprecated use GlobalServiceStatus */
export const ServiceStatusBadge = GlobalServiceStatus;

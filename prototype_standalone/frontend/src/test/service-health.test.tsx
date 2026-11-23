import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  INITIAL_SERVICE_HEALTH,
  STATE_LABEL,
  interpretHttpHealth,
  reduceServiceHealth,
  type HealthCheckResult,
} from "@/lib/eo/serviceHealth";
import { SimulatedServiceHealth, type ServiceHealthSource } from "@/lib/eo/serviceHealthBackend";
import { useServiceHealth } from "@/components/eo/ServiceStatus";

const ok = (o = true): HealthCheckResult => ({ ok: o, at: 1, latencyMs: 5, detail: o ? "Healthy" : "Down" });

describe("service health rules", () => {
  it("starts CONNECTING and uses the three required labels", () => {
    expect(INITIAL_SERVICE_HEALTH.state).toBe("connecting");
    expect(STATE_LABEL).toEqual({
      connecting: "CONNECTING...",
      online: "SERVICE ONLINE",
      offline: "SERVICE OFFLINE",
    });
  });
  it("goes online only after a successful response", () => {
    const s = reduceServiceHealth(INITIAL_SERVICE_HEALTH, { type: "check-start" });
    expect(s.state).toBe("connecting");
    expect(reduceServiceHealth(s, { type: "check-done", result: ok() }).state).toBe("online");
  });
  it("goes offline when a check fails, even right after being online", () => {
    const online = reduceServiceHealth(INITIAL_SERVICE_HEALTH, { type: "check-done", result: ok() });
    const rechecking = reduceServiceHealth(online, { type: "check-start" });
    expect(rechecking.state).toBe("online");
    expect(reduceServiceHealth(rechecking, { type: "check-done", result: ok(false) }).state).toBe("offline");
  });
  it("shows CONNECTING again while retrying after an outage", () => {
    const off = reduceServiceHealth(INITIAL_SERVICE_HEALTH, { type: "check-done", result: ok(false) });
    expect(reduceServiceHealth(off, { type: "check-start" }).state).toBe("connecting");
  });
  it("reads ASP.NET Core health responses", () => {
    expect(interpretHttpHealth(200, "Healthy").ok).toBe(true);
    expect(interpretHttpHealth(503, "Unhealthy").ok).toBe(false);
    expect(interpretHttpHealth(200, '{"status":"Unhealthy"}').ok).toBe(false);
    expect(interpretHttpHealth(500, "").ok).toBe(false);
  });
});

describe("useServiceHealth polling", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const scripted = (answers: boolean[], delay = 100) => {
    let calls = 0;
    let active = 0;
    let maxActive = 0;
    const src: ServiceHealthSource = {
      mode: "native",
      check: () =>
        new Promise((r) => {
          const i = calls++;
          active++;
          maxActive = Math.max(maxActive, active);
          setTimeout(() => {
            active--;
            r(ok(answers[Math.min(i, answers.length - 1)]));
          }, delay);
        }),
    };
    return { src, stats: () => ({ calls, maxActive }) };
  };

  it("Connecting → Online, then Online → Offline on a failed recheck", async () => {
    const { src } = scripted([true, false]);
    const { result } = renderHook(() => useServiceHealth(() => src, 10_000, 4_000));
    expect(result.current.state).toBe("connecting");
    await act(() => vi.advanceTimersByTimeAsync(150));
    expect(result.current.state).toBe("online");
    await act(() => vi.advanceTimersByTimeAsync(10_000));
    expect(result.current.state).toBe("online"); // still checking
    await act(() => vi.advanceTimersByTimeAsync(150));
    expect(result.current.state).toBe("offline");
  });

  it("Connecting → Offline when the service never answers in time", async () => {
    const slow: ServiceHealthSource = {
      mode: "native",
      check: (signal) =>
        new Promise((r) => {
          signal.addEventListener("abort", () => r({ ok: false, at: 0, latencyMs: 0, detail: "Timed out" }));
        }),
    };
    const { result } = renderHook(() => useServiceHealth(() => slow, 10_000, 4_000));
    await act(() => vi.advanceTimersByTimeAsync(4_100));
    expect(result.current.state).toBe("offline");
    expect(result.current.last?.detail).toBe("Timed out");
  });

  it("polls every 10 s without overlapping and stops after unmount", async () => {
    const { src, stats } = scripted([true], 100);
    const { unmount } = renderHook(() => useServiceHealth(() => src, 10_000, 4_000));
    await act(() => vi.advanceTimersByTimeAsync(30_500));
    expect(stats().calls).toBe(4);
    expect(stats().maxActive).toBe(1);
    unmount();
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    expect(stats().calls).toBe(4);
  });

  it("the browser simulator is labelled as simulated", () => {
    expect(new SimulatedServiceHealth().mode).toBe("simulated");
  });
});

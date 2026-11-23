import { act, render, renderHook, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  EMPTY_READINGS,
  SAMPLE_READINGS,
  bottleneck,
  evaluateScan,
  parseReadings,
  v4Readiness,
} from "@/lib/eo/dashboardScan";
import { SAMPLE_SCAN_MS, useSystemScan } from "@/components/eo/useSystemScan";
import { HealthCheck } from "@/components/eo/HealthCheck";

describe("dashboard scan score", () => {
  it("calculates the score from the scanned readings", () => {
    const ev = evaluateScan(SAMPLE_READINGS);
    expect(ev.categories.map((c) => c.value)).toEqual([100, 100, 100, 83]);
    expect(ev.overall).toBe(97);
    expect(bottleneck(ev)?.key).toBe("efficiency");
  });

  it("has no score before anything is measured", () => {
    expect(evaluateScan(EMPTY_READINGS).overall).toBeNull();
  });

  it("a different scan result gives a different score", () => {
    const hot = { ...SAMPLE_READINGS, cpu: { ...SAMPLE_READINGS.cpu, loadPct: 100 } };
    expect(evaluateScan(hot).overall).toBe(76);
  });

  it("V4 readiness needs a host score and adds connection and engines", () => {
    expect(v4Readiness(null, { verifiedPasses: 3, totalEligibleAreas: 5, alreadyOptimal: 0 })).toBeNull();
    expect(v4Readiness(97, { verifiedPasses: 0, totalEligibleAreas: 5, alreadyOptimal: 0 })).toBe(78);
    expect(v4Readiness(97, { verifiedPasses: 5, totalEligibleAreas: 5, alreadyOptimal: 0 })).toBe(98);
  });

  it("rejects a host payload with no usable reading", () => {
    expect(parseReadings({ cpu: { loadPct: "high" } })).toBeNull();
    expect(parseReadings(SAMPLE_READINGS)).toEqual(SAMPLE_READINGS);
  });
});

describe("scan behaviour", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("a dashboard scan starts on load and ends with a labelled sample result", () => {
    const { result } = renderHook(() => useSystemScan({ auto: true }));
    expect(result.current.phase).toBe("scanning");
    expect(result.current.result).toBeNull();
    act(() => {
      vi.advanceTimersByTime(SAMPLE_SCAN_MS + 200);
    });
    expect(result.current.phase).toBe("done");
    expect(result.current.result?.source).toBe("sample");
  });

  it("without auto, no scan starts", () => {
    const { result } = renderHook(() => useSystemScan({ auto: false }));
    act(() => {
      vi.advanceTimersByTime(SAMPLE_SCAN_MS + 200);
    });
    expect(result.current.phase).toBe("idle");
  });

  it("System Health Check never runs on open and starts at 0 / 100", () => {
    window.localStorage.setItem("eo-health-last", "yesterday");
    render(<HealthCheck />);
    act(() => {
      vi.advanceTimersByTime(5000);
    });
    const ring = screen.getByRole("img", { name: "System health 0 out of 100, not checked yet" });
    expect(ring.textContent).toContain("0");
    expect(ring.textContent).toContain("/ 100");
    expect(ring.textContent).not.toContain("—");
    expect(screen.queryByText("Analysing…")).toBeNull();
    expect(screen.getByRole("button", { name: /Run health check/i })).toBeTruthy();
  });
});

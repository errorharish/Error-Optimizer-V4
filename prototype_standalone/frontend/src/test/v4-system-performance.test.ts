import { describe, expect, it } from "vitest";
import { SAMPLE_READINGS, type ScanResult } from "@/lib/eo/dashboardScan";
import {
  LIVE_STALE_MS,
  isLiveFresh,
  parseLiveSample,
  usageLevel,
  v4PerfMetrics,
  type PerfMetric,
} from "@/lib/eo/v4Telemetry";

const sample: ScanResult = { source: "sample", readings: SAMPLE_READINGS, at: "9:00:00 AM" };
const native: ScanResult = { source: "native", readings: SAMPLE_READINGS, at: "9:00:00 AM" };
const byKey = (ms: ReturnType<typeof v4PerfMetrics>) =>
  Object.fromEntries(ms.map((m) => [m.key, m])) as Partial<Record<PerfMetric["key"], PerfMetric>>;

describe("V4 System Performance", () => {
  it("never shows the browser sample snapshot as telemetry", () => {
    const ms = v4PerfMetrics({ scan: sample, live: null, liveFresh: false });
    expect(ms.map((m) => m.value)).toEqual([null, null, null, null]);
    expect(ms.every((m) => m.source === "unavailable" && m.detail === "Unavailable")).toBe(true);
  });

  it("shows the Windows scan readings when there is no live feed", () => {
    const m = byKey(v4PerfMetrics({ scan: native, live: null, liveFresh: false }));
    expect(m.cpu?.value).toBe(18);
    expect(m.gpu?.value).toBe(14);
    expect(Math.round(m.ram?.value ?? 0)).toBe(38);
    expect(m.ram?.detail).toBe("12.2 / 32 GB in use");
    expect(m.background?.value).toBe(142);
    expect(m.cpu?.source).toBe("scan");
  });

  it("live samples replace scan values and are marked live only while fresh", () => {
    const live = parseLiveSample({
      type: "telemetry",
      cpu: { loadPct: 72 },
      gpu: { loadPct: 91 },
      ram: { usedGb: 16, totalGb: 32 },
      backgroundProcesses: 120,
    });
    const fresh = byKey(v4PerfMetrics({ scan: native, live, liveFresh: true }));
    expect([fresh.cpu?.value, fresh.gpu?.value, fresh.ram?.value, fresh.background?.value]).toEqual([
      72, 91, 50, 120,
    ]);
    expect(fresh.cpu?.source).toBe("live");
    const stale = byKey(v4PerfMetrics({ scan: native, live, liveFresh: false }));
    expect(stale.cpu?.source).toBe("scan");
  });

  it("usage indicator: under 60 Normal, 60-84 Elevated, 85+ High", () => {
    expect([usageLevel(59), usageLevel(60), usageLevel(84), usageLevel(85), usageLevel(null)]).toEqual([
      "Normal",
      "Elevated",
      "Elevated",
      "High",
      null,
    ]);
  });

  it("the live indicator turns off once samples stop arriving", () => {
    expect(isLiveFresh(1000, 1000 + LIVE_STALE_MS)).toBe(true);
    expect(isLiveFresh(1000, 1001 + LIVE_STALE_MS)).toBe(false);
    expect(isLiveFresh(null, 1000)).toBe(false);
  });

  it("ignores host messages without usable readings", () => {
    expect(parseLiveSample({ type: "telemetry", cpu: { loadPct: "high" } })).toBeNull();
  });
});

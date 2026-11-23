import { describe, expect, it } from "vitest";
import {
  EMPTY_THERMALS,
  THERMAL_HISTORY,
  graphGrid,
  graphRange,
  graphTicks,
  thermalAreaPath,
  thermalWindow,
  overallThermal,
  parseThermalSample,
  pushThermal,
  sensorsAvailable,
  thermalFromScan,
  thermalLevel,
  thermalPath,
} from "@/lib/eo/thermals";
import { nextSimThermal, seedSimHistory } from "@/lib/eo/simulatedThermals";
import { SAMPLE_READINGS, type ScanResult } from "@/lib/eo/dashboardScan";

describe("Temperature Monitoring rules", () => {
  it("reads CPU, GPU, GPU hotspot and SSD from the host telemetry message", () => {
    expect(
      parseThermalSample({
        type: "telemetry",
        cpu: { tempC: 51.24 },
        gpu: { tempC: 48, hotspotC: 61 },
        ssd: { tempC: 39 },
      }),
    ).toEqual({ cpu: 51.2, gpu: 48, gpuHotspot: 61, ssd: 39 });
  });

  it("leaves a sensor the host does not report unavailable instead of filling it in", () => {
    const s = parseThermalSample({ type: "telemetry", cpu: { tempC: 50, loadPct: 20 }, gpu: { tempC: 45 } });
    expect(s).toEqual({ cpu: 50, gpu: 45, gpuHotspot: null, ssd: null });
    expect(sensorsAvailable(s)).toEqual({ available: 2, total: 4 });
  });

  it("ignores telemetry without any temperature and implausible sensor values", () => {
    expect(parseThermalSample({ type: "telemetry", cpu: { loadPct: 30 } })).toBeNull();
    expect(parseThermalSample({ cpu: { tempC: 400 }, gpu: { tempC: Number.NaN } })).toBeNull();
  });

  it("classifies NORMAL / WARM / CRITICAL per sensor", () => {
    expect(thermalLevel("cpu", 79)).toBe("NORMAL");
    expect(thermalLevel("cpu", 80)).toBe("WARM");
    expect(thermalLevel("cpu", 95)).toBe("CRITICAL");
    expect(thermalLevel("ssd", 60)).toBe("WARM");
    expect(thermalLevel("ssd", null)).toBeNull();
  });

  it("reports the worst available sensor and nothing when no sensor reports", () => {
    expect(overallThermal({ cpu: 50, gpu: 85, gpuHotspot: null, ssd: 40 })).toBe("WARM");
    expect(overallThermal({ cpu: 50, gpu: 60, gpuHotspot: 70, ssd: 72 })).toBe("CRITICAL");
    expect(overallThermal(EMPTY_THERMALS)).toBeNull();
  });

  it("never treats browser sample scans as real sensor readings", () => {
    const sample: ScanResult = { source: "sample", readings: SAMPLE_READINGS, at: "12:00" };
    expect(thermalFromScan(sample)).toBeNull();
    const native: ScanResult = { ...sample, source: "native" };
    expect(thermalFromScan(native)).toEqual({ cpu: 42, gpu: 44, gpuHotspot: null, ssd: null });
  });

  it("keeps a fixed-length graph history", () => {
    let h = seedSimHistory(THERMAL_HISTORY);
    h = pushThermal(h, { cpu: 50, gpu: 50, gpuHotspot: 60, ssd: 40 });
    expect(h).toHaveLength(THERMAL_HISTORY);
    expect(h[h.length - 1]?.cpu).toBe(50);
  });

  it("breaks the graph line at missing readings", () => {
    const d = thermalPath([40, 42, null, 50, 52], 100, 100, 5);
    expect(d.match(/M/g)).toHaveLength(2);
    expect(thermalPath([null, null], 100, 100)).toBe("");
  });

  it("scales the graph to the readings in 10 °C steps, at least 40 °C tall", () => {
    const s = (cpu: number, ssd: number) => ({ cpu, gpu: null, gpuHotspot: null, ssd });
    expect(graphRange([s(36, 48)])).toEqual({ min: 30, max: 70 });
    expect(graphRange([s(34, 70)])).toEqual({ min: 20, max: 80 });
    expect(graphRange([])).toEqual({ min: 20, max: 100 });
    expect(graphRange([s(36, 72)])).toEqual({ min: 30, max: 90 });
    expect(graphGrid({ min: 30, max: 70 })).toEqual([40, 50, 60]);
  });

  it("plots 30 SEC, 1 MIN and 5 MIN windows from the bounded history", () => {
    const s = (c: number) => ({ cpu: c, gpu: c, gpuHotspot: null, ssd: c });
    const hist = Array.from({ length: THERMAL_HISTORY }, (_, i) => s(40 + (i % 10)));
    expect(THERMAL_HISTORY).toBe(200);
    // 1.5 s preview interval: 30 s = 20 samples; 60 s = 40 samples in 20 pairs; 5 min = 200 samples in 29 buckets of 7.
    expect(thermalWindow(hist, 500, 30, 1500).values).toHaveLength(20);
    const one = thermalWindow(hist, 500, 60, 1500);
    expect(one.values).toHaveLength(20);
    expect(one.stepMs).toBe(3000);
    const five = thermalWindow(hist, 500, 300, 1500);
    expect(five.values).toHaveLength(29);
    expect(five.stepMs).toBe(10500);
    // The newest sample is always plotted.
    expect(thermalWindow([...hist.slice(1), s(77)], 501, 30, 1500).values.at(-1)?.cpu).toBe(77);
  });

  it("keeps a sensor unavailable inside a window instead of filling it in", () => {
    const hist = Array.from({ length: 20 }, () => ({ cpu: 50, gpu: null, gpuHotspot: null, ssd: null }));
    const { values } = thermalWindow(hist, 20, 30, 1500);
    expect(values.every((v) => v.gpu === null && v.cpu === 50)).toBe(true);
  });

  it("closes the gradient area to the bottom and labels five axis steps", () => {
    const d = thermalAreaPath([40, 42, 44], 600, 176, 3);
    expect(d.endsWith("Z")).toBe(true);
    expect(d).toContain("L600 176L0 176Z");
    expect(thermalAreaPath([null, null], 600, 176)).toBe("");
    expect(graphTicks({ min: 30, max: 70 })).toEqual([70, 60, 50, 40, 30]);
  });

  it("keeps simulated temperatures inside realistic bands", () => {
    let s = { cpu: 46, gpu: 41, gpuHotspot: 52, ssd: 38 };
    for (let i = 0; i < 500; i++) {
      s = nextSimThermal(s) as typeof s;
      expect(s.cpu).toBeGreaterThanOrEqual(38);
      expect(s.cpu).toBeLessThanOrEqual(72);
      expect(s.gpuHotspot - s.gpu).toBeGreaterThanOrEqual(8 - 1e-9); // one-decimal rounding
      expect(s.ssd).toBeLessThanOrEqual(48);
    }
  });
});

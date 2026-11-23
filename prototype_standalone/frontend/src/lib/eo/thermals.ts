// Temperature Monitoring — pure rules for the V3 Dashboard thermal card. Real readings come only
// from the Windows host: optional thermal fields on the existing WebView2 `telemetry` message
// (`cpu.tempC`, `gpu.tempC`, `gpu.hotspotC`, `ssd.tempC`) or, before the feed starts, the native
// dashboard scan. A sensor the host does not report is `null` → SENSOR UNAVAILABLE; it is never
// filled in. Browser-preview values come from simulatedThermals.ts and are labelled SIMULATED.
import type { ScanResult } from "./dashboardScan";

export type ThermalSensor = "cpu" | "gpu" | "gpuHotspot" | "ssd";
export type ThermalSample = Record<ThermalSensor, number | null>;
export type ThermalLevel = "NORMAL" | "WARM" | "CRITICAL";

export interface ThermalSensorSpec {
  key: ThermalSensor;
  label: string;
  /** WARM from this temperature (°C). */
  warmC: number;
  /** CRITICAL from this temperature (°C). */
  criticalC: number;
}

/** CPU/GPU limits match the score's thermal rule (dashboardScan → thermalScore). */
export const THERMAL_SENSORS: readonly ThermalSensorSpec[] = [
  { key: "cpu", label: "CPU", warmC: 80, criticalC: 95 },
  { key: "gpu", label: "GPU", warmC: 83, criticalC: 95 },
  { key: "gpuHotspot", label: "GPU HOTSPOT", warmC: 95, criticalC: 105 },
  { key: "ssd", label: "SSD", warmC: 60, criticalC: 70 },
];

export const EMPTY_THERMALS: ThermalSample = { cpu: null, gpu: null, gpuHotspot: null, ssd: null };

/** Samples kept (bounded): 5 minutes at the 1.5 s preview interval, longer at the 2 s host interval. */
export const THERMAL_HISTORY = 200;
/** Default graph scale (no readings yet). */
export const GRAPH_MIN_C = 20;
export const GRAPH_MAX_C = 100;
/** Smallest visible span, so small fluctuations never look dramatic. */
export const GRAPH_MIN_SPAN_C = 40;

export interface GraphRange {
  min: number;
  max: number;
}

const spec = (k: ThermalSensor) => THERMAL_SENSORS.find((s) => s.key === k)!;
const o = (v: unknown): Record<string, unknown> =>
  v && typeof v === "object" ? (v as Record<string, unknown>) : {};
/** Physically plausible sensor value, else null (a broken sensor is not a reading). */
const temp = (v: unknown) =>
  typeof v === "number" && Number.isFinite(v) && v > -20 && v < 150 ? Math.round(v * 10) / 10 : null;

export function thermalLevel(key: ThermalSensor, c: number | null): ThermalLevel | null {
  if (c === null) return null;
  const s = spec(key);
  return c >= s.criticalC ? "CRITICAL" : c >= s.warmC ? "WARM" : "NORMAL";
}

const RANK: Record<ThermalLevel, number> = { NORMAL: 0, WARM: 1, CRITICAL: 2 };

/** Worst level across the available sensors; null when no sensor reports. */
export function overallThermal(s: ThermalSample | null): ThermalLevel | null {
  if (!s) return null;
  let worst: ThermalLevel | null = null;
  for (const { key } of THERMAL_SENSORS) {
    const l = thermalLevel(key, s[key]);
    if (l && (worst === null || RANK[l] > RANK[worst])) worst = l;
  }
  return worst;
}

export function sensorsAvailable(s: ThermalSample | null): { available: number; total: number } {
  const total = THERMAL_SENSORS.length;
  if (!s) return { available: 0, total };
  return { available: THERMAL_SENSORS.filter(({ key }) => s[key] !== null).length, total };
}

/** Thermal fields of a host `telemetry` message; null when it carries no temperature at all. */
export function parseThermalSample(raw: unknown): ThermalSample | null {
  const m = o(raw);
  const gpu = o(m["gpu"]);
  const s: ThermalSample = {
    cpu: temp(o(m["cpu"])["tempC"]),
    gpu: temp(gpu["tempC"]),
    gpuHotspot: temp(gpu["hotspotC"]),
    ssd: temp(o(m["ssd"])["tempC"]),
  };
  return Object.values(s).some((v) => v !== null) ? s : null;
}

/** CPU/GPU/SSD temperatures from a native dashboard scan. Browser sample scans are never used. */
export function thermalFromScan(scan: ScanResult | null): ThermalSample | null {
  if (!scan || scan.source !== "native") return null;
  const s: ThermalSample = {
    ...EMPTY_THERMALS,
    cpu: temp(scan.readings.cpu.tempC),
    gpu: temp(scan.readings.gpu.tempC),
    gpuHotspot: temp(scan.readings.gpu.hotspotC),
    ssd: temp(scan.readings.storage.tempC),
  };
  return Object.values(s).some((v) => v !== null) ? s : null;
}

export function pushThermal(
  history: readonly ThermalSample[],
  s: ThermalSample,
  max: number = THERMAL_HISTORY,
): ThermalSample[] {
  const next = history.length >= max ? history.slice(history.length - max + 1) : history.slice();
  next.push(s);
  return next;
}

/**
 * Graph scale in 10 °C steps around the plotted readings (at least 40 °C tall), so lines use
 * the graph height and the axis only moves when readings cross a 10 °C step.
 */
export function graphRange(history: readonly ThermalSample[]): GraphRange {
  let lo = Infinity;
  let hi = -Infinity;
  for (const h of history)
    for (const { key } of THERMAL_SENSORS) {
      const v = h[key];
      if (v === null) continue;
      if (v < lo) lo = v;
      if (v > hi) hi = v;
    }
  if (lo === Infinity) return { min: GRAPH_MIN_C, max: GRAPH_MAX_C };
  let min = Math.max(0, Math.floor((lo - 5) / 10) * 10);
  let max = Math.ceil((hi + 5) / 10) * 10;
  if (max - min < GRAPH_MIN_SPAN_C) {
    const extra = GRAPH_MIN_SPAN_C - (max - min);
    min = Math.max(0, min - Math.floor(extra / 20) * 10);
    max = min + Math.max(GRAPH_MIN_SPAN_C, max - min);
  }
  // Span in 20 °C steps, so the five axis labels land on whole 5 °C values.
  if ((max - min) % 20) max += 10;
  return { min, max };
}

/** Three evenly spaced grid temperatures inside the range. */
export const graphGrid = (r: GraphRange): number[] =>
  [1, 2, 3].map((i) => Math.round(r.min + ((r.max - r.min) * i) / 4));

/** Vertical position (0 = top, 1 = bottom) of a temperature on the graph scale. */
export function graphY(c: number, r: GraphRange = { min: GRAPH_MIN_C, max: GRAPH_MAX_C }): number {
  const f = (c - r.min) / (r.max - r.min);
  return 1 - Math.min(1, Math.max(0, f));
}

type Pt = readonly [number, number];

/** Plotted points split into runs; a missing reading ends a run instead of being drawn. */
function runs(
  values: readonly (number | null)[],
  w: number,
  h: number,
  slots: number,
  range: GraphRange,
): Pt[][] {
  const offset = Math.max(0, slots - values.length);
  const step = slots > 1 ? w / (slots - 1) : 0;
  const out: Pt[][] = [];
  let run: Pt[] = [];
  values.forEach((v, i) => {
    if (v === null) {
      if (run.length) out.push(run);
      run = [];
    } else run.push([(offset + i) * step, graphY(v, range) * h]);
  });
  if (run.length) out.push(run);
  return out;
}

const f = (n: number) => (Math.round(n * 100) / 100).toString();

/** Same curve as System Performance Telemetry: horizontal-tangent cubic segments (no overshoot). */
function curve(run: readonly Pt[]): string {
  let d = `M${f(run[0]![0])} ${f(run[0]![1])}`;
  if (run.length === 1) return `${d}h0.01`;
  for (let i = 1; i < run.length; i++) {
    const [px, py] = run[i - 1]!;
    const [x, y] = run[i]!;
    const mx = f((px + x) / 2);
    d += `C${mx} ${f(py)} ${mx} ${f(y)} ${f(x)} ${f(y)}`;
  }
  return d;
}

/**
 * Smooth SVG line for one series in a `w × h` box. The newest sample sits on the right edge and
 * older ones scroll left across `slots` positions. Missing readings break the line.
 */
export function thermalPath(
  values: readonly (number | null)[],
  w: number,
  h: number,
  slots: number = THERMAL_HISTORY,
  range: GraphRange = { min: GRAPH_MIN_C, max: GRAPH_MAX_C },
): string {
  return runs(values, w, h, slots, range).map(curve).join("");
}

/** Gradient area under each run of `thermalPath`, closed to the bottom edge. */
export function thermalAreaPath(
  values: readonly (number | null)[],
  w: number,
  h: number,
  slots: number = THERMAL_HISTORY,
  range: GraphRange = { min: GRAPH_MIN_C, max: GRAPH_MAX_C },
): string {
  return runs(values, w, h, slots, range)
    .filter((r) => r.length > 1)
    .map((r) => `${curve(r)}L${f(r[r.length - 1]![0])} ${f(h)}L${f(r[0]![0])} ${f(h)}Z`)
    .join("");
}

/** Five axis temperatures from the top of the graph to the bottom (grid lines at quarters). */
export const graphTicks = (r: GraphRange): number[] =>
  [4, 3, 2, 1, 0].map((i) => Math.round(r.min + ((r.max - r.min) * i) / 4));

/** Graph time windows, same choices as System Performance Telemetry. */
export const THERMAL_WINDOWS = [
  { label: "30 SEC", seconds: 30, axis: "−30s", text: "Last 30 s" },
  { label: "1 MIN", seconds: 60, axis: "−60s", text: "Last 60 s" },
  { label: "5 MIN", seconds: 300, axis: "−5m", text: "Last 5 min" },
] as const;
export type ThermalWindow = (typeof THERMAL_WINDOWS)[number]["label"];

/** Most points drawn per series; longer windows average neighbouring samples into buckets. */
export const GRAPH_MAX_POINTS = 30;

/**
 * Samples plotted for a time window. `total` counts every sample ever received, so buckets stay
 * aligned to the same samples between updates (the line never reshuffles); the newest bucket may
 * be partial, so the latest reading is always on the graph. A bucket with no reading for a
 * sensor stays null (unavailable), never filled in.
 */
export function thermalWindow(
  history: readonly ThermalSample[],
  total: number,
  seconds: number,
  intervalMs: number,
): { values: ThermalSample[]; slots: number; stepMs: number } {
  const need = Math.max(2, Math.ceil((seconds * 1000) / Math.max(1, intervalMs)));
  const per = Math.max(1, Math.ceil(need / GRAPH_MAX_POINTS));
  const slots = Math.ceil(need / per);
  const first = Math.max(0, total - history.length);
  const startAbs = Math.max(first, total - need);
  const buckets = new Map<number, ThermalSample[]>();
  for (let abs = startAbs; abs < total; abs++) {
    const b = Math.floor(abs / per);
    const s = history[abs - first];
    if (!s) continue;
    const list = buckets.get(b);
    if (list) list.push(s);
    else buckets.set(b, [s]);
  }
  const keys = [...buckets.keys()].sort((a, b) => a - b).slice(-slots);
  const values = keys.map((k) => {
    const list = buckets.get(k)!;
    const out = { ...EMPTY_THERMALS };
    for (const { key } of THERMAL_SENSORS) {
      const vals = list.map((x) => x[key]).filter((v): v is number => v !== null);
      out[key] = vals.length ? Math.round((vals.reduce((a, c) => a + c, 0) / vals.length) * 10) / 10 : null;
    }
    return out;
  });
  return { values, slots, stepMs: per * intervalMs };
}

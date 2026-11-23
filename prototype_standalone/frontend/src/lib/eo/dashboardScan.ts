// Dashboard system scan — pure rules shared by the V3 Dashboard and the V4 Performance Center.
// The scan collects CPU, RAM, GPU, storage and responsiveness readings (from the Windows host
// when connected, otherwise a clearly labelled SAMPLE snapshot) and turns them into scores with
// documented rules. Missing readings are `null` and are left out — never counted as perfect.
import { capacityScore, thermalScore, utilizationScore } from "./health";
import { scoreStatus, type ScoreStatus } from "./score";

export type ScanSource = "native" | "sample";

export interface ScanReadings {
  cpu: {
    model: string | null;
    cores: number | null;
    threads: number | null;
    loadPct: number | null;
    tempC: number | null;
    clockMhz?: number | null | undefined;
  };
  ram: {
    totalGb: number | null;
    usedGb: number | null;
    spec: string | null;
    commitPct?: number | null | undefined;
  };
  gpu: {
    model: string | null;
    loadPct: number | null;
    tempC: number | null;
    hotspotC?: number | null | undefined;
    vramUsedGb: number | null;
    vramTotalGb: number | null;
  };
  storage: {
    model: string | null;
    usedGb: number | null;
    totalGb: number | null;
    healthPct: number | null;
    tempC?: number | null | undefined;
  };
  latency: { dpcUs: number | null; timerMs: number | null };
  backgroundProcesses: number | null;
}

export interface ScanResult {
  source: ScanSource;
  readings: ScanReadings;
  /** Local time the scan finished. */
  at: string;
}

/** Browser-preview snapshot — matches the Dashboard sample tiles; nothing is measured. */
export const SAMPLE_READINGS: ScanReadings = {
  cpu: { model: "AMD Ryzen 7 7800X3D", cores: 8, threads: 16, loadPct: 18, tempC: 42 },
  ram: { totalGb: 32, usedGb: 12.2, spec: "DDR5-6000 CL30" },
  gpu: { model: "RTX 4080 Super", loadPct: 14, tempC: 44, vramUsedGb: 1.4, vramTotalGb: 16 },
  storage: { model: "Samsung 990 PRO", usedGb: 920, totalGb: 2000, healthPct: 100 },
  latency: { dpcUs: 22, timerMs: 0.5 },
  backgroundProcesses: 142,
};

/** Nothing measured yet — used to lay out the category rows before the first scan. */
export const EMPTY_READINGS: ScanReadings = {
  cpu: { model: null, cores: null, threads: null, loadPct: null, tempC: null },
  ram: { totalGb: null, usedGb: null, spec: null },
  gpu: { model: null, loadPct: null, tempC: null, vramUsedGb: null, vramTotalGb: null },
  storage: { model: null, usedGb: null, totalGb: null, healthPct: null },
  latency: { dpcUs: null, timerMs: null },
  backgroundProcesses: null,
};

export const SCAN_STAGES = [
  "Detecting CPU…",
  "Reading memory…",
  "Detecting GPU…",
  "Reading storage…",
  "Measuring responsiveness…",
  "Calculating score…",
] as const;

export function scanStageAt(progress: number): string {
  const i = Math.floor((Math.max(0, Math.min(99.999, progress)) / 100) * SCAN_STAGES.length);
  return SCAN_STAGES[i] ?? "Calculating score…";
}

const clamp = (v: number) => Math.min(100, Math.max(0, v));

/** DPC latency ≤ 50 µs scores 100, then −0.2 per µs (floor 20). */
export const dpcScore = (us: number) => Math.max(20, clamp(us <= 50 ? 100 : 100 - (us - 50) * 0.2));
/** Timer resolution ≤ 1 ms scores 100, then −3 per ms (15.6 ms → 56). */
export const timerScore = (ms: number) => clamp(ms <= 1 ? 100 : 100 - (ms - 1) * 3);
/** ≤ 100 background processes scores 100, then −0.4 per process (floor 30). */
export const processScore = (n: number) => Math.max(30, clamp(n <= 100 ? 100 : 100 - (n - 100) * 0.4));

const pct = (used: number | null, total: number | null) =>
  used === null || total === null || total <= 0 ? null : (used / total) * 100;

const worst = (xs: (number | null)[]) => {
  const s = xs.filter((v): v is number => v !== null && Number.isFinite(v));
  return s.length ? Math.round(Math.min(...s)) : null;
};

export interface ScanCategory {
  key: "performance" | "responsiveness" | "memory" | "efficiency";
  label: string;
  weight: number;
  /** 0–100, or null when none of its readings were available. */
  value: number | null;
  basis: string;
}

export interface ScanEvaluation {
  categories: ScanCategory[];
  /** Weighted average of measured categories, or null when nothing was measured. */
  overall: number | null;
  status: ScoreStatus | "Not enough data";
}

/** Overall Optimization Score from scan readings. Same readings → same score. */
export function evaluateScan(r: ScanReadings): ScanEvaluation {
  const on = <T,>(v: T | null, f: (x: T) => number) => (v === null ? null : f(v));
  const categories: ScanCategory[] = [
    {
      key: "performance",
      label: "Performance",
      weight: 0.3,
      value: worst([
        on(r.cpu.loadPct, utilizationScore),
        on(r.gpu.loadPct, utilizationScore),
        on(r.cpu.tempC, (t) => thermalScore(t, 80, 95)),
        on(r.gpu.tempC, (t) => thermalScore(t, 83, 95)),
      ]),
      basis: "Worst of CPU/GPU load (≤ 60% = 100) and CPU/GPU temperature.",
    },
    {
      key: "responsiveness",
      label: "Responsiveness",
      weight: 0.3,
      value: worst([on(r.latency.dpcUs, dpcScore), on(r.latency.timerMs, timerScore)]),
      basis: "Worst of DPC latency (≤ 50 µs = 100) and timer resolution (≤ 1 ms = 100).",
    },
    {
      key: "memory",
      label: "Memory",
      weight: 0.2,
      value: worst([on(pct(r.ram.usedGb, r.ram.totalGb), utilizationScore)]),
      basis: "RAM in use (≤ 60% = 100, then −1.75 per %).",
    },
    {
      key: "efficiency",
      label: "System efficiency",
      weight: 0.2,
      value: worst([
        on(r.backgroundProcesses, processScore),
        on(pct(r.storage.usedGb, r.storage.totalGb), capacityScore),
        r.storage.healthPct === null ? null : clamp(r.storage.healthPct),
      ]),
      basis: "Worst of background processes (≤ 100 = 100), drive space used and drive health.",
    },
  ];
  const measured = categories.filter((c) => c.value !== null);
  const w = measured.reduce((s, c) => s + c.weight, 0);
  const overall = w
    ? Math.round(clamp(measured.reduce((s, c) => s + (c.value ?? 0) * c.weight, 0) / w))
    : null;
  return { categories, overall, status: overall === null ? "Not enough data" : scoreStatus(overall) };
}

/** Lowest-scoring category below 85, or null when nothing needs attention. */
export function bottleneck(ev: ScanEvaluation): ScanCategory | null {
  const low = ev.categories
    .filter((c): c is ScanCategory & { value: number } => c.value !== null && c.value < 85)
    .sort((a, b) => a.value - b.value);
  return low[0] ?? null;
}

/**
 * Core Optimization readiness score:
 * Combines measured system score with verified Core Optimization engine state.
 * Evaluates hardware telemetry and verified applied passes.
 * Ineligible/unsupported items are excluded from the denominator.
 */
export function coreOptimizationReadiness(
  hostScore: number | null,
  s?: { verifiedPasses?: number; totalEligibleAreas?: number; alreadyOptimal?: number }
): number | null {
  if (hostScore === null) return null;
  const verified = s?.verifiedPasses ?? 0;
  const optimal = s?.alreadyOptimal ?? 0;
  const total = Math.max(1, s?.totalEligibleAreas ?? 5);
  const factor = Math.min(1, Math.max(0, (verified + optimal) / total));
  return Math.round(clamp(hostScore * 0.8 + factor * 20));
}

export const v4Readiness = coreOptimizationReadiness;

/* ── Native payload validation: anything not a finite number / string becomes null ── */
const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? v : null);
const str = (v: unknown) => (typeof v === "string" && v.trim() ? v.trim() : null);
const obj = (v: unknown): Loose =>
  v && typeof v === "object" ? (v as Loose) : {};
type Loose = {
  [K in
    | "cpu" | "ram" | "gpu" | "storage" | "latency" | "backgroundProcesses"
    | "model" | "cores" | "threads" | "loadPct" | "tempC" | "clockMhz" | "totalGb" | "usedGb" | "spec" | "commitPct"
    | "vramUsedGb" | "vramTotalGb" | "hotspotC" | "healthPct" | "dpcUs" | "timerMs"]?: unknown;
};

/** Parses readings sent by the Windows host; null when the payload has no usable reading. */
export function parseReadings(raw: unknown): ScanReadings | null {
  if (!raw || typeof raw !== "object") return null;
  const o = obj(raw);
  const cpu = obj(o.cpu);
  const ram = obj(o.ram);
  const gpu = obj(o.gpu);
  const st = obj(o.storage);
  const lat = obj(o.latency);
  const clockMhz = num(cpu.clockMhz);
  const commitPct = num(ram.commitPct);
  const stTempC = num(st.tempC);
  const gpuHotspotC = num(gpu.hotspotC);
  const r: ScanReadings = {
    cpu: {
      model: str(cpu.model),
      cores: num(cpu.cores),
      threads: num(cpu.threads),
      loadPct: num(cpu.loadPct),
      tempC: num(cpu.tempC),
      ...(clockMhz !== null ? { clockMhz } : {}),
    },
    ram: {
      totalGb: num(ram.totalGb),
      usedGb: num(ram.usedGb),
      spec: str(ram.spec),
      ...(commitPct !== null ? { commitPct } : {}),
    },
    gpu: {
      model: str(gpu.model),
      loadPct: num(gpu.loadPct),
      tempC: num(gpu.tempC),
      ...(gpuHotspotC !== null ? { hotspotC: gpuHotspotC } : {}),
      vramUsedGb: num(gpu.vramUsedGb),
      vramTotalGb: num(gpu.vramTotalGb),
    },
    storage: {
      model: str(st.model),
      usedGb: num(st.usedGb),
      totalGb: num(st.totalGb),
      healthPct: num(st.healthPct),
      ...(stTempC !== null ? { tempC: stTempC } : {}),
    },
    latency: { dpcUs: num(lat.dpcUs), timerMs: num(lat.timerMs) },
    backgroundProcesses: num(o.backgroundProcesses),
  };
  return evaluateScan(r).overall === null ? null : r;
}

export const usedPct = pct;

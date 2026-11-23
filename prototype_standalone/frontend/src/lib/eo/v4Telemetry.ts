// V4 System Performance — pure rules for the V4 Dashboard's CPU / GPU / RAM / background
// process card. Values come only from the Windows host: a live `telemetry` sample when the
// feed is running, otherwise the native dashboard scan. Sample (browser-preview) readings are
// never shown here — missing telemetry is "--" / "Unavailable".
import type { ScanResult } from "./dashboardScan";

export interface LiveSample {
  cpuPct: number | null;
  gpuPct: number | null;
  ramUsedGb: number | null;
  ramTotalGb: number | null;
  backgroundProcesses: number | null;
}

/** A live sample older than this no longer counts as "updating". */
export const LIVE_STALE_MS = 6000;
export const TELEMETRY_INTERVAL_MS = 2000;

const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? v : null);
const o = (v: unknown): Record<string, unknown> =>
  v && typeof v === "object" ? (v as Record<string, unknown>) : {};
const clampPct = (v: number | null) => (v === null ? null : Math.min(100, Math.max(0, v)));

/** Parses a host `telemetry` message; null when it carries no usable reading. */
export function parseLiveSample(raw: unknown): LiveSample | null {
  const m = o(raw);
  const s: LiveSample = {
    cpuPct: clampPct(num(o(m["cpu"])["loadPct"])),
    gpuPct: clampPct(num(o(m["gpu"])["loadPct"])),
    ramUsedGb: num(o(m["ram"])["usedGb"]),
    ramTotalGb: num(o(m["ram"])["totalGb"]),
    backgroundProcesses: num(m["backgroundProcesses"]),
  };
  if (s.backgroundProcesses !== null && s.backgroundProcesses < 0) s.backgroundProcesses = null;
  return Object.values(s).some((v) => v !== null) ? s : null;
}

export const isLiveFresh = (lastAt: number | null, now: number) =>
  lastAt !== null && now - lastAt >= 0 && now - lastAt <= LIVE_STALE_MS;

export type UsageLevel = "Normal" | "Elevated" | "High";
/** Usage indicator: under 60% Normal, 60–84% Elevated, 85%+ High. */
export function usageLevel(pct: number | null): UsageLevel | null {
  if (pct === null) return null;
  return pct >= 85 ? "High" : pct >= 60 ? "Elevated" : "Normal";
}

export type MetricSource = "live" | "scan" | "unavailable";
export interface PerfMetric {
  key: "cpu" | "gpu" | "ram" | "background";
  label: string;
  /** Percentage (CPU/GPU/RAM) or count (background); null when unavailable. */
  value: number | null;
  isPercent: boolean;
  level: UsageLevel | null;
  /** Concise supporting label (model, used/total, …). */
  detail: string;
  source: MetricSource;
}

const ramPct = (used: number | null, total: number | null) =>
  used !== null && total !== null && total > 0 ? clampPct((used / total) * 100) : null;
const gb = (v: number) => (Math.round(v * 10) / 10).toString();

export function v4PerfMetrics(s: {
  scan: ScanResult | null;
  live: LiveSample | null;
  liveFresh: boolean;
}): PerfMetric[] {
  // Only native scans are real; the browser sample snapshot is ignored.
  const rd = s.scan?.source === "native" ? s.scan.readings : null;
  const live = s.live;
  const pick = (lv: number | null | undefined, sv: number | null | undefined) =>
    lv !== null && lv !== undefined
      ? { v: lv, src: (s.liveFresh ? "live" : "scan") as MetricSource }
      : sv !== null && sv !== undefined
        ? { v: sv, src: "scan" as MetricSource }
        : { v: null, src: "unavailable" as MetricSource };

  const cpu = pick(live?.cpuPct, clampPct(rd?.cpu.loadPct ?? null));
  const gpu = pick(live?.gpuPct, clampPct(rd?.gpu.loadPct ?? null));
  const used = live?.ramUsedGb ?? rd?.ram.usedGb ?? null;
  const total = live?.ramTotalGb ?? rd?.ram.totalGb ?? null;
  const ram = pick(
    live ? ramPct(live.ramUsedGb, live.ramTotalGb ?? rd?.ram.totalGb ?? null) : null,
    ramPct(rd?.ram.usedGb ?? null, rd?.ram.totalGb ?? null),
  );
  const bg = pick(live?.backgroundProcesses, rd?.backgroundProcesses ?? null);

  return [
    {
      key: "cpu",
      label: "CPU USAGE",
      value: cpu.v,
      isPercent: true,
      level: usageLevel(cpu.v),
      detail: cpu.v === null ? "Unavailable" : (rd?.cpu.model ?? "Processor load"),
      source: cpu.src,
    },
    {
      key: "gpu",
      label: "GPU USAGE",
      value: gpu.v,
      isPercent: true,
      level: usageLevel(gpu.v),
      detail: gpu.v === null ? "Unavailable" : (rd?.gpu.model ?? "Graphics load"),
      source: gpu.src,
    },
    {
      key: "ram",
      label: "RAM USAGE",
      value: ram.v,
      isPercent: true,
      level: usageLevel(ram.v),
      detail:
        ram.v === null || used === null || total === null
          ? "Unavailable"
          : `${gb(used)} / ${gb(total)} GB in use`,
      source: ram.src,
    },
    {
      key: "background",
      label: "BACKGROUND PROCESSES",
      value: bg.v === null ? null : Math.round(bg.v),
      isPercent: false,
      level: null,
      detail: bg.v === null ? "Unavailable" : "Running in the background",
      source: bg.src,
    },
  ];
}

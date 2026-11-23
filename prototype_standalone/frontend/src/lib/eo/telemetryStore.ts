// Canonical shared frontend telemetry & dashboard-scan store.
// Owns a single reference-counted WebView2 `telemetry:subscribe` lifecycle, enforces
// idempotent cleanup (safe under React Strict Mode), honors the shortest active cadence,
// prevents out-of-order async samples from overwriting newer data, tracks per-metric
// timestamps, and separates live freshness cleanly from stale snapshots.
import { useEffect, useState, useSyncExternalStore } from "react";
import { hasNativeBackend } from "@/lib/eo/nativeBridge";
import { isBackendConnected, subscribeConnection } from "@/lib/eo/connectionStore";
import { postHostFireAndForget, subscribeHostEvent } from "@/lib/eo/hostRpc";
import { getSettingsSnapshot, subscribeSettings } from "@/components/eo/settingsStore";
import {
  parseReadings,
  type ScanReadings,
  type ScanResult,
} from "@/lib/eo/dashboardScan";
import {
  THERMAL_HISTORY,
  parseThermalSample,
  pushThermal,
  type ThermalSample,
  type ThermalSensor,
} from "@/lib/eo/thermals";
import {
  LIVE_STALE_MS,
  isLiveFresh,
  parseLiveSample,
  type LiveSample,
} from "@/lib/eo/v4Telemetry";

export interface SensorMetadataItem {
  device: string;
  sensorId: string;
  unit: string;
  value: number | null;
  timestampUtc: string;
  provider: string;
  availability: string;
  reason?: string | null;
}

export interface LiveSeriesPoint {
  cpu: number | null;
  ram: number | null;
  gpu: number | null;
}

export interface LiveExtras {
  cpuTempC: number | null;
  cpuClockMhz: number | null;
  ramCommitPct: number | null;
  gpuTempC: number | null;
  gpuHotspotC: number | null;
  gpuVramUsedGb: number | null;
  gpuVramTotalGb: number | null;
  ssdTempC: number | null;
  storageUsedGb: number | null;
  storageFreeGb: number | null;
  storageTotalGb: number | null;
  storageUsagePct: number | null;
  dpcUs: number | null;
  timerMs: number | null;
  timestampUtc: string | null;
  source: string | null;
}

export interface MetricTimestampEntry {
  atMs: number;
  timestampUtc: string;
}

export type SharedScanPhase = "idle" | "scanning" | "done" | "error";

export const SCAN_FRESH_MS = 120_000;

export interface TelemetryStoreState {
  connected: boolean;
  live: LiveSample | null;
  thermal: ThermalSample | null;
  thermalHistory: ThermalSample[];
  thermalTotal: number;
  sampleSeries: LiveSeriesPoint[];
  sensorMeta: Partial<Record<ThermalSensor, SensorMetadataItem>>;
  liveExtras: LiveExtras;
  metricTimestamps: Record<string, MetricTimestampEntry>;
  lastAt: number | null;
  lastSampleServerUtcMs: number | null;
  lastTelemetrySeq: number;
  scanCompletedAtMs: number | null;
  lastScanServerUtcMs: number | null;
  intervalMs: number;
  scanPhase: SharedScanPhase;
  scanProgress: number;
  scanResult: ScanResult | null;
  effectiveScanResult: ScanResult | null;
  scanError: string | null;
}

const MAX_SERIES = 200;
const NATIVE_SCAN_TIMEOUT_MS = 20000;

interface WebView {
  postMessage: (m: unknown) => void;
  addEventListener?: (t: "message", h: (e: { data: unknown }) => void) => void;
  removeEventListener?: (t: "message", h: (e: { data: unknown }) => void) => void;
}

const EMPTY_EXTRAS: LiveExtras = {
  cpuTempC: null,
  cpuClockMhz: null,
  ramCommitPct: null,
  gpuTempC: null,
  gpuHotspotC: null,
  gpuVramUsedGb: null,
  gpuVramTotalGb: null,
  ssdTempC: null,
  storageUsedGb: null,
  storageFreeGb: null,
  storageTotalGb: null,
  storageUsagePct: null,
  dpcUs: null,
  timerMs: null,
  timestampUtc: null,
  source: null,
};

const SMOOTH_WINDOW = 3;
let cpuLoadWindow: number[] = [];
let gpuLoadWindow: number[] = [];

function pushSmoothWindow(prev: number[], incoming: number | null): number[] {
  if (incoming === null || !Number.isFinite(incoming)) return prev;
  return [...prev.slice(-(SMOOTH_WINDOW - 1)), incoming];
}

function smoothedLoad(latest: number | null, window: number[]): number | null {
  if (latest === null || !Number.isFinite(latest)) return null;
  const samples = window.length > 0 ? window : [latest];
  const avg = samples.reduce((sum, x) => sum + x, 0) / samples.length;
  return Math.round(avg * 10) / 10;
}

function settingsIntervalMs(): number {
  const sec = getSettingsSnapshot().monitorIntervalSec;
  return Math.max(1000, Math.min(10000, (Number.isFinite(sec) && sec > 0 ? sec : 3) * 1000));
}

const o = (v: unknown): Record<string, unknown> =>
  v && typeof v === "object" ? (v as Record<string, unknown>) : {};
const num = (v: unknown): number | null =>
  typeof v === "number" && Number.isFinite(v) ? v : null;
const round1 = (v: number | null): number | null =>
  v === null ? null : Math.round(v * 10) / 10;

function parseIsoMs(raw: unknown): number | null {
  if (typeof raw !== "string" || !raw.trim()) return null;
  const ms = Date.parse(raw);
  return Number.isFinite(ms) ? ms : null;
}

let state: TelemetryStoreState = {
  connected: false,
  live: null,
  thermal: null,
  thermalHistory: [],
  thermalTotal: 0,
  sampleSeries: [],
  sensorMeta: {},
  liveExtras: EMPTY_EXTRAS,
  metricTimestamps: {},
  lastAt: null,
  lastSampleServerUtcMs: null,
  lastTelemetrySeq: 0,
  scanCompletedAtMs: null,
  lastScanServerUtcMs: null,
  intervalMs: typeof window !== "undefined" ? settingsIntervalMs() : 3000,
  scanPhase: "idle",
  scanProgress: 0,
  scanResult: null,
  effectiveScanResult: null,
  scanError: null,
};

const storeListeners = new Set<() => void>();
const subscriberMap = new Map<number, number | undefined>();
let nextSubToken = 1;
let telemetryOff: (() => void) | null = null;
let bridgeAttached = false;
let subscribedOnHost = false;
let lastSubscribedIntervalMs = 0;
let scanCleanup: (() => void) | null = null;
let scanSeq = 0;

function emit() {
  storeListeners.forEach((l) => l());
}

function desiredIntervalMs(): number {
  let minMs = settingsIntervalMs();
  for (const override of subscriberMap.values()) {
    if (typeof override === "number" && Number.isFinite(override) && override >= 500 && override < minMs) {
      minMs = Math.round(override);
    }
  }
  return minMs;
}

function mergeScanWithLive(
  base: ScanResult | null,
  live: LiveSample | null,
  thermal: ThermalSample | null,
  extras: LiveExtras,
  lastAt: number | null,
): ScanResult | null {
  if (!base || base.source !== "native") return base;
  if (!live && !thermal) return base;

  const cpuTemp = extras.cpuTempC ?? thermal?.cpu ?? base.readings.cpu.tempC;
  const gpuTemp = extras.gpuTempC ?? thermal?.gpu ?? base.readings.gpu.tempC;
  const gpuHotspot = extras.gpuHotspotC ?? thermal?.gpuHotspot ?? base.readings.gpu.hotspotC;
  const ssdTemp = extras.ssdTempC ?? thermal?.ssd ?? base.readings.storage.tempC;
  const cpuClock = extras.cpuClockMhz ?? base.readings.cpu.clockMhz;
  const commitPct = extras.ramCommitPct ?? base.readings.ram.commitPct;
  const cpuLoad = smoothedLoad(live?.cpuPct ?? null, cpuLoadWindow) ?? base.readings.cpu.loadPct;
  const gpuLoad = smoothedLoad(live?.gpuPct ?? null, gpuLoadWindow) ?? base.readings.gpu.loadPct;
  const sampleMs = parseIsoMs(extras.timestampUtc) ?? lastAt;

  const mergedReadings: ScanReadings = {
    cpu: {
      ...base.readings.cpu,
      loadPct: cpuLoad,
      tempC: cpuTemp,
      ...(cpuClock !== undefined && cpuClock !== null ? { clockMhz: cpuClock } : {}),
    },
    ram: {
      ...base.readings.ram,
      usedGb: live?.ramUsedGb ?? base.readings.ram.usedGb,
      totalGb: live?.ramTotalGb ?? base.readings.ram.totalGb,
      ...(commitPct !== undefined && commitPct !== null ? { commitPct } : {}),
    },
    gpu: {
      ...base.readings.gpu,
      loadPct: gpuLoad,
      tempC: gpuTemp,
      ...(gpuHotspot !== undefined && gpuHotspot !== null ? { hotspotC: gpuHotspot } : {}),
      vramUsedGb: extras.gpuVramUsedGb ?? base.readings.gpu.vramUsedGb,
      vramTotalGb: extras.gpuVramTotalGb ?? base.readings.gpu.vramTotalGb,
    },
    storage: {
      ...base.readings.storage,
      usedGb: extras.storageUsedGb ?? base.readings.storage.usedGb,
      totalGb: extras.storageTotalGb ?? base.readings.storage.totalGb,
      ...(ssdTemp !== undefined && ssdTemp !== null ? { tempC: ssdTemp } : {}),
    },
    latency: {
      dpcUs: extras.dpcUs ?? base.readings.latency.dpcUs,
      timerMs: extras.timerMs ?? base.readings.latency.timerMs,
    },
    backgroundProcesses: live?.backgroundProcesses ?? base.readings.backgroundProcesses,
  };

  return {
    source: "native",
    readings: mergedReadings,
    at: sampleMs ? new Date(sampleMs).toLocaleTimeString() : base.at,
  };
}

function parseSensorMeta(raw: unknown): Partial<Record<ThermalSensor, SensorMetadataItem>> | null {
  const m = o(raw);
  const rawSensors = m["sensors"];
  if (!rawSensors || typeof rawSensors !== "object") return null;
  const rec = rawSensors as Record<string, Record<string, unknown>>;
  const mapped: Partial<Record<ThermalSensor, SensorMetadataItem>> = {};
  const keyMap: Record<string, ThermalSensor> = {
    cpuTemp: "cpu",
    gpuTemp: "gpu",
    gpuHotspot: "gpuHotspot",
    ssdTemp: "ssd",
  };
  for (const [k, rawMeta] of Object.entries(rec)) {
    const target = keyMap[k];
    if (!target || !rawMeta || typeof rawMeta !== "object") continue;
    mapped[target] = {
      device: String(rawMeta["device"] ?? ""),
      sensorId: String(rawMeta["sensorId"] ?? k),
      unit: String(rawMeta["unit"] ?? "C"),
      value: typeof rawMeta["value"] === "number" && Number.isFinite(rawMeta["value"]) ? rawMeta["value"] : null,
      timestampUtc: String(rawMeta["timestampUtc"] ?? ""),
      provider: String(rawMeta["provider"] ?? "Unavailable"),
      availability: String(rawMeta["availability"] ?? "unavailable"),
      reason: typeof rawMeta["reason"] === "string" ? rawMeta["reason"] : null,
    };
  }
  return mapped;
}

function stampMetricTimestamps(
  existing: Record<string, MetricTimestampEntry>,
  nowMs: number,
  isoUtc: string,
  values: Record<string, number | null | undefined>,
  sensorMeta?: Partial<Record<ThermalSensor, SensorMetadataItem>> | null,
): Record<string, MetricTimestampEntry> {
  const next = { ...existing };
  const sensorTime = (sensor: ThermalSensor): { atMs: number; timestampUtc: string } => {
    const rawIso = sensorMeta?.[sensor]?.timestampUtc;
    const parsedMs = parseIsoMs(rawIso);
    if (parsedMs !== null && rawIso) {
      return { atMs: parsedMs, timestampUtc: rawIso };
    }
    return { atMs: nowMs, timestampUtc: isoUtc };
  };

  for (const [k, v] of Object.entries(values)) {
    if (v === null || v === undefined) continue;
    if (k === "cpu-temp") next[k] = sensorTime("cpu");
    else if (k === "gpu-temp") next[k] = sensorTime("gpu");
    else if (k === "gpu-hotspot") next[k] = sensorTime("gpuHotspot");
    else if (k === "disk-temp") next[k] = sensorTime("ssd");
    else next[k] = { atMs: nowMs, timestampUtc: isoUtc };
  }
  return next;
}

function handleTelemetryMessage(raw: unknown) {
  const m = o(raw);
  const incomingSeq = num(m["seq"]);
  const serverUtcMs = parseIsoMs(m["timestampUtc"]);

  // Reject delayed out-of-order async samples from overwriting newer telemetry.
  if (incomingSeq !== null && incomingSeq < state.lastTelemetrySeq) return;
  if (serverUtcMs !== null && state.lastSampleServerUtcMs !== null && serverUtcMs < state.lastSampleServerUtcMs) {
    return;
  }

  const parsedLive = parseLiveSample(m);
  const parsedThermal = parseThermalSample(m);
  const parsedMeta = parseSensorMeta(m);
  const reportedInterval = num(m["intervalMs"]);

  if (!parsedLive && !parsedThermal && !parsedMeta) return;

  const nowMs = Date.now();
  const sampleIso = typeof m["timestampUtc"] === "string" && m["timestampUtc"] ? m["timestampUtc"] : new Date(nowMs).toISOString();
  const cpu = o(m["cpu"]);
  const ram = o(m["ram"]);
  const gpu = o(m["gpu"]);
  const ssd = o(m["ssd"]);
  const st = o(m["storage"]);
  const lat = o(m["latency"]);

  const vramUsedMb = num(gpu["vramUsedMb"]);
  const vramTotalMb = num(gpu["vramTotalMb"]);

  if (parsedLive) {
    cpuLoadWindow = pushSmoothWindow(cpuLoadWindow, parsedLive.cpuPct);
    gpuLoadWindow = pushSmoothWindow(gpuLoadWindow, parsedLive.gpuPct);
  }

  const nextExtras: LiveExtras = {
    cpuTempC: parsedThermal?.cpu ?? round1(num(cpu["tempC"])),
    cpuClockMhz: num(cpu["clockMhz"]),
    ramCommitPct: num(ram["commitPct"]),
    gpuTempC: parsedThermal?.gpu ?? round1(num(gpu["tempC"])),
    gpuHotspotC: parsedThermal?.gpuHotspot ?? round1(num(gpu["hotspotC"])),
    gpuVramUsedGb: vramUsedMb !== null && vramUsedMb >= 0 ? Math.round((vramUsedMb / 1024) * 100) / 100 : state.liveExtras.gpuVramUsedGb,
    gpuVramTotalGb: vramTotalMb !== null && vramTotalMb > 0 ? Math.round((vramTotalMb / 1024) * 100) / 100 : state.liveExtras.gpuVramTotalGb,
    ssdTempC: parsedThermal?.ssd ?? round1(num(ssd["tempC"])),
    storageUsedGb: round1(num(st["usedGb"])) ?? state.liveExtras.storageUsedGb,
    storageFreeGb: round1(num(st["freeGb"])) ?? state.liveExtras.storageFreeGb,
    storageTotalGb: round1(num(st["totalGb"])) ?? state.liveExtras.storageTotalGb,
    storageUsagePct: round1(num(st["usagePct"])) ?? state.liveExtras.storageUsagePct,
    dpcUs: num(lat["dpcUs"]),
    timerMs: num(lat["timerMs"]),
    timestampUtc: sampleIso,
    source: typeof m["source"] === "string" ? m["source"] : "host",
  };

  let nextSeries = state.sampleSeries;
  if (parsedLive) {
    const cpuVal = parsedLive.cpuPct !== null ? Math.round(parsedLive.cpuPct) : null;
    const ramVal =
      parsedLive.ramUsedGb !== null && parsedLive.ramTotalGb !== null && parsedLive.ramTotalGb > 0
        ? Math.round((parsedLive.ramUsedGb / parsedLive.ramTotalGb) * 100)
        : null;
    const gpuVal = parsedLive.gpuPct !== null ? Math.round(parsedLive.gpuPct) : null;
    nextSeries = [...state.sampleSeries.slice(-(MAX_SERIES - 1)), { cpu: cpuVal, ram: ramVal, gpu: gpuVal }];
  }

  const nextThermal = parsedThermal ?? state.thermal;
  const nextHistory = parsedThermal
    ? pushThermal(state.thermalHistory, parsedThermal, THERMAL_HISTORY)
    : state.thermalHistory;
  const nextTotal = parsedThermal ? state.thermalTotal + 1 : state.thermalTotal;
  const nextLive = parsedLive ?? state.live;
  const nextInterval =
    reportedInterval !== null && reportedInterval >= 500 && reportedInterval <= 10000
      ? Math.round(reportedInterval)
      : state.intervalMs;
  const mergedMeta = parsedMeta ? { ...state.sensorMeta, ...parsedMeta } : state.sensorMeta;

  const ramPct =
    nextLive?.ramUsedGb !== null && nextLive?.ramUsedGb !== undefined && nextLive?.ramTotalGb
      ? (nextLive.ramUsedGb / nextLive.ramTotalGb) * 100
      : null;

  const nextTimestamps = stampMetricTimestamps(
    state.metricTimestamps,
    nowMs,
    sampleIso,
    {
      "cpu-load": parsedLive?.cpuPct,
      "cpu-temp": nextExtras.cpuTempC,
      "mem-used": ramPct,
      "mem-commit": nextExtras.ramCommitPct,
      "gpu-load": parsedLive?.gpuPct,
      "gpu-temp": nextExtras.gpuTempC,
      "gpu-hotspot": nextExtras.gpuHotspotC,
      "disk-used": round1(num(st["usagePct"])),
      "disk-temp": nextExtras.ssdTempC,
      dpc: nextExtras.dpcUs,
      timer: nextExtras.timerMs,
    },
    mergedMeta,
  );

  state = {
    ...state,
    connected: true,
    live: nextLive,
    thermal: nextThermal,
    thermalHistory: nextHistory,
    thermalTotal: nextTotal,
    sampleSeries: nextSeries,
    sensorMeta: mergedMeta,
    liveExtras: nextExtras,
    metricTimestamps: nextTimestamps,
    lastAt: nowMs,
    lastSampleServerUtcMs: serverUtcMs ?? state.lastSampleServerUtcMs,
    lastTelemetrySeq: incomingSeq !== null ? Math.max(state.lastTelemetrySeq, incomingSeq) : state.lastTelemetrySeq,
    intervalMs: nextInterval,
    effectiveScanResult: mergeScanWithLive(
      state.scanResult,
      nextLive,
      nextThermal,
      nextExtras,
      nowMs,
    ),
  };
  emit();
}

function syncHostSubscription() {
  if (typeof window === "undefined") return;
  const canConnect = hasNativeBackend() && isBackendConnected();

  if (subscriberMap.size > 0 && canConnect) {
    if (!telemetryOff) {
      telemetryOff = subscribeHostEvent("telemetry", handleTelemetryMessage);
    }
    const desired = desiredIntervalMs();
    if (!subscribedOnHost || lastSubscribedIntervalMs !== desired) {
      postHostFireAndForget("telemetry:subscribe", { intervalMs: desired, setInterval: true });
      subscribedOnHost = true;
      lastSubscribedIntervalMs = desired;
      if (state.intervalMs !== desired || !state.connected) {
        state = { ...state, connected: true, intervalMs: desired };
        emit();
      }
    }
  } else {
    if (telemetryOff) {
      telemetryOff();
      telemetryOff = null;
    }
    if (subscribedOnHost) {
      if (hasNativeBackend()) {
        postHostFireAndForget("telemetry:unsubscribe");
      }
      subscribedOnHost = false;
      lastSubscribedIntervalMs = 0;
    }
    if (!canConnect && state.connected) {
      state = {
        ...state,
        connected: false,
      };
      emit();
    }
  }
}

function ensureGlobalBridges() {
  if (bridgeAttached || typeof window === "undefined") return;
  bridgeAttached = true;

  subscribeConnection(() => {
    const conn = hasNativeBackend() && isBackendConnected();
    if (!conn) {
      subscribedOnHost = false;
      lastSubscribedIntervalMs = 0;
      cpuLoadWindow = [];
      gpuLoadWindow = [];
      if (state.connected) {
        state = { ...state, connected: false };
        emit();
      }
    }
    syncHostSubscription();
  });

  subscribeSettings(() => {
    const desired = desiredIntervalMs();
    if (desired !== state.intervalMs) {
      state = { ...state, intervalMs: desired };
      emit();
    }
    if (subscriberMap.size > 0 && hasNativeBackend() && isBackendConnected()) {
      syncHostSubscription();
    }
  });
}

export function retainTelemetrySubscription(requestedIntervalMs?: number): () => void {
  ensureGlobalBridges();
  const token = nextSubToken++;
  const normalized =
    typeof requestedIntervalMs === "number" && Number.isFinite(requestedIntervalMs)
      ? Math.max(500, Math.min(10000, Math.round(requestedIntervalMs)))
      : undefined;
  subscriberMap.set(token, normalized);
  syncHostSubscription();

  let released = false;
  return () => {
    if (released) return;
    released = true;
    subscriberMap.delete(token);
    syncHostSubscription();
  };
}

export function updateSharedScanReadings(rawReadings: unknown, rawSensors?: unknown, rawTimestampUtc?: unknown) {
  const readings = parseReadings(rawReadings);
  if (!readings) return;

  const incomingServerMs = parseIsoMs(rawTimestampUtc);
  if (
    incomingServerMs !== null &&
    state.lastScanServerUtcMs !== null &&
    incomingServerMs < state.lastScanServerUtcMs
  ) {
    return;
  }

  const nowMs = Date.now();
  const isoUtc = typeof rawTimestampUtc === "string" && rawTimestampUtc ? rawTimestampUtc : new Date(nowMs).toISOString();
  const at = new Date(nowMs).toLocaleTimeString();
  const scanRes: ScanResult = { source: "native", readings, at };
  const parsedMeta = rawSensors ? parseSensorMeta({ sensors: rawSensors }) : null;
  const mergedMeta = parsedMeta ? { ...state.sensorMeta, ...parsedMeta } : state.sensorMeta;

  const ramPct =
    readings.ram.usedGb !== null && readings.ram.totalGb
      ? (readings.ram.usedGb / readings.ram.totalGb) * 100
      : null;
  const diskPct =
    readings.storage.usedGb !== null && readings.storage.totalGb
      ? (readings.storage.usedGb / readings.storage.totalGb) * 100
      : null;

  const nextTimestamps = stampMetricTimestamps(
    state.metricTimestamps,
    nowMs,
    isoUtc,
    {
      "cpu-load": readings.cpu.loadPct,
      "cpu-temp": readings.cpu.tempC,
      "mem-used": ramPct,
      "mem-commit": readings.ram.commitPct,
      "gpu-load": readings.gpu.loadPct,
      "gpu-temp": readings.gpu.tempC,
      "gpu-hotspot": readings.gpu.hotspotC,
      "disk-used": diskPct,
      "disk-temp": readings.storage.tempC,
      "disk-health": readings.storage.healthPct,
      dpc: readings.latency.dpcUs,
      timer: readings.latency.timerMs,
    },
    mergedMeta,
  );

  const fresh = isLiveFresh(state.lastAt, nowMs);
  cpuLoadWindow = readings.cpu.loadPct !== null && Number.isFinite(readings.cpu.loadPct) ? [readings.cpu.loadPct] : [];
  gpuLoadWindow = readings.gpu.loadPct !== null && Number.isFinite(readings.gpu.loadPct) ? [readings.gpu.loadPct] : [];
  state = {
    ...state,
    scanPhase: "done",
    scanProgress: 100,
    scanError: null,
    scanResult: scanRes,
    scanCompletedAtMs: nowMs,
    lastScanServerUtcMs: incomingServerMs ?? nowMs,
    sensorMeta: mergedMeta,
    metricTimestamps: nextTimestamps,
    effectiveScanResult: fresh
      ? mergeScanWithLive(scanRes, state.live, state.thermal, state.liveExtras, state.lastAt)
      : scanRes,
  };
  emit();
}

export function startSharedSystemScan(options?: { force?: boolean }) {
  ensureGlobalBridges();
  if (!hasNativeBackend()) {
    state = {
      ...state,
      scanPhase: "idle",
      scanProgress: 0,
      scanResult: null,
      effectiveScanResult: null,
      scanError: null,
    };
    emit();
    return;
  }

  if (state.scanPhase === "scanning" && !options?.force) return;
  if (options?.force) {
    cpuLoadWindow = [];
    gpuLoadWindow = [];
  }

  scanCleanup?.();
  scanCleanup = null;

  state = { ...state, scanPhase: "scanning", scanProgress: 0, scanError: null };
  emit();

  const wv = window.chrome?.webview as unknown as WebView | undefined;
  if (!wv) {
    state = {
      ...state,
      scanPhase: "error",
      scanError: "The Windows host bridge is unavailable.",
    };
    emit();
    return;
  }

  const mySeq = ++scanSeq;
  const id = `scan-${mySeq}`;
  const finish = (scanRes: ScanResult | null, rawSensors?: unknown, rawTimestampUtc?: unknown, err?: string) => {
    if (mySeq !== scanSeq) return;
    scanCleanup?.();
    scanCleanup = null;
    if (scanRes) {
      updateSharedScanReadings(scanRes.readings, rawSensors, rawTimestampUtc);
      return;
    }
    state = {
      ...state,
      scanPhase: "error",
      scanError: err ?? "Scan failed",
    };
    emit();
  };

  const tick = window.setInterval(() => {
    if (mySeq !== scanSeq) return;
    state = { ...state, scanProgress: Math.min(90, state.scanProgress + (90 - state.scanProgress) * 0.08) };
    emit();
  }, 150);

  const timeout = window.setTimeout(
    () => finish(null, undefined, undefined, "The Windows backend did not return readings. No score was calculated."),
    NATIVE_SCAN_TIMEOUT_MS,
  );

  const onMsg = (ev: { data: unknown }) => {
    if (mySeq !== scanSeq) return;
    let m: unknown = ev.data;
    if (typeof m === "string") {
      try {
        m = JSON.parse(m);
      } catch {
        return;
      }
    }
    if (!m || typeof m !== "object") return;
    const msg = m as {
      id?: unknown;
      ok?: unknown;
      error?: unknown;
      progress?: unknown;
      timestampUtc?: unknown;
      readings?: unknown;
      result?: { readings?: unknown; timestampUtc?: unknown };
    };
    if (msg.id !== id) return;
    if (typeof msg.progress === "number" && Number.isFinite(msg.progress)) {
      state = { ...state, scanProgress: Math.max(0, Math.min(99, msg.progress)) };
      emit();
      return;
    }
    if (msg.ok === false) {
      finish(
        null,
        undefined,
        undefined,
        typeof msg.error === "string" ? msg.error : "The Windows backend refused the scan.",
      );
      return;
    }
    const rawObj = msg.readings ?? msg.result?.readings;
    const readings = parseReadings(rawObj);
    if (!readings) {
      finish(null, undefined, undefined, "The Windows backend returned empty hardware readings.");
      return;
    }
    const rawSensors = rawObj && typeof rawObj === "object" ? (rawObj as Record<string, unknown>)["sensors"] : undefined;
    const ts = msg.timestampUtc ?? msg.result?.timestampUtc;
    finish({ source: "native", readings, at: new Date().toLocaleTimeString() }, rawSensors, ts);
  };

  wv.addEventListener?.("message", onMsg);
  scanCleanup = () => {
    window.clearInterval(tick);
    window.clearTimeout(timeout);
    wv.removeEventListener?.("message", onMsg);
  };
  wv.postMessage(JSON.stringify({ id, action: "system:scan" }));
}

export function getTelemetryState(): TelemetryStoreState {
  ensureGlobalBridges();
  return state;
}

export function useSharedTelemetryState(options?: {
  subscribe?: boolean;
  intervalMs?: number;
}): TelemetryStoreState & { now: number } {
  const shouldSub = options?.subscribe ?? true;
  const reqInterval = options?.intervalMs;
  const snap = useSyncExternalStore(
    (l) => {
      ensureGlobalBridges();
      storeListeners.add(l);
      return () => storeListeners.delete(l);
    },
    getTelemetryState,
    () => state,
  );
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    if (!shouldSub) return;
    const release = retainTelemetrySubscription(reqInterval);
    const timer = window.setInterval(() => setNow(Date.now()), Math.min(1000, LIVE_STALE_MS));
    return () => {
      window.clearInterval(timer);
      release();
    };
  }, [shouldSub, reqInterval]);

  return { ...snap, now };
}

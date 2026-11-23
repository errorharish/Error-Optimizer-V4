// Temperature feed for the V3 Dashboard Temperature Monitoring card.
// Delegates to the canonical shared `telemetryStore` (one subscription cadence across the app)
// and falls back to the native scan's CPU/GPU/SSD temperatures until live samples arrive.
import { useEffect, useMemo, useState } from "react";
import { useIsBackendConnected } from "@/lib/eo/nativeBridge";
import type { ScanResult } from "@/lib/eo/dashboardScan";
import { isLiveFresh } from "@/lib/eo/v4Telemetry";
import {
  useSharedTelemetryState,
  type SensorMetadataItem,
} from "@/lib/eo/telemetryStore";
import {
  thermalFromScan,
  type ThermalSample,
  type ThermalSensor,
} from "@/lib/eo/thermals";

/** pending = before the first client effect (identical on server and client). */
export type ThermalSource = "pending" | "live" | "scan" | "simulated" | "unavailable";

export type { SensorMetadataItem };

export interface Thermals {
  source: ThermalSource;
  /** Values shown in the reading tiles; null while nothing is known. */
  current: ThermalSample | null;
  /** Per-sensor structured metadata (provider, device, timestamp, availability). */
  sensorMeta: Partial<Record<ThermalSensor, SensorMetadataItem>>;
  /** Graph samples, oldest first (live or simulated only), bounded to THERMAL_HISTORY. */
  history: ThermalSample[];
  /** Samples received so far (keeps graph buckets aligned while old samples drop off). */
  total: number;
  intervalMs: number;
}

export function useThermals(scan: ScanResult | null): Thermals {
  const backendConnected = useIsBackendConnected();
  const [hydrated, setHydrated] = useState(false);
  useEffect(() => {
    setHydrated(true);
  }, []);

  const snap = useSharedTelemetryState({ subscribe: true });
  const effectiveScan = scan ?? snap.effectiveScanResult ?? snap.scanResult;
  const fromScan = useMemo(() => thermalFromScan(effectiveScan), [effectiveScan]);

  if (!hydrated) {
    return {
      source: "pending",
      current: null,
      sensorMeta: snap.sensorMeta,
      history: [],
      total: 0,
      intervalMs: snap.intervalMs,
    };
  }
  if (!backendConnected) {
    return {
      source: "unavailable",
      current: null,
      sensorMeta: {},
      history: [],
      total: 0,
      intervalMs: snap.intervalMs,
    };
  }

  const fresh = isLiveFresh(snap.lastAt, snap.now);
  if (fresh && snap.thermal) {
    return {
      source: "live",
      current: snap.thermalHistory[snap.thermalHistory.length - 1] ?? snap.thermal,
      sensorMeta: snap.sensorMeta,
      history: snap.thermalHistory,
      total: snap.thermalTotal,
      intervalMs: snap.intervalMs,
    };
  }

  return {
    source: fromScan ? "scan" : "unavailable",
    current: fromScan,
    sensorMeta: snap.sensorMeta,
    history: [],
    total: 0,
    intervalMs: snap.intervalMs,
  };
}

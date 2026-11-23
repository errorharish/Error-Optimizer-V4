// Dashboard system scan hook backed by the shared `telemetryStore`.
// Keeps V3 Dashboard, V3 System Telemetry, and V4 Readiness synchronized with live updates
// while preserving explicit manual `start()` full hardware scans and offline/missing honesty.
import { useCallback, useEffect } from "react";
import { toast } from "sonner";
import { hasNativeBackend, useIsBackendConnected } from "@/lib/eo/nativeBridge";
import {
  scanStageAt,
  type ScanResult,
} from "@/lib/eo/dashboardScan";
import {
  getTelemetryState,
  startSharedSystemScan,
  useSharedTelemetryState,
} from "@/lib/eo/telemetryStore";
import { isLiveFresh } from "@/lib/eo/v4Telemetry";

export type ScanPhase = "idle" | "scanning" | "done" | "error";

export interface SystemScan {
  phase: ScanPhase;
  progress: number;
  stage: string;
  result: ScanResult | null;
  error: string | null;
  start: () => void;
}

export const SAMPLE_SCAN_MS = 2400;
export const NATIVE_SCAN_TIMEOUT_MS = 20000;

export function useSystemScan({ auto }: { auto: boolean }): SystemScan {
  const connected = useIsBackendConnected();
  const snap = useSharedTelemetryState({ subscribe: auto });

  const start = useCallback(() => {
    if (!hasNativeBackend()) {
      toast.info("Backend connection required", {
        description: "System scan requires the ERROR OPTIMIZER background service.",
      });
      startSharedSystemScan({ force: true });
      return;
    }
    startSharedSystemScan({ force: true });
  }, []);

  useEffect(() => {
    if (!auto || !connected) return;
    const cur = getTelemetryState();
    if (cur.scanPhase === "idle" || cur.scanPhase === "error") {
      startSharedSystemScan();
    }
  }, [auto, connected]);

  const liveFresh = isLiveFresh(snap.lastAt, snap.now);

  return {
    phase: snap.scanPhase,
    progress: snap.scanProgress,
    stage: scanStageAt(snap.scanProgress),
    result: liveFresh ? (snap.effectiveScanResult ?? snap.scanResult) : snap.scanResult,
    error: snap.scanError,
    start,
  };
}

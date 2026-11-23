// Live system telemetry for the V4 Dashboard. Delegates to the canonical shared
// `telemetryStore` so all V3 and V4 views share one subscription cadence and recover cleanly
// across host/service reconnects.
import { useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { useSharedTelemetryState } from "@/lib/eo/telemetryStore";
import {
  isLiveFresh,
  type LiveSample,
} from "@/lib/eo/v4Telemetry";

export interface LiveTelemetry {
  connected: boolean;
  sample: LiveSample | null;
  /** True only while samples keep arriving. */
  fresh: boolean;
  lastAt: number | null;
}

export function useLiveTelemetry(): LiveTelemetry {
  const backendConnected = useIsBackendConnected();
  const snap = useSharedTelemetryState({ subscribe: true });
  if (!backendConnected) {
    return { connected: false, sample: null, fresh: false, lastAt: null };
  }
  return {
    connected: true,
    sample: snap.live,
    fresh: isLiveFresh(snap.lastAt, snap.now),
    lastAt: snap.lastAt,
  };
}

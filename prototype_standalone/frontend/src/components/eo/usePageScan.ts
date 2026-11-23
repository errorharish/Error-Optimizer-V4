// Automatic page scan for optimization pages (rules in src/lib/eo/pageScan.ts). The page passes
// its existing scan handler as `task` (resolve when results are in, throw on failure); when the
// native backend is connected, PageHeader renders REFRESH and scan status from the returned object.
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { toast } from "sonner";
import { hasNativeBackend, isDesktopApp, useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { ensureBackendHandshake, isBackendConnected } from "@/lib/eo/connectionStore";
import {
  IDLE_SCAN,
  abandonPageScan,
  getPageScan,
  runPageScan,
  subscribePageScan,
  type PageScanState,
} from "@/lib/eo/pageScan";
import { liveScanCoordinator } from "@/lib/eo/liveScanCoordinator";

export interface PageScan extends PageScanState {
  scanning: boolean;
  /** Results from a scan are available. True only once current mount scan has completed. */
  ready: boolean;
  /** Always false - loading screens and artificial delays have been removed. */
  entryLoading: boolean;
  /** REFRESH: repeats this page's scan; ignored while one is already running. */
  refresh: () => void;
}

export function usePageScan(key: string, task?: () => unknown): PageScan {
  const connected = useIsBackendConnected();
  const taskRef = useRef(task);
  taskRef.current = task;
  const started = useRef<number | null>(null);

  const state = useSyncExternalStore(
    subscribePageScan,
    () => getPageScan(key),
    () => IDLE_SCAN,
  );

  const run = useCallback(
    async (force: boolean) => {
      if (isDesktopApp() && !isBackendConnected()) {
        try {
          await ensureBackendHandshake();
        } catch {
          // Handshake timed out or failed
        }
      }
      if (!isBackendConnected() && !hasNativeBackend()) return Promise.resolve();
      const { gen, promise } = runPageScan(
        key,
        () => (taskRef.current ? taskRef.current() : Promise.resolve()),
        { force },
      );
      if (gen !== null) started.current = gen;
      return promise;
    },
    [key],
  );

  // Initial scan on page entry (and re-entry on navigation) when the native backend is connected.
  useEffect(() => {
    let active = true;
    const isConn = isBackendConnected() || hasNativeBackend() || isDesktopApp();

    if (isConn) {
      void run(true);
    }

    return () => {
      active = false;
      if (started.current !== null) abandonPageScan(key, started.current);
      started.current = null;
    };
  }, [key, run, connected]);

  // Event-driven automatic rescan on external changes or optimization completion
  useEffect(() => {
    return liveScanCoordinator.subscribe(key, () => {
      if (getPageScan(key).phase !== "scanning") {
        void run(true);
      }
    });
  }, [key, run]);

  const refresh = useCallback(() => {
    if (!hasNativeBackend()) {
      if (isDesktopApp()) {
        void ensureBackendHandshake(true).then((snap) => {
          if (snap.phase === "CONNECTED") {
            if (getPageScan(key).phase !== "scanning") void run(true);
          } else {
            toast.error("Backend handshake failed", { description: snap.message });
          }
        });
        return;
      }
      toast.info("Backend connection required", {
        description: "System scanning requires the ERROR OPTIMIZER background service.",
      });
      return;
    }
    if (getPageScan(key).phase !== "scanning") void run(true);
  }, [key, run]);

  const ready = state.runs > 0 || state.phase === "done" || state.phase === "error";

  return {
    ...state,
    scanning: state.phase === "scanning",
    ready,
    entryLoading: false,
    refresh,
  };
}

/** Wraps a `useSimRun().start(onDone)` runner as a scan task that resolves when it finishes. */
export const simRunTask = (start: (onDone?: () => void) => void, onDone?: () => void) => () =>
  new Promise<void>((resolve) =>
    start(() => {
      onDone?.();
      resolve();
    }),
  );

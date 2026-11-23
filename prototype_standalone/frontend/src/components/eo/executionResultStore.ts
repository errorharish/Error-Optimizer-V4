// The one global result dialog: any optimization action calls `showExecutionResult` (like a toast)
// and the root-mounted ExecutionResultDialog renders it with the shared ExecutionResultPanel.
import { useSyncExternalStore } from "react";
import { simulatedResult, type ExecutionResult, type PlannedOperation } from "@/lib/eo/executionResult";

export interface ShownResult {
  result: ExecutionResult;
  onRetry?: ((ids: string[]) => void) | undefined;
  onRollback?: (() => void) | undefined;
}

let current: ShownResult | null = null;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());

export function showExecutionResult(r: ShownResult) {
  current = r;
  emit();
}

export function closeExecutionResult() {
  current = null;
  emit();
}

/** Shows the honest result of a simulated (browser preview) run: nothing was applied. */
export function reportSimulatedRun(
  module: string,
  title: string,
  ops: readonly PlannedOperation[],
  durationMs?: number,
) {
  showExecutionResult({ result: simulatedResult(module, title, ops, durationMs) });
}

export function useShownResult(): ShownResult | null {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => current,
    () => null,
  );
}

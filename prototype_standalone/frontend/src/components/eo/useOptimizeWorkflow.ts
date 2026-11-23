// Shared state for the 1-Click workflow: Analyze → Review → Confirm → Result.
// Selection and summaries go through src/lib/eo/selection.ts; the selection itself is
// persisted (first use: every eligible action selected, protected items never).
import { useCallback, useMemo, useState } from "react";
import { toast } from "sonner";
import { hasNativeBackend } from "@/lib/eo/nativeBridge";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { notifyBackendRequired } from "./kit";
import { useModedSelection } from "./useSelection";
import { ONE_CLICK_ACTIONS } from "@/lib/eo/data";
import { summarizeSelection, type SimulatedRunSummary } from "@/lib/eo/selection";

export type LastRun = SimulatedRunSummary & { at: string; titles: string[] };

export function useOptimizeWorkflow(_scanMs = 1400) {
  const [running, setRunning] = useState(false);
  const [progress, setProgress] = useState(0);
  const [analyzed, setAnalyzed] = useState(false);
  const [actionStates, setActionStates] = useState<Record<string, string>>({});
  const [actionStatuses, setActionStatuses] = useState<Record<string, string>>({});

  const actionableItems = useMemo(() => {
    if (!analyzed) return [];
    return ONE_CLICK_ACTIONS.filter((a) => {
      const st = actionStatuses[a.id.toLowerCase()] ?? actionStatuses[a.title.toLowerCase()] ?? "";
      const isDone = st === "AlreadyCompleted" || st === "Verified";
      return !isDone;
    });
  }, [analyzed, actionStatuses]);

  const selection = useModedSelection("one-click", actionableItems);
  const { sel, setSel } = selection;
  const [lastRun, setLastRun] = useState<LastRun | null>(null);

  const summary = useMemo(() => summarizeSelection(ONE_CLICK_ACTIONS, sel), [sel]);
  const selectedItems = useMemo(() => ONE_CLICK_ACTIONS.filter((a) => sel.has(a.id)), [sel]);

  const analyze = useCallback(async (onDone?: () => void) => {
    if (!hasNativeBackend()) {
      notifyBackendRequired("1-Click system analysis");
      onDone?.();
      return;
    }
    setRunning(true);
    setProgress(25);
    const timer = window.setInterval(() => setProgress((p) => Math.min(90, p + 10)), 250);
    try {
      const plan = await sendHostRequest<Record<string, unknown>>("oneclick:discover", undefined, 30000);
      const rawActions = plan?.["actions"] ?? plan?.["Actions"];
      if (Array.isArray(rawActions)) {
        const nextStates: Record<string, string> = {};
        const nextStatuses: Record<string, string> = {};
        for (const row of rawActions) {
          if (!row || typeof row !== "object") continue;
          const r = row as Record<string, unknown>;
          const id = String(r["id"] ?? r["Id"] ?? "").toLowerCase();
          const title = String(r["title"] ?? r["Title"] ?? "").toLowerCase();
          const statusRaw = String(r["status"] ?? r["Status"] ?? "");
          const current =
            typeof (r["currentStateText"] ?? r["CurrentStateText"] ?? r["currentState"] ?? r["CurrentState"]) === "string"
              ? String(r["currentStateText"] ?? r["CurrentStateText"] ?? r["currentState"] ?? r["CurrentState"])
              : statusRaw === "AlreadyCompleted" || statusRaw === "Verified"
                ? "Already optimized"
                : "Ready to optimize";
          if (id) {
            nextStates[id] = current;
            if (statusRaw) nextStatuses[id] = statusRaw;
          }
          if (title) {
            nextStates[title] = current;
            if (statusRaw) nextStatuses[title] = statusRaw;
          }
        }
        setActionStates(nextStates);
        setActionStatuses(nextStatuses);
      }
      setProgress(100);
      setAnalyzed(true);
    } catch (err) {
      setProgress(0);
      setAnalyzed(false);
      toast.error("System analysis failed", {
        description: err instanceof Error ? err.message : String(err),
      });
    } finally {
      window.clearInterval(timer);
      setRunning(false);
      onDone?.();
    }
  }, []);

  const scan = useMemo(
    () => ({
      running,
      progress,
      start: (onDone?: () => void) => {
        void analyze(onDone);
      },
      cancel: () => setRunning(false),
    }),
    [analyze, progress, running],
  );

  return {
    scan,
    analyzed,
    sel,
    setSel,
    summary,
    selectedItems,
    lastRun,
    moded: selection,
    toggle: selection.toggle,
    analyze: (onDone?: () => void) => {
      void analyze(onDone);
    },
    selectRecommended: selection.selectAll,
    clear: selection.clear,
    complete: (s: SimulatedRunSummary) =>
      setLastRun({
        ...s,
        at: new Date().toLocaleTimeString(),
        titles: selectedItems.map((a) => a.title),
      }),
    dismissRun: () => setLastRun(null),
    getStateLabel: (id: string, title?: string) =>
      analyzed
        ? (actionStates[id.toLowerCase()] ??
            (title ? actionStates[title.toLowerCase()] : undefined) ??
            "Analyzed — ready to apply")
        : "Not scanned yet",
    getStatus: (id: string, title?: string) =>
      analyzed
        ? (actionStatuses[id.toLowerCase()] ??
            (title ? actionStatuses[title.toLowerCase()] : undefined) ??
            null)
        : null,
  };
}

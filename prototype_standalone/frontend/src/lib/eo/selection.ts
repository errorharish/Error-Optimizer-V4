// Pure selection + simulated-run helpers shared by every selectable list and run flow.
// Nothing here touches a real system: results only describe what the prototype simulated.
import type { Risk } from "./data";

/** Returns a new Set with `id` toggled (immutable, safe for React state). */
export function toggleInSet<T>(set: Set<T>, id: T): Set<T> {
  const next = new Set(set);
  if (next.has(id)) next.delete(id);
  else next.add(id);
  return next;
}

interface Selectable {
  id: string;
  risk: Risk;
}

/** Anything that may appear as a selectable row. Flags mark items that must never be
 *  selected automatically even though the user may still pick some of them by hand. */
export interface SelectionCandidate<T extends string | number = string> {
  id: T;
  risk?: Risk | undefined;
  /** Already applied on the sample system — nothing to do. */
  done?: boolean | undefined;
  /** Not applicable to the sample hardware / unsupported. Never selectable. */
  unsupported?: boolean | undefined;
  /** Incompatible with the current device / profile / configuration. Never selectable. */
  incompatible?: boolean | undefined;
  /** Evaluated ineligible by server or hardware rules. Never selectable. */
  ineligible?: boolean | undefined;
  /** Not installed on the system (e.g. absent browser). Never selectable. */
  notInstalled?: boolean | undefined;
  /** Irreversible or data-deleting — needs an explicit, confirmed user choice. */
  destructive?: boolean | undefined;
  /** Explicitly not recommended for this mode (omitted = recommended). */
  recommended?: boolean | undefined;
  /** Exclusion or eligibility explanation. */
  reason?: string | undefined;
}

/** May the user select this item at all? Protected, unsupported, incompatible, and already optimal items never. */
export function isSelectable(i: SelectionCandidate<string | number> | null | undefined): boolean {
  if (!i || i.id === undefined || i.id === null) return false;
  return (
    i.risk !== "PROTECTED" &&
    i.risk !== "UNKNOWN" &&
    !i.unsupported &&
    !i.incompatible &&
    !i.ineligible &&
    !i.notInstalled &&
    !i.done
  );
}

/** Is this item part of the first-use default selection?
 *  Applicable AND eligible: excludes protected, unsupported, incompatible,
 *  destructive, and already-applied items.
 *  Eligible high-risk items are explicitly included in automatic selection. */
export function isDefaultEligible(i: SelectionCandidate<string | number> | null | undefined): boolean {
  if (!i || i.id === undefined || i.id === null) return false;
  return (
    isSelectable(i) &&
    !i.destructive &&
    !i.done &&
    !i.unsupported &&
    !i.incompatible &&
    !i.ineligible &&
    !i.notInstalled
  );
}

/** First-use default: every eligible item selected. */
export function eligibleSelection<T extends string | number>(
  items: readonly SelectionCandidate<T>[],
): Set<T> {
  return new Set(items.filter(isDefaultEligible).map((i) => i.id));
}

/** Restores a saved selection: unknown, protected or unsupported ids are dropped, so a stale
 *  or tampered save can never make a protected item executable. */
export function restoreSelection<T extends string | number>(
  saved: readonly string[],
  items: readonly SelectionCandidate<T>[],
): Set<T> {
  const keep = new Set(saved);
  return new Set(items.filter((i) => isSelectable(i) && keep.has(String(i.id))).map((i) => i.id));
}

/** Clamps any requested selection to selectable items (Select all, toggles, restores). */
export function clampSelection<T extends string | number>(
  requested: Iterable<T>,
  items: readonly SelectionCandidate<T>[],
): Set<T> {
  const want = new Set(requested);
  return new Set(items.filter((i) => isSelectable(i) && want.has(i.id)).map((i) => i.id));
}

/** Default selection after an analysis: protected items are never selected silently. */
export function recommendedSelection(items: readonly Selectable[]): Set<string> {
  return eligibleSelection(items);
}

export interface SelectionSummary {
  total: number;
  restart: number;
  byRisk: Record<Risk, number>;
}

export function summarizeSelection(
  items: readonly (Selectable & { restart?: boolean })[],
  selected: Set<string>,
): SelectionSummary {
  const picked = items.filter((i) => i && selected.has(i.id));
  const byRisk: Record<Risk, number> = { SAFE: 0, LOW: 0, MEDIUM: 0, HIGH: 0, PROTECTED: 0, UNKNOWN: 0 };
  for (const i of picked) {
    const r = (i && i.risk) ? i.risk : "LOW";
    if (r in byRisk) byRisk[r] += 1;
    else byRisk.LOW += 1;
  }
  return { total: picked.length, restart: picked.filter((i) => i && i.restart).length, byRisk };
}

export interface SimulatedRunSummary {
  selected: number;
  simulatedSteps: number;
  restartNoted: number;
  realChanges: number;
  verification: string;
  elapsedMs: number;
}

export function simulatedRunSummary(
  selected: number,
  restartNoted: number,
  elapsedMs: number,
): SimulatedRunSummary {
  return {
    selected,
    simulatedSteps: selected,
    restartNoted,
    realChanges: 0,
    verification: "Not performed (prototype)",
    elapsedMs,
  };
}

/* ── Optimization modes & the one authoritative execution plan ──
 * ALL_ELIGIBLE / AUTO = every eligible item is selected automatically based on system & catalog eligibility.
 * Eligible high-risk items are included by default.
 * CUSTOM = user-defined selection within selectable scope via the shared selection modal.
 * Counts, confirm dialogs, run queues and summaries all read the same plan. */
export type SelectionMode = "ALL_ELIGIBLE" | "CUSTOM" | "AUTO" | "ELIGIBLE_ONLY";

export interface PlanCandidate<T extends string | number> extends SelectionCandidate<T> {
  name?: string | undefined;
  title?: string | undefined;
  t?: string | undefined;
  n?: string | undefined;
  cat?: string | undefined;
  category?: string | undefined;
  group?: string | undefined;
  desc?: string | undefined;
  d?: string | undefined;
  why?: string | undefined;
  note?: string | undefined;
  status?: string | undefined;
  size?: string | undefined;
  sizeBytes?: number | undefined;
  /** Ids that must also run for this item to be safe (unmet → excluded). */
  requires?: readonly T[] | undefined;
}

export interface ExecutionPlan<T extends string | number> {
  mode: SelectionMode;
  /** Unique catalog entries (duplicates removed). */
  available: number;
  /** Items that are eligible to run. */
  eligible: number;
  /** Number of eligible items with HIGH risk. */
  highRiskEligible: number;
  /** Number of currently selected items in the queue with HIGH risk. */
  highRiskSelected: number;
  /** Ordered, de-duplicated ids that will run. */
  queue: T[];
  /** Items not in the queue, with a reason (why they are ineligible or excluded). */
  excluded: { id: T; reason: string }[];
  duplicates: number;
}

/** Why an item is not part of the automatic scope, or null when it is eligible. */
export function exclusionReasonFor(i: SelectionCandidate<string | number> | null | undefined): string | null {
  if (!i || i.id === undefined || i.id === null) return "Invalid item";
  if (i.notInstalled) return "Not installed";
  if (i.unsupported) return "Not applicable";
  if (i.incompatible) return "Incompatible";
  if (i.ineligible) return (i as { reason?: string }).reason ?? "Ineligible for this configuration";
  if (i.risk === "PROTECTED" || (i as { status?: string }).status === "PROTECTED") return "Protected";
  if (i.risk === "UNKNOWN") return "Unknown risk metadata";
  if (i.done || (i as { status?: string }).status === "ALREADY_CLEAN") return "Already applied";
  if (i.destructive) return "Requires separate authorization";
  return null;
}

export interface ItemEligibilityStatus {
  eligible: boolean;
  selectable: boolean;
  statusText: string;
  badgeTone: "success" | "cyan" | "warning" | "danger" | "muted";
  reason?: string;
}

/** Normalized classification status for rendering in lists, custom modals and review flows. */
export function getEligibilityStatus<T extends string | number>(
  i: PlanCandidate<T> | null | undefined,
  selectedSet?: ReadonlySet<T>,
): ItemEligibilityStatus {
  if (!i || i.id === undefined || i.id === null) {
    return {
      eligible: false,
      selectable: false,
      statusText: "INVALID",
      badgeTone: "danger",
      reason: "Malformed optimization item record",
    };
  }
  if (i.notInstalled) {
    return {
      eligible: false,
      selectable: false,
      statusText: "NOT INSTALLED",
      badgeTone: "muted",
      reason: "Software or package is not installed on this PC",
    };
  }
  if (i.ineligible) {
    return {
      eligible: false,
      selectable: false,
      statusText: "INELIGIBLE",
      badgeTone: "danger",
      reason: i.reason ?? "Ineligible for current hardware/OS configuration",
    };
  }
  if (i.unsupported) {
    return {
      eligible: false,
      selectable: false,
      statusText: "NOT APPLICABLE",
      badgeTone: "muted",
      reason: "Not applicable to this system",
    };
  }
  if (i.incompatible) {
    return {
      eligible: false,
      selectable: false,
      statusText: "INCOMPATIBLE",
      badgeTone: "muted",
      reason: "Incompatible with current environment",
    };
  }
  if (i.risk === "PROTECTED" || i.status === "PROTECTED") {
    return {
      eligible: false,
      selectable: false,
      statusText: "PROTECTED",
      badgeTone: "muted",
      reason: "Protected system component — never selectable",
    };
  }
  if (i.risk === "UNKNOWN") {
    return {
      eligible: false,
      selectable: false,
      statusText: "UNKNOWN RISK",
      badgeTone: "danger",
      reason: "Item has unknown risk classification and cannot be executed",
    };
  }
  if (i.done || i.status === "ALREADY_CLEAN") {
    return {
      eligible: false,
      selectable: false,
      statusText: "ALREADY APPLIED",
      badgeTone: "muted",
      reason: "Already applied on this system",
    };
  }
  if (i.destructive) {
    return {
      eligible: false,
      selectable: true,
      statusText: "CONFIRM REQUIRED",
      badgeTone: "warning",
      reason: "Requires explicit user confirmation",
    };
  }
  if (i.requires && selectedSet) {
    const missing = i.requires.filter((req) => !selectedSet.has(req));
    if (missing.length > 0) {
      return {
        eligible: false,
        selectable: false,
        statusText: "DEPENDENCY BLOCKED",
        badgeTone: "warning",
        reason: `Requires: ${missing.join(", ")}`,
      };
    }
  }
  return { eligible: true, selectable: true, statusText: "ELIGIBLE", badgeTone: "success" };
}

export function buildExecutionPlan<T extends string | number>(
  items: readonly PlanCandidate<T>[] | null | undefined,
  mode: SelectionMode = "ALL_ELIGIBLE",
  custom: ReadonlySet<T> = new Set(),
): ExecutionPlan<T> {
  const safeItems = items || [];
  const seen = new Set<T>();
  const unique: PlanCandidate<T>[] = [];
  for (const i of safeItems) {
    if (!i || i.id === undefined || i.id === null || seen.has(i.id)) continue;
    seen.add(i.id);
    unique.push(i);
  }
  const reasons = new Map<T, string>();

  // 1. Authoritative candidate eligibility
  const eligibleCandidates: PlanCandidate<T>[] = [];
  let highRiskEligible = 0;
  for (const i of unique) {
    if (!i) continue;
    const r = exclusionReasonFor(i);
    if (r) {
      reasons.set(i.id, r);
    } else {
      eligibleCandidates.push(i);
      if (i.risk === "HIGH") highRiskEligible++;
    }
  }

  const isCustom = mode === "CUSTOM";
  let queue: PlanCandidate<T>[];
  if (!isCustom) {
    // ALL ELIGIBLE / AUTO: every eligible item is selected by default, including high-risk
    queue = [...eligibleCandidates];
  } else {
    // CUSTOM: user-defined selection within selectable scope
    queue = unique.filter((i) => {
      if (custom.has(i.id)) {
        if (!isSelectable(i)) {
          reasons.set(i.id, "Protected / Not selectable");
          return false;
        }
        if (i.done || (i as { status?: string }).status === "ALREADY_CLEAN") {
          reasons.set(i.id, "Already applied");
          return false;
        }
        return true;
      }
      reasons.set(i.id, "Not selected");
      return false;
    });
  }

  // 2. Resolve dependencies (drop items whose required dependencies are not in the queue)
  for (;;) {
    const ids = new Set(queue.map((i) => i.id));
    const next = queue.filter((i) => (i.requires ?? []).every((d) => ids.has(d)));
    for (const i of queue) {
      if (!next.includes(i)) reasons.set(i.id, "Missing dependency");
    }
    if (next.length === queue.length) break;
    queue = next;
  }

  const highRiskSelected = queue.filter((i) => i && i.risk === "HIGH").length;

  return {
    mode,
    available: unique.length,
    eligible: eligibleCandidates.length,
    highRiskEligible,
    highRiskSelected,
    queue: queue.map((i) => i.id),
    excluded: unique
      .filter((i) => reasons.has(i.id))
      .map((i) => ({ id: i.id, reason: reasons.get(i.id)! })),
    duplicates: safeItems.length - unique.length,
  };
}

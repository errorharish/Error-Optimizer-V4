// One execution-result model for every V3 + V4 optimization run. Overall status is computed only
// from operation-level results — never from button clicks, progress or selection counts.
//
// Windows host contract (not sent by any host yet — the missing information): an
// `{ type: "optimize:result", runId, durationMs?, error?, rollback?, operations: [...] }` message
// whose operations carry `executed`, `ok`, `verified` (true / false / null), `error`,
// `skippedReason` and `retryable`. `parseHostResult` normalizes it; any missing verification stays
// UNVERIFIED. Browser preview runs never touch the PC, so `simulatedResult` reports every operation
// as SKIPPED with the preview reason, and the result is labelled simulated.

export type OpStatus = "succeeded" | "failed" | "skipped" | "unverified";
export type RunStatus =
  | "SUCCESS"
  | "PARTIAL"
  | "FAILED"
  | "UNVERIFIED"
  | "SKIPPED"
  | "ALREADY_OPTIMAL"
  | "NO_ACTIONABLE_OPTIMIZATIONS"
  | "AUDIT_ONLY"
  | "CANCELLED"
  | "DEFERRED";

export interface OperationResult {
  id: string;
  title: string;
  status: OpStatus;
  /** Why it failed, was skipped or could not be verified — shown as written. */
  reason?: string | undefined;
  /** Verification readback, when the host performed one. */
  verification?: string | undefined;
  /** Failed operations the host allows to run again. */
  retryable?: boolean | undefined;
  /** Module-specific context (risk, target, step detail). */
  detail?: string | undefined;
  /** Canonical backend status (e.g., "ALREADY_OPTIMIZED", "VERIFIED", "FAILED", "UNVERIFIED", "SKIPPED_BUSY", "READ_ONLY_AUDIT"). */
  canonicalStatus?: string | undefined;
  /** Structured flag indicating this operation's target state was already satisfied (excluded from visible UI). */
  isAlreadyOptimized?: boolean | undefined;
}

export interface Recovery {
  available: boolean;
  /** Button / restore point label when available, otherwise why rollback is not offered. */
  label: string;
}

export interface ExecutionResult {
  title: string;
  module: string;
  simulated: boolean;
  operations: OperationResult[];
  rawStatus?: string | undefined;
  /** Execution-level failure (the run itself could not start or crashed). */
  error?: string | undefined;
  durationMs?: number | undefined;
  recovery: Recovery;
}

export interface ResultCounts {
  requested: number;
  succeeded: number;
  failed: number;
  skipped: number;
  unverified: number;
  alreadyOptimized: number;
  newlyApplied: number;
  busy: number;
  notInstalled: number;
  ineligible: number;
  audits: number;
  protectedCount: number;
  unsupported: number;
}

/**
 * Canonical result-visibility rule:
 * Returns true if the operation represents an already-optimized / already-optimal state.
 * These operations are kept in internal accounting but MUST NEVER appear in any visible
 * optimization execution result UI list.
 */
export function isAlreadyOptimizedOp(
  o: Pick<OperationResult, "status" | "detail" | "reason" | "verification" | "canonicalStatus" | "isAlreadyOptimized"> | null | undefined
): boolean {
  if (!o) return false;
  if (o.isAlreadyOptimized === true) return true;
  const canonical = (o.canonicalStatus ?? "").toUpperCase();
  if (
    canonical === "ALREADY_OPTIMIZED" ||
    canonical === "ALREADY_OPTIMAL" ||
    canonical === "ALREADY_APPLIED"
  ) {
    return true;
  }
  const det = (o.detail ?? "").toUpperCase();
  if (
    det === "ALREADY_OPTIMIZED" ||
    det === "ALREADY_OPTIMAL" ||
    det === "ALREADY_APPLIED"
  ) {
    return true;
  }
  const reason = (o.reason ?? "").toLowerCase();
  const verif = (o.verification ?? "").toLowerCase();
  if (
    reason.startsWith("already optimized") ||
    reason.startsWith("already optimal") ||
    reason.includes("already in desired") ||
    reason.includes("matches target") ||
    verif.startsWith("already optimized") ||
    verif.includes("matches target state")
  ) {
    return true;
  }
  return false;
}

/**
 * Returns true only if the operation should be visible in the execution result modal.
 * Visible outcomes: VERIFIED, FAILED, UNVERIFIED, SKIPPED_BUSY / DEFERRED, and READ_ONLY_AUDIT.
 * Already-optimized operations and permanently ineligible items are strictly excluded.
 */
export function isVisibleResultOp(o: OperationResult | null | undefined): boolean {
  if (!o) return false;
  if (isAlreadyOptimizedOp(o)) return false;
  // Read-only firmware/hardware audits and diagnostics are visible execution outcomes
  if (
    o.canonicalStatus?.toUpperCase() === "READ_ONLY_AUDIT" ||
    o.detail?.toUpperCase() === "READ_ONLY_AUDIT" ||
    o.canonicalStatus?.toUpperCase() === "AUDIT"
  ) {
    return true;
  }
  // Protected system and security components are visible execution outcomes explaining the protection policy
  if (
    o.canonicalStatus?.toUpperCase() === "PROTECTED" ||
    o.detail?.toUpperCase() === "PROTECTED"
  ) {
    return true;
  }
  // If an operation was excluded/ineligible and NOT busy, it is also not a visible execution outcome
  if (o.status === "skipped" && (o.detail === "INELIGIBLE" || o.canonicalStatus === "INELIGIBLE")) {
    return false;
  }
  return true;
}

/**
 * Filters an operation result collection to return ONLY visible execution outcomes.
 * Excludes all ALREADY_OPTIMIZED operations and non-busy exclusions.
 */
export function visibleResultOperations(ops: readonly OperationResult[]): OperationResult[] {
  return ops.filter(isVisibleResultOp);
}

export function countResults(ops: readonly OperationResult[]): ResultCounts {
  const alreadyOptimized = ops.filter(isAlreadyOptimizedOp).length;

  const newlyApplied = ops.filter(
    (o) => o.status === "succeeded" && !isAlreadyOptimizedOp(o),
  ).length;

  const protectedCount = ops.filter(
    (o) =>
      !isAlreadyOptimizedOp(o) &&
      (o.canonicalStatus?.toUpperCase() === "PROTECTED" ||
       o.detail?.toUpperCase() === "PROTECTED" ||
       o.reason?.toLowerCase().includes("protected")),
  ).length;

  const unsupported = ops.filter(
    (o) =>
      !isAlreadyOptimizedOp(o) &&
      (o.canonicalStatus?.toUpperCase() === "UNSUPPORTED" ||
       o.detail?.toUpperCase() === "UNSUPPORTED" ||
       o.reason?.toLowerCase().includes("unsupported")),
  ).length;

  const failed = ops.filter(
    (o) =>
      o.status === "failed" &&
      !isAlreadyOptimizedOp(o) &&
      o.canonicalStatus?.toUpperCase() !== "PROTECTED" &&
      o.detail?.toUpperCase() !== "PROTECTED" &&
      o.canonicalStatus?.toUpperCase() !== "UNSUPPORTED" &&
      o.canonicalStatus?.toUpperCase() !== "READ_ONLY_AUDIT",
  ).length;

  const unverified = ops.filter(
    (o) => o.status === "unverified" && !isAlreadyOptimizedOp(o),
  ).length;

  // Skipped strictly partitions the remaining operations:
  const skipped = ops.filter(
    (o) => o.status === "skipped" && !isAlreadyOptimizedOp(o),
  ).length;

  const busy = ops.filter(
    (o) =>
      o.status === "skipped" &&
      !isAlreadyOptimizedOp(o) &&
      (o.detail?.toUpperCase() === "SKIPPED_BUSY" ||
       o.canonicalStatus?.toUpperCase() === "SKIPPED_BUSY" ||
       o.reason?.toLowerCase().includes("running") ||
       o.reason?.toLowerCase().includes("contention") ||
       o.reason?.toLowerCase().includes("busy")),
  ).length;

  const notInstalled = ops.filter(
    (o) =>
      o.status === "skipped" &&
      !isAlreadyOptimizedOp(o) &&
      (o.detail?.toUpperCase() === "NOT_INSTALLED" ||
       o.canonicalStatus?.toUpperCase() === "NOT_INSTALLED" ||
       o.reason?.toLowerCase().includes("not installed") ||
       o.reason?.toLowerCase().includes("absent")),
  ).length;

  const audits = ops.filter(
    (o) =>
      o.status === "skipped" &&
      !isAlreadyOptimizedOp(o) &&
      (o.canonicalStatus?.toUpperCase() === "READ_ONLY_AUDIT" ||
       o.detail?.toUpperCase() === "READ_ONLY_AUDIT" ||
       o.canonicalStatus?.toUpperCase() === "AUDIT"),
  ).length;

  const ineligible = Math.max(0, skipped - busy - notInstalled - audits - protectedCount - unsupported);

  return {
    requested: ops.length,
    succeeded: newlyApplied + alreadyOptimized,
    failed,
    skipped,
    unverified,
    alreadyOptimized,
    newlyApplied,
    busy,
    notInstalled,
    ineligible,
    audits,
    protectedCount,
    unsupported,
  };
}

/**
 * SUCCESS only when every requested operation succeeded/already-optimal and was verified.
 * PARTIAL when at least one succeeded and anything else failed, was deferred or is unverified.
 * FAILED when nothing succeeded and something failed (or the run itself failed).
 * UNVERIFIED when nothing succeeded or failed but something ran without confirmation.
 * SKIPPED when nothing was executed at all.
 * ALREADY_OPTIMAL when all requested operations already match optimal target state.
 * NO_ACTIONABLE_OPTIMIZATIONS when requested items are non-mutating audits or diagnostics.
 */
export function runStatus(r: Pick<ExecutionResult, "operations" | "error" | "rawStatus">): RunStatus {
  const raw = (r.rawStatus ?? "").toUpperCase();
  if (raw === "NO_ACTIONABLE_OPTIMIZATIONS") return "NO_ACTIONABLE_OPTIMIZATIONS";
  if (raw === "AUDIT_ONLY") return "AUDIT_ONLY";
  if (raw === "ALREADY_OPTIMAL") return "ALREADY_OPTIMAL";
  if (raw === "DEFERRED") return "DEFERRED";
  if (raw === "CANCELLED") return "CANCELLED";

  const c = countResults(r.operations);
  if (c.requested === 0) return r.error ? "FAILED" : "UNVERIFIED";

  // If all requested operations are read-only audits:
  if (c.audits > 0 && c.audits === c.requested) {
    return "NO_ACTIONABLE_OPTIMIZATIONS";
  }
  if (c.audits > 0 && c.audits + c.alreadyOptimized === c.requested) {
    return "NO_ACTIONABLE_OPTIMIZATIONS";
  }

  // All operations already optimal (or combination of already optimal, protected, audits, unsupported without failures):
  if (
    c.alreadyOptimized > 0 &&
    c.failed === 0 &&
    c.unverified === 0 &&
    (c.alreadyOptimized + c.protectedCount + c.audits + c.unsupported === c.requested)
  ) {
    return "ALREADY_OPTIMAL";
  }
  if (c.alreadyOptimized === c.requested && c.requested > 0) {
    return "ALREADY_OPTIMAL";
  }

  if (r.error && raw !== "NO_ACTIONABLE_OPTIMIZATIONS" && raw !== "ALREADY_OPTIMAL") {
    return (c.newlyApplied > 0 || c.alreadyOptimized > 0) ? "PARTIAL" : "FAILED";
  }
  if (c.failed > 0) return (c.newlyApplied > 0 || c.alreadyOptimized > 0) ? "PARTIAL" : "FAILED";
  if (c.unverified > 0) return (c.newlyApplied > 0 || c.alreadyOptimized > 0) ? "PARTIAL" : "UNVERIFIED";

  // If any requested operation was deferred because a target was busy/running:
  if (c.busy > 0) {
    return (c.newlyApplied > 0 || c.alreadyOptimized > 0) ? "PARTIAL" : "SKIPPED";
  }

  // All actionable, non-deferred operations reached verified terminal state
  const executedOrEligible = c.requested - c.skipped;
  if (executedOrEligible > 0 && (c.newlyApplied + c.alreadyOptimized) === executedOrEligible) {
    return "SUCCESS";
  }
  if ((c.newlyApplied + c.alreadyOptimized + c.protectedCount) >= c.requested && c.failed === 0) {
    return c.newlyApplied > 0 ? "SUCCESS" : "ALREADY_OPTIMAL";
  }
  if ((c.newlyApplied + c.alreadyOptimized) === c.requested) return "SUCCESS";
  if (c.newlyApplied > 0 || c.alreadyOptimized > 0) return "PARTIAL";

  return "SKIPPED";
}

export const RUN_STATUS_TITLE: Record<RunStatus, string> = {
  SUCCESS: "OPTIMIZATION SUCCESSFUL",
  PARTIAL: "PARTIALLY OPTIMIZED",
  FAILED: "OPTIMIZATION FAILED",
  UNVERIFIED: "VERIFICATION PENDING",
  SKIPPED: "NO ELIGIBLE OPTIMIZATIONS",
  ALREADY_OPTIMAL: "ALREADY OPTIMAL",
  NO_ACTIONABLE_OPTIMIZATIONS: "NO ACTIONABLE OPTIMIZATIONS",
  AUDIT_ONLY: "AUDIT ONLY",
  CANCELLED: "OPTIMIZATION CANCELLED",
  DEFERRED: "OPTIMIZATION DEFERRED",
};

export const OP_STATUS_LABEL: Record<OpStatus, string> = {
  succeeded: "VERIFIED",
  failed: "FAILED",
  skipped: "SKIPPED",
  unverified: "UNVERIFIED",
};

/** Failed operations that may run again (successful ones are never re-run). */
export const retryTargets = (r: ExecutionResult): OperationResult[] =>
  r.operations.filter((o) => o.status === "failed" && o.retryable === true);

/** Applies a retry result: only the retried operations change; everything else is kept. */
export function mergeRetry(prev: ExecutionResult, retry: ExecutionResult): ExecutionResult {
  const next = new Map(retry.operations.map((o) => [o.id, o]));
  const eligible = new Set(retryTargets(prev).map((o) => o.id));
  return {
    ...prev,
    rawStatus: retry.rawStatus ?? prev.rawStatus,
    error: retry.error,
    durationMs:
      prev.durationMs !== undefined || retry.durationMs !== undefined
        ? (prev.durationMs ?? 0) + (retry.durationMs ?? 0)
        : undefined,
    operations: prev.operations.map((o) => (eligible.has(o.id) ? (next.get(o.id) ?? o) : o)),
    recovery: retry.recovery.available ? retry.recovery : prev.recovery,
  };
}

/* ── Unconnected backend fallback ── */

export const PREVIEW_REASON = "Backend connection required — connect the Windows service to apply changes";
const NO_ROLLBACK_PREVIEW: Recovery = {
  available: false,
  label: "Rollback unavailable without an executed transaction",
};

export interface PlannedOperation {
  id: string;
  title: string;
  /** Eligibility / safety / dependency reason the module already gives for not running it. */
  skipReason?: string | undefined;
  detail?: string | undefined;
}

/** Result of a simulated run: nothing executed, so every operation is SKIPPED with its reason. */
export function simulatedResult(
  module: string,
  title: string,
  ops: readonly PlannedOperation[],
  durationMs?: number,
): ExecutionResult {
  return {
    module,
    title,
    simulated: true,
    durationMs,
    recovery: NO_ROLLBACK_PREVIEW,
    operations: ops.map((o) => ({
      id: o.id,
      title: o.title,
      status: "skipped",
      reason: o.skipReason ?? PREVIEW_REASON,
      detail: o.detail,
    })),
  };
}

/* ── Windows host result (contract above) ── */

const obj = (v: unknown): Record<string, unknown> =>
  v && typeof v === "object" ? (v as Record<string, unknown>) : {};
const str = (v: unknown) => (typeof v === "string" && v.trim() ? v.trim() : undefined);

/** One host operation → status. Missing or unknown verification is UNVERIFIED, never success. */
export function hostOpStatus(raw: unknown): {
  status: OpStatus;
  reason?: string | undefined;
  canonicalStatus?: string | undefined;
  isAlreadyOptimized?: boolean | undefined;
} {
  const o = obj(raw);
  const statusUpper = (str(o["status"]) ?? str(o["Status"]) ?? str(o["canonicalStatus"]) ?? "").toUpperCase();
  const outcomeLower = (str(o["outcome"]) ?? str(o["Outcome"]) ?? "").toLowerCase();

  const isAudit =
    statusUpper === "READ_ONLY_AUDIT" ||
    statusUpper === "AUDIT_ONLY" ||
    statusUpper === "AUDIT" ||
    str(o["finalResult"])?.toUpperCase() === "READ_ONLY_AUDIT" ||
    str(o["eligibilityState"])?.toUpperCase() === "READ_ONLY_AUDIT" ||
    str(o["executionType"])?.toUpperCase() === "READ_ONLY_AUDIT" ||
    str(o["detail"])?.toUpperCase() === "READ_ONLY_AUDIT";

  if (isAudit) {
    return {
      status: "skipped",
      reason: str(o["reason"]) ?? str(o["Reason"]) ?? "Read-only hardware/firmware diagnostic audit; non-mutating",
      canonicalStatus: "READ_ONLY_AUDIT",
      isAlreadyOptimized: false,
    };
  }

  const isAlready =
    o["alreadyOptimized"] === true ||
    o["alreadyDesired"] === true ||
    o["isAlreadyOptimized"] === true ||
    statusUpper === "ALREADY_OPTIMIZED" ||
    statusUpper === "ALREADY_OPTIMAL" ||
    statusUpper === "ALREADY_APPLIED" ||
    str(o["finalResult"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
    str(o["eligibilityState"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
    outcomeLower === "alreadyoptimized" ||
    outcomeLower === "alreadycompleted" ||
    outcomeLower === "alreadyoptimal" ||
    str(o["detail"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
    str(o["detail"])?.toUpperCase() === "ALREADY_OPTIMAL" ||
    str(o["reason"])?.toLowerCase().startsWith("already optimized") ||
    str(o["reason"])?.toLowerCase().startsWith("already optimal") ||
    str(o["skippedReason"])?.toLowerCase().startsWith("already optimized") ||
    str(o["skippedReason"])?.toLowerCase().startsWith("already optimal");

  if (isAlready) {
    return {
      status: "succeeded",
      reason: str(o["reason"]) ?? str(o["skippedReason"]) ?? "Already in desired optimal state",
      canonicalStatus: "ALREADY_OPTIMIZED",
      isAlreadyOptimized: true,
    };
  }

  const isProtected =
    statusUpper === "PROTECTED" ||
    statusUpper.includes("PROTECTED") ||
    str(o["finalResult"])?.toUpperCase() === "PROTECTED" ||
    str(o["eligibilityState"])?.toUpperCase() === "PROTECTED_BY_POLICY" ||
    outcomeLower === "protected" ||
    str(o["detail"])?.toUpperCase() === "PROTECTED" ||
    str(o["reason"])?.toLowerCase().includes("protected by") ||
    str(o["message"])?.toLowerCase().includes("protected by");

  if (isProtected) {
    return {
      status: "skipped",
      reason: str(o["reason"]) ?? str(o["message"]) ?? str(o["skippedReason"]) ?? "Protected by Windows system security policy or driver isolation",
      canonicalStatus: "PROTECTED",
      isAlreadyOptimized: false,
    };
  }

  const isUnsupported =
    statusUpper === "UNSUPPORTED" ||
    statusUpper === "NOT_APPLICABLE" ||
    str(o["finalResult"])?.toUpperCase() === "UNSUPPORTED" ||
    str(o["eligibilityState"])?.toUpperCase() === "UNSUPPORTED" ||
    str(o["eligibilityState"])?.toUpperCase() === "NOT_APPLICABLE" ||
    outcomeLower === "unsupported" ||
    outcomeLower === "notapplicable";

  if (isUnsupported) {
    return {
      status: "skipped",
      reason: str(o["reason"]) ?? str(o["message"]) ?? str(o["skippedReason"]) ?? "Target is unsupported on current hardware or Windows build",
      canonicalStatus: "UNSUPPORTED",
      isAlreadyOptimized: false,
    };
  }

  const skipped = str(o["skippedReason"]) ?? str(o["SkippedReason"]);
  if (skipped) return { status: "skipped", reason: skipped, canonicalStatus: "SKIPPED" };
  if (o["executed"] !== true && o["Executed"] !== true) {
    return { status: "skipped", reason: str(o["error"]) ?? str(o["Error"]) ?? str(o["reason"]) ?? str(o["Reason"]) ?? str(o["resultText"]) ?? str(o["ResultText"]) ?? "Not executed by the Windows service", canonicalStatus: "SKIPPED" };
  }
  if (o["ok"] !== true && o["Ok"] !== true && o["success"] !== true && o["Success"] !== true) {
    return { status: "failed", reason: str(o["error"]) ?? str(o["Error"]) ?? str(o["reason"]) ?? str(o["Reason"]) ?? str(o["resultText"]) ?? str(o["ResultText"]) ?? "The operation reported a failure", canonicalStatus: "FAILED" };
  }
  if (o["verified"] === true || o["Verified"] === true) return { status: "succeeded", canonicalStatus: "VERIFIED" };
  if (o["verified"] === false || o["Verified"] === false) {
    return { status: "failed", reason: str(o["verifyDetail"]) ?? str(o["VerifyDetail"]) ?? str(o["error"]) ?? str(o["Error"]) ?? str(o["reason"]) ?? str(o["Reason"]) ?? str(o["resultText"]) ?? str(o["ResultText"]) ?? "Verification failed — the new state was not found", canonicalStatus: "FAILED" };
  }
  return { status: "unverified", reason: str(o["reason"]) ?? str(o["Reason"]) ?? str(o["resultText"]) ?? str(o["ResultText"]) ?? "Applied, but the resulting state could not be confirmed", canonicalStatus: "UNVERIFIED" };
}

export function parseHostResult(raw: unknown, module: string, title: string): ExecutionResult | null {
  const m = obj(raw);
  const typeStr = str(m["type"]) ?? str(m["Type"]);
  const modId = str(m["moduleId"]) ?? str(m["ModuleId"]);
  const modTitle = str(m["moduleTitle"]) ?? str(m["ModuleTitle"]);
  if (typeStr !== "optimize:result" && !modId && !modTitle) return null;

  const rawOps = Array.isArray(m["operations"])
    ? [...m["operations"]]
    : Array.isArray(m["Operations"])
      ? [...m["Operations"]]
      : Array.isArray(m["completedActions"])
        ? [...m["completedActions"]]
        : Array.isArray(m["CompletedActions"])
          ? [...m["CompletedActions"]]
          : Array.isArray(m["items"])
            ? [...m["items"]]
            : Array.isArray(m["Items"])
              ? [...m["Items"]]
              : [];

  const rawExOps = Array.isArray(m["excludedOperations"])
    ? m["excludedOperations"]
    : Array.isArray(m["ExcludedOperations"])
      ? m["ExcludedOperations"]
      : null;

  // Only consider excludedOperations as a fallback when operations is completely empty,
  // and ONLY for already-optimal items (never dump unselected or permanently ineligible items).
  if (rawOps.length === 0 && Array.isArray(rawExOps)) {
    for (const ex of rawExOps as unknown[]) {
      const exObj = obj(ex);
      const exId = str(exObj["id"]) ?? str(exObj["Id"]);
      const isAlreadyEx =
        exObj["alreadyOptimized"] === true ||
        exObj["AlreadyOptimized"] === true ||
        exObj["alreadyDesired"] === true ||
        exObj["AlreadyDesired"] === true ||
        str(exObj["finalResult"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        str(exObj["FinalResult"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        str(exObj["eligibilityState"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        str(exObj["EligibilityState"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        str(exObj["reason"])?.toLowerCase().startsWith("already optimized") ||
        str(exObj["Reason"])?.toLowerCase().startsWith("already optimized") ||
        str(exObj["reason"])?.toLowerCase().startsWith("already optimal") ||
        str(exObj["Reason"])?.toLowerCase().startsWith("already optimal");

      if (exId && isAlreadyEx && !rawOps.some((o) => (str(obj(o)["id"]) ?? str(obj(o)["Id"]))?.toLowerCase() === exId.toLowerCase())) {
        rawOps.push({
          ...exObj,
          alreadyOptimized: true,
          status: "ALREADY_OPTIMIZED",
          canonicalStatus: "ALREADY_OPTIMIZED",
          detail: "ALREADY_OPTIMIZED",
        });
      }
    }
  }
  const rb = obj(m["rollback"]) || obj(m["Rollback"]);
  const restore =
    str(rb["restorePoint"]) ??
    str(rb["RestorePoint"]) ??
    str(m["transactionId"]) ??
    str(m["TransactionId"]) ??
    str(rb["transactionId"]) ??
    str(rb["TransactionId"]);
  const hasBackup = (rb["available"] === true || rb["Available"] === true || Boolean(restore)) && Boolean(restore);
  return {
    module: modId ?? module,
    title: modTitle ?? title,
    simulated: false,
    rawStatus: str(m["status"]) ?? str(m["Status"]),
    error: str(m["error"]) ?? str(m["Error"]) ?? str(m["errorMessage"]) ?? str(m["ErrorMessage"]),
    durationMs: typeof m["durationMs"] === "number" ? m["durationMs"] : typeof m["DurationMs"] === "number" ? m["DurationMs"] : undefined,
    recovery:
      hasBackup && restore
        ? { available: true, label: restore }
        : { available: false, label: "No backup or restore point was reported for this run" },
    operations: rawOps.map((raw, i) => {
      const o = obj(raw);
      const s = hostOpStatus(o);
      const isAlready = s.isAlreadyOptimized === true;
      const isAudit = s.canonicalStatus === "READ_ONLY_AUDIT";
      return {
        id: str(o["id"]) ?? str(o["Id"]) ?? str(o["itemId"]) ?? str(o["ItemId"]) ?? `op-${i + 1}`,
        title: str(o["title"]) ?? str(o["Title"]) ?? str(o["name"]) ?? str(o["Name"]) ?? `Operation ${i + 1}`,
        status: s.status,
        reason: s.reason ?? str(o["resultText"]) ?? str(o["ResultText"]),
        verification: str(o["verifyDetail"]) ?? str(o["VerifyDetail"]) ?? str(o["verification"]) ?? str(o["Verification"]) ?? (isAlready ? "Current configuration already matches target" : isAudit ? (str(o["currentValue"]) ?? str(o["CurrentValue"]) ? `Current: ${str(o["currentValue"]) ?? str(o["CurrentValue"])}` : "Diagnostic audit inspection") : undefined),
        retryable: s.status === "failed" && (o["retryable"] === true || o["Retryable"] === true),
        detail: isAlready ? "ALREADY_OPTIMIZED" : isAudit ? "READ_ONLY_AUDIT" : (str(o["detail"]) ?? str(o["Detail"])),
        canonicalStatus: s.canonicalStatus ?? (isAlready ? "ALREADY_OPTIMIZED" : s.status === "succeeded" ? "VERIFIED" : s.status.toUpperCase()),
        isAlreadyOptimized: isAlready,
      };
    }),
  };
}

/** One-line explanation under the result title, from the counts only. */
export function resultSummary(r: ExecutionResult): string {
  const c = countResults(r.operations);
  const s = runStatus(r);
  const ops = (n: number) => `${n} operation${n === 1 ? "" : "s"}`;
  switch (s) {
    case "ALREADY_OPTIMAL":
      if (c.protectedCount > 0) {
        return `All ${c.alreadyOptimized} operations already in optimal state · ${c.protectedCount} protected by system policy`;
      }
      return c.alreadyOptimized > 0
        ? `All ${c.alreadyOptimized} operations are already in their desired optimal state.`
        : "All operations are already in their desired optimal state.";
    case "NO_ACTIONABLE_OPTIMIZATIONS":
    case "AUDIT_ONLY":
      return `${c.audits || c.requested} read-only diagnostic audit${(c.audits || c.requested) === 1 ? " is" : "s are"} available. No firmware mutations were attempted.`;
    case "CANCELLED":
      return "The optimization was cancelled by the user.";
    case "DEFERRED":
      return `${c.busy || c.requested} operation${(c.busy || c.requested) === 1 ? " was" : "s were"} deferred due to running applications.`;
    case "SUCCESS": {
      if (c.alreadyOptimized === c.requested && c.requested > 0) {
        return `All ${c.alreadyOptimized} operations are already in their desired optimal state.`;
      }
      if (c.newlyApplied === c.requested && c.requested > 0) {
        return `SUCCESSFULLY OPTIMIZED — ${c.newlyApplied}/${c.newlyApplied}`;
      }
      if (c.skipped > 0) {
        const eligibleCount = c.requested - c.skipped;
        if (c.alreadyOptimized === eligibleCount) {
          return `All ${c.alreadyOptimized} operations are already in their desired optimal state.`;
        }
        return `${c.newlyApplied} newly applied and verified${c.alreadyOptimized > 0 ? ` · ${c.alreadyOptimized} already optimal` : ""}`;
      }
      if (c.alreadyOptimized > 0) {
        return `${c.newlyApplied} newly applied and verified · ${c.alreadyOptimized} confirmed already optimal`;
      }
      return `All ${ops(c.requested)} applied and verified`;
    }
    case "PARTIAL":
      return [
        c.newlyApplied ? `${c.newlyApplied} applied and verified` : "",
        c.alreadyOptimized ? `${c.alreadyOptimized} already optimal` : "",
        c.failed ? `${c.failed} failed` : "",
        c.busy ? `${c.busy} deferred (running process)` : "",
        c.notInstalled ? `${c.notInstalled} not installed` : "",
        c.audits ? `${c.audits} diagnostic audits` : "",
        c.ineligible ? `${c.ineligible} excluded` : "",
        c.unverified ? `${c.unverified} unverified` : "",
      ]
        .filter(Boolean)
        .join(" · ");
    case "FAILED":
      return r.error ?? `None of the ${ops(c.requested)} succeeded`;
    case "UNVERIFIED":
      return c.requested
        ? `${ops(c.unverified)} applied — the final state could not be confirmed`
        : "The Windows service returned no operation results";
    case "SKIPPED":
      return r.simulated
        ? `Backend connection required · ${ops(c.requested)} not applied`
        : `No operation was executed`;
  }
}
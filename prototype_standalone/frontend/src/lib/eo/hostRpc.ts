// Shared WebView2 RPC transport & backend result normalizers (100% non-visual).
import { isDesktopApp } from "./nativeBridge";
import { ensureBackendHandshake, getConnectionSnapshot } from "./connectionStore";
import { parseHostResult, type ExecutionResult, type OperationResult } from "./executionResult";
import {
  normalizeOptimizationCatalog,
  type CatalogNormalizationResult,
  type CanonicalOptimizationItem,
} from "./catalogContract";

export { normalizeOptimizationCatalog, type CatalogNormalizationResult, type CanonicalOptimizationItem };

export function normalizeBackendScanResponse(
  raw: unknown,
  sourceContext = "backend",
): CatalogNormalizationResult {
  return normalizeOptimizationCatalog(raw, sourceContext);
}

interface WebViewPort {
  postMessage: (message: string) => void;
  addEventListener?: (type: "message", handler: (event: { data: unknown }) => void) => void;
  removeEventListener?: (type: "message", handler: (event: { data: unknown }) => void) => void;
}

let rpcSeq = 0;

function getWebView(): WebViewPort | null {
  if (typeof window === "undefined") return null;
  return (window.chrome?.webview as WebViewPort | undefined) ?? null;
}

export function parseHostMessageData(raw: unknown): Record<string, unknown> | null {
  let parsed = raw;
  if (typeof parsed === "string") {
    try {
      parsed = JSON.parse(parsed);
    } catch {
      return null;
    }
  }
  return parsed && typeof parsed === "object" ? (parsed as Record<string, unknown>) : null;
}

export function postHostFireAndForget(action: string, payload?: Record<string, unknown>): void {
  const wv = getWebView();
  if (!wv) return;
  try {
    wv.postMessage(JSON.stringify(payload ? { action, ...payload } : { action }));
  } catch {
    /* ignore WebView teardown races */
  }
}

export async function sendHostRequest<T = unknown>(
  action: string,
  payload?: unknown,
  timeoutMs = 60000,
): Promise<T> {
  if (!isDesktopApp()) {
    throw new Error("Windows desktop host bridge is not connected.");
  }

  // Ensure initial handshake completed before dispatching feature RPCs.
  if (action !== "host:handshake" && action !== "service:health") {
    const snap = getConnectionSnapshot();
    if (snap.phase === "INITIALIZING" || snap.phase === "CONNECTING") {
      await ensureBackendHandshake();
    }
  }

  return new Promise<T>((resolve, reject) => {
    const wv = getWebView();
    if (!wv) {
      reject(new Error("Windows desktop host bridge is not connected."));
      return;
    }

    const id = `rpc-${++rpcSeq}-${Date.now()}`;
    let settled = false;

    const cleanup = () => {
      window.clearTimeout(timer);
      wv.removeEventListener?.("message", onMessage);
    };

    const onMessage = (event: { data: unknown }) => {
      if (settled) return;
      const msg = parseHostMessageData(event.data);
      if (!msg || msg["id"] !== id) return;

      settled = true;
      cleanup();

      if (msg["ok"] === false) {
        const rawErr = msg["error"];
        const errText =
          typeof rawErr === "string" && rawErr.trim()
            ? rawErr
            : `Backend action '${action}' failed.`;
        reject(new Error(errText));
        return;
      }

      resolve((msg["result"] !== undefined ? msg["result"] : msg) as T);
    };

    const timer = window.setTimeout(() => {
      if (settled) return;
      settled = true;
      cleanup();
      reject(new Error(`Timed out waiting for Windows backend response (${action}).`));
    }, timeoutMs);

    wv.addEventListener?.("message", onMessage);
    try {
      const frame: Record<string, unknown> = { id, action };
      if (payload !== undefined) frame["payload"] = payload;
      wv.postMessage(JSON.stringify(frame));
    } catch (err) {
      if (!settled) {
        settled = true;
        cleanup();
        reject(err instanceof Error ? err : new Error(String(err)));
      }
    }
  });
}

export function subscribeHostEvent<T = Record<string, unknown>>(
  eventType: string,
  handler: (msg: T) => void,
): () => void {
  const wv = getWebView();
  if (!wv) return () => undefined;

  const onMessage = (event: { data: unknown }) => {
    const msg = parseHostMessageData(event.data);
    if (!msg || msg["type"] !== eventType) return;
    handler(msg as unknown as T);
  };

  wv.addEventListener?.("message", onMessage);
  return () => wv.removeEventListener?.("message", onMessage);
}

function asRec(v: unknown): Record<string, unknown> {
  return v && typeof v === "object" ? (v as Record<string, unknown>) : {};
}

function asStr(v: unknown): string | undefined {
  return typeof v === "string" && v.trim() ? v.trim() : undefined;
}

/**
 * Normalizes any C# service / host execution payload into `ExecutionResult`
 * for `ExecutionResultPanel` & `RunModal`. Never invents synthetic success.
 */
function formatOutcomeDetail(o: Record<string, unknown>): string | undefined {
  const base =
    asStr(o["verifyDetail"]) ??
    asStr(o["verification"]) ??
    asStr(o["detail"]) ??
    asStr(o["message"]) ??
    asStr(o["reason"]) ??
    asStr(o["resultText"]);
  const bytes = typeof o["reclaimedBytes"] === "number" ? o["reclaimedBytes"] : undefined;
  const files = typeof o["filesDeleted"] === "number" ? o["filesDeleted"] : undefined;
  if (base) return base;
  if (bytes !== undefined && bytes > 0) {
    const mb = bytes >= 1024 * 1024 ? `${(bytes / (1024 * 1024)).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`;
    return files !== undefined ? `Reclaimed ${mb} (${files} files)` : `Reclaimed ${mb}`;
  }
  return undefined;
}

const KNOWN_OP_TITLES: Record<string, string> = {
  "temp-windows": "Windows Temp Files",
  "temp-user": "User Temp Files",
  "temp-prefetch": "Prefetch Cache",
  "browser-chrome": "Chrome Cache",
  "browser-edge": "Edge Cache",
  "browser-firefox": "Firefox Cache",
  "mem-standby": "Clear Standby Memory",
  "sys-shader": "Clear Shader Cache",
  "sys-dns": "Flush DNS Cache",
  "recycle-bin": "Empty Recycle Bin",
};

export function normalizeBackendExecutionResult(
  raw: unknown,
  module: string,
  title: string,
  requestedTitles: readonly { id: string; title: string }[] = [],
  durationMs?: number,
): ExecutionResult {
  const direct = parseHostResult(raw, module, title);
  if (direct) return direct;

  const outerRec = asRec(raw);
  const rec =
    outerRec["entry"] && typeof outerRec["entry"] === "object" && !Array.isArray(outerRec["entry"])
      ? asRec(outerRec["entry"])
      : outerRec;

  const getArrayProp = (r: Record<string, unknown>, keys: string[]): unknown[] | null => {
    for (const k of keys) {
      if (Array.isArray(r[k])) return r[k] as unknown[];
    }
    return null;
  };

  const opsRaw = Array.isArray(raw)
    ? raw
    : getArrayProp(rec, [
        "operations", "Operations",
        "results", "Results",
        "tasks", "Tasks",
        "completedActions", "CompletedActions",
        "items", "Items",
        "outcomes", "Outcomes",
        "actions", "Actions",
      ]);

  let operations: OperationResult[] = [];

  if (opsRaw && opsRaw.length > 0) {
    operations = opsRaw.map((entry, idx) => {
      const o = asRec(entry);
      const id =
        asStr(o["id"]) ??
        asStr(o["Id"]) ??
        asStr(o["itemId"]) ??
        asStr(o["ItemId"]) ??
        asStr(o["canonicalId"]) ??
        asStr(o["CanonicalId"]) ??
        asStr(o["optimizationId"]) ??
        asStr(o["OptimizationId"]) ??
        asStr(o["key"]) ??
        asStr(o["Key"]) ??
        requestedTitles[idx]?.id ??
        `op-${idx + 1}`;
      const matchedReq = requestedTitles.find((r) => r.id.toLowerCase() === id.toLowerCase());
      const opTitle =
        asStr(o["title"]) ??
        asStr(o["Title"]) ??
        asStr(o["displayName"]) ??
        asStr(o["DisplayName"]) ??
        asStr(o["name"]) ??
        asStr(o["Name"]) ??
        asStr(o["actionName"]) ??
        asStr(o["ActionName"]) ??
        matchedReq?.title ??
        KNOWN_OP_TITLES[id.toLowerCase()] ??
        requestedTitles[idx]?.title ??
        id;

      const outcomeRaw =
        asStr(o["outcome"]) ??
        asStr(o["Outcome"]) ??
        asStr(o["status"]) ??
        asStr(o["Status"]) ??
        asStr(o["canonicalStatus"]) ??
        asStr(o["CanonicalStatus"]);
      const outcomeLower = outcomeRaw?.toLowerCase();
      const statusUpper = (asStr(o["canonicalStatus"]) ?? asStr(o["CanonicalStatus"]) ?? asStr(o["status"]) ?? asStr(o["Status"]) ?? "").toUpperCase();

      const isAlready =
        o["alreadyOptimized"] === true ||
        o["AlreadyOptimized"] === true ||
        o["alreadyDesired"] === true ||
        o["AlreadyDesired"] === true ||
        o["isAlreadyOptimized"] === true ||
        o["IsAlreadyOptimized"] === true ||
        statusUpper === "ALREADY_OPTIMIZED" ||
        statusUpper === "ALREADY_OPTIMAL" ||
        statusUpper === "ALREADY_APPLIED" ||
        asStr(o["finalResult"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        asStr(o["FinalResult"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        asStr(o["eligibilityState"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        asStr(o["EligibilityState"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        outcomeLower === "alreadyoptimized" ||
        outcomeLower === "alreadycompleted" ||
        outcomeLower === "alreadyoptimal" ||
        outcomeLower === "alreadydesired" ||
        outcomeLower === "already_optimized" ||
        asStr(o["detail"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        asStr(o["Detail"])?.toUpperCase() === "ALREADY_OPTIMIZED" ||
        asStr(o["detail"])?.toUpperCase() === "ALREADY_OPTIMAL" ||
        asStr(o["Detail"])?.toUpperCase() === "ALREADY_OPTIMAL" ||
        asStr(o["reason"])?.toLowerCase().startsWith("already optimized") ||
        asStr(o["Reason"])?.toLowerCase().startsWith("already optimized") ||
        asStr(o["reason"])?.toLowerCase().startsWith("already optimal") ||
        asStr(o["Reason"])?.toLowerCase().startsWith("already optimal") ||
        asStr(o["skippedReason"])?.toLowerCase().startsWith("already optimized") ||
        asStr(o["SkippedReason"])?.toLowerCase().startsWith("already optimized") ||
        asStr(o["skippedReason"])?.toLowerCase().startsWith("already optimal") ||
        asStr(o["SkippedReason"])?.toLowerCase().startsWith("already optimal");

      if (isAlready) {
        return {
          id,
          title: opTitle,
          status: "succeeded",
          isAlreadyOptimized: true,
          canonicalStatus: "ALREADY_OPTIMIZED",
          detail: "ALREADY_OPTIMIZED",
          verification: formatOutcomeDetail(o) || "Current configuration matches target optimal state",
          reason: asStr(o["reason"]) ?? asStr(o["Reason"]) ?? asStr(o["skippedReason"]) ?? asStr(o["SkippedReason"]) ?? asStr(o["resultText"]) ?? asStr(o["ResultText"]) ?? "Already in desired optimal state",
          retryable: false,
        };
      }

      const skippedReason =
        asStr(o["skippedReason"]) ??
        asStr(o["SkippedReason"]) ??
        (o["skipped"] === true ||
        o["Skipped"] === true ||
        outcomeLower === "skipped" ||
        outcomeLower === "cancelled" ||
        outcomeLower === "notavailable" ||
        outcomeLower === "notapplicable" ||
        outcomeLower === "protected" ||
        outcomeLower === "unsupported" ||
        outcomeLower === "requires_elevation" ||
        statusUpper === "NOT_INSTALLED" ||
        statusUpper === "SKIPPED_BUSY" ||
        statusUpper === "PROTECTED" ||
        statusUpper === "UNSUPPORTED" ||
        statusUpper === "REQUIRES_ELEVATION" ||
        statusUpper === "READ_ONLY_AUDIT"
          ? asStr(o["reason"]) ?? asStr(o["Reason"]) ?? asStr(o["message"]) ?? asStr(o["Message"]) ?? "Skipped by backend rule"
          : undefined);
      if (skippedReason) {
        let detail = asStr(o["detail"]) ?? asStr(o["Detail"]) ?? statusUpper;
        if (!detail || detail === "SKIPPED") {
          if (statusUpper === "NOT_INSTALLED" || skippedReason.toLowerCase().includes("not installed")) detail = "NOT_INSTALLED";
          else if (statusUpper === "SKIPPED_BUSY" || skippedReason.toLowerCase().includes("running") || skippedReason.toLowerCase().includes("contention")) detail = "SKIPPED_BUSY";
          else if (statusUpper === "PROTECTED" || skippedReason.toLowerCase().includes("protected")) detail = "PROTECTED";
          else if (statusUpper === "REQUIRES_ELEVATION" || skippedReason.toLowerCase().includes("elevation") || skippedReason.toLowerCase().includes("administrator")) detail = "REQUIRES_ELEVATION";
          else if (statusUpper === "UNSUPPORTED" || skippedReason.toLowerCase().includes("unsupported")) detail = "UNSUPPORTED";
          else if (statusUpper === "READ_ONLY_AUDIT" || statusUpper === "AUDIT") detail = "READ_ONLY_AUDIT";
        }
        return {
          id,
          title: opTitle,
          status: "skipped",
          reason: skippedReason,
          detail,
          canonicalStatus: statusUpper || detail,
          retryable: false,
        };
      }

      const failFlag =
        o["failed"] === true ||
        o["Failed"] === true ||
        o["success"] === false ||
        o["Success"] === false ||
        o["ok"] === false ||
        o["Ok"] === false ||
        outcomeLower === "failed" ||
        outcomeLower === "error" ||
        outcomeLower === "blocked" ||
        outcomeLower === "timeout" ||
        outcomeLower === "blockedthermal";
      if (failFlag) {
        const errReason = asStr(o["error"]) ?? asStr(o["Error"]) ?? asStr(o["message"]) ?? asStr(o["Message"]) ?? asStr(o["reason"]) ?? asStr(o["Reason"]) ?? asStr(o["resultText"]) ?? asStr(o["ResultText"]) ?? "Backend operation failed";
        const isPerm =
          errReason.toLowerCase().includes("access denied") ||
          errReason.toLowerCase().includes("administrator") ||
          errReason.toLowerCase().includes("privilege") ||
          errReason.toLowerCase().includes("not found") ||
          errReason.toLowerCase().includes("unsupported") ||
          errReason.toLowerCase().includes("policy");
        const retryable = o["retryable"] !== undefined ? Boolean(o["retryable"]) : (o["Retryable"] !== undefined ? Boolean(o["Retryable"]) : !isPerm);
        return {
          id,
          title: opTitle,
          status: "failed",
          reason: errReason,
          retryable,
          canonicalStatus: "FAILED",
        };
      }

      const okFlag =
        o["succeeded"] === true ||
        o["Succeeded"] === true ||
        o["success"] === true ||
        o["Success"] === true ||
        o["ok"] === true ||
        o["Ok"] === true ||
        o["verified"] === true ||
        o["Verified"] === true ||
        outcomeLower === "completed" ||
        outcomeLower === "succeeded" ||
        outcomeLower === "success" ||
        outcomeLower === "verified" ||
        outcomeLower === "verifiedsuccess" ||
        outcomeLower === "applied" ||
        outcomeLower === "requiresreboot" ||
        outcomeLower === "alreadyoptimized" ||
        outcomeLower === "alreadycompleted" ||
        outcomeLower === "restored";
      if (okFlag) {
        const isAlreadyOptimal = isAlready || o["alreadyOptimized"] === true || o["AlreadyOptimized"] === true;
        const verified = !isAlreadyOptimal && (o["verified"] === true || o["Verified"] === true || o["readbackVerified"] === true || o["ReadbackVerified"] === true || outcomeLower === "verified" || outcomeLower === "verifiedsuccess");
        return {
          id,
          title: opTitle,
          status: isAlreadyOptimal ? "succeeded" : (verified ? "succeeded" : "unverified"),
          isAlreadyOptimized: isAlreadyOptimal,
          canonicalStatus: isAlreadyOptimal ? "ALREADY_OPTIMIZED" : (verified ? "VERIFIED" : "UNVERIFIED"),
          verification: formatOutcomeDetail(o),
          reason: isAlreadyOptimal
            ? (asStr(o["reason"]) ?? asStr(o["Reason"]) ?? asStr(o["resultText"]) ?? asStr(o["ResultText"]) ?? "Already in desired optimal state")
            : (verified ? undefined : (asStr(o["reason"]) ?? asStr(o["Reason"]) ?? asStr(o["resultText"]) ?? asStr(o["ResultText"]) ?? "Applied, but readback state unconfirmed")),
          detail: isAlreadyOptimal ? "ALREADY_OPTIMIZED" : (asStr(o["detail"]) ?? asStr(o["Detail"])),
          retryable: false,
        };
      }

      return {
        id,
        title: opTitle,
        status: "unverified",
        reason: asStr(o["message"]) ?? asStr(o["Message"]) ?? asStr(o["detail"]) ?? asStr(o["Detail"]) ?? asStr(o["reason"]) ?? asStr(o["Reason"]) ?? asStr(o["resultText"]) ?? asStr(o["ResultText"]) ?? "Executed without verification flag",
      };
    });
  } else {
    const topStatus = (asStr(rec["status"]) ?? asStr(rec["Status"]) ?? asStr(rec["verificationResult"]) ?? asStr(rec["VerificationResult"]) ?? "").toLowerCase();
    const topMsg = (asStr(rec["message"]) ?? asStr(rec["Message"]) ?? asStr(rec["summaryMessage"]) ?? asStr(rec["SummaryMessage"]) ?? asStr(rec["summary"]) ?? asStr(rec["Summary"]) ?? "").toLowerCase();

    const appliedCountNum = typeof rec["appliedCount"] === "number" ? (rec["appliedCount"] as number) : (typeof rec["AppliedCount"] === "number" ? (rec["AppliedCount"] as number) : 0);
    const restoredCountNum = typeof rec["restoredCount"] === "number" ? (rec["restoredCount"] as number) : (typeof rec["RestoredCount"] === "number" ? (rec["RestoredCount"] as number) : 0);
    const verifiedCountNum = typeof rec["verifiedCount"] === "number" ? (rec["verifiedCount"] as number) : (typeof rec["VerifiedCount"] === "number" ? (rec["VerifiedCount"] as number) : 0);
    const alreadyOptCountNum = Number(rec["alreadyOptimizedCount"]) || Number(rec["AlreadyOptimizedCount"]) || Number(rec["alreadyOptimalCount"]) || Number(rec["AlreadyOptimalCount"]) || 0;
    const failedCountNum = typeof rec["failedCount"] === "number" ? (rec["failedCount"] as number) : (typeof rec["FailedCount"] === "number" ? (rec["FailedCount"] as number) : 0);
    const eligibleCountNum = typeof rec["eligibleCount"] === "number" ? (rec["eligibleCount"] as number) : (typeof rec["EligibleCount"] === "number" ? (rec["EligibleCount"] as number) : undefined);

    const hasAppliedCount = appliedCountNum > 0;
    const hasRestoredCount = restoredCountNum > 0;
    const hasVerifiedCount = verifiedCountNum > 0;
    const hasAlreadyCount = alreadyOptCountNum > 0;
    const hasRealOperations = hasAppliedCount || hasRestoredCount || hasVerifiedCount || hasAlreadyCount;

    const isExplicitSuccess =
      rec["success"] === true ||
      rec["Success"] === true ||
      rec["ok"] === true ||
      rec["Ok"] === true ||
      rec["overallSuccess"] === true ||
      rec["OverallSuccess"] === true;

    const isNoEligible =
      topStatus.includes("no_eligible") ||
      topStatus.includes("not_applicable") ||
      topMsg.includes("no eligible") ||
      rec["status"] === "NO_ELIGIBLE_OPERATIONS" ||
      rec["Status"] === "NO_ELIGIBLE_OPERATIONS" ||
      eligibleCountNum === 0 ||
      (appliedCountNum === 0 && alreadyOptCountNum === 0 && verifiedCountNum === 0 && failedCountNum === 0 && !hasRealOperations && (topStatus.includes("complete") || topStatus.includes("optimal")));

    const topOk =
      !isNoEligible &&
      (hasRealOperations || isExplicitSuccess) &&
      (isExplicitSuccess ||
      topStatus === "completed" ||
      topStatus === "verifiedsuccess" ||
      topStatus === "succeeded" ||
      topStatus === "ok" ||
      (topStatus === "partial" && (appliedCountNum > 0 || verifiedCountNum > 0)));

    const extractedErr =
      asStr(rec["error"]) ??
      asStr(rec["Error"]) ??
      asStr(rec["errorMessage"]) ??
      asStr(rec["ErrorMessage"]) ??
      (topOk ? undefined : (asStr(rec["reason"]) ?? asStr(rec["Reason"]) ?? asStr(rec["summaryMessage"]) ?? asStr(rec["SummaryMessage"]) ?? asStr(rec["message"]) ?? asStr(rec["Message"])));

    const errText = isNoEligible
      ? "No eligible optimizations to apply."
      : (extractedErr ?? "Backend operation failed");

    const verifyMsg =
      asStr(rec["summaryMessage"]) ??
      asStr(rec["SummaryMessage"]) ??
      asStr(rec["summary"]) ??
      asStr(rec["Summary"]) ??
      asStr(rec["message"]) ??
      asStr(rec["Message"]) ??
      asStr(rec["detail"]) ??
      asStr(rec["Detail"]) ??
      "Confirmed by Windows backend";

    if (requestedTitles.length > 0) {
      const allAlready = hasAlreadyCount && !hasAppliedCount && !hasVerifiedCount && failedCountNum === 0;
      operations = requestedTitles.map((item, idx) => {
        const isThisSucceeded = topOk || (idx < (verifiedCountNum + alreadyOptCountNum));
        const isThisAlready = allAlready || (idx < alreadyOptCountNum);
        return {
          id: item.id,
          title: item.title,
          status: isNoEligible ? "skipped" : (isThisSucceeded ? "succeeded" : "failed"),
          isAlreadyOptimized: isThisAlready,
          canonicalStatus: isThisAlready ? "ALREADY_OPTIMIZED" : (isNoEligible ? "INELIGIBLE" : isThisSucceeded ? "VERIFIED" : "FAILED"),
          detail: isThisAlready ? "ALREADY_OPTIMIZED" : undefined,
          reason: isNoEligible ? "Ineligible for this hardware" : (isThisAlready ? "Already in desired optimal state" : (isThisSucceeded ? undefined : errText ?? "Operation did not succeed")),
          verification: isThisSucceeded ? verifyMsg : undefined,
          retryable: !isThisSucceeded && !isNoEligible && !isThisAlready,
        };
      });
    } else if (Object.keys(rec).length > 0) {
      operations = [
        {
          id: "exec-1",
          title,
          status: isNoEligible ? "skipped" : (topOk ? "succeeded" : "failed"),
          reason: topOk ? undefined : errText ?? "Backend execution failed",
          verification: topOk ? verifyMsg : undefined,
          retryable: !topOk && !isNoEligible,
        },
      ];
    }
  }

  const rawExcluded = rec["excludedOperations"] ?? rec["ExcludedOperations"];
  if (Array.isArray(rawExcluded)) {
    for (const rawEx of rawExcluded as unknown[]) {
      const ex = asRec(rawEx);
      const exId = asStr(ex["id"]) ?? `ex-${operations.length + 1}`;
      if (!operations.some((o) => o.id.toLowerCase() === exId.toLowerCase())) {
        operations.push({
          id: exId,
          title: asStr(ex["name"]) ?? asStr(ex["title"]) ?? exId,
          status: "skipped",
          reason: asStr(ex["reason"]) ?? "Ineligible for this machine configuration",
        });
      }
    }
  }

  const backupId =
    asStr(rec["transactionId"]) ??
    asStr(rec["backupId"]) ??
    asStr(rec["snapshotId"]) ??
    asStr(rec["restorePoint"]);

  return {
    module,
    title,
    simulated: false,
    error: asStr(rec["error"]),
    durationMs: typeof rec["durationMs"] === "number" ? rec["durationMs"] : durationMs,
    recovery: backupId
      ? { available: true, label: backupId }
      : { available: false, label: "Manage snapshots in Backup & Restore" },
    operations,
  };
}

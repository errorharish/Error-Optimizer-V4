import type { Risk } from "./data";
import { CATALOG } from "./data";

export type CanonicalRisk = "SAFE" | "LOW" | "MEDIUM" | "HIGH" | "PROTECTED" | "UNKNOWN";

export type CanonicalEligibilityState =
  | "ELIGIBLE"
  | "INELIGIBLE"
  | "UNSUPPORTED"
  | "NOT_APPLICABLE"
  | "ALREADY_APPLIED"
  | "ALREADY_OPTIMIZED"
  | "REQUIRES_ADMIN"
  | "REQUIRES_REBOOT"
  | "REQUIRES_DRIVER"
  | "DEPENDENCY_MISSING"
  | "DETECTION_UNAVAILABLE"
  | "BLOCKED_BY_SAFETY"
  | "RECOMMENDATION_ONLY"
  | "NOT_INSTALLED"
  | "SKIPPED_BUSY"
  | "DEFERRED"
  | "REQUIRES_ELEVATION"
  | "UNKNOWN";

export type PartitionCategory = "NEEDS_OPTIMIZATION" | "ALREADY_OPTIMIZED" | "DEFERRED" | "INELIGIBLE";

export interface CanonicalOptimizationItem {
  id: string;
  name: string;
  description: string;
  category: string;
  risk: CanonicalRisk;
  executionType: string;
  eligibilityState: CanonicalEligibilityState;
  partition: PartitionCategory;
  applicabilityReason?: string | undefined;
  currentState?: string | undefined;
  targetValue?: string | undefined;
  alreadyOptimized: boolean;
  isApplied: boolean;
  isEligible: boolean;
  isSelectable: boolean;
  scope: "WINDOWS" | "UEFI";
  done: boolean;
  deferred: boolean;
  ineligible: boolean;
  notInstalled: boolean;
  reason?: string | undefined;
}

export interface CatalogNormalizationResult {
  items: CanonicalOptimizationItem[];
  eligibleItems: CanonicalOptimizationItem[];
  needsOptimizationItems: CanonicalOptimizationItem[];
  alreadyOptimizedItems: CanonicalOptimizationItem[];
  deferredItems: CanonicalOptimizationItem[];
  ineligibleItems: CanonicalOptimizationItem[];
  notInstalledItems: CanonicalOptimizationItem[];
  diagnostics: string[];
  totalCatalogCount: number;
  scanId?: string | undefined;
  catalogVersion?: string | undefined;
  systemFingerprint?: string | undefined;
  hardwareSummary?: string | undefined;
}

// Authoritative static lookup index by lowercase ID for metadata enhancement
const STATIC_CATALOG_INDEX = new Map<string, { id: string; name: string; cat: string; risk: Risk }>();
for (const entry of CATALOG) {
  STATIC_CATALOG_INDEX.set(entry.id.toLowerCase(), entry);
}

/**
 * Normalizes raw optimization items from backend DTOs or fallback catalogs into
 * authoritative canonical items with guaranteed fields, strict type safety,
 * duplicate filtering, and risk classification validation.
 */
export function normalizeOptimizationCatalog(
  rawRows: unknown,
  sourceContext = "catalog",
): CatalogNormalizationResult {
  const diagnostics: string[] = [];
  const normalized: CanonicalOptimizationItem[] = [];
  const seenIds = new Set<string>();

  let rowsToProcess: unknown[] = [];
  let scanId: string | undefined;
  let catalogVersion: string | undefined;
  let systemFingerprint: string | undefined;
  let hardwareSummary: string | undefined;

  if (Array.isArray(rawRows)) {
    rowsToProcess = rawRows;
  } else if (rawRows && typeof rawRows === "object") {
    const obj = rawRows as Record<string, unknown>;
    scanId = typeof obj["scanId"] === "string" ? obj["scanId"] : typeof obj["ScanId"] === "string" ? obj["ScanId"] : undefined;
    catalogVersion = typeof obj["catalogVersion"] === "string" ? obj["catalogVersion"] : typeof obj["CatalogVersion"] === "string" ? obj["CatalogVersion"] : undefined;
    systemFingerprint = typeof obj["systemFingerprint"] === "string" ? obj["systemFingerprint"] : typeof obj["SystemFingerprint"] === "string" ? obj["SystemFingerprint"] : undefined;
    hardwareSummary = typeof obj["hardwareSummary"] === "string" ? obj["hardwareSummary"] : typeof obj["HardwareSummary"] === "string" ? obj["HardwareSummary"] : undefined;

    const items = Array.isArray(obj["items"]) ? obj["items"] : Array.isArray(obj["Items"]) ? obj["Items"] : [];
    const excluded = Array.isArray(obj["excludedItems"]) ? obj["excludedItems"] : Array.isArray(obj["ExcludedItems"]) ? obj["ExcludedItems"] : [];
    const deferred = Array.isArray(obj["deferredItems"]) ? obj["deferredItems"] : Array.isArray(obj["DeferredItems"]) ? obj["DeferredItems"] : [];
    const ops = Array.isArray(obj["operations"]) ? obj["operations"] : Array.isArray(obj["Operations"]) ? obj["Operations"] : [];
    const tasks = Array.isArray(obj["tasks"]) ? obj["tasks"] : Array.isArray(obj["Tasks"]) ? obj["Tasks"] : [];
    const actions = Array.isArray(obj["actions"]) ? obj["actions"] : Array.isArray(obj["Actions"]) ? obj["Actions"] : [];
    const excludedActions = Array.isArray(obj["excludedActions"]) ? obj["excludedActions"] : Array.isArray(obj["ExcludedActions"]) ? obj["ExcludedActions"] : [];

    rowsToProcess = [...items, ...excluded, ...deferred, ...ops, ...tasks, ...actions, ...excludedActions];
    if (rowsToProcess.length === 0 && Array.isArray(obj["results"])) {
      rowsToProcess = obj["results"] as unknown[];
    }
  } else {
    if (rawRows !== undefined && rawRows !== null) {
      diagnostics.push(`[${sourceContext}] Expected array or object container of items, got ${typeof rawRows}`);
    }
    return {
      items: [],
      eligibleItems: [],
      needsOptimizationItems: [],
      alreadyOptimizedItems: [],
      deferredItems: [],
      ineligibleItems: [],
      notInstalledItems: [],
      diagnostics,
      totalCatalogCount: 0,
    };
  }

  for (let idx = 0; idx < rowsToProcess.length; idx++) {
    const raw = rowsToProcess[idx];
    if (!raw || typeof raw !== "object") {
      diagnostics.push(`[${sourceContext}] Skipped non-object item at index ${idx}`);
      continue;
    }

    const row = raw as Record<string, unknown>;

    // 1. Authoritative ID extraction
    const rawId = (
      row["id"] ??
      row["Id"] ??
      row["itemId"] ??
      row["ItemId"] ??
      row["name"] ??
      row["Name"]
    );
    const id = typeof rawId === "string" ? rawId.trim() : String(rawId || "").trim();

    if (!id) {
      diagnostics.push(`[${sourceContext}] Skipped item at index ${idx} without valid identifier`);
      continue;
    }

    // 2. Duplicate detection
    const lowerId = id.toLowerCase();
    if (seenIds.has(lowerId)) {
      diagnostics.push(`[${sourceContext}] Duplicate item ID "${id}" at index ${idx} excluded`);
      continue;
    }
    seenIds.add(lowerId);

    // 3. Authoritative static fallback metadata lookup
    const staticMeta = STATIC_CATALOG_INDEX.get(lowerId);

    // 4. Name & Category
    const name = (
      (typeof row["name"] === "string" && row["name"].trim()) ||
      (typeof row["Name"] === "string" && row["Name"].trim()) ||
      (typeof row["displayName"] === "string" && row["displayName"].trim()) ||
      (typeof row["title"] === "string" && row["title"].trim()) ||
      staticMeta?.name ||
      id
    );

    const category = (
      (typeof row["category"] === "string" && row["category"].trim().toUpperCase()) ||
      (typeof row["Category"] === "string" && row["Category"].trim().toUpperCase()) ||
      (typeof row["cat"] === "string" && row["cat"].trim().toUpperCase()) ||
      staticMeta?.cat ||
      "GENERAL"
    );

    const description = (
      (typeof row["description"] === "string" && row["description"].trim()) ||
      (typeof row["Description"] === "string" && row["Description"].trim()) ||
      (typeof row["desc"] === "string" && row["desc"].trim()) ||
      ""
    );

    // 5. Risk classification validation (Rules 3 & 4: Never invent default risk; unknown must be explicit)
    const rawRisk = (
      (typeof row["risk"] === "string" && row["risk"].trim()) ||
      (typeof row["Risk"] === "string" && row["Risk"].trim()) ||
      staticMeta?.risk ||
      ""
    ).toUpperCase();

    let risk: CanonicalRisk;
    if (rawRisk === "HIGH" || rawRisk === "CRITICAL" || rawRisk === "EXPERIMENTAL") risk = "HIGH";
    else if (rawRisk === "MEDIUM" || rawRisk === "MODERATE" || rawRisk === "ADVANCED") risk = "MEDIUM";
    else if (rawRisk === "LOW" || rawRisk === "CORE") risk = "LOW";
    else if (rawRisk === "SAFE") risk = "SAFE";
    else if (rawRisk === "PROTECTED") risk = "PROTECTED";
    else {
      risk = "UNKNOWN";
      diagnostics.push(`[${sourceContext}] Item "${id}" has unknown risk "${rawRisk}" — marked non-actionable`);
    }

    // 6. Eligibility State & Terminal Classification
    const rawEligibility = String(
      row["eligibilityState"] ??
      row["EligibilityState"] ??
      row["supportState"] ??
      row["SupportState"] ??
      row["executionState"] ??
      row["ExecutionState"] ??
      row["state"] ??
      row["State"] ??
      row["status"] ??
      row["Status"] ??
      ""
    ).toUpperCase();

    const isApplied = Boolean(
      row["isApplied"] ||
      row["IsApplied"] ||
      rawEligibility === "ALREADY_APPLIED" ||
      rawEligibility === "APPLIED" ||
      row["state"] === "Applied" ||
      row["State"] === "Applied" ||
      row["state"] === "APPLIED"
    );

    const alreadyDone = Boolean(
      row["alreadyOptimized"] ||
      row["AlreadyOptimized"] ||
      isApplied ||
      rawEligibility === "ALREADY_OPTIMIZED" ||
      rawEligibility === "ALREADY_OPTIMAL" ||
      rawEligibility === "OPTIMIZED" ||
      rawEligibility === "ALREADY_CLEAN" ||
      rawEligibility === "VERIFIED"
    );

    const deferred = Boolean(
      row["deferred"] ||
      row["Deferred"] ||
      rawEligibility === "DEFERRED" ||
      rawEligibility === "TEMPORARILY_UNAVAILABLE" ||
      rawEligibility === "SKIPPED_BUSY"
    );

    const isAuditOnly = Boolean(
      row["executionType"] === "READ_ONLY_AUDIT" ||
      row["ExecutionType"] === "READ_ONLY_AUDIT" ||
      row["executionType"] === "DIAGNOSTIC_ONLY" ||
      row["ExecutionType"] === "DIAGNOSTIC_ONLY" ||
      row["executionType"] === "AUDIT" ||
      row["ExecutionType"] === "AUDIT" ||
      rawEligibility === "AUDIT_ONLY" ||
      rawEligibility === "READ_ONLY_AUDIT"
    );

    const notInstalled = Boolean(
      row["notInstalled"] ||
      row["NotInstalled"] ||
      rawEligibility === "NOT_INSTALLED" ||
      rawEligibility === "NOTINSTALLED" ||
      rawEligibility === "APP_NOT_INSTALLED" ||
      row["currentValue"] === "Not Installed" ||
      row["CurrentValue"] === "Not Installed"
    );

    const notApplicable = Boolean(
      row["applicable"] === false ||
      row["Applicable"] === false ||
      row["isApplicable"] === false ||
      rawEligibility === "NOT APPLICABLE" ||
      rawEligibility === "NOT_APPLICABLE" ||
      rawEligibility === "NOT_AVAILABLE"
    );

    const unsupported = Boolean(
      row["unsupported"] ||
      row["Unsupported"] ||
      rawEligibility === "UNSUPPORTED" ||
      rawEligibility === "NOT_SUPPORTED" ||
      rawEligibility === "UNSUPPORTED_HARDWARE" ||
      rawEligibility === "UNSUPPORTED_OS" ||
      rawEligibility === "DRIVER_REQUIREMENT_NOT_MET"
    );

    const requiresElevation = Boolean(
      row["requiresAdmin"] ||
      row["RequiresAdmin"] ||
      rawEligibility === "REQUIRES_ELEVATION" ||
      rawEligibility === "PERMISSION_REQUIRED"
    );

    const blockedBySafety = Boolean(
      rawEligibility === "PROTECTED_BY_POLICY" ||
      rawEligibility === "CONFLICT_DETECTED" ||
      risk === "PROTECTED"
    );

    const recommendationOnly = Boolean(
      rawEligibility === "RECOMMENDATION_ONLY" ||
      row["executionType"] === "MANUAL_ACTION" ||
      row["ExecutionType"] === "MANUAL_ACTION"
    );

    let partition: PartitionCategory;
    let eligibilityState: CanonicalEligibilityState;

    if (alreadyDone) {
      partition = "ALREADY_OPTIMIZED";
      eligibilityState = "ALREADY_OPTIMIZED";
    } else if (deferred) {
      partition = "DEFERRED";
      eligibilityState = "DEFERRED";
    } else if (
      rawEligibility === "ELIGIBLE" ||
      rawEligibility === "PENDING" ||
      rawEligibility === "SUPPORTED" ||
      rawEligibility === "APPLICABLE" ||
      (!notApplicable && !unsupported && (row["applicable"] === true || row["Applicable"] === true))
    ) {
      partition = "NEEDS_OPTIMIZATION";
      eligibilityState = "ELIGIBLE";
    } else {
      // Backend eligibility is AUTHORITATIVE. Any non-eligible item is partitioned into INELIGIBLE.
      partition = "INELIGIBLE";
      if (notInstalled) eligibilityState = "NOT_INSTALLED";
      else if (notApplicable) eligibilityState = "NOT_APPLICABLE";
      else if (unsupported) eligibilityState = "UNSUPPORTED";
      else if (isAuditOnly) eligibilityState = "UNSUPPORTED";
      else if (requiresElevation) eligibilityState = "REQUIRES_ELEVATION";
      else if (blockedBySafety) eligibilityState = "BLOCKED_BY_SAFETY";
      else if (recommendationOnly) eligibilityState = "RECOMMENDATION_ONLY";
      else if (rawEligibility === "INELIGIBLE") eligibilityState = "INELIGIBLE";
      else if (risk === "UNKNOWN") eligibilityState = "UNKNOWN";
      else eligibilityState = "DETECTION_UNAVAILABLE";
    }

    // 7. Actionability & Scope (Strict 4-way partition)
    const isEligible = partition === "NEEDS_OPTIMIZATION";
    const isSelectable = isEligible;
    const done = partition === "ALREADY_OPTIMIZED";
    const isDeferred = partition === "DEFERRED";
    const isIneligible = partition === "INELIGIBLE";

    const scopeRaw = String(row["scope"] ?? row["Scope"] ?? (category === "SYSTEM" || category === "BIOS" ? "UEFI" : "WINDOWS")).toUpperCase();
    const scope: "WINDOWS" | "UEFI" = scopeRaw.includes("UEFI") || scopeRaw.includes("BIOS") ? "UEFI" : "WINDOWS";

    const reason = (
      (typeof row["reason"] === "string" && row["reason"].trim()) ||
      (typeof row["Reason"] === "string" && row["Reason"].trim()) ||
      (typeof row["skippedReason"] === "string" && row["skippedReason"].trim()) ||
      (isAuditOnly ? "Read-only diagnostic audit (no firmware mutation performed)" : undefined) ||
      (notInstalled ? "Software or package not installed on this PC" : undefined) ||
      (notApplicable ? "Target configuration not present on this machine" : undefined) ||
      (unsupported ? "Unsupported on current hardware/OS configuration" : undefined) ||
      (isDeferred ? "Temporarily deferred due to system contention or active process" : undefined) ||
      (done ? "Current configuration already matches target optimal state" : undefined) ||
      (risk === "UNKNOWN" ? "Excluded due to unknown risk classification" : undefined) ||
      undefined
    );

    const currentState = typeof row["currentValue"] === "string" ? row["currentValue"] : (typeof row["CurrentValue"] === "string" ? row["CurrentValue"] : undefined);
    const targetValue = typeof row["targetValue"] === "string" ? row["targetValue"] : (typeof row["TargetValue"] === "string" ? row["TargetValue"] : undefined);
    const executionType =
      typeof row["executionType"] === "string" && row["executionType"].trim()
        ? row["executionType"].trim()
        : typeof row["ExecutionType"] === "string" && row["ExecutionType"].trim()
          ? row["ExecutionType"].trim()
          : isAuditOnly
            ? "READ_ONLY_AUDIT"
            : "WINDOWS_AUTOMATIC";

    normalized.push({
      id,
      name,
      description,
      category,
      risk,
      executionType,
      eligibilityState,
      partition,
      applicabilityReason: reason,
      currentState,
      targetValue,
      alreadyOptimized: done,
      isApplied,
      isEligible,
      isSelectable,
      scope,
      done,
      deferred: isDeferred,
      ineligible: isIneligible,
      notInstalled,
      reason,
    });
  }

  const needsOptimizationItems = normalized.filter((i) => i.partition === "NEEDS_OPTIMIZATION");
  const alreadyOptimizedItems = normalized.filter((i) => i.partition === "ALREADY_OPTIMIZED");
  const deferredItems = normalized.filter((i) => i.partition === "DEFERRED");
  const ineligibleItems = normalized.filter((i) => i.partition === "INELIGIBLE");
  const notInstalledItems = normalized.filter((i) => i.notInstalled);
  const eligibleItems = needsOptimizationItems;

  return {
    items: normalized,
    eligibleItems,
    needsOptimizationItems,
    alreadyOptimizedItems,
    deferredItems,
    ineligibleItems,
    notInstalledItems,
    diagnostics,
    totalCatalogCount: normalized.length,
    scanId,
    catalogVersion,
    systemFingerprint,
    hardwareSummary,
  };
}

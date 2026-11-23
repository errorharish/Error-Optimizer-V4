// The one optimization result display for V3 + V4 (rules: src/lib/eo/executionResult.ts).
// ExecutionResultPanel is embedded by run dialogs (RunModal, Quick Optimize); ExecutionResultDialog
// is mounted once in __root and shows results sent with showExecutionResult / reportSimulatedRun.
// Looks live in the "Execution results" block of styles.css.
import type { ElementType } from "react";
import { AlertTriangle, Check, ChevronDown, Clock3, Minus, RotateCcw, RefreshCw, X } from "lucide-react";
import { Badge, Btn, Modal } from "./kit";
import { StatusIndicator, type StatusTone } from "./StatusIndicator";
import { closeExecutionResult, useShownResult } from "./executionResultStore";
import { cn } from "@/lib/utils";
import {
  OP_STATUS_LABEL,
  PREVIEW_REASON,
  RUN_STATUS_TITLE,
  countResults,
  isAlreadyOptimizedOp,
  resultSummary,
  retryTargets,
  runStatus,
  visibleResultOperations,
  type ExecutionResult,
  type OpStatus,
  type RunStatus,
} from "@/lib/eo/executionResult";

const RUN_TONE: Record<RunStatus, StatusTone> = {
  SUCCESS: "pos",
  PARTIAL: "warn",
  FAILED: "err",
  UNVERIFIED: "warn",
  SKIPPED: "info",
  ALREADY_OPTIMAL: "pos",
  NO_ACTIONABLE_OPTIMIZATIONS: "info",
  AUDIT_ONLY: "info",
  CANCELLED: "warn",
  DEFERRED: "warn",
};

const OP_TONE: Record<OpStatus, StatusTone> = {
  succeeded: "pos",
  failed: "err",
  unverified: "warn",
  skipped: "info",
};

const RUN_ICON: Record<RunStatus, ElementType> = {
  SUCCESS: Check,
  PARTIAL: AlertTriangle,
  FAILED: X,
  UNVERIFIED: Clock3,
  SKIPPED: Minus,
  ALREADY_OPTIMAL: Check,
  NO_ACTIONABLE_OPTIMIZATIONS: Check,
  AUDIT_ONLY: Clock3,
  CANCELLED: X,
  DEFERRED: Clock3,
};

const fmtDuration = (ms: number) => (ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`);

/** Ring filled to the verified share of the requested operations. */
function ResultRing({ status, share }: { status: RunStatus; share: number }) {
  const r = 26;
  const len = 2 * Math.PI * r;
  const fill =
    status === "SUCCESS" ||
    status === "ALREADY_OPTIMAL" ||
    status === "NO_ACTIONABLE_OPTIMIZATIONS" ||
    status === "AUDIT_ONLY"
      ? 1
      : status === "FAILED" || status === "SKIPPED"
        ? 1
        : share;
  const Icon = RUN_ICON[status] ?? Minus;
  return (
    <span className="eo-xr-ring" aria-hidden="true">
      <svg className="eo-xr-ring-svg" viewBox="0 0 64 64" width="64" height="64">
        <circle className="eo-xr-ring-track" cx="32" cy="32" r={r} />
        <circle
          className="eo-xr-ring-arc"
          cx="32"
          cy="32"
          r={r}
          strokeDasharray={len.toFixed(2)}
          style={{ ["--xr-off" as string]: (len * (1 - Math.max(0.04, fill))).toFixed(2), ["--xr-len" as string]: len.toFixed(2) }}
        />
      </svg>
      <Icon className="eo-xr-ring-icon h-5 w-5" />
    </span>
  );
}

/** Problems first so failures are never hidden below a long list of verified items. */
const OP_ORDER: Record<OpStatus, number> = { failed: 0, unverified: 1, skipped: 2, succeeded: 3 };

export function ExecutionResultPanel({
  result,
  onRetry,
  onRollback,
}: {
  result: ExecutionResult;
  /** Re-runs only the given failed operation ids; omit when the module cannot retry. */
  onRetry?: ((ids: string[]) => void) | undefined;
  /** Offered only when result.recovery.available. */
  onRollback?: (() => void) | undefined;
}) {
  const status = runStatus(result);
  const c = countResults(result.operations);
  const retry = retryTargets(result);
  const visibleOps = visibleResultOperations(result.operations);
  const allAlreadyOptimal = (c.alreadyOptimized > 0 && c.newlyApplied === 0 && c.failed === 0 && c.unverified === 0) || status === "ALREADY_OPTIMAL";
  const isAudit = status === "NO_ACTIONABLE_OPTIMIZATIONS" || status === "AUDIT_ONLY";
  const share = c.requested ? c.succeeded / c.requested : 0;
  const executed = c.newlyApplied + c.failed + c.unverified;
  const verifiedLine = result.simulated
    ? "Not performed — simulated run, nothing to verify on this PC"
    : isAudit
      ? `${c.audits || c.requested} diagnostic audit${(c.audits || c.requested) === 1 ? "" : "s"} completed. No firmware or OS mutations were attempted.`
      : allAlreadyOptimal
        ? `All ${c.alreadyOptimized || c.requested} operations confirmed already in desired optimal state`
        : c.requested
          ? (c.newlyApplied > 0
              ? `${c.newlyApplied} of ${executed} executed operation${executed === 1 ? "" : "s"} verified${
                  c.alreadyOptimized > 0 ? ` · ${c.alreadyOptimized} already optimal` : ""
                }${c.unverified ? ` · ${c.unverified} could not be confirmed` : ""}`
              : (c.alreadyOptimized > 0
                  ? `${c.alreadyOptimized} operation${c.alreadyOptimized === 1 ? "" : "s"} already optimal${
                      c.failed > 0 ? ` · ${c.failed} failed` : ""
                    }${c.unverified > 0 ? ` · ${c.unverified} unverified` : ""}`
                  : c.failed > 0
                    ? `${c.failed} of ${executed} operation${executed === 1 ? "" : "s"} failed`
                    : `All ${c.alreadyOptimized || c.requested} operations are already in their desired optimal state.`))
          : "No operation results to verify";

  return (
    <section className="eo-xr" data-status={status} aria-label={`${result.title}: ${RUN_STATUS_TITLE[status]}`}>
      <header className="eo-xr-head">
        <ResultRing status={status} share={share} />
        <div className="min-w-0 flex-1">
          <p className="eo-xr-module">{result.module}</p>
          <h3 className="eo-xr-title" role="status" aria-live="polite">
            {allAlreadyOptimal ? "ALREADY OPTIMAL" : RUN_STATUS_TITLE[status]}
          </h3>
          <p className="eo-xr-sub">
            {allAlreadyOptimal
              ? `All ${c.alreadyOptimized || c.requested} operations are already in their desired optimal state.`
              : resultSummary(result)}
          </p>
        </div>
        <div className="eo-xr-tags">
          <StatusIndicator tone={RUN_TONE[status]} size="sm" live={status === "UNVERIFIED" || status === "PARTIAL"}>
            {status}
          </StatusIndicator>
        </div>
      </header>

      {result.error && !isAudit && !allAlreadyOptimal && (
        <div role="alert" className="eo-xr-error">
          <b>Execution error</b>
          <span>{result.error}</span>
        </div>
      )}

      {(() => {
        const verifiedCount = c.newlyApplied;
        const durationText = result.durationMs !== undefined ? fmtDuration(result.durationMs) : "—";
        const metrics = [
          {
            id: "requested",
            labelTop: "REQUESTED",
            labelBottom: null,
            value: c.requested,
            kind: "all" as const,
            isZero: c.requested === 0,
            textClass: "text-foreground",
            borderClass: "border-border/60 bg-window/60 hover:border-border",
          },
          {
            id: "verified",
            labelTop: "VERIFIED",
            labelBottom: null,
            value: verifiedCount,
            kind: "succeeded" as const,
            isZero: verifiedCount === 0,
            textClass: verifiedCount > 0 ? "text-emerald-400" : "text-muted-foreground/50",
            borderClass:
              verifiedCount > 0
                ? "border-emerald-500/30 bg-emerald-500/[0.04] hover:border-emerald-500/50"
                : "border-border/40 bg-window/30",
          },
          {
            id: "already-optimal",
            labelTop: "ALREADY",
            labelBottom: "OPTIMAL",
            value: c.alreadyOptimized,
            kind: "already" as const,
            isZero: c.alreadyOptimized === 0,
            textClass: c.alreadyOptimized > 0 ? "text-cyan-400" : "text-muted-foreground/50",
            borderClass:
              c.alreadyOptimized > 0
                ? "border-cyan-500/30 bg-cyan-500/[0.04] hover:border-cyan-500/50"
                : "border-border/40 bg-window/30",
          },
          {
            id: "failed",
            labelTop: "FAILED",
            labelBottom: null,
            value: c.failed,
            kind: "failed" as const,
            isZero: c.failed === 0,
            textClass: c.failed > 0 ? "text-rose-400" : "text-muted-foreground/50",
            borderClass:
              c.failed > 0
                ? "border-rose-500/35 bg-rose-500/[0.05] hover:border-rose-500/55"
                : "border-border/40 bg-window/30",
          },
          {
            id: "skipped",
            labelTop: "SKIPPED",
            labelBottom: null,
            value: c.skipped,
            kind: "skipped" as const,
            isZero: c.skipped === 0,
            textClass: c.skipped > 0 ? "text-muted-foreground" : "text-muted-foreground/50",
            borderClass: "border-border/40 bg-window/30 hover:border-border/60",
          },
          {
            id: "unverified",
            labelTop: "UNVERIFIED",
            labelBottom: null,
            value: c.unverified,
            kind: "unverified" as const,
            isZero: c.unverified === 0,
            textClass: c.unverified > 0 ? "text-amber-400" : "text-muted-foreground/50",
            borderClass:
              c.unverified > 0
                ? "border-amber-500/30 bg-amber-500/[0.04] hover:border-amber-500/50"
                : "border-border/40 bg-window/30",
          },
          {
            id: "duration",
            labelTop: "DURATION",
            labelBottom: null,
            value: durationText,
            kind: "time" as const,
            isZero: false,
            textClass: "text-cyan-400",
            borderClass: "border-cyan-500/25 bg-window/40 hover:border-cyan-500/40",
          },
        ];

        return (
          <dl
            className="grid grid-cols-4 min-[500px]:grid-cols-7 gap-1 sm:gap-1.5 w-full select-none"
            aria-label="Optimization metrics summary"
          >
            {metrics.map((m) => (
              <div
                key={m.id}
                data-kind={m.kind}
                data-zero={m.isZero || undefined}
                className={cn(
                  "group relative flex flex-col items-center justify-between min-w-0 rounded-lg border py-2 px-0.5 sm:px-1 transition-all duration-150",
                  m.borderClass,
                )}
              >
                <dt className="flex h-7 w-full flex-col items-center justify-center text-center">
                  <span className="text-[8px] font-bold uppercase tracking-tight text-muted-foreground/80 leading-none truncate max-w-full">
                    {m.labelTop}
                  </span>
                  {m.labelBottom ? (
                    <span className="mt-0.5 text-[8px] font-bold uppercase tracking-tight text-muted-foreground/80 leading-tight truncate max-w-full">
                      {m.labelBottom}
                    </span>
                  ) : null}
                </dt>
                <dd className="mt-1.5 flex w-full items-baseline justify-center font-mono tabular-nums leading-none">
                  {typeof m.value === "number" ? (
                    <span className={cn("text-base font-bold sm:text-lg", m.textClass)}>
                      {m.value}
                    </span>
                  ) : (
                    <span
                      className={cn("text-xs font-semibold sm:text-[13px] truncate max-w-full", m.textClass)}
                      title={String(m.value)}
                    >
                      {m.value}
                    </span>
                  )}
                </dd>
              </div>
            ))}
          </dl>
        );
      })()}

      <div className="eo-xr-facts">
        <div>
          <span>Verification</span>
          <b>{verifiedLine}</b>
        </div>
        <div>
          <span>Rollback</span>
          <b>{result.recovery.label}</b>
        </div>
      </div>

      {(retry.length > 0 && onRetry) || (result.recovery.available && onRollback) ? (
        <div className="eo-xr-actions">
          {retry.length > 0 && onRetry && (
            <Btn size="sm" onClick={() => onRetry(retry.map((o) => o.id))}>
              <RefreshCw aria-hidden="true" className="h-3.5 w-3.5" />
              Retry {retry.length} failed
            </Btn>
          )}
          {result.recovery.available && onRollback && (
            <Btn size="sm" variant="danger" onClick={onRollback}>
              <RotateCcw aria-hidden="true" className="h-3.5 w-3.5" />
              Roll back
            </Btn>
          )}
        </div>
      ) : null}

      {visibleOps.length > 0 ? (
        <ul className="eo-xr-ops eo-scroll-fade" aria-label="Operation results">
          {[...visibleOps]
            .sort((a, b) => (OP_ORDER[a.status] ?? 0) - (OP_ORDER[b.status] ?? 0))
            .map((o) => {
              const isOpAudit = o.canonicalStatus === "READ_ONLY_AUDIT" || o.detail === "READ_ONLY_AUDIT";
              const isOpProtected = o.canonicalStatus === "PROTECTED" || o.detail === "PROTECTED";
              return (
                <li key={o.id} data-status={o.status}>
                  <details>
                    <summary>
                      <span className="eo-xr-op-title">{o.title}</span>
                      <StatusIndicator tone={isOpAudit ? "info" : isOpProtected ? "info" : OP_TONE[o.status]} size="sm">
                        {isOpAudit ? "AUDIT" : isOpProtected ? "PROTECTED" : OP_STATUS_LABEL[o.status]}
                      </StatusIndicator>
                      <ChevronDown aria-hidden="true" className="eo-xr-chev h-3.5 w-3.5" />
                    </summary>
                    <div className="eo-xr-op-body">
                      {o.reason && (
                        <p>
                          <span>{o.status === "failed" ? "Error" : isOpAudit ? "Diagnostic Note" : isOpProtected ? "Protection Policy" : o.status === "skipped" ? "Reason" : "Note"}</span>
                          {o.reason}
                        </p>
                      )}
                      {o.verification && (
                        <p>
                          <span>{isOpAudit ? "Current State" : isOpProtected ? "System State" : "Verification"}</span>
                          {o.verification}
                        </p>
                      )}
                      {o.status === "succeeded" && !o.verification && (
                        <p>
                          <span>Verification</span>Resulting state confirmed by the Windows service
                        </p>
                      )}
                      {o.detail && !isOpAudit && (
                        <p>
                          <span>Details</span>
                          {o.detail}
                        </p>
                      )}
                      {o.status === "failed" && (
                        <p>
                          <span>Retry</span>
                          {o.retryable ? "Can be retried" : "Not retryable for this operation"}
                        </p>
                      )}
                    </div>
                  </details>
                </li>
              );
            })}
        </ul>
      ) : allAlreadyOptimal ? (
        <div className="rounded-lg border border-border bg-window/60 p-4 text-center text-xs text-muted-foreground select-none">
          Everything is already optimized. No changes were necessary.
        </div>
      ) : null}
    </section>
  );
}

/** Root-mounted dialog for results reported with showExecutionResult. */
export function ExecutionResultDialog() {
  const shown = useShownResult();
  const status = shown ? runStatus(shown.result) : "SKIPPED";
  return (
    <Modal
      open={!!shown}
      onClose={closeExecutionResult}
      title={shown?.result.title ?? "Optimization result"}
      sub={
        shown?.result.simulated
          ? "Backend connection required to apply system changes."
          : "Result reported by the Windows service"
      }
      width="max-w-xl"
      tone={
        status === "SUCCESS" || status === "ALREADY_OPTIMAL"
          ? "success"
          : status === "FAILED"
            ? "danger"
            : status === "PARTIAL" || status === "UNVERIFIED" || status === "CANCELLED" || status === "DEFERRED"
              ? "warning"
              : "cyan"
      }
      footer={
        <Btn variant="primary" onClick={closeExecutionResult}>
          Close
        </Btn>
      }
    >
      {shown && <ExecutionResultPanel result={shown.result} onRetry={shown.onRetry} onRollback={shown.onRollback} />}
    </Modal>
  );
}
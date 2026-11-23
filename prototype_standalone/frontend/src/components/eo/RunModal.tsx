// Shared optimization execution workflow modal: Approve → Internal backup → Execute → Summary.
import { useEffect, useRef, useState } from "react";
import { CheckCircle2, ShieldCheck, Square, Zap, AlertTriangle } from "lucide-react";
import { Btn, Chips, Details, KV, Modal, PremiumCircularCheckbox, RiskBadge, Stepper, notifyBackendRequired } from "./kit";
import { hasNativeBackend } from "@/lib/eo/nativeBridge";
import { sendHostRequest, normalizeBackendExecutionResult } from "@/lib/eo/hostRpc";
import { ExecutionResultPanel } from "./ExecutionResult";
import { countResults, runStatus, type ExecutionResult } from "@/lib/eo/executionResult";
import type { Risk } from "@/lib/eo/data";
import { OpProgress } from "./OpProgress";
import { type SimulatedRunSummary } from "@/lib/eo/selection";
import { liveScanCoordinator } from "@/lib/eo/liveScanCoordinator";
import { cn } from "@/lib/utils";

const STAGES = ["ANALYZING", "PREPARING", "APPLYING", "VERIFYING", "FINALIZING"];
const FLOW = ["Approve", "Internal backup", "Execute", "Summary"];
type Phase = "approve" | "backup" | "running" | "done" | "cancelled";

export interface RunItem {
  id: string;
  title: string;
  risk: Risk;
  restart?: boolean | undefined;
}

export function RunModal({
  open,
  onClose,
  profile,
  count,
  items,
  onComplete,
}: {
  open: boolean;
  onClose: () => void;
  profile: string;
  count: number;
  items?: RunItem[] | undefined;
  onComplete?: ((summary: SimulatedRunSummary) => void) | undefined;
}) {
  const [phase, setPhase] = useState<Phase>("approve");
  const [p, setP] = useState(0);
  const [backupName, setBackupName] = useState(`Before ${profile} optimization`);
  const [backupCreated, setBackupCreated] = useState(true);
  const [tab, setTab] = useState("Kernel");
  const [summary, setSummary] = useState<SimulatedRunSummary | null>(null);
  const [result, setResult] = useState<ExecutionResult | null>(null);
  const [highRiskAck, setHighRiskAck] = useState(false);
  const started = useRef(0);
  const cancelRef = useRef(false);
  const completeRef = useRef(onComplete);
  completeRef.current = onComplete;
  const restartNoted = items?.filter((i) => Boolean(i && i.restart)).length ?? 0;
  const highRiskItems = items?.filter((i) => Boolean(i && i.risk === "HIGH")) ?? [];

  useEffect(() => {
    if (open) {
      cancelRef.current = false;
      setPhase("approve");
      setP(0);
      setSummary(null);
      setResult(null);
      setHighRiskAck(false);
      setBackupCreated(true);
      setBackupName(`Before ${profile} optimization`);
    }
  }, [open, profile]);

  const startExecution = async (withBackup: boolean) => {
    if (!hasNativeBackend()) {
      notifyBackendRequired(`${profile} optimization`);
      return;
    }
    cancelRef.current = false;
    setBackupCreated(withBackup);
    setPhase("running");
    setP(10);
    started.current = Date.now();
    const timer = window.setInterval(() => setP((v) => Math.min(90, v + 5)), 300);

    const reqIds = (items ?? []).filter((i): i is RunItem => Boolean(i && i.id)).map((i) => i.id);
    const reqTitles = (items ?? []).filter((i): i is RunItem => Boolean(i && i.id)).map((i) => ({ id: i.id, title: i.title || i.id }));
    try {
      if (withBackup) {
        try {
          const desc = backupName.trim() || `Before ${profile} optimization`;
          const created = (await sendHostRequest(
            "backup:create",
            {
              name: desc,
              notes: `Verified pre-optimization application backup before ${profile} execution`,
            },
            60000,
          )) as {
            transactionId?: string;
            TransactionId?: string;
            transaction?: { transactionId?: string; TransactionId?: string; status?: string; Status?: string };
            Transaction?: { transactionId?: string; TransactionId?: string; status?: string; Status?: string };
          };
          const tx = created?.transaction ?? created?.Transaction;
          const txId = String(
            tx?.transactionId ?? tx?.TransactionId ?? created?.transactionId ?? created?.TransactionId ?? "",
          ).trim();
          const txStatus = String(tx?.status ?? tx?.Status ?? "");
          if (!txId || txStatus.toUpperCase() === "BACKUP_WRITE_FAILED") {
            throw new Error("Application backup snapshot did not persist a verified transaction.");
          }
          const validation = (await sendHostRequest(
            "backup:validate",
            { transactionId: txId },
            20000,
          )) as { valid?: boolean; Valid?: boolean; reason?: string; Reason?: string };
          if (validation?.valid !== true && validation?.Valid !== true) {
            throw new Error(
              validation?.reason ?? validation?.Reason ?? "SHA-256 integrity validation failed for pre-optimization backup.",
            );
          }
        } catch (bkErr) {
          setBackupCreated(false);
          const msg = bkErr instanceof Error ? bkErr.message : String(bkErr);
          throw new Error(`Pre-optimization application backup failed: ${msg}`);
        }
        if (cancelRef.current) return;
      }

      let raw: unknown;
      if (profile === "1-Click") {
        raw = await sendHostRequest("oneclick:execute", { selectedIds: reqIds }, 120000);
      } else if (profile === "Input Latency") {
        const riskLevel = highRiskItems.length ? "High" : "Safe";
        raw = await sendHostRequest("input:apply", { riskLevel, selectedIds: reqIds }, 90000);
      } else if (profile === "Registry Intelligence") {
        raw = await sendHostRequest("registry:tweaks-apply", { ids: reqIds }, 90000);
      } else {
        raw = await sendHostRequest("tier:execute", { tierId: profile, selectedItemIds: reqIds }, 120000);
      }

      if (cancelRef.current) return;
      const elapsed = Date.now() - started.current;
      const exec = normalizeBackendExecutionResult(raw, profile, `${profile} Optimization`, reqTitles, elapsed);
      const counts = countResults(exec.operations);
      const realSummary: SimulatedRunSummary = {
        selected: count,
        simulatedSteps: exec.operations.length || count,
        restartNoted,
        realChanges: counts.newlyApplied,
        verification: counts.failed > 0
          ? "Completed with failures"
          : counts.newlyApplied === 0 && counts.alreadyOptimized > 0
            ? `All ${counts.alreadyOptimized} operations are already in their desired optimal state.`
            : counts.succeeded > 0
              ? (counts.alreadyOptimized > 0
                  ? `${counts.newlyApplied} applied, ${counts.alreadyOptimized} already optimal`
                  : `${counts.succeeded} of ${Math.max(1, counts.requested - counts.skipped)} verified`)
              : "No eligible optimizations executed",
        elapsedMs: elapsed,
      };
      setResult(exec);
      setSummary(realSummary);
      setP(100);
      setPhase("done");
      completeRef.current?.(realSummary);
      liveScanCoordinator.notifyOptimizationCompleted(profile);
      liveScanCoordinator.notifyOptimizationCompleted(`tier:${profile.toLowerCase()}`);
    } catch (err) {
      if (cancelRef.current) return;
      const msg = err instanceof Error ? err.message : String(err);
      const elapsed = Date.now() - started.current;
      const failExec: ExecutionResult = {
        module: profile,
        title: `${profile} Optimization`,
        simulated: false,
        error: msg,
        durationMs: elapsed,
        recovery: { available: false, label: "No changes committed" },
        operations: reqTitles.map((t) => ({
          id: t.id,
          title: t.title,
          status: "skipped",
          reason: `Execution aborted: ${msg}`,
        })),
      };
      const failSummary: SimulatedRunSummary = {
        selected: count,
        simulatedSteps: 0,
        restartNoted,
        realChanges: 0,
        verification: "Aborted before execution",
        elapsedMs: elapsed,
      };
      setResult(failExec);
      setSummary(failSummary);
      setP(100);
      setPhase("done");
      completeRef.current?.(failSummary);
    } finally {
      window.clearInterval(timer);
    }
  };

  const stageIdx = Math.min(4, Math.floor(p / 20));
  const flowIdx =
    phase === "approve"
      ? 0
      : phase === "backup"
        ? 1
        : phase === "running" || phase === "cancelled"
          ? 2
          : 4;

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={`${profile} — Optimization`}
      sub="Review and execute selected system optimizations."
      width="max-w-xl"
      icon={
        phase === "done" ? (
          <ShieldCheck className="h-4 w-4" />
        ) : phase === "cancelled" ? (
          <Square className="h-4 w-4" />
        ) : phase === "approve" || phase === "backup" ? (
          <ShieldCheck className="h-4 w-4" />
        ) : (
          <Zap className="h-4 w-4" />
        )
      }
      tone={
        phase === "done" && result
          ? (({
              SUCCESS: "success",
              PARTIAL: "warning",
              FAILED: "danger",
              UNVERIFIED: "warning",
              SKIPPED: "cyan",
              ALREADY_OPTIMAL: "success",
              NO_ACTIONABLE_OPTIMIZATIONS: "cyan",
              AUDIT_ONLY: "cyan",
              CANCELLED: "warning",
              DEFERRED: "warning",
            }) as const)[runStatus(result)]
          : phase === "cancelled"
            ? "warning"
            : "cyan"
      }
      footer={
        phase === "approve" ? (
          <>
            <Btn variant="ghost" onClick={onClose}>
              Cancel optimization
            </Btn>
            <Btn
              variant="primary"
              disabled={!count || (highRiskItems.length > 0 && !highRiskAck)}
              onClick={() => setPhase("backup")}
            >
              Approve & proceed
            </Btn>
          </>
        ) : phase === "backup" ? (
          <>
            <Btn variant="ghost" onClick={() => setPhase("approve")}>
              Back
            </Btn>
            <Btn
              variant="ghost"
              onClick={() => startExecution(false)}
            >
              Skip backup
            </Btn>
            <Btn
              variant="primary"
              disabled={!backupName.trim()}
              onClick={() => startExecution(true)}
            >
              Create backup &amp; continue
            </Btn>
          </>
        ) : phase === "running" ? (
          <Btn variant="danger" onClick={() => { cancelRef.current = true; setPhase("cancelled"); }}>
            Stop execution
          </Btn>
        ) : (
          <>
            <Btn variant="primary" onClick={onClose}>
              Close
            </Btn>
          </>
        )
      }
    >
      <div className="mb-4">
        <Stepper steps={FLOW} current={flowIdx} label="Optimization run progress" />
      </div>
      {phase === "approve" && (
        <div className="space-y-3 text-sm">
          <p className="text-muted-foreground">
            {count} selected optimization{count === 1 ? "" : "s"} will be queued for analysis,
            application, and verification through the ERROR OPTIMIZER background service.
          </p>
          <KV k="Profile" v={profile} />
          <KV k="Selected items" v={count} />
          {items && (
            <KV
              k="Restart noted in metadata"
              v={restartNoted ? `${restartNoted} item${restartNoted === 1 ? "" : "s"}` : "None"}
            />
          )}
          <KV k="Backup" v="Error Optimizer Application Vault snapshot" />
          <KV k="Rollback" v="Per-operation transaction rollback" />

          {/* High-Risk Acknowledgement Warning Banner */}
          {highRiskItems.length > 0 && (
            <div className="rounded-lg border border-warning/40 bg-warning/10 p-3 text-xs space-y-2">
              <div className="flex items-center gap-2 font-bold text-warning">
                <AlertTriangle className="h-4 w-4 shrink-0" />
                <span>HIGH-RISK OPTIMIZATIONS INCLUDED ({highRiskItems.length})</span>
              </div>
              <p className="text-muted-foreground leading-relaxed">
                The selected configuration includes advanced kernel optimizations (e.g. CPU priority separation, core unparking, memory paging executive, or driver power states). Explicit acknowledgement is required before proceeding.
              </p>
              <label className="flex items-center gap-2 pt-1 font-semibold text-foreground cursor-pointer select-none">
                <PremiumCircularCheckbox
                  on={highRiskAck}
                  onChange={setHighRiskAck}
                  label="I understand and acknowledge the impact of these high-risk optimizations"
                />
                <span>I understand and acknowledge the impact of these high-risk optimizations</span>
              </label>
            </div>
          )}

          {/* Detailed Selected Items List with Risk Badges */}
          {items && items.length > 0 && (
            <div className="space-y-1.5 pt-1">
              <div className="text-[10.5px] font-bold text-muted-foreground uppercase tracking-wider">
                Selected Operations ({items.length})
              </div>
              <div className="max-h-36 overflow-y-auto space-y-1 rounded-lg border border-border bg-window/50 p-2 text-xs">
                {items.filter((it): it is RunItem => Boolean(it && it.id)).map((it) => (
                  <div
                    key={it.id}
                    className="flex items-center justify-between gap-2 rounded bg-window px-2.5 py-1.5"
                  >
                    <span className="truncate font-medium text-foreground">{it?.title || it?.id}</span>
                    <RiskBadge risk={it?.risk || "LOW"} />
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
      {phase === "backup" && (
        <div className="space-y-4 text-sm">
          <div className="rounded-lg border border-border bg-window p-4 space-y-3">
            <div className="flex items-start gap-3">
              <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg border border-cyan/40 bg-cyan/10 text-cyan">
                <ShieldCheck className="h-5 w-5" />
              </div>
              <div className="min-w-0 flex-1">
                <div className="font-semibold text-foreground">Application Configuration Backup</div>
                <p className="text-xs text-muted-foreground mt-0.5">
                  Error Optimizer creates an application-managed configuration snapshot before applying changes. Targeted registry keys, services, and system configuration are snapshotted to Error Optimizer's secure local vault.
                </p>
              </div>
            </div>

            <div className="pt-2">
              <label htmlFor="eo-backup-name" className="block text-xs font-semibold text-foreground mb-1.5">
                BACKUP SNAPSHOT NAME
              </label>
              <input
                id="eo-backup-name"
                type="text"
                value={backupName}
                onChange={(e) => setBackupName(e.target.value)}
                placeholder="Enter backup snapshot name…"
                className="w-full rounded-md border border-border bg-background px-3 py-2 text-xs font-mono text-foreground focus:border-cyan focus:outline-none"
              />
            </div>

            <div className="space-y-1.5 border-t border-border/60 pt-3 text-xs">
              <KV k="Storage vault" v="Error Optimizer Application Vault" />
              <KV k="Scope" v={`${count} item${count === 1 ? "" : "s"} (${profile})`} />
              <KV k="Rollback method" v="Full per-operation restore via Backup & Restore" />
              <KV k="System Protection" v="Not required (independent internal snapshot)" />
            </div>
          </div>

          <div className="flex items-center gap-2 rounded-md border border-success/30 bg-success/10 px-3 py-2 text-xs text-success">
            <CheckCircle2 className="h-4 w-4 shrink-0" />
            <span>Error Optimizer internal backup protects all modified settings independently of Windows System Restore.</span>
          </div>
        </div>
      )}
      {(phase === "running" || phase === "cancelled") && (
        <div className="space-y-4">
          <OpProgress
            value={p}
            title={phase === "running" ? `${STAGES[stageIdx]}…` : "Execution stopped"}
            detail={
              phase === "running"
                ? `Stage ${stageIdx + 1} of ${STAGES.length} · ${Math.round((p / 100) * count)} of ${count} items processed`
                : `${Math.round((p / 100) * count)} of ${count} items reached`
            }
            active={phase === "running"}
          />
          <ol aria-label="Run stages" className="grid grid-cols-5 gap-1.5">
            {STAGES.map((s, i) => (
              <li
                key={s}
                aria-current={i === stageIdx && phase === "running" ? "step" : undefined}
                className={cn(
                  "rounded-md border py-1.5 text-center text-[10px] font-bold",
                  i < stageIdx
                    ? "border-success/40 text-success"
                    : i === stageIdx && phase === "running"
                      ? "border-cyan/60 bg-cyan/10 text-cyan"
                      : "border-border text-subtle",
                )}
              >
                {s}
              </li>
            ))}
          </ol>
          <p role="status" aria-live="polite" className="sr-only">
            {phase === "running"
              ? `${STAGES[stageIdx]} ${Math.round(p / 10) * 10}%`
              : "Execution stopped"}
          </p>
          {phase === "cancelled" && (
            <div
              role="alert"
              className="rounded-lg border border-warning/40 bg-warning/10 p-3 text-xs text-warning"
            >
              Execution stopped at {p.toFixed(0)}%. Completed steps can be restored individually.
            </div>
          )}
        </div>
      )}
      {phase === "done" && summary && result && (
        <div className="space-y-4">
          <ExecutionResultPanel result={result} />
          <KV
            k="Internal backup"
            v={backupCreated ? `“${backupName}”` : "Skipped"}
          />
          <KV k="Restart noted in metadata" v={items ? summary.restartNoted : "—"} />
          <Details inline title="ADVANCED DETAILS" hint="4 subsystems">
            <Chips
              label="Advanced details subsystem"
              options={["Kernel", "Network", "Interrupts", "Protected"]}
              value={tab}
              onChange={setTab}
            />
            <div className="mt-2 rounded-lg border border-border bg-window p-3 text-xs text-muted-foreground">
              Subsystem execution results for {tab.toLowerCase()} appear here after a completed run.
            </div>
          </Details>
        </div>
      )}
    </Modal>
  );
}

export { OpProgress };

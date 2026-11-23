// Shared restore point workflow (Backup & Restore, Dashboard Quick Optimize, run dialogs).
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import {
  CheckCircle2,
  HardDrive,
  Loader2,
  ShieldAlert,
  ShieldCheck,
  ShieldX,
  type LucideIcon,
} from "lucide-react";
import { Badge, Btn, KV, Modal, notifyBackendRequired } from "./kit";
import { hasNativeBackend, isDesktopApp, useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { toast } from "sonner";
import { OpProgress } from "./OpProgress";
import { setRestoreState } from "./restorePointStore";
import {
  MANUAL_ENABLE_STEPS,
  SYSTEM_DRIVE,
  canCancel,
  restoreProgress,
  restoreStep,
  type RestoreEvent,
  type RestoreStage,
} from "@/lib/eo/restorePoint";

type Tone = "cyan" | "success" | "warning" | "danger";

export function stageMeta(stage: RestoreStage): {
  title: string;
  sub: string;
  tone: Tone;
  Icon: LucideIcon;
} {
  switch (stage) {
    case "protection-required":
      return {
        title: "System Protection Is Disabled",
        sub: "Required before a restore point can be created",
        tone: "warning",
        Icon: ShieldAlert,
      };
    case "confirm-enable":
      return {
        title: "Enable System Protection?",
        sub: `Applies to ${SYSTEM_DRIVE.label} only`,
        tone: "warning",
        Icon: ShieldAlert,
      };
    case "enabling":
    case "verifying-protection":
      return {
        title: "Enabling System Protection",
        sub: "Configuring Windows System Protection",
        tone: "cyan",
        Icon: Loader2,
      };
    case "checking":
      return {
        title: "Checking System Protection",
        sub: "Verifying drive protection status",
        tone: "cyan",
        Icon: Loader2,
      };
    case "creating":
    case "verifying-point":
      return {
        title: "Creating Restore Point",
        sub: "Writing and verifying Windows restore point",
        tone: "cyan",
        Icon: Loader2,
      };
    case "success":
      return {
        title: "Restore Point Created",
        sub: "Verified in Windows System Protection",
        tone: "success",
        Icon: CheckCircle2,
      };
    case "enable-failed":
      return {
        title: "System Protection Couldn't Be Enabled",
        sub: "No restore point was created",
        tone: "danger",
        Icon: ShieldX,
      };
    case "create-failed":
      return {
        title: "Restore Point Not Created",
        sub: "Creation could not be verified",
        tone: "danger",
        Icon: ShieldX,
      };
    default:
      return {
        title: "Create Restore Point",
        sub: "Checks System Protection on the system drive first",
        tone: "cyan",
        Icon: ShieldCheck,
      };
  }
}

export interface RestoreFlow {
  stage: RestoreStage;
  enableStep: 0 | 1;
  result: { name: string; at: string } | null;
  name: string;
  setName: (v: string) => void;
  driveLabel?: string;
  send: (e: RestoreEvent) => void;
  start: () => void;
  /** Back to the name step; optionally with a new default name. */
  reset: (name?: string) => void;
}

export function useRestorePointFlow(
  defaultName: string,
  origin: "dashboard" | "restricted" = "restricted",
): RestoreFlow {
  const connected = useIsBackendConnected();
  const [stage, setStage] = useState<RestoreStage>("idle");
  const [enableStep, setEnableStep] = useState<0 | 1>(0);
  const [result, setResult] = useState<RestoreFlow["result"]>(null);
  const [name, setName] = useState(defaultName);
  const [driveLabel, setDriveLabel] = useState<string>(SYSTEM_DRIVE.label);
  const nameRef = useRef(name);
  nameRef.current = name;
  const createdSeqRef = useRef<number | null>(null);
  const flowStartMsRef = useRef<number>(0);
  const send = useCallback((e: RestoreEvent) => setStage((s) => restoreStep(s, e)), []);

  useEffect(() => {
    if (!connected && !hasNativeBackend()) return;
    let cancelled = false;

    const runStage = async () => {
      if (stage === "checking") {
        try {
          const status = (await sendHostRequest("restorepoint:status", undefined, 15000)) as {
            isAvailable?: boolean;
            isEnabled?: boolean;
            enabled?: boolean;
            protectionEnabled?: boolean;
            drive?: string;
            reason?: string;
          };
          if (cancelled) return;
          if (typeof status?.drive === "string" && status.drive.trim()) {
            const drv = status.drive.trim().replace(/\\+$/, "");
            setDriveLabel(`Local Disk (${drv})`);
          }
          const isOn = Boolean(status?.isEnabled ?? status?.enabled ?? status?.protectionEnabled ?? false);
          send({ type: "checked", protection: isOn ? "on" : "off" });
        } catch (err) {
          if (!cancelled) {
            toast.error("Could not query Windows System Protection", {
              description: err instanceof Error ? err.message : String(err),
            });
            send({ type: "cancel" });
          }
        }
      } else if (stage === "enabling") {
        try {
          setEnableStep(1);
          await sendHostRequest("restorepoint:enable", undefined, 60000);
          if (!cancelled) send({ type: "enable-finished" });
        } catch (err) {
          if (!cancelled) {
            toast.error("Failed to enable System Protection", {
              description: err instanceof Error ? err.message : String(err),
            });
            send({ type: "protection-verified", on: false });
          }
        }
      } else if (stage === "verifying-protection") {
        try {
          const status = (await sendHostRequest("restorepoint:status", undefined, 15000)) as {
            isEnabled?: boolean;
            enabled?: boolean;
            protectionEnabled?: boolean;
          };
          if (cancelled) return;
          const on = Boolean(status?.isEnabled ?? status?.enabled ?? status?.protectionEnabled);
          setRestoreState({ scenario: on ? "enabled" : "disabled" });
          send({ type: "protection-verified", on });
        } catch {
          if (!cancelled) send({ type: "protection-verified", on: false });
        }
      } else if (stage === "creating") {
        try {
          if (origin !== "dashboard") {
            throw new Error("Windows System Restore Point creation is restricted to the V3 Dashboard CREATE RESTORE POINT action.");
          }
          if (!flowStartMsRef.current) flowStartMsRef.current = Date.now();
          createdSeqRef.current = null;
          const res = (await sendHostRequest(
            "restorepoint:create",
            {
              description: nameRef.current.trim() || "ErrorOptimizer Restore Point",
              origin: "dashboard",
            },
            110000,
          )) as {
            success?: boolean;
            Success?: boolean;
            status?: string | number;
            Status?: string | number;
            sequenceNumber?: number;
            SequenceNumber?: number;
            errorMessage?: string;
            statusMessage?: string;
          };
          if (cancelled) return;
          const statusVal = res?.status ?? res?.Status;
          const skippedByPolicy =
            statusVal === 1 ||
            String(statusVal ?? "").toLowerCase() === "skippedbypolicy";
          const rawSeq = res?.sequenceNumber ?? res?.SequenceNumber;
          const seqNum = typeof rawSeq === "number" ? rawSeq : Number(rawSeq ?? 0);
          if (res?.success === false || res?.Success === false || skippedByPolicy || !(seqNum > 0)) {
            const msg = res?.errorMessage ?? res?.statusMessage ?? (skippedByPolicy
              ? "Skipped by Windows creation frequency policy."
              : "Restore point creation failed or returned no sequence number.");
            throw new Error(skippedByPolicy && !msg.toLowerCase().includes("policy") ? `SKIPPED BY POLICY: ${msg}` : msg);
          }
          createdSeqRef.current = seqNum;
          send({ type: "created" });
        } catch (err) {
          if (!cancelled) {
            const message = err instanceof Error ? err.message : String(err);
            const isPolicySkip =
              message.toLowerCase().includes("policy") ||
              message.toLowerCase().includes("frequency");
            toast.error(
              isPolicySkip ? "Restore point skipped by Windows policy" : "Restore point creation failed",
              { description: message },
            );
            send({ type: "point-verified", ok: false });
          }
        }
      } else if (stage === "verifying-point") {
        try {
          const list = await sendHostRequest<Array<Record<string, unknown>>>("restorepoint:list", undefined, 20000);
          if (cancelled) return;
          const targetName = nameRef.current.trim().toLowerCase();
          const expectedSeq = createdSeqRef.current;
          const minCreatedMs = flowStartMsRef.current > 0 ? flowStartMsRef.current - 15_000 : Date.now() - 120_000;
          const matched = Array.isArray(list)
            ? list.find((p) => {
                const seq = Number(p["sequenceNumber"] ?? p["SequenceNumber"] ?? 0);
                if (expectedSeq && expectedSeq > 0) {
                  return seq === expectedSeq;
                }
                const desc = String(p["description"] ?? p["Description"] ?? "").trim().toLowerCase();
                if (!targetName || desc !== targetName) return false;
                const rawUtc = p["creationTimeUtc"] ?? p["CreationTimeUtc"];
                const parsedMs = typeof rawUtc === "string" && rawUtc ? Date.parse(rawUtc) : NaN;
                return Number.isFinite(parsedMs) && parsedMs >= minCreatedMs;
              }) ?? null
            : null;
          if (!matched) {
            send({ type: "point-verified", ok: false });
            return;
          }
          const rawUtc = matched["creationTimeUtc"] ?? matched["CreationTimeUtc"];
          const formattedAt = typeof rawUtc === "string" && rawUtc
            ? new Date(rawUtc).toLocaleString()
            : String(matched["creationTime"] ?? matched["CreationTime"] ?? matched["creationTimeString"] ?? matched["CreationTimeString"] ?? new Date().toLocaleString());
          const point = {
            name: String(matched["description"] ?? matched["Description"] ?? nameRef.current.trim()),
            at: formattedAt,
          };
          setResult(point);
          setRestoreState({ last: point });
          send({ type: "point-verified", ok: true });
        } catch {
          if (!cancelled) send({ type: "point-verified", ok: false });
        }
      }
    };

    void runStage();
    return () => {
      cancelled = true;
    };
  }, [stage, send, connected]);

  return {
    stage,
    enableStep,
    result,
    name,
    setName,
    driveLabel,
    send,
    start: () => {
      if (origin !== "dashboard") {
        toast.error("Windows System Restore is restricted", {
          description: "Only the V3 Dashboard CREATE RESTORE POINT action may initiate Windows System Restore point creation.",
        });
        return;
      }
      if (!hasNativeBackend() && !isDesktopApp()) {
        notifyBackendRequired("System restore point");
        return;
      }
      flowStartMsRef.current = Date.now();
      createdSeqRef.current = null;
      setResult(null);
      send({ type: "start" });
    },
    reset: (next) => {
      flowStartMsRef.current = 0;
      createdSeqRef.current = null;
      setStage("idle");
      setResult(null);
      setName(next ?? defaultName);
    },
  };
}

function DriveRow({ on, driveLabel }: { on: boolean; driveLabel?: string }) {
  return (
    <div className="eo-rp-drive">
      <span className="eo-row-icon" aria-hidden="true">
        <HardDrive className="h-3.5 w-3.5" />
      </span>
      <span className="min-w-0 flex-1">
        <span className="block text-[12.5px] font-semibold">{driveLabel || SYSTEM_DRIVE.label}</span>
        <span className="block text-[11px] text-muted-foreground">
          {SYSTEM_DRIVE.role}
        </span>
      </span>
      <Badge tone={on ? "success" : "warning"}>{on ? "PROTECTION ON" : "PROTECTION OFF"}</Badge>
    </div>
  );
}

/** Stage body. `heading` repeats the stage title inside the body (used inside other dialogs). */
export function RestorePointBody({
  flow,
  heading,
}: {
  flow: RestoreFlow & { driveLabel?: string };
  heading?: boolean | undefined;
}) {
  const { stage } = flow;
  const meta = stageMeta(stage);
  const value = restoreProgress(stage);
  const name = flow.name.trim() || "Restore point";
  const dl = flow.driveLabel ?? SYSTEM_DRIVE.label;
  return (
    <div className="space-y-3" role="status" aria-live="polite">
      {heading && stage !== "idle" && (
        <h3 className="flex items-center gap-2 text-[13.5px] font-bold" data-tone={meta.tone}>
          <meta.Icon
            aria-hidden="true"
            className={meta.Icon === Loader2 ? "eo-spin h-4 w-4" : "h-4 w-4"}
          />
          {meta.title}
        </h3>
      )}
      {stage === "checking" && (
        <OpProgress
          value={value}
          title="Checking System Protection…"
          detail={`${dl} · reading protection status`}
          active
        />
      )}
      {stage === "protection-required" && (
        <>
          <p className="text-[12.5px] text-muted-foreground">
            Windows can only create a restore point on a drive with System Protection turned on. It
            is off for your system drive, so a restore point can't be created yet.
          </p>
          <DriveRow on={false} driveLabel={dl} />
          <p className="text-[11px] text-subtle">
            Nothing changes until you choose Enable System Protection and confirm. Other drives are
            never touched.
          </p>
        </>
      )}
      {stage === "confirm-enable" && (
        <>
          <ul className="eo-rp-list" aria-label="What will happen">
            <li>
              Turns on System Protection for <b>{dl}</b> only — other drives stay as
              they are.
            </li>
            <li>Windows asks for administrator permission first.</li>
            <li>
              Windows reserves part of {dl.replace(/^Local Disk \((.+)\)$/, "$1")} for restore points; you can change this
              later in System Properties.
            </li>
            <li>Existing restore points are kept. Protection is never turned off by the app.</li>
            <li>The setting is read back before the restore point is created.</li>
          </ul>
          <DriveRow on={false} driveLabel={dl} />
        </>
      )}
      {(stage === "enabling" || stage === "verifying-protection") && (
        <OpProgress
          value={value}
          title={
            stage === "verifying-protection"
              ? "Verifying protection status…"
              : flow.enableStep === 0
                ? "Requesting administrator permission…"
                : `Turning on protection for ${dl.replace(/^Local Disk \((.+)\)$/, "$1")}…`
          }
          detail={
            stage === "verifying-protection"
              ? `Reading the setting back for ${dl.replace(/^Local Disk \((.+)\)$/, "$1")}`
              : "The native app shows the Windows permission prompt here"
          }
          active
        />
      )}
      {(stage === "creating" || stage === "verifying-point") && (
        <OpProgress
          value={value}
          title={stage === "creating" ? `Creating “${name}”…` : "Verifying restore point…"}
          detail={
            stage === "creating"
              ? `System Protection verified on ${dl.replace(/^Local Disk \((.+)\)$/, "$1")}`
              : "Confirming it appears in the restore point list"
          }
          active
        />
      )}
      {stage === "success" && flow.result && (
        <>
          <div className="eo-rp-alert" data-tone="success">
            <CheckCircle2 aria-hidden="true" className="h-4 w-4 shrink-0" />
            <span>
              Restore point created and verified in Windows System Protection.
            </span>
          </div>
          <div>
            <KV k="Name" v={`“${flow.result.name}”`} />
            <KV k="Drive" v={`${SYSTEM_DRIVE.label} · protection on`} />
            <KV k="Created" v={flow.result.at} />
            <KV k="Verification" v="Verified in System Protection" />
          </div>
        </>
      )}
      {stage === "enable-failed" && (
        <>
          <div className="eo-rp-alert" data-tone="danger">
            <ShieldX aria-hidden="true" className="h-4 w-4 shrink-0" />
            <span>
              Protection is still off for {SYSTEM_DRIVE.letter} after the attempt, so no restore
              point was created.
            </span>
          </div>
          <div>
            <div className="eo-label mb-1.5 text-[10px] text-foreground">TURN IT ON MANUALLY</div>
            <ol className="eo-rp-steps">
              {MANUAL_ENABLE_STEPS.map((s) => (
                <li key={s}>{s}</li>
              ))}
            </ol>
          </div>
        </>
      )}
      {stage === "create-failed" && (
        <div className="eo-rp-alert" data-tone="danger">
          <ShieldX aria-hidden="true" className="h-4 w-4 shrink-0" />
          <span>The restore point could not be verified, so it is not reported as created.</span>
        </div>
      )}
    </div>
  );
}

/** Footer buttons for the current stage. */
export function RestorePointActions({
  flow,
  onCancel,
  onDone,
  doneLabel = "Done",
  extra,
}: {
  flow: RestoreFlow;
  onCancel: () => void;
  onDone: () => void;
  doneLabel?: string | undefined;
  /** Extra buttons shown next to Cancel on failure stages. */
  extra?: ReactNode | undefined;
}) {
  const { stage, send } = flow;
  const cancel = () => {
    send({ type: "cancel" });
    onCancel();
  };
  if (stage === "protection-required")
    return (
      <>
        <Btn variant="ghost" onClick={cancel}>
          Cancel
        </Btn>
        <Btn variant="primary" onClick={() => send({ type: "request-enable" })}>
          <ShieldCheck aria-hidden="true" className="h-3.5 w-3.5" />
          Enable System Protection
        </Btn>
      </>
    );
  if (stage === "confirm-enable")
    return (
      <>
        <Btn variant="ghost" onClick={() => send({ type: "back" })}>
          Back
        </Btn>
        <Btn variant="primary" onClick={() => send({ type: "confirm-enable" })}>
          Confirm & enable
        </Btn>
      </>
    );
  if (stage === "success")
    return (
      <Btn variant="primary" onClick={onDone}>
        {doneLabel}
      </Btn>
    );
  if (stage === "enable-failed" || stage === "create-failed")
    return (
      <>
        <Btn variant="ghost" onClick={cancel}>
          Cancel
        </Btn>
        {extra}
        <Btn variant="primary" onClick={() => send({ type: "retry" })}>
          Try again
        </Btn>
      </>
    );
  return (
    <Btn variant="ghost" disabled={!canCancel(stage)} onClick={cancel}>
      Cancel
    </Btn>
  );
}

/** Stand-alone dialog: name → workflow → result. */
export function RestorePointDialog({
  open,
  onClose,
  defaultName = "Before optimization",
  origin = "restricted",
}: {
  open: boolean;
  onClose: () => void;
  defaultName?: string | undefined;
  origin?: "dashboard" | "restricted" | undefined;
}) {
  const flow = useRestorePointFlow(defaultName, origin);
  const { reset } = flow;
  useEffect(() => {
    if (open) reset();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  const meta = stageMeta(flow.stage);
  const busy = !canCancel(flow.stage) && flow.stage !== "success";
  const close = () => {
    if (busy) return;
    flow.send({ type: "cancel" });
    onClose();
  };
  return (
    <Modal
      open={open}
      onClose={close}
      title={meta.title}
      sub={meta.sub}
      width="max-w-md"
      tone={meta.tone}
      icon={
        <meta.Icon
          className={meta.Icon === Loader2 ? "eo-spin h-4 w-4" : "h-4 w-4"}
          aria-hidden="true"
        />
      }
      footer={
        flow.stage === "idle" ? (
          <>
            <Btn variant="ghost" onClick={close}>
              Cancel
            </Btn>
            <Btn variant="primary" disabled={!flow.name.trim()} onClick={flow.start}>
              Create restore point
            </Btn>
          </>
        ) : (
          <RestorePointActions flow={flow} onCancel={onClose} onDone={onClose} />
        )
      }
    >
      {flow.stage === "idle" ? <RestoreNameField flow={flow} /> : <RestorePointBody flow={flow} />}
    </Modal>
  );
}

export function RestoreNameField({ flow }: { flow: RestoreFlow }) {
  return (
    <div className="space-y-2">
      <label htmlFor="eo-rp-name" className="eo-label block text-foreground">
        Restore point name
      </label>
      <input
        id="eo-rp-name"
        value={flow.name}
        onChange={(e) => flow.setName(e.target.value)}
        className="h-9 w-full rounded-lg border border-border bg-window px-3 text-sm outline-none focus:border-cyan/60"
      />
      <p className="text-[11px] text-muted-foreground">
        System Protection on {SYSTEM_DRIVE.label} is verified before creating a restore point.
      </p>
    </div>
  );
}

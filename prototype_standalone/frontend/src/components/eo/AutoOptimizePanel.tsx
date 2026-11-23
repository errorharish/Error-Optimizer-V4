// Dashboard Auto Optimize panel — control centre for the recurring maintenance engine.
import { Link } from "@tanstack/react-router";
import { CalendarClock, History, Play, Power, Settings2, Square } from "lucide-react";
import type { CSSProperties } from "react";
import { useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { Btn, Card, Label, btnProps } from "./kit";
import { GuideTip } from "./Guide";
import { StatusIndicator, type StatusTone } from "./StatusIndicator";
import { autoOptimize, useAutoOptimize, useNow, type AutoState } from "./autoOptimizeStore";
import { MAINT_TASKS, countdownParts, formatInterval } from "@/lib/eo/autoOptimize";

export const clockTime = (t: number | null) =>
  t
    ? new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false })
    : "—";

export function relTime(t: number | null, now: number) {
  if (!t) return "Never";
  if (!now) return clockTime(t);
  const s = Math.max(0, Math.round((now - t) / 1000));
  if (s < 45) return "Just now";
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  return h < 24 ? `${h} h ago` : new Date(t).toLocaleDateString();
}

export type EngineStatus = "Running" | "Enabled" | "Paused" | "Disabled" | "Suspended";
export function engineStatus(s: AutoState): EngineStatus {
  if (s.run) return "Running";
  if (!s.config.enabled) return "Disabled";
  if (s.config.paused) return "Paused";
  if (s.power === "app-closed" || s.power === "pc-off") return "Suspended";
  return "Enabled";
}
/** Running / Enabled = green, Paused / Suspended = amber (pending), Disabled = red. */
export const STATUS_TONE: Record<EngineStatus, StatusTone> = {
  Running: "pos",
  Enabled: "pos",
  Paused: "warn",
  Disabled: "neg",
  Suspended: "warn",
};

export function StatusPill({ status }: { status: EngineStatus }) {
  return (
    <StatusIndicator tone={STATUS_TONE[status]} live reserve="SUSPENDED">
      {status}
    </StatusIndicator>
  );
}

/** Auto Optimize is ONLINE only while its schedule is actually active (enabled and not paused). */
export const autoOnline = (s: AutoState) => s.config.enabled && !s.config.paused;

/** Two-state status on the shared status display: never reports ONLINE without a backend. */
export function AutoStatusBadge({ s }: { s: AutoState }) {
  const online = autoOnline(s);
  const connected = useIsBackendConnected();
  const label = !online
    ? "AUTO OPTIMIZE OFFLINE"
    : connected
      ? "AUTO OPTIMIZE ONLINE"
      : "AWAITING BACKEND";
  return (
    <StatusIndicator
      tone={!online ? "neg" : connected ? "pos" : "warn"}
      live
      reserve="AUTO OPTIMIZE OFFLINE"
      tipAlign="start"
      tip={
        <>
          <p className="eo-svc-tip-title">Auto Optimize</p>
          <dl>
            <div>
              <dt>Source</dt>
              <dd>{connected ? "Windows background service" : "Not connected"}</dd>
            </div>
            <div>
              <dt>Schedule</dt>
              <dd>
                {online
                  ? connected
                    ? `Active · next at ${clockTime(s.nextAt)}`
                    : "Armed · waiting for backend service"
                  : s.config.paused
                    ? "Paused"
                    : "Disabled"}
              </dd>
            </div>
          </dl>
          {!connected && (
            <p className="eo-svc-tip-note">
              Waiting for ERROR OPTIMIZER background service connection.
            </p>
          )}
        </>
      }
    >
      {label}
    </StatusIndicator>
  );
}

const UNITS = [
  ["days", "DAYS"],
  ["hours", "HOURS"],
  ["minutes", "MINUTES"],
  ["seconds", "SECONDS"],
] as const;

/** Compact primary action to the existing Maintenance History (Auto Optimize page, #history). */
function HistoryLink() {
  return (
    <Link
      to="/$page"
      params={{ page: "auto-optimize" }}
      hash="history"
      {...btnProps("primary", "sm")}
      className="eo-btn eo-auto-hist shrink-0"
    >
      <History aria-hidden="true" className="h-3.5 w-3.5" /> View full history
    </Link>
  );
}

/** Countdown block — the only part of the panel that re-renders every second. Shows the real
 * time left until the saved schedule's next run as DD : HH : MM : SS; the scheduler in
 * autoOptimizeStore starts the run at zero and sets the next time from the saved interval. */
function Countdown({ s }: { s: AutoState }) {
  const now = useNow();
  const status = engineStatus(s);
  const total = s.config.intervalMin * 60_000;
  const remaining =
    s.nextAt !== null && now ? Math.max(0, s.nextAt - now) : (s.pausedRemaining ?? null);
  const frac = remaining === null ? 0 : 1 - Math.min(1, remaining / total);
  const scheduled = status !== "Disabled" && status !== "Suspended" && remaining !== null;
  const parts = scheduled ? countdownParts(remaining) : null;
  return (
    <div className="eo-countdown" data-state={status}>
      <div className="flex h-[15px] items-center justify-between gap-2">
        <Label className="text-[9.5px]">
          {status === "Running"
            ? "MAINTENANCE IN PROGRESS"
            : status === "Paused"
              ? "PAUSED · TIME LEFT"
              : status === "Suspended"
                ? "SUSPENDED"
                : "NEXT MAINTENANCE IN"}
        </Label>
        {s.nextAt !== null && status === "Enabled" && (
          <span className="shrink-0 text-[10.5px] font-semibold text-muted-foreground">
            at {clockTime(s.nextAt)}
          </span>
        )}
      </div>
      {parts ? (
        <div
          className="eo-cd"
          role="timer"
          aria-live="off"
          aria-label={`${parts.days} days ${parts.hours} hours ${parts.minutes} minutes ${parts.seconds} seconds`}
        >
          {UNITS.map(([key, label], i) => (
            <div key={key} className="eo-cd-unit">
              {i > 0 && (
                <span className="eo-cd-sep" aria-hidden="true">
                  :
                </span>
              )}
              <span className="eo-cd-num" aria-hidden="true">
                {parts[key]}
              </span>
              <span className="eo-cd-label" aria-hidden="true">
                {label}
              </span>
            </div>
          ))}
        </div>
      ) : (
        <div className="eo-cd-none">NOT SCHEDULED</div>
      )}
      <div
        className="eo-interval-track mt-2"
        role="progressbar"
        aria-label="Time elapsed in the current interval"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(frac * 100)}
      >
        <span style={{ "--v": frac } as CSSProperties} />
      </div>
      <div className="eo-countdown-foot">
        <span className="min-w-0 truncate text-[11px] font-semibold">
          {formatInterval(s.config.intervalMin)}
        </span>
        <HistoryLink />
      </div>
    </div>
  );
}

function RunProgress({ s }: { s: AutoState }) {
  const run = s.run;
  if (!run) return null;
  const done = run.items.filter((i) => i.state !== "Pending" && i.state !== "Running").length;
  return (
    <div className="eo-swap">
      <div className="mb-1.5 flex items-center justify-between text-[10.5px] font-bold">
        <span className="text-cyan">
          {run.trigger.toUpperCase()} RUN · {done}/{run.items.length}
        </span>
        <span className="flex shrink-0 items-center gap-2">
          <HistoryLink />
          <Btn size="sm" variant="ghost" onClick={autoOptimize.cancel}>
            <Square aria-hidden="true" className="h-3 w-3" /> Cancel
          </Btn>
        </span>
      </div>
      <ul className="eo-run-list space-y-1" aria-live="polite">
        {run.items.map((i) => (
          <li key={i.taskId} className="eo-run-row" data-state={i.state}>
            <span aria-hidden="true" className="eo-run-dot" />
            <span className="min-w-0 flex-1 truncate">{i.name}</span>
            <span className="eo-run-state">{i.state === "Running" ? "Working…" : i.state}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function AutoOptimizePanel() {
  const s = useAutoOptimize();
  const now = useNow();
  const c = s.config;
  const noTasks = !c.tasks.length;

  return (
    <Card className="eo-hero eo-auto eo-auto-dash flex flex-col">
      {/* Header: mark | title + Configure, with Online / Offline under both (no separate row). */}
      <div className="eo-card-head">
        <span className="eo-auto-mark" aria-hidden="true">
          <CalendarClock className="h-4 w-4" />
        </span>
        <div className="flex min-w-0 items-center gap-2">
          <h2
            className="eo-heading truncate"
            title="Recurring maintenance engine · runs the tasks you choose on your schedule."
          >
            Auto Optimize
          </h2>
          <GuideTip id="dashboard.auto" />
        </div>
        <Link to="/$page" params={{ page: "auto-optimize" }} className="eo-card-head-action">
          <Btn size="sm" label="Configure Auto Optimize" title="Configure Auto Optimize">
            <Settings2 aria-hidden="true" className="h-3.5 w-3.5" />
            <span className="eo-auto-cfg-label">Configure</span>
          </Btn>
        </Link>
        <div className="eo-auto-status eo-card-head-status">
          <AutoStatusBadge s={s} />
        </div>
      </div>

      {/* Schedule (countdown or live run) above the facts — one balanced column. */}
      <div className="eo-auto-body-dash flex flex-col gap-2.5">
        {s.run ? <RunProgress s={s} /> : <Countdown s={s} />}
        <dl className="eo-auto-facts">
          {(
            [
              ["Last run", relTime(s.lastRunAt, now)],
              ["Tasks", `${c.tasks.length} of ${MAINT_TASKS.length} selected`],
              ["Profile", c.profile],
              [
                "Lifecycle",
                [c.runOnLaunch && "On launch", c.runOnWindowsStartup && "Windows startup"]
                  .filter(Boolean)
                  .join(" · ") || "Schedule only",
              ],
            ] as const
          ).map(([k, v]) => (
            <div key={k}>
              <dt>{k}</dt>
              <dd title={v}>{v}</dd>
            </div>
          ))}
        </dl>
      </div>

      <div
        className="eo-auto-foot"
        title="Schedule managed by the ERROR OPTIMIZER background service"
      >
        {noTasks && (
          <span className="eo-auto-note text-[10.5px] text-subtle">
            Select at least one maintenance task to enable Auto Optimize.
          </span>
        )}
        {autoOnline(s) ? (
          <Btn variant="primary" size="lg" onClick={autoOptimize.disable}>
            <Power aria-hidden="true" className="h-4 w-4" /> Disable Auto Optimize
          </Btn>
        ) : (
          <Btn variant="primary" size="lg" disabled={noTasks} onClick={() => autoOptimize.enable()}>
            <Play aria-hidden="true" className="h-4 w-4" /> Enable Auto Optimize
          </Btn>
        )}
      </div>
    </Card>
  );
}

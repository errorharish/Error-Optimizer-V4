// Dashboard — premium control centre. Hierarchy (user-directed, see MISSING_REFERENCES):
// 1. Overall Optimization Score · 2. Auto Optimize maintenance panel · 2b. Temperature Monitoring
// (one equal-width row) · 3. compact system overview · 4. system information strip · 5. navigation shortcuts + Quick Optimize. Optimization Status was removed.
// The full System Performance Telemetry chart lives on the System Telemetry page.
import { memo, useEffect, useRef, useState, type CSSProperties } from "react";
import { toast } from "sonner";
import { Link } from "@tanstack/react-router";
import {
  Activity,
  ArrowUpRight,
  Cpu,
  Eraser,
  HardDrive,
  HeartPulse,
  Info,
  Loader2,
  MemoryStick,
  Microchip,
  MonitorCog,
  Play,
  RefreshCw,
  Rocket,
  ArchiveRestore,
  Sparkles,
  Trash2,
  Wifi,
  Zap,
  type LucideIcon,
} from "lucide-react";
import {
  Badge,
  Bar,
  Btn,
  Card,
  KV,
  Label,
  Modal,
  PageGuide,
  PageHeader,
  SectionTitle,
  notifyBackendRequired,
} from "../kit";
import { hasNativeBackend, isDesktopApp, useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { isBackendConnected } from "@/lib/eo/connectionStore";
import { startSharedSystemScan } from "@/lib/eo/telemetryStore";
import { sendHostRequest, normalizeBackendExecutionResult } from "@/lib/eo/hostRpc";
import { GuideTip } from "../Guide";
import { OpProgress } from "../OpProgress";
import { ExecutionResultPanel } from "../ExecutionResult";
import { RUN_STATUS_TITLE, runStatus, type ExecutionResult } from "@/lib/eo/executionResult";
import {
  NAV_SHORTCUTS,
  QUICK_OPTIMIZE,
  canStartQuick,
  type QuickAction,
} from "@/lib/eo/quickActions";
import { RestorePointDialog } from "../RestorePoint";
import { useRestoreState } from "../restorePointStore";
import { AnimatedNumber, ScoreRing } from "../controls";
import { liveScanCoordinator } from "@/lib/eo/liveScanCoordinator";
import { AutoOptimizePanel } from "../AutoOptimizePanel";
import { TemperatureCard } from "../TemperatureCard";
import { EMPTY_READINGS, evaluateScan, type ScanReadings } from "@/lib/eo/dashboardScan";
import { useSystemScan, type SystemScan } from "../useSystemScan";
import { ScanStatusLine, ScanSourceBadge } from "../ScanStatus";

interface Tile {
  icon: LucideIcon;
  label: string;
  value: number | null;
  decimals?: number;
  l1: string;
  l2: string;
  foot: string;
}

function tilesFromReadings(r: ScanReadings | null, overallScoreVal: number | null): Tile[] {
  if (!r) {
    return [
      { icon: Cpu, label: "CPU", value: null, l1: "Data unavailable", l2: "Waiting for system data", foot: "NOT DETECTED" },
      { icon: MemoryStick, label: "RAM", value: null, l1: "Data unavailable", l2: "Waiting for system data", foot: "NOT DETECTED" },
      { icon: MonitorCog, label: "GPU", value: null, l1: "Data unavailable", l2: "Waiting for system data", foot: "NOT DETECTED" },
      { icon: HardDrive, label: "STORAGE", value: null, l1: "Data unavailable", l2: "Waiting for system data", foot: "NOT DETECTED" },
      { icon: HeartPulse, label: "HEALTH", value: null, decimals: 1, l1: "No scan available", l2: "Awaiting backend connection", foot: "NOT SCANNED" },
    ];
  }
  const ramPct = r.ram.usedGb !== null && r.ram.totalGb ? Math.round((r.ram.usedGb / r.ram.totalGb) * 100) : null;
  const storPct = r.storage.usedGb !== null && r.storage.totalGb ? Math.round((r.storage.usedGb / r.storage.totalGb) * 100) : null;
  return [
    {
      icon: Cpu,
      label: "CPU",
      value: r.cpu.loadPct,
      l1: r.cpu.model ?? "Processor",
      l2: [r.cpu.cores ? `${r.cpu.cores}C / ${r.cpu.threads ?? r.cpu.cores * 2}T` : null, r.cpu.tempC !== null ? `${r.cpu.tempC}°C` : null].filter(Boolean).join(" · ") || "Data unavailable",
      foot: r.cpu.loadPct === null ? "UNAVAILABLE" : r.cpu.loadPct >= 80 ? "HIGH LOAD" : "OPTIMAL",
    },
    {
      icon: MemoryStick,
      label: "RAM",
      value: ramPct,
      l1: r.ram.usedGb !== null && r.ram.totalGb !== null ? `${r.ram.usedGb} / ${r.ram.totalGb} GB` : "Data unavailable",
      l2: r.ram.spec ?? "System memory",
      foot: ramPct === null ? "UNAVAILABLE" : ramPct >= 85 ? "HIGH USAGE" : "NORMAL",
    },
    {
      icon: MonitorCog,
      label: "GPU",
      value: r.gpu.loadPct,
      l1: r.gpu.model ?? "Graphics adapter",
      l2: [r.gpu.vramUsedGb !== null && r.gpu.vramTotalGb !== null ? `${r.gpu.vramUsedGb} / ${r.gpu.vramTotalGb} GB VRAM` : null, r.gpu.tempC !== null ? `${r.gpu.tempC}°C` : null].filter(Boolean).join(" · ") || "Data unavailable",
      foot: r.gpu.loadPct === null ? "UNAVAILABLE" : r.gpu.loadPct >= 80 ? "ACTIVE LOAD" : "NORMAL",
    },
    {
      icon: HardDrive,
      label: "STORAGE",
      value: storPct,
      l1: r.storage.usedGb !== null && r.storage.totalGb !== null ? `${r.storage.usedGb} / ${r.storage.totalGb} GB` : "Data unavailable",
      l2: r.storage.model ?? "Primary drive",
      foot: r.storage.healthPct !== null ? `HEALTH ${r.storage.healthPct}%` : "UNAVAILABLE",
    },
    {
      icon: HeartPulse,
      label: "HEALTH",
      value: overallScoreVal,
      decimals: 1,
      l1: overallScoreVal !== null ? "System responsiveness" : "No scan available",
      l2: r.latency.dpcUs !== null ? `DPC ${r.latency.dpcUs} µs` : "Awaiting backend connection",
      foot: overallScoreVal !== null ? "MEASURED" : "NOT SCANNED",
    },
  ];
}

/** One System Overview hardware card — the System Telemetry metric-card language at
 * Dashboard size: icon + name, reading, model, supporting detail, slim bar, status label. */
const HwMetric = memo(function HwMetric({ tile: t, index }: { tile: Tile; index: number }) {
  const unit = t.label === "HEALTH" ? "" : "%";
  const pct = t.value ?? 0;
  return (
    <li
      className="eo-card eo-stat eo-hwm"
      data-hw={t.label.toLowerCase()}
      style={{ "--i": index } as CSSProperties}
    >
      <div className="eo-hwm-head">
        <span className="eo-hwm-icon" aria-hidden="true">
          <t.icon className="h-3.5 w-3.5" />
        </span>
        <Label className="eo-hwm-name">
          {t.label}
        </Label>
        <span className="eo-hwm-value tabular-nums">
          {t.value === null ? (
            "—"
          ) : (
            <AnimatedNumber value={t.value} decimals={t.decimals ?? 0} suffix={unit} duration={650} />
          )}
        </span>
      </div>
      <div className="eo-hwm-model" title={t.l1}>
        {t.l1}
      </div>
      <div className="eo-hwm-info" title={t.l2}>
        {t.l2}
      </div>
      <div
        className="eo-hwm-bar"
        role="progressbar"
        aria-label={`${t.label} ${t.label === "HEALTH" ? "health" : "utilization"}`}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(pct)}
      >
        <span style={{ "--v": pct / 100 } as CSSProperties} />
      </div>
      <div className="eo-hwm-foot">{t.foot}</div>
    </li>
  );
});

export function DashboardPage() {
  // The V3 Dashboard starts its own system scan when it loads.
  const scan = useSystemScan({ auto: true });
  const [rpOpen, setRpOpen] = useState(false);
  const connected = useIsBackendConnected();

  useEffect(() => {
    const isConn = isBackendConnected() || hasNativeBackend() || isDesktopApp();
    if (isConn) {
      startSharedSystemScan({ force: true });
    }
  }, [connected]);

  if (scan.error && !scan.result) {
    return (
      <div className="space-y-4 pb-16">
        <PageHeader
          title="Dashboard"
          guide={null}
          badges={<Badge tone="cyan">V4</Badge>}
          sub="System overview, optimization score and automatic maintenance"
        />
        <Card className="border-danger/40 bg-window/80 p-6">
          <div className="flex items-start gap-3">
            <div className="rounded-md bg-danger/10 p-2 text-danger">
              <Activity className="h-6 w-6" />
            </div>
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2">
                <h2 className="text-base font-bold text-foreground">
                  System Scan Communication Failed
                </h2>
                <Badge tone="danger">SCAN ERROR</Badge>
              </div>
              <p className="mt-1 text-xs text-muted-foreground">
                Unable to retrieve hardware telemetry and system health ratings from the background service.
              </p>
              <div className="mt-3 rounded-md bg-black/40 p-2.5 font-mono text-[11px] text-danger/90">
                {scan.error}
              </div>
              <div className="mt-4 flex items-center gap-2">
                <Btn variant="primary" size="sm" onClick={() => scan.start()}>
                  <RefreshCw className="mr-1.5 h-3.5 w-3.5" />
                  Retry Scan
                </Btn>
              </div>
            </div>
          </div>
        </Card>
      </div>
    );
  }

  return (
    <div className="space-y-3.5">
      <PageHeader
        title="Dashboard"
        guide={null}
        badges={<Badge tone="cyan">V4</Badge>}
        sub="System overview, optimization score and automatic maintenance"
        actions={
          <>
            {/* Order: Create Restore Point → Guide → Refresh */}
            <Btn
              variant="primary"
              onClick={() => setRpOpen(true)}
              loading={rpOpen}
              aria-label="Create restore point"
            >
              <ArchiveRestore aria-hidden="true" className="h-3.5 w-3.5" /> Create Restore Point
            </Btn>
            <PageGuide id="dashboard" />
            <Btn onClick={scan.start} loading={scan.phase === "scanning"}>
              <RefreshCw aria-hidden="true" className="h-3.5 w-3.5" /> Refresh
            </Btn>
          </>
        }
      />
      <RestorePointDialog
        open={rpOpen}
        onClose={() => setRpOpen(false)}
        defaultName="Before optimization"
        origin="dashboard"
      />

      {/* 1–3: score, Auto Optimize maintenance engine and Temperature Monitoring — three equal columns */}
      <div className="eo-dash-hero">
        <ScoreCard scan={scan} />
        <AutoOptimizePanel />
        <TemperatureCard scan={scan.result} />
      </div>

      <SystemOverview scan={scan} />
      <SystemInfo scan={scan} />
      <NavShortcuts />
      <QuickOptimize />
    </div>
  );
}

/* ── 4. System information strip ── */
const SystemInfo = memo(function SystemInfo({ scan }: { scan: SystemScan }) {
  const rp = useRestoreState();
  const rd = scan.result?.readings ?? null;
  const rows: [string, string][] = [
    ["Operating system", rd ? "Windows (Detected)" : "Not detected"],
    ["Optimization state", rd ? "Ready" : "Awaiting scan"],
    ["Timer resolution", rd?.latency.timerMs !== null && rd?.latency.timerMs !== undefined ? `${rd.latency.timerMs} ms` : "Unavailable"],
    ["Restore point", rp.last ? rp.last.name : "None created"],
  ];
  return (
    <section aria-labelledby="eo-sysinfo-title" className="eo-card eo-sysinfo px-4 py-2.5">
      <h2 id="eo-sysinfo-title" className="sr-only">
        System information
      </h2>
      <dl className="eo-sysinfo-list">
        {rows.map(([k, v]) => (
          <div key={k}>
            <dt>{k}</dt>
            <dd>{v}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
});

/* ── 1. Overall Optimization Score — 0 / 100 until the automatic scan returns readings ── */
const ScoreCard = memo(function ScoreCard({ scan }: { scan: SystemScan }) {
  const [open, setOpen] = useState(false);
  const ev = scan.result ? evaluateScan(scan.result.readings) : null;
  const score = ev?.overall ?? 0;
  const status = ev?.status ?? "Not scanned";
  const tone = status === "Excellent" ? "success" : status === "Good" ? "cyan" : "warning";
  const categories = ev?.categories ?? evaluateScan(EMPTY_READINGS).categories;
  return (
    <Card
      className={`eo-hero eo-score-card flex flex-col${scan.phase === "scanning" ? " eo-scan" : ""}`}
    >
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-1.5">
          <Label as="h2" className="text-foreground">
            OVERALL OPTIMIZATION SCORE
          </Label>
          <GuideTip id="dashboard.score" />
        </span>
        <ScanSourceBadge scan={scan} />
      </div>
      {/* Gauge beside its classification, then the four categories in a 2 × 2 grid. */}
      <div className="eo-score-main">
        <ScoreRing
          value={score}
          size={128}
          stroke={10}
          valueClassName="text-[34px] leading-none font-black"
          unitClassName="mt-0.5 text-[10px] font-bold text-subtle"
          label={
            ev && ev.overall !== null
              ? `Overall optimization score ${score} out of 100, ${status}.`
              : scan.phase === "scanning"
                ? "Scanning system, score 0 out of 100"
                : "Overall optimization score 0 out of 100, not scanned"
          }
        />
        <div className="eo-score-meta">
          <span className="eo-score-meta-k">CLASSIFICATION</span>
          {scan.phase === "done" && ev ? (
            <Badge tone={tone} className="self-start text-[10px]">
              {status}
            </Badge>
          ) : (
            <ScanStatusLine scan={scan} />
          )}
          <span className="eo-score-meta-note">
            {ev
              ? `${categories.filter((c) => c.value !== null).length} of ${categories.length} categories measured`
              : "Weighted from 4 categories"}
          </span>
        </div>
      </div>
      <ul className="eo-score-cats" aria-label="Score categories">
        {categories.map((c, i) => (
          <li key={c.key}>
            <span className="eo-score-cat-head">
              <span className="truncate">{c.label}</span>
              <b className="tabular-nums">
                {ev && c.value === null ? (
                  "N/A"
                ) : (
                  <AnimatedNumber value={c.value ?? 0} duration={850} />
                )}
              </b>
            </span>
            <span
              className="eo-score-bar"
              style={{ "--v": `${c.value ?? 0}%`, "--i": i } as CSSProperties}
            />
          </li>
        ))}
      </ul>
      <Btn size="sm" variant="ghost" className="eo-score-more w-full" onClick={() => setOpen(true)}>
        <Info aria-hidden="true" className="h-3.5 w-3.5" />
        What makes up this score
      </Btn>
      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title="Overall Optimization Score"
        sub={
          !scan.result
            ? "No scan result yet — awaiting ERROR OPTIMIZER background service connection."
            : `Readings from the Windows backend · scanned ${scan.result.at}`
        }
        footer={
          <Btn variant="primary" onClick={() => setOpen(false)}>
            Got it
          </Btn>
        }
      >
        <div className="space-y-3">
          <p className="text-[12.5px] text-muted-foreground">
            The Dashboard calculates the overall score when the background service returns system
            diagnostics. Each category takes its worst reading, and missing readings are excluded.
          </p>
          {categories.map((c) => (
            <div key={c.key} className="rounded-lg border border-border bg-window p-3">
              <div className="flex items-center justify-between text-[13px] font-bold">
                {c.label}
                <span className="tabular-nums">
                  {c.value ?? "—"}{" "}
                  <span className="text-[10.5px] text-subtle">
                    · weight {Math.round(c.weight * 100)}%
                  </span>
                </span>
              </div>
              <Bar value={c.value ?? 0} className="mt-2" />
              <p className="mt-1.5 text-[11.5px] text-muted-foreground">{c.basis}</p>
            </div>
          ))}
          <KV k="Excellent" v="85 – 100" />
          <KV k="Good" v="70 – 84" />
          <KV k="Needs Attention" v="Below 70" />
        </div>
      </Modal>
    </Card>
  );
});

/* ── 3. Compact system overview ── */
const SystemOverview = memo(function SystemOverview({ scan }: { scan: SystemScan }) {
  const ev = scan.result ? evaluateScan(scan.result.readings) : null;
  const tiles = tilesFromReadings(scan.result?.readings ?? null, ev?.overall ?? null);
  return (
    <section aria-labelledby="eo-overview-title">
      <SectionTitle
        id="eo-overview-title"
        title="SYSTEM OVERVIEW"
        sub={scan.result ? `Last scanned ${scan.result.at}` : "Waiting for system telemetry"}
        right={
          <Link to="/$page" params={{ page: "system-info" }}>
            <Btn size="sm">
              Open System Telemetry
              <ArrowUpRight aria-hidden="true" className="h-3.5 w-3.5" />
            </Btn>
          </Link>
        }
      />
      <ul className="eo-hwm-grid" aria-label="Hardware overview">
        {tiles.map((t, i) => (
          <HwMetric key={t.label} tile={t} index={i} />
        ))}
      </ul>
    </section>
  );
});

/* ── 5a. Navigation shortcuts — open existing pages, never run anything ── */
const ACTION_ICONS: Record<string, LucideIcon> = {
  "one-click": Sparkles,
  boost: Microchip,
  startup: Rocket,
  telemetry: Activity,
  temp: Trash2,
  ram: MemoryStick,
  cache: Eraser,
  network: Wifi,
};

const NavShortcuts = memo(function NavShortcuts() {
  return (
    <section aria-labelledby="eo-shortcuts-title">
      <SectionTitle
        id="eo-shortcuts-title"
        title="NAVIGATION SHORTCUTS"
        guide="dashboard.shortcuts"
        sub="Open a page — nothing runs until you start it there"
      />
      <ul className="eo-nav-grid">
        {NAV_SHORTCUTS.map((a) => {
          const Icon = ACTION_ICONS[a.id] ?? Zap;
          return (
            <li key={a.id}>
              <Link
                to="/$page"
                params={{ page: a.to ?? "" }}
                className="eo-nav-tile"
                data-primary={a.id === "one-click" ? "true" : undefined}
              >
                <span className="eo-quick-icon" aria-hidden="true">
                  <Icon className="h-4 w-4" />
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[12.5px] font-bold">{a.title}</span>
                  <span className="block truncate text-[11px] text-muted-foreground">{a.desc}</span>
                </span>
                <ArrowUpRight aria-hidden="true" className="eo-quick-arrow h-4 w-4 text-cyan" />
              </Link>
            </li>
          );
        })}
      </ul>
    </section>
  );
});

/* ── 5b. Quick Optimize — each card triggers its optimization action ── */
const QuickOptimize = memo(function QuickOptimize() {
  const connected = useIsBackendConnected();
  const [runningId, setRunningId] = useState<string | null>(null);
  const [action, setAction] = useState<QuickAction | null>(null);
  const [execResult, setExecResult] = useState<ExecutionResult | null>(null);
  const [open, setOpen] = useState(false);

  const trigger = async (a: QuickAction) => {
    if (!hasNativeBackend()) {
      notifyBackendRequired(a.title);
      return;
    }
    if (runningId === a.id) {
      setOpen(true);
      return;
    }
    if (!canStartQuick(runningId)) return;
    setRunningId(a.id);
    setAction(a);
    setExecResult(null);
    setOpen(true);
    const t0 = Date.now();
    try {
      let raw: unknown;
      if (a.id === "ram") {
        raw = await sendHostRequest("auto:execute", { tasks: ["mem-standby"], trigger: "Quick Optimize" }, 60000);
      } else if (a.id === "temp") {
        raw = await sendHostRequest("auto:execute", { tasks: ["temp-windows", "temp-user", "temp-crash"], trigger: "Quick Optimize" }, 90000);
      } else if (a.id === "cache") {
        raw = await sendHostRequest("auto:execute", { tasks: ["sys-shader", "browser-chrome", "browser-edge", "browser-firefox"], trigger: "Quick Optimize" }, 90000);
      } else if (a.id === "network") {
        raw = await sendHostRequest("network:apply-quick", undefined, 30000);
      } else {
        raw = await sendHostRequest("auto:run-now", { trigger: a.title }, 90000);
      }
      const rawObj = raw && typeof raw === "object" ? (raw as Record<string, unknown>) : null;
      const taskArr = Array.isArray(rawObj?.["tasks"]) ? (rawObj["tasks"] as Array<Record<string, unknown>>) : null;
      const failedTasks = taskArr?.filter((t) => String(t["outcome"] ?? "").toLowerCase() === "failed") ?? [];
      const completedTasks = taskArr?.filter((t) => String(t["outcome"] ?? "").toLowerCase() === "completed") ?? [];
      if (failedTasks.length > 0 && completedTasks.length === 0) {
        const reason = String(failedTasks[0]?.["reason"] ?? "Operation declined or requires Administrator elevation.");
        throw new Error(reason);
      }
      if (rawObj?.["success"] === false || rawObj?.["Success"] === false) {
        throw new Error(String(rawObj["message"] ?? rawObj["Message"] ?? "Optimization failed."));
      }
      const mappedSteps = taskArr
        ? taskArr.map((t, idx) => {
            const rawStatus = String(t["status"] ?? "").toUpperCase();
            const rawOutcome = String(t["outcome"] ?? "Completed").toLowerCase();
            const isAlready = t["alreadyOptimized"] === true || rawStatus === "ALREADY_OPTIMIZED";
            const status = rawOutcome === "failed" ? ("failed" as const) : (rawOutcome === "skipped" || rawStatus === "NOT_INSTALLED" || rawStatus === "SKIPPED_BUSY") ? ("skipped" as const) : ("completed" as const);
            let detail = rawStatus;
            if (isAlready) detail = "ALREADY_OPTIMIZED";
            else if (rawStatus === "SKIPPED_BUSY" || String(t["reason"] ?? "").toLowerCase().includes("running") || String(t["reason"] ?? "").toLowerCase().includes("contention")) detail = "SKIPPED_BUSY";
            else if (rawStatus === "NOT_INSTALLED" || String(t["reason"] ?? "").toLowerCase().includes("not installed")) detail = "NOT_INSTALLED";

            return {
              id: String(t["id"] ?? t["taskId"] ?? `${a.id}-${idx}`),
              title: String(t["name"] ?? a.steps[idx]?.t ?? t["taskId"] ?? `Task ${idx + 1}`),
              status,
              reason: typeof t["reason"] === "string" ? t["reason"] : undefined,
              detail,
              alreadyOptimized: isAlready,
            };
          })
        : a.steps.map((s, idx) => ({ id: `${a.id}-${idx}`, title: s.t }));
      const res = normalizeBackendExecutionResult(
        raw,
        a.cat,
        a.title,
        mappedSteps,
        Date.now() - t0,
      );
      setExecResult(res);
      liveScanCoordinator.notifyOptimizationCompleted("dashboard");
      liveScanCoordinator.notifyOptimizationCompleted(a.id);
      const st = runStatus(res);
      if (st === "FAILED") {
        toast.error(`${a.title} failed`, { description: res.error ?? "Execution reported failures." });
      } else if (st === "PARTIAL") {
        toast.warning(RUN_STATUS_TITLE[st], { description: a.title });
      } else {
        toast.success(RUN_STATUS_TITLE[st], { description: a.title });
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setExecResult({
        module: a.cat,
        title: a.title,
        simulated: false,
        error: msg,
        durationMs: Date.now() - t0,
        recovery: { available: false, label: "No changes applied" },
        operations: [{ id: a.id, title: a.title, status: "failed", reason: msg }],
      });
      toast.error(`${a.title} failed`, { description: msg });
    } finally {
      setRunningId(null);
    }
  };

  const ActionIcon = action ? (ACTION_ICONS[action.id] ?? Zap) : Zap;

  return (
    <section aria-labelledby="eo-quick-title">
      <SectionTitle
        id="eo-quick-title"
        title="QUICK OPTIMIZE"
        guide="dashboard.quick-actions"
        sub="Runs one action right here on the Dashboard"
      />
      <div className="eo-qo-grid">
        {QUICK_OPTIMIZE.map((q) => {
          const Icon = ACTION_ICONS[q.id] ?? Zap;
          const isRunning = runningId === q.id;
          const blocked = runningId !== null && !isRunning;
          return (
            <button
              key={q.id}
              type="button"
              onClick={() => trigger(q)}
              aria-disabled={blocked || undefined}
              aria-describedby={blocked ? "eo-quick-busy" : undefined}
              data-state={isRunning ? "running" : undefined}
              className="eo-quick"
            >
              <span className="flex items-start justify-between gap-2">
                <span className="eo-quick-icon" aria-hidden="true">
                  <Icon className="h-4 w-4" />
                </span>
                {isRunning ? (
                  <Loader2 aria-hidden="true" className="h-4 w-4 animate-spin text-cyan" />
                ) : (
                  <Play aria-hidden="true" className="eo-quick-arrow h-3.5 w-3.5 text-subtle" />
                )}
              </span>
              <Label className="mt-2 text-[9.5px] text-cyan">{q.cat}</Label>
              <span className="mt-0.5 block text-[13px] font-bold">{q.title}</span>
              <span className="mt-0.5 block flex-1 text-[11px] leading-snug text-muted-foreground">
                {q.desc}
              </span>
              <span className="eo-quick-foot">
                <span>{connected ? "Run now" : "Requires backend"}</span>
              </span>
            </button>
          );
        })}
      </div>
      <p id="eo-quick-busy" className="sr-only">
        Another quick optimization is running. Wait for it to finish.
      </p>

      <Modal
        open={open && action !== null}
        onClose={() => setOpen(false)}
        title={action?.title ?? "Quick optimization"}
        sub={execResult ? RUN_STATUS_TITLE[runStatus(execResult)] : "Executing optimization action"}
        width="max-w-md"
        icon={<ActionIcon className="h-4 w-4" />}
        tone={execResult ? (runStatus(execResult) === "SUCCESS" || runStatus(execResult) === "ALREADY_OPTIMAL" ? "success" : "warning") : "cyan"}
        footer={
          <Btn variant="primary" onClick={() => setOpen(false)}>
            Dismiss
          </Btn>
        }
      >
        {action &&
          (execResult ? (
            <ExecutionResultPanel result={execResult} />
          ) : (
            <OpProgress
              value={50}
              title={action.title}
              detail="Executing on Windows background service"
              active
            />
          ))}
      </Modal>
    </section>
  );
});

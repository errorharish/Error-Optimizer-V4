// Settings & Control Center: one page with a category navigation, settings search and compact
// sections. Theme + effect controls come from ThemeSettings.tsx; preferences are saved
// in settingsStore.
import { useEffect, useState, type ReactNode } from "react";
import { Link, useBlocker } from "@tanstack/react-router";
import {
  ChevronRight,
  Gauge,
  Info,
  LayoutDashboard,
  Palette,
  Rocket,
  ScrollText,
  Search,
  ShieldCheck,
  Sparkles,
  type LucideIcon,
} from "lucide-react";
import { Badge, Btn, Card, Details, KV, Modal, PageHeader, Toggle, btnProps } from "./kit";
import { hasNativeBackend, useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { toast } from "sonner";
import { Segmented } from "./controls";
import { PremiumSelect } from "./dropdown";
import { AppearanceSection, MotionSection, saveDraftWithToast } from "./ThemeSettings";
import { SettingRow, SettingsCard, StatusDot } from "./settingsUi";
import { StatusIndicator } from "./StatusIndicator";
import {
  onSettingsCategoryRequest,
  restoreSettingsDefaults,
  setSettings,
  takeSettingsCategoryRequest,
  useSettings,
  toggleAutoOptimization,
  toggleAiOptimization,
} from "./settingsStore";
import { discardThemeDraft, hasThemeDraft, resetThemeToDefault, resolveSaved, useThemeState } from "./themeStore";
import { useAppearance } from "./Appearance";
import { useServiceHealth } from "./ServiceStatus";
import { SOURCE_LABEL, STATE_LABEL } from "@/lib/eo/serviceHealth";
import { isDirty } from "@/lib/eo/theme";
import {
  AI_POLICIES,
  LOG_RETENTION_DAYS,
  MONITOR_INTERVALS,
  PERFORMANCE_MODES,
  SETTINGS_CATEGORIES,
  categoryLabel,
  intervalLabel,
  needsSafetyConfirm,
  performanceLabel,
  retentionLabel,
  safetyStatus,
  searchSettings,
  type LogRetention,
  type MonitorInterval,
  type PerformanceMode,
  type SafetyKey,
  type SettingsCategory,
} from "@/lib/eo/settings";
import { cn } from "@/lib/utils";

const ICONS: Record<SettingsCategory, LucideIcon> = {
  overview: LayoutDashboard,
  appearance: Palette,
  effects: Sparkles,
  performance: Gauge,
  startup: Rocket,
  safety: ShieldCheck,
  logs: ScrollText,
  system: Info,
};

/* ───────── Overview ───────── */
function Tile({
  label,
  value,
  sub,
  tone,
  icon: Icon,
  cta,
  onOpen,
}: {
  label: string;
  value: string;
  sub: string;
  tone: "success" | "warning" | "cyan" | "muted";
  icon: LucideIcon;
  cta: string;
  onOpen: () => void;
}) {
  return (
    <button type="button" className="eo-set-tile" data-tone={tone} onClick={onOpen}>
      <span className="eo-set-tile-head">
        <span className="eo-label text-muted-foreground">{label}</span>
        <span className="eo-set-tile-icon" aria-hidden="true">
          <Icon className="h-3.5 w-3.5" />
        </span>
      </span>
      <span className="eo-set-tile-value" title={value}>{value}</span>
      <span className="eo-set-tile-sub" title={sub}>{sub}</span>
      <span className="eo-set-tile-cta">
        {cta}
        <ChevronRight aria-hidden="true" className="h-3 w-3" />
      </span>
    </button>
  );
}

function OverviewSection({ open, onRestore }: { open: (c: SettingsCategory) => void; onRestore: () => void }) {
  const s = useSettings();
  const theme = useThemeState();
  const appearance = useAppearance();
  const saved = resolveSaved(theme);
  const dirty = isDirty(theme);
  const motionOff = appearance.reduced || appearance.systemReduced;
  const safe = safetyStatus(s) === "protected";
  return (
    <div className="eo-set-overview">
      <SettingsCard id="overview-status" className="eo-set-grow" title="CONFIGURATION OVERVIEW" sub="Shared by V3 Classic and V4 Professional.">
        <div className="eo-set-tiles">
          <Tile
            label="Active Theme"
            icon={Palette}
            cta="Appearance & Themes"
            value={saved.name}
            sub={dirty ? "Unsaved changes in preview" : "Saved"}
            tone={dirty ? "warning" : "cyan"}
            onOpen={() => open("appearance")}
          />
          <Tile
            label="Product Edition"
            icon={Info}
            cta="System Information"
            value="V4"
            sub="ERROR OPTIMIZER — VERSION V4"
            tone="cyan"
            onOpen={() => open("system")}
          />
          <Tile
            label="Animation Status"
            icon={Sparkles}
            cta="Animations & Motion"
            value={motionOff ? "Reduced motion" : "Full motion"}
            sub={appearance.transitions ? "Page transitions on" : "Page transitions off"}
            tone={motionOff ? "muted" : "success"}
            onOpen={() => open("effects")}
          />
          <Tile
            label="Performance Mode"
            icon={Gauge}
            cta="Performance & Telemetry"
            value={performanceLabel(s.performanceMode)}
            sub={`Updates every ${intervalLabel(s.monitorIntervalSec).replace(/ \(.*\)$/, "")}`}
            tone="cyan"
            onOpen={() => open("performance")}
          />
          <Tile
            label="Safety Status"
            icon={ShieldCheck}
            cta="Safety & Recovery"
            value={safe ? "Fully protected" : "Reduced protection"}
            sub={safe ? "Confirmations and backups on" : "A safeguard is turned off"}
            tone={safe ? "success" : "warning"}
            onOpen={() => open("safety")}
          />
          <Tile
            label="Configuration"
            icon={LayoutDashboard}
            cta={dirty ? "Review theme changes" : "All changes saved"}
            value={dirty ? "Unsaved theme changes" : "Saved"}
            sub="Local UI configuration"
            tone={dirty ? "warning" : "success"}
            onOpen={() => open(dirty ? "appearance" : "overview")}
          />
        </div>
      </SettingsCard>
      <SettingsCard id="restore-defaults" title="QUICK ACTIONS" sub="Common changes, one click away.">
        <div className="eo-set-actions">
          <Btn size="sm" onClick={() => open("appearance")}>
            <Palette aria-hidden="true" className="h-3.5 w-3.5" />
            Customize appearance
          </Btn>
          <Btn size="sm" onClick={() => open("performance")}>
            <Gauge aria-hidden="true" className="h-3.5 w-3.5" />
            Performance settings
          </Btn>
          <Btn size="sm" variant="danger" onClick={onRestore}>
            Restore defaults
          </Btn>
        </div>
      </SettingsCard>
    </div>
  );
}

/* ───────── Performance ───────── */
function PerformanceSection() {
  const s = useSettings();
  const mode = PERFORMANCE_MODES.find((m) => m.id === s.performanceMode)!;
  return (
    <SettingsCard
      title="PERFORMANCE & TELEMETRY ENGINE"
      sub="How much the app spends on live monitoring. Optimization results are not affected."
    >
      <SettingRow id="perf-mode" title="System Performance Mode" desc={mode.hint} tip="Auto is right for most PCs.">
        <Segmented
          label="System performance mode"
          options={PERFORMANCE_MODES.map((m) => m.label)}
          value={mode.label}
          onChange={(l) =>
            setSettings({ performanceMode: (PERFORMANCE_MODES.find((m) => m.label === l)?.id ?? "auto") as PerformanceMode })
          }
        />
      </SettingRow>
      <SettingRow
        id="perf-interval"
        title="Monitor Update Interval"
        desc="How often live CPU, GPU and RAM readings refresh."
        tip="Faster updates feel more live; slower updates use less CPU."
      >
        <PremiumSelect
          size="sm"
          className="w-48"
          label="Monitor update interval"
          value={String(s.monitorIntervalSec)}
          onChange={(v) => setSettings({ monitorIntervalSec: Number(v) as MonitorInterval })}
          options={MONITOR_INTERVALS.map((n) => ({ value: String(n), label: intervalLabel(n) }))}
        />
      </SettingRow>
      <p className="mt-2 text-[11px] text-subtle">
        Saved preference for the ERROR OPTIMIZER background monitoring service.
      </p>
    </SettingsCard>
  );
}

/* ───────── Startup & Automation ───────── */
interface StartupRegState {
  isEnabled?: boolean;
  displayStatus?: string;
  detailMessage?: string;
  exePath?: string;
  arguments?: string;
  checkedAt?: string;
}

function StartupSection() {
  const s = useSettings();
  const connected = useIsBackendConnected();
  const [regStatus, setRegStatus] = useState<StartupRegState | null>(null);
  const [checking, setChecking] = useState(false);

  const refreshStartupStatus = async (showToast = false) => {
    if (!hasNativeBackend()) return;
    setChecking(true);
    try {
      const res = (await sendHostRequest("settings:startup-status", undefined, 10000)) as StartupRegState;
      setRegStatus({ ...res, checkedAt: new Date().toLocaleTimeString() });
      if (showToast) toast.success("Startup registration refreshed", { description: res?.displayStatus ?? "Inspected registry logon entry." });
    } catch (err) {
      if (showToast) toast.error("Startup status check failed", { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setChecking(false);
    }
  };

  useEffect(() => {
    if (connected) void refreshStartupStatus(false);
  }, [connected]);

  const updateStartupSetting = async (enable: boolean, startMinimized: boolean) => {
    const prevEnable = s.startWithWindows;
    const prevMin = s.startMinimized;
    setSettings({ startWithWindows: enable, startMinimized });
    if (!hasNativeBackend()) return;
    try {
      const res = (await sendHostRequest("settings:set-startup", { enable, startMinimized }, 10000)) as StartupRegState & {
        success?: boolean;
      };
      if (res?.success === false || Boolean(res?.isEnabled) !== enable) {
        throw new Error(res?.displayStatus ?? "Windows startup registration could not be verified.");
      }
      setRegStatus({ ...res, checkedAt: new Date().toLocaleTimeString() });
    } catch (err) {
      setSettings({ startWithWindows: prevEnable, startMinimized: prevMin });
      toast.error("Failed to update Windows startup", { description: err instanceof Error ? err.message : String(err) });
    }
  };

  const registered = regStatus?.isEnabled === true;
  return (
    <div className="eo-set-grid2">
      <SettingsCard
        title="AUTO OPTIMIZE & AI INTELLIGENCE SYSTEM"
        sub="Synchronized background engine, startup, and tray operational settings."
        right={
          <Btn size="sm" loading={checking} onClick={() => void refreshStartupStatus(true)}>
            Refresh status
          </Btn>
        }
      >
        <SettingRow
          id="auto-optimize-global"
          title="Auto Optimize (Global Master)"
          desc="Master autonomous maintenance engine active across system tray and background."
          status={
            <StatusDot tone={s.autoOptimizationEnabled ? "success" : "muted"}>
              {s.autoOptimizationEnabled ? "ACTIVE" : "DISABLED"}
            </StatusDot>
          }
        >
          <Toggle
            label="Auto Optimize"
            on={s.autoOptimizationEnabled}
            onChange={() => void toggleAutoOptimization()}
          />
        </SettingRow>
        <SettingRow
          id="ai-optimization-global"
          title="AI Optimization (Adaptive Decision Layer)"
          desc="Dynamic workload context and hardware detection layer governing Auto Optimize."
          status={
            <StatusDot tone={s.aiOptimizationEnabled ? "cyan" : "muted"}>
              {s.aiOptimizationEnabled ? "ADAPTIVE" : "OFF"}
            </StatusDot>
          }
        >
          <Toggle
            label="AI Optimization"
            on={s.aiOptimizationEnabled}
            onChange={() => void toggleAiOptimization()}
          />
        </SettingRow>
        <SettingRow
          id="startup-windows"
          title="Start with Windows"
          desc="Open ERROR OPTIMIZER automatically when signing into Windows."
          status={
            <StatusDot tone={regStatus ? (registered ? "success" : "muted") : "muted"}>
              {regStatus ? (registered ? "ACTIVE" : "OFF") : "PENDING"}
            </StatusDot>
          }
        >
          <Toggle
            label="Start with Windows"
            on={s.startWithWindows}
            onChange={(on) => void updateStartupSetting(on, s.startMinimized)}
          />
        </SettingRow>
        <SettingRow
          id="startup-minimized"
          title="Start Minimized"
          desc="Launch quietly into the system tray on startup."
          status={
            <StatusDot tone={s.startMinimized ? "cyan" : "muted"}>
              {s.startMinimized ? "QUIET" : "NORMAL"}
            </StatusDot>
          }
        >
          <Toggle
            label="Start minimized"
            on={s.startMinimized}
            onChange={(on) => void updateStartupSetting(s.startWithWindows, on)}
          />
        </SettingRow>
        <SettingRow
          id="system-tray-enabled"
          title="Run in System Tray"
          desc="Keep background automation active in the Windows notification area on close."
          status={
            <StatusDot tone={s.runInSystemTray ? "success" : "muted"}>
              {s.runInSystemTray ? "TRAY" : "EXIT"}
            </StatusDot>
          }
        >
          <Toggle
            label="Run in System Tray"
            on={s.runInSystemTray}
            onChange={(on) => setSettings({ runInSystemTray: on })}
          />
        </SettingRow>
        <SettingRow
          id="adaptive-hw-detect"
          title="Adaptive Hardware Detection"
          desc="Inspect CPU architecture, GPU drivers and thermals before applying profile rules."
          status={
            <StatusDot tone={s.adaptiveHardwareDetectionEnabled ? "cyan" : "muted"}>
              {s.adaptiveHardwareDetectionEnabled ? "ENABLED" : "OFF"}
            </StatusDot>
          }
        >
          <Toggle
            label="Adaptive Hardware Detection"
            on={s.adaptiveHardwareDetectionEnabled}
            onChange={(on) => setSettings({ adaptiveHardwareDetectionEnabled: on })}
          />
        </SettingRow>
        <div data-setting-id="startup-status">
          <Details inline title="STARTUP STATUS" hint="Registration & bootstrap status">
            <div className="grid grid-cols-1 gap-x-4 sm:grid-cols-3">
              <KV
                k="Task Manager Status"
                v={regStatus?.displayStatus ?? (s.startWithWindows ? "Pending sync" : "Not registered")}
              />
              <KV
                k="Startup Bootstrap"
                v={registered ? (s.startMinimized ? "Minimized background" : "Desktop launch") : "Idle"}
              />
              <KV
                k="System Tray"
                v={s.runInSystemTray ? "Persistent Notification Icon" : "Disabled"}
              />
              <KV k="Last Scan" v={s.lastSuccessfulScan || regStatus?.checkedAt || "—"} />
            </div>
          </Details>
        </div>
      </SettingsCard>
      <SettingsCard title="AI ENGINE STARTUP POLICIES" sub="What starts automatically after Windows boots.">
        {AI_POLICIES.map((p) => (
          <SettingRow key={p.id} id={`policy-${p.id}`} title={p.label} desc={p.hint}>
            <Toggle
              label={p.label}
              on={s.policies[p.id]}
              onChange={(on) => setSettings({ policies: { ...s.policies, [p.id]: on } })}
            />
          </SettingRow>
        ))}
      </SettingsCard>
    </div>
  );
}

/* ───────── Safety & Recovery ───────── */
const SAFETY_ROWS: { key: SafetyKey; title: string; desc: string; tip: string }[] = [
  {
    key: "confirmMedium",
    title: "Confirm Medium Risk",
    desc: "Ask before moderate system changes.",
    tip: "Medium-risk tweaks can change how Windows behaves. A quick confirmation prevents accidents.",
  },
  {
    key: "confirmHigh",
    title: "Confirm High Risk",
    desc: "Always show a warning before sensitive changes.",
    tip: "High-risk tweaks touch sensitive settings. Keep this on unless you know exactly what you are doing.",
  },
  {
    key: "autoBackup",
    title: "Automatic Transactional Backup",
    desc: "Snapshot registry values and services before every run.",
    tip: "The snapshot lets you undo a run from Backup & Restore.",
  },
];

function SafetySection() {
  const s = useSettings();
  const [pending, setPending] = useState<SafetyKey | null>(null);
  const safe = safetyStatus(s) === "protected";
  const row = SAFETY_ROWS.find((r) => r.key === pending);
  return (
    <>
      <SettingsCard
        title="SAFETY GUARDRAILS & TRANSACTIONAL RECOVERY"
        sub="These keep every optimization reversible and confirmed."
        right={<StatusIndicator tone={safe ? "pos" : "warn"} live reserve="REDUCED PROTECTION">{safe ? "FULLY PROTECTED" : "REDUCED PROTECTION"}</StatusIndicator>}
      >
        {SAFETY_ROWS.map((r) => (
          <SettingRow
            key={r.key}
            id={`safety-${r.key}`}
            title={r.title}
            desc={r.desc}
            tip={r.tip}
            status={<StatusDot tone={s[r.key] ? "success" : "warning"}>{s[r.key] ? "ON" : "OFF"}</StatusDot>}
          >
            <Toggle
              label={r.title}
              on={s[r.key]}
              onChange={(on) => (needsSafetyConfirm(r.key, on) ? setPending(r.key) : setSettings({ [r.key]: on }))}
            />
          </SettingRow>
        ))}
        <SettingRow
          id="safety-restore"
          title="Restore & Recovery"
          desc="Create restore points, browse snapshots and undo past runs."
        >
          <Link to="/$page" params={{ page: "backup-restore" }} {...btnProps("primary", "sm")}>
            Open Backup & Restore
          </Link>
        </SettingRow>
      </SettingsCard>
      <Modal
        open={!!pending}
        onClose={() => setPending(null)}
        title={`Turn off ${row?.title ?? "safeguard"}?`}
        sub="This removes a safety net for future optimizations."
        tone="warning"
        width="max-w-md"
        footer={
          <>
            <Btn onClick={() => setPending(null)}>Keep it on</Btn>
            <Btn
              variant="danger"
              onClick={() => {
                if (pending) setSettings({ [pending]: false });
                setPending(null);
              }}
            >
              Turn off
            </Btn>
          </>
        }
      >
        <p className="text-[12.5px] text-muted-foreground">{row?.tip}</p>
      </Modal>
    </>
  );
}

/* ───────── Logs ───────── */
function LogsSection() {
  const s = useSettings();
  return (
    <SettingsCard title="AUDIT LOGGING & LOG RETENTION" sub="What the app records about each run and how long it keeps it.">
      <SettingRow
        id="logs-detailed"
        title="Detailed Execution Logs"
        desc="Record extra technical detail for troubleshooting."
        tip="Adds return codes and traces to logs. Useful when reporting a problem."
      >
        <Toggle label="Detailed execution logs" on={s.detailedLogs} onChange={(on) => setSettings({ detailedLogs: on })} />
      </SettingRow>
      <SettingRow id="logs-retention" title="Log Retention Period" desc="Older logs are cleaned up automatically.">
        <Segmented
          label="Log retention period"
          options={LOG_RETENTION_DAYS.map(retentionLabel)}
          value={retentionLabel(s.logRetentionDays)}
          onChange={(l) =>
            setSettings({ logRetentionDays: (LOG_RETENTION_DAYS.find((n) => retentionLabel(n) === l) ?? 7) as LogRetention })
          }
        />
      </SettingRow>
    </SettingsCard>
  );
}

/* ───────── System information ───────── */
function SystemSection() {
  const h = useServiceHealth();
  const tone = h.state === "online" ? "success" : h.state === "offline" ? "danger" : "warning";
  return (
    <div className="eo-set-grid2">
      <SettingsCard id="system-version" title="SYSTEM SPECIFICATIONS & VERSION">
        <KV k="Application Version" v="ERROR OPTIMIZER — VERSION V4" />
        <KV k="Architecture" v="Unified Windows Engine" />
        <KV k="Build Identifier" v="desktop-ui" mono />
        <KV k="Optimization Architecture" v="Unified Safe IPC Service" />
      </SettingsCard>
      <SettingsCard id="system-service" title="BACKEND / SERVICE STATUS">
        <KV k="Status" v={<StatusDot tone={tone}>{STATE_LABEL[h.state]}</StatusDot>} />
        <KV k="Source" v={h.source ? SOURCE_LABEL[h.source] : "Detecting…"} />
        <KV k="Last check" v={h.last ? new Date(h.last.at).toLocaleTimeString() : "—"} />
      </SettingsCard>
    </div>
  );
}

/* ───────── Page ───────── */
export function SettingsCenter() {
  // A pending APPEARANCE-shortcut request opens its category straight away (no Overview flash).
  const [cat, setCat] = useState<SettingsCategory>(() => takeSettingsCategoryRequest() ?? "overview");
  const [q, setQ] = useState("");
  const [target, setTarget] = useState<string | null>(null);
  const [confirmReset, setConfirmReset] = useState(false);
  const [pendingCat, setPendingCat] = useState<{ c: SettingsCategory; id?: string | undefined } | null>(null);
  const results = searchSettings(q);
  const themeDirty = isDirty(useThemeState());

  // Live Preview follows the user across pages (global tray), so in-app navigation never blocks;
  // only closing/reloading the window with an unsaved draft asks first. Nothing is saved silently.
  const blocker = useBlocker({
    shouldBlockFn: () => false,
    enableBeforeUnload: () => hasThemeDraft(),
    withResolver: true,
  });

  // After a search result opens its category, scroll to the setting and highlight it.
  useEffect(() => {
    if (!target) return;
    const id = window.requestAnimationFrame(() => {
      const el = document.querySelector<HTMLElement>(`[data-setting-id="${target}"]`);
      if (el) {
        el.scrollIntoView({ block: "center", behavior: "smooth" });
        el.dataset["flash"] = "true";
        window.setTimeout(() => delete el.dataset["flash"], 1600);
      }
      setTarget(null);
    });
    return () => window.cancelAnimationFrame(id);
  }, [target, cat]);

  // APPEARANCE shortcut (Live Preview tray): open the requested category on mount or while open.
  useEffect(
    () =>
      onSettingsCategoryRequest((c) => {
        takeSettingsCategoryRequest();
        setQ("");
        setCat(c);
      }),
    [],
  );

  const go = (c: SettingsCategory, settingId?: string) => {
    setCat(c);
    if (settingId) setTarget(settingId);
  };
  const open = (c: SettingsCategory, settingId?: string) => {
    go(c, settingId);
  };
  const leaveOpen = blocker.status === "blocked" || !!pendingCat;
  const keepEditing = () => {
    if (blocker.status === "blocked") blocker.reset();
    setPendingCat(null);
  };
  const leave = (save: boolean) => {
    if (save) {
      if (!saveDraftWithToast()) return keepEditing();
    } else discardThemeDraft();
    if (blocker.status === "blocked") blocker.proceed();
    if (pendingCat) go(pendingCat.c, pendingCat.id);
    setPendingCat(null);
  };

  let body: ReactNode;
  switch (cat) {
    case "overview":
      body = <OverviewSection open={open} onRestore={() => setConfirmReset(true)} />;
      break;
    case "appearance":
      body = <AppearanceSection />;
      break;
    case "effects":
      body = <MotionSection />;
      break;
    case "performance":
      body = <PerformanceSection />;
      break;
    case "startup":
      body = <StartupSection />;
      break;
    case "safety":
      body = <SafetySection />;
      break;
    case "logs":
      body = <LogsSection />;
      break;
    case "system":
      body = <SystemSection />;
      break;
  }

  return (
    <div className="eo-set-page">
      <PageHeader
        title="Settings & Control Center"
        sub="Manage your ERROR OPTIMIZER experience, appearance, performance and system preferences."
        actions={
          <Btn size="sm" variant="danger" onClick={() => setConfirmReset(true)}>
            Restore defaults
          </Btn>
        }
      />
      <div className="eo-set-wrap">
      <div className="eo-set-layout" data-cat={cat}>
        <nav className="eo-set-nav" aria-label="Settings categories">
          <div className="eo-set-search">
            <Search aria-hidden="true" className="h-3.5 w-3.5" />
            <input
              type="search"
              aria-label="Search settings"
              placeholder="Search settings"
              value={q}
              onChange={(e) => setQ(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Escape") setQ("");
                if (e.key === "Enter" && results[0]) {
                  open(results[0].category, results[0].id);
                  setQ("");
                }
              }}
            />
          </div>
          {q.trim() && (
            <div className="eo-set-results" role="list" aria-label="Search results">
              {results.length === 0 && <div className="px-2 py-2 text-[11.5px] text-muted-foreground">No settings match.</div>}
              {results.map((r) => (
                <button
                  key={r.id}
                  type="button"
                  role="listitem"
                  className="eo-set-result"
                  onClick={() => {
                    open(r.category, r.id);
                    setQ("");
                  }}
                >
                  <span className="truncate text-[12px] font-semibold text-foreground">{r.label}</span>
                  <span className="truncate text-[10.5px] text-muted-foreground">{categoryLabel(r.category)}</span>
                </button>
              ))}
            </div>
          )}
          <div className="eo-set-cats">
            {SETTINGS_CATEGORIES.map((c) => {
              const Icon = ICONS[c.id];
              return (
                <button
                  key={c.id}
                  type="button"
                  className="eo-set-cat"
                  aria-current={cat === c.id ? "page" : undefined}
                  data-dirty={c.id === "appearance" && themeDirty ? "true" : undefined}
                  title={c.hint}
                  onClick={() => open(c.id)}
                >
                  <Icon aria-hidden="true" className="h-4 w-4 shrink-0" />
                  <span className="truncate">{c.label}</span>
                </button>
              );
            })}
          </div>
          <p className="eo-set-nav-foot">Shared by V3 Classic and V4 Professional.</p>
        </nav>
        <section className="eo-set-main" aria-label={categoryLabel(cat)}>
          <div key={cat} className={cn("eo-set-pane", cat === "overview" && "eo-set-pane-fill")}>
            {body}
          </div>
        </section>
      </div>
      </div>
      <Modal
        open={leaveOpen}
        onClose={keepEditing}
        title="Discard unsaved theme changes?"
        sub="Your theme edits are only in the live preview and have not been saved."
        tone="warning"
        width="max-w-md"
        footer={
          <>
            <Btn onClick={keepEditing}>Continue editing</Btn>
            <Btn onClick={() => leave(true)}>Save changes</Btn>
            <Btn variant="danger" onClick={() => leave(false)}>
              Discard changes
            </Btn>
          </>
        }
      >
        <p className="text-[12.5px] text-muted-foreground">
          Discard keeps your saved theme. Continue editing to stay on Appearance &amp; Themes.
        </p>
      </Modal>
      <Modal
        open={confirmReset}
        onClose={() => setConfirmReset(false)}
        title="Restore default settings?"
        sub="Theme, motion, performance, startup, safety and log settings go back to their defaults."
        tone="warning"
        width="max-w-md"
        footer={
          <>
            <Btn onClick={() => setConfirmReset(false)}>Cancel</Btn>
            <Btn
              variant="danger"
              onClick={() => {
                restoreSettingsDefaults();
                resetThemeToDefault();
                setConfirmReset(false);
                setCat("overview");
              }}
            >
              Restore defaults
            </Btn>
          </>
        }
      >
        <p className="text-[12.5px] text-muted-foreground">
          ERROR DEFAULT becomes the active theme. Your saved custom themes are kept.
        </p>
      </Modal>
    </div>
  );
}

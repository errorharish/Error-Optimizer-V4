// AiOptimizationView, BackupRestoreView, SettingsView, AboutView, PerformanceTuningView
import { useEffect, useRef, useState, useMemo, type ReactNode } from "react";
import { hasNativeBackend, useIsBackendConnected, isDesktopApp } from "@/lib/eo/nativeBridge";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { parseReadings, type ScanReadings } from "@/lib/eo/dashboardScan";
import {
  Badge,
  Bar,
  Btn,
  Card,
  Chips,
  Cols,
  Details,
  EmptyState,
  KV,
  Label,
  Modal,
  PageHeader,
  PremiumCircularCheckbox,
  RiskBadge,
  SearchBox,
  SectionTitle,
  Stepper,
  StatTiles,
  Toggle,
} from "../kit";
import { GuideTip } from "../Guide";
import { usePageScan } from "../usePageScan";
import { useAppearance } from "../Appearance";
import { SettingsCenter } from "../SettingsCenter";
import { setSettings, useSettings, toggleAutoOptimization, toggleAiOptimization } from "../settingsStore";
import { CountUp, NumberField, RangeSlider, Segmented } from "../controls";
import { StatusIndicator, type StatusTone } from "../StatusIndicator";
import {
  Activity,
  AlertCircle,
  Check,
  CheckCircle2,
  CheckSquare,
  Cpu,
  Eye,
  Feather,
  FileCode,
  Flame,
  Folder,
  Info,
  Layers,
  Maximize2,
  MemoryStick,
  Monitor,
  Play,
  Plus,
  RotateCcw,
  ShieldAlert,
  ShieldCheck,
  Sliders,
  SlidersHorizontal,
  Sparkles,
  Square,
  Target,
  Trash2,
  Zap,
} from "lucide-react";
import { RestorePointDialog } from "../RestorePoint";
import { WorkloadOptimizationPanel } from "../WorkloadOptimizationPanel";
import { useWorkloads, workloadActions } from "../workloadStore";
import { summarizeWorkloads } from "@/lib/eo/workload/optimizer";
import { PremiumSelect } from "../dropdown";
import { useRestoreState } from "../restorePointStore";
import { SYSTEM_DRIVE } from "@/lib/eo/restorePoint";
import { CATALOG, PROFILES, type Risk } from "@/lib/eo/data";
import { cn } from "@/lib/utils";
import { toast } from "sonner";

/* ───────── AI OPTIMIZATION (2 sub-modules via top segmented control) ───────── */

interface PowerPlanItem {
  name: string;
  guid: string;
  type: "AI_CUSTOM" | "WINDOWS_STANDARD" | "WINDOWS_ULTIMATE" | "CUSTOM_USER";
  description: string;
  cpuMin: number;
  cpuMax: number;
  cooling: "Active" | "Passive";
}

const DEFAULT_POWER_PLANS: PowerPlanItem[] = [
  {
    name: "Error Optimizer AI — Max Performance",
    guid: "a1b00001-e0e0-4000-8000-000000000001",
    type: "AI_CUSTOM",
    description:
      "Adaptive AI scheme: 5%-100% adaptive processor scaling with zero-latency autonomous boost, active cooling, low-jitter thread dispatch, and full PCIe link speed.",
    cpuMin: 5,
    cpuMax: 100,
    cooling: "Active",
  },
  {
    name: "Error Optimizer AI — Battery Efficiency",
    guid: "a1b00002-e0e0-4000-8000-000000000002",
    type: "AI_CUSTOM",
    description:
      "Calibrated battery efficiency: 5%-80% processor scaling, passive cooling on DC, EPP energy bias, and PCIe ASPM maximum power savings.",
    cpuMin: 5,
    cpuMax: 80,
    cooling: "Passive",
  },
  {
    name: "Ultimate Performance",
    guid: "e9a42b02-d5df-448d-aa00-03f14749eb61",
    type: "WINDOWS_ULTIMATE",
    description: "OEM high-performance workstation scheme with disabled micro-latencies.",
    cpuMin: 100,
    cpuMax: 100,
    cooling: "Active",
  },
  {
    name: "High performance",
    guid: "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
    type: "WINDOWS_STANDARD",
    description: "Favors performance over energy consumption across all CPU p-states.",
    cpuMin: 100,
    cpuMax: 100,
    cooling: "Active",
  },
  {
    name: "Balanced",
    guid: "381b4222-f694-41f0-9685-ff5bb260df2e",
    type: "WINDOWS_STANDARD",
    description: "Standard Windows dynamic scaling balancing performance and power usage.",
    cpuMin: 5,
    cpuMax: 100,
    cooling: "Active",
  },
  {
    name: "Power saver",
    guid: "a1841308-3541-4fab-bc81-f71556f20b4a",
    type: "WINDOWS_STANDARD",
    description: "Reduces processor performance to save energy and extend laptop battery life.",
    cpuMin: 5,
    cpuMax: 70,
    cooling: "Passive",
  },
];

export function AiOptimizationPage() {
  const connected = useIsBackendConnected();
  const appSettings = useSettings();
  // Master AI Optimization ON/OFF Switch persisted via unified settings store
  const aiMasterEnabled = appSettings.aiEnabled;
  const setAiMasterEnabled = (v: boolean) => setSettings({ aiEnabled: v });

  // Active Sub-Module Navigation (WORKLOAD OPTIMIZATION | POWER PLAN OPTIMIZATION)
  const [mod, setMod] = useState("WORKLOAD OPTIMIZATION");

  /* ────────────── 1. WORKLOAD OPTIMIZER STATE (shared, event-driven) ────────────── */
  const wl = useWorkloads();
  const wlResolved = wl.workloads;
  const wlSummary = useMemo(() => summarizeWorkloads(wlResolved), [wlResolved]);
  const activeWorkloads = wlResolved.filter((w) => w.status === "OPTIMIZED");

  /* ────────────── 2. POWER PLAN GOVERNOR STATE ────────────── */
  const [powerPlans, setPowerPlans] = useState<PowerPlanItem[]>(DEFAULT_POWER_PLANS);
  const [activePowerPlan, setActivePowerPlan] = useState<string | null>(null);
  const [activePowerPlanGuid, setActivePowerPlanGuid] = useState<string | null>(null);
  const [lastVerifiedSettings, setLastVerifiedSettings] = useState<Array<{
    name: string;
    supported: boolean;
    verified: boolean;
    currentAC?: number;
    targetAC?: number;
    currentDC?: number;
    targetDC?: number;
    description?: string;
  }> | null>(null);
  const [powerRecommendation, setPowerRecommendation] = useState<{
    planName?: string;
    planGuid?: string;
    hardware?: string;
    reason?: string;
    isOptimal?: boolean;
  } | null>(null);
  const [activatingPlan, setActivatingPlan] = useState<string | null>(null);
  const autoPowerPlan = true; // Automatic power-plan selection follows detected workloads

  const isPlanActive = (p: PowerPlanItem) => {
    if (activePowerPlanGuid && p.guid) {
      if (activePowerPlanGuid.toLowerCase() === p.guid.toLowerCase()) return true;
    }
    if (!activePowerPlan) return false;
    const cleanActive = activePowerPlan.toLowerCase().replace(/^(error optimizer ai\s*[\u2014\-:]\s*)/i, "").trim();
    const cleanP = p.name.toLowerCase().replace(/^(error optimizer ai\s*[\u2014\-:]\s*)/i, "").trim();
    return cleanActive === cleanP || activePowerPlan.toLowerCase() === p.name.toLowerCase();
  };

  const refreshPowerPlans = async (notify = false) => {
    if (!hasNativeBackend()) return;
    try {
      const [res] = await Promise.all([
        sendHostRequest<{
          activeName?: string;
          activeGuid?: string;
          plans?: Array<Record<string, unknown>>;
          recommendation?: {
            planName?: string;
            planGuid?: string;
            hardware?: string;
            reason?: string;
            isOptimal?: boolean;
          };
        }>("power:discover", undefined, 25000),
        sendHostRequest<{ readings?: unknown }>("system:scan", undefined, 25000).catch(() => null),
      ]);
      if (res?.activeName) setActivePowerPlan(res.activeName);
      if (res?.activeGuid) setActivePowerPlanGuid(res.activeGuid);
      if (res?.recommendation) {
        setPowerRecommendation(res.recommendation);
      }
      if (Array.isArray(res?.plans) && res.plans.length > 0) {
        const discovered: PowerPlanItem[] = res.plans.map((p) => {
          const name = String(p["name"] ?? p["Name"] ?? p["friendlyName"] ?? "Windows Power Plan");
          const guid = String(p["guid"] ?? p["Guid"] ?? name);
          const typeRaw = String(p["planType"] ?? p["PlanType"] ?? p["type"] ?? "");
          const type: PowerPlanItem["type"] =
            typeRaw.includes("AI") || name.includes("Error Optimizer AI")
              ? "AI_CUSTOM"
              : name.toLowerCase().includes("ultimate")
                ? "WINDOWS_ULTIMATE"
                : typeRaw.includes("CUSTOM")
                  ? "CUSTOM_USER"
                  : "WINDOWS_STANDARD";
          return {
            name,
            guid,
            type,
            description: String(p["description"] ?? p["Description"] ?? `Power scheme GUID ${guid}`),
            cpuMin: Number(p["cpuMin"] ?? 5),
            cpuMax: Number(p["cpuMax"] ?? 100),
            cooling: "Active",
          };
        });
        setPowerPlans(discovered);
      }
      if (notify) toast.success("AI Optimization & Power Plans refreshed");
    } catch (err) {
      if (notify) toast.error("Power plan scan failed", { description: err instanceof Error ? err.message : String(err) });
    }
  };

  const scan = usePageScan("ai-optimization", async () => {
    await Promise.all([
      refreshPowerPlans(false),
      workloadActions.rescan().catch(() => undefined),
    ]);
  });

  useEffect(() => {
    if (connected) void refreshPowerPlans(false);
  }, [connected]);

  // Re-sync actual Windows power plan when active workloads transition
  const prevOptimized = useRef(activeWorkloads.length);
  useEffect(() => {
    const prev = prevOptimized.current;
    prevOptimized.current = activeWorkloads.length;
    if (!connected || !autoPowerPlan || prev === activeWorkloads.length) return;
    void refreshPowerPlans(false);
  }, [activeWorkloads.length, autoPowerPlan, connected]);

  /* Modal state for creating power plan */
  const [planModalOpen, setPlanModalOpen] = useState(false);
  const [newPlanName, setNewPlanName] = useState("EO Custom Performance");
  const [newPlanBaseScheme, setNewPlanBaseScheme] = useState("High performance");
  const [newPlanCpuMin, setNewPlanCpuMin] = useState(100);
  const [newPlanCpuMax, setNewPlanCpuMax] = useState(100);
  const [newPlanCooling, setNewPlanCooling] = useState<"Active" | "Passive">("Active");

  /* Handlers */
  const handleRefresh = () => {
    void refreshPowerPlans(true);
  };

  const handleActivatePowerPlan = async (p: string) => {
    setActivatingPlan(p);
    try {
      const pLower = p.toLowerCase();
      interface PowerActivationResponse {
        success?: boolean;
        verified?: boolean;
        activeSchemeVerified?: boolean;
        settingsTotal?: number;
        settingsVerified?: number;
        settingsUnsupported?: number;
        settingsFailed?: number;
        name?: string;
        guid?: string;
        message?: string;
        verifiedSettings?: Array<{
          name: string;
          supported: boolean;
          verified: boolean;
          currentAC?: number;
          targetAC?: number;
          currentDC?: number;
          targetDC?: number;
          description?: string;
        }>;
      }
      let res: PowerActivationResponse | null = null;

      if (pLower.includes("ai max") || pLower.includes("a1b00001")) {
        res = await sendHostRequest<PowerActivationResponse>(
          "power:create-ai-max",
          undefined,
          30000
        );
      } else if (pLower.includes("ai battery") || pLower.includes("a1b00002")) {
        res = await sendHostRequest<PowerActivationResponse>(
          "power:create-ai-battery",
          undefined,
          30000
        );
      } else if (pLower.includes("ultimate") || pLower.includes("e9a42b02")) {
        res = await sendHostRequest<PowerActivationResponse>(
          "power:enable-ultimate",
          undefined,
          30000
        );
      } else if (pLower === "balanced" || pLower.includes("381b4222")) {
        res = await sendHostRequest<PowerActivationResponse>(
          "power:restore-balanced",
          undefined,
          30000
        );
      } else {
        const matched = powerPlans.find(
          (x) => x.guid.toLowerCase() === pLower || x.name.toLowerCase() === pLower
        );
        const targetGuid = matched?.guid && matched.guid.includes("-") ? matched.guid : p;
        res = await sendHostRequest<PowerActivationResponse>(
          "power:apply",
          { guid: targetGuid },
          30000
        );
      }

      if (!res || !res.success || !res.verified) {
        toast.error(`Failed to activate power plan`, {
          description: res?.message || "Windows rejected power scheme activation or readback verification failed.",
        });
        await refreshPowerPlans(false);
        return;
      }

      const activeName = res.name || p;
      const activeGuid = res.guid || "";
      setActivePowerPlan(activeName);
      if (activeGuid) setActivePowerPlanGuid(activeGuid);

      if (res.verifiedSettings && res.verifiedSettings.length > 0) {
        setLastVerifiedSettings(res.verifiedSettings);
      }

      const totalSettings = res.settingsTotal ?? 0;
      const verifiedSettings = res.settingsVerified ?? 0;
      const unsupportedSettings = res.settingsUnsupported ?? 0;

      const desc = totalSettings > 0
        ? `Windows verified active plan '${activeName}'. ${verifiedSettings}/${totalSettings} settings confirmed${unsupportedSettings > 0 ? ` (${unsupportedSettings} unsupported by firmware/build)` : ""}.`
        : `Windows verified ${activeName} (${activeGuid}) as active.`;

      toast.success(`Power Plan Activated: ${activeName}`, {
        description: desc,
      });

      await refreshPowerPlans(false);
    } catch (err) {
      toast.error(`Power plan activation error`, {
        description: err instanceof Error ? err.message : String(err),
      });
      await refreshPowerPlans(false);
    } finally {
      setActivatingPlan(null);
    }
  };

  const handleCreatePowerPlan = async () => {
    if (!newPlanName.trim()) return;
    try {
      const res = (await sendHostRequest("power:create-ai-max", {
        name: newPlanName.trim(),
        baseScheme: newPlanBaseScheme,
        cpuMin: newPlanCpuMin,
        cpuMax: newPlanCpuMax,
        cooling: newPlanCooling,
      }, 30000)) as { success?: boolean; verified?: boolean; name?: string; guid?: string; message?: string };
      setPlanModalOpen(false);
      if (res?.success && res?.verified) {
        toast.success("Power Plan Created & Activated", {
          description: `Enforced ${res?.name ?? newPlanName.trim()} (${newPlanCpuMin}%-${newPlanCpuMax}% CPU state).`,
        });
      } else {
        toast.error("Power plan creation failed", {
          description: res?.message ?? "Scheme creation or activation was rejected by Windows.",
        });
      }
      await refreshPowerPlans(false);
    } catch (err) {
      toast.error("Power plan creation failed", { description: err instanceof Error ? err.message : String(err) });
    }
  };

  // Dynamic auto actions count
  const autoActionCount =
    (aiMasterEnabled ? 1 : 0) + wlSummary.autoActions + (autoPowerPlan ? 1 : 0);

  return (
    <div className="space-y-4">
      {/* 1. Page Header */}
      <PageHeader
        title="AI Optimization"
        badges={
          <>
            <Badge tone="cyan">2 MODULES</Badge>
            <StatusIndicator
              tone={!aiMasterEnabled ? "neg" : connected ? "pos" : "warn"}
              size="sm"
              live
              reserve="AWAITING BACKEND"
            >
              {!aiMasterEnabled ? "AI PAUSED" : connected ? "AI READY" : "AWAITING BACKEND"}
            </StatusIndicator>
          </>
        }
        sub="Automatic workload optimization and Windows power scheme governance."
        scan={scan}
      />

      {/* 2. Top Navigation Toolbar */}
      <Card className="flex flex-wrap items-center justify-between gap-3 py-3">
        <Segmented
          label="AI module"
          options={["WORKLOAD OPTIMIZATION", "POWER PLAN OPTIMIZATION"]}
          value={mod}
          onChange={setMod}
        />
        <div className="flex items-center gap-3">
          <Btn size="sm" variant="ghost" onClick={handleRefresh}>
            Refresh
          </Btn>
          <div className="flex items-center gap-2 pl-3 border-l border-border">
            <Label className="text-[10.5px] font-bold tracking-wider text-muted-foreground">
              AUTO OPTIMIZE
            </Label>
            <Toggle
              label="Master Auto Optimize"
              on={appSettings.autoOptimizationEnabled}
              onChange={() => void toggleAutoOptimization()}
            />
          </div>
          <div className="flex items-center gap-2 pl-3 border-l border-border">
            <Label className="text-[10.5px] font-bold tracking-wider text-muted-foreground">
              AI DECISION LAYER
            </Label>
            <Toggle
              label="Master AI Intelligence"
              on={appSettings.aiOptimizationEnabled}
              onChange={() => void toggleAiOptimization()}
            />
          </div>
        </div>
      </Card>

      {/* Auto Optimize & AI Decision Layer Explanation */}
      <Card className="p-3 border border-border/50 bg-card/60">
        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2">
          <div className="min-w-0">
            <div className="flex items-center gap-2 mb-0.5">
              <span className="text-[12px] font-bold uppercase tracking-wider text-foreground">
                Autonomous Intelligence Layer
              </span>
              <Badge tone={appSettings.autoOptimizationEnabled ? "cyan" : "muted"}>
                {appSettings.autoOptimizationEnabled ? "AUTO OPTIMIZE: ACTIVE" : "AUTO OPTIMIZE: DISABLED"}
              </Badge>
              <Badge tone={appSettings.aiOptimizationEnabled ? "success" : "muted"}>
                {appSettings.aiOptimizationEnabled ? "AI ADAPTATION: ON" : "AI ADAPTATION: OFF"}
              </Badge>
            </div>
            <p className="text-[11px] text-muted-foreground">
              {appSettings.aiOptimizationEnabled
                ? "AI works as the adaptive decision engine of Auto Optimize—evaluating real-time hardware capabilities, CPU/GPU workload context, and thermals to prevent redundant or incompatible tweaks."
                : "AI adaptive decision-making is paused. Auto Optimize runs according to static scheduled rules without real-time workload profiling."}
            </p>
          </div>
        </div>
      </Card>

      {/* 3. StatTiles */}
      <StatTiles
        items={[
          { label: "ACTIVE WORKLOADS", value: connected ? activeWorkloads.length : "—" },
          { label: "AUTO ACTIONS", value: connected ? autoActionCount : "—" },
          {
            label: "BACKGROUND APPS",
            value: connected ? wlResolved.filter((w) => !w.foreground).length : "—",
          },
          { label: "SAFE", value: connected ? wlSummary.safe : "—", tone: "success" },
          { label: "MEDIUM RISK", value: connected ? wlSummary.medium : "—", tone: "warning" },
          { label: "HIGH RISK", value: connected ? wlSummary.high : "—", tone: "danger" },
        ]}
      />

      {/* 4. Sub-Module Status Banner */}
      <Card className="flex flex-wrap items-center justify-between gap-3 py-3">
        <div className="text-[12px] text-muted-foreground">
          <span className="font-semibold text-foreground">{mod}</span> · Real-time priority tuning · Intelligent governance
        </div>
        <StatusIndicator
          tone={!aiMasterEnabled ? "neg" : connected ? "pos" : "warn"}
          size="sm"
          reserve="AWAITING BACKEND"
        >
          {!aiMasterEnabled ? "AI PAUSED" : connected ? "AI READY" : "AWAITING BACKEND"}
        </StatusIndicator>
      </Card>

      {/* 5. Dynamic Module Content */}
      <div
        key={mod}
        className={cn(
          "eo-swap space-y-4 transition-opacity duration-200",
          !aiMasterEnabled && "opacity-60 pointer-events-none",
        )}
      >
        {/* ── SUB-OPTIMIZATION A: WORKLOAD OPTIMIZATION ── */}
        {mod === "WORKLOAD OPTIMIZATION" && <WorkloadOptimizationPanel />}

        {/* ── SUB-OPTIMIZATION C: POWER PLAN OPTIMIZATION ── */}
        {mod === "POWER PLAN OPTIMIZATION" && (
          <>
            <Card>
              <Label className="mb-3 text-cyan">AI POWER RECOMMENDATION</Label>
              <div className="grid grid-cols-4 gap-3.5">
                {[
                  [
                    "RECOMMENDED PLAN:",
                    powerRecommendation?.planName ?? (connected ? "Error Optimizer AI — Max Performance" : "—"),
                  ],
                  [
                    "HARDWARE:",
                    powerRecommendation?.hardware ?? (connected ? "Detected host profile" : "Waiting for system data"),
                  ],
                  [
                    "WORKLOAD:",
                    connected
                      ? activeWorkloads.length > 0
                        ? activeWorkloads.map((w) => w.name).join(", ")
                        : "Idle / Multitasking"
                      : "Awaiting detection",
                  ],
                  ["CURRENT ACTIVE:", activePowerPlan ?? "Not detected"],
                ].map(([k, v]) => (
                  <div key={k}>
                    <Label className="text-[10px]">{k}</Label>
                    <div className="text-[13px] font-semibold">{v}</div>
                  </div>
                ))}
              </div>
              <div className="mt-4 flex flex-wrap gap-2">
                <Btn variant="primary" onClick={() => setPlanModalOpen(true)}>
                  + Create power plan
                </Btn>
                <Btn
                  loading={activatingPlan === "Balanced"}
                  onClick={() => handleActivatePowerPlan("Balanced")}
                >
                  Restore Balanced
                </Btn>
                <Btn variant="ghost" onClick={handleRefresh}>
                  Refresh
                </Btn>
              </div>
            </Card>

            <Card>
              <div className="mb-3 flex items-start justify-between">
                <div>
                  <div className="flex items-center gap-2">
                    <Label className="text-foreground">ERROR OPTIMIZER AI</Label>
                    <Badge tone="cyan">MACHINE-SPECIFIC POLICIES</Badge>
                  </div>
                  <p className="mt-1 text-xs text-muted-foreground">
                    Calibrated AI power schemes designed for balanced thermal headroom and latency
                    reduction.
                  </p>
                </div>
                <div className="flex flex-wrap gap-2">
                  <Btn
                    size="sm"
                    loading={activatingPlan === "AI Battery Efficiency"}
                    onClick={() => handleActivatePowerPlan("AI Battery Efficiency")}
                  >
                    AI battery efficiency
                  </Btn>
                  <Btn
                    size="sm"
                    variant="primary"
                    loading={activatingPlan === "AI Max Performance"}
                    onClick={() => handleActivatePowerPlan("AI Max Performance")}
                  >
                    AI max performance
                  </Btn>
                </div>
              </div>
              {lastVerifiedSettings && lastVerifiedSettings.length > 0 && (
                <div className="mt-3 border-t border-border/50 pt-2.5">
                  <div className="flex items-center justify-between text-xs mb-1.5">
                    <span className="text-muted-foreground font-medium">INDEPENDENT SETTING VERIFICATION</span>
                    <span className="font-semibold text-cyan text-[11px]">
                      {lastVerifiedSettings.filter((s) => s.verified).length} / {lastVerifiedSettings.length} Confirmed
                    </span>
                  </div>
                  <div className="flex flex-wrap gap-1.5">
                    {lastVerifiedSettings.map((s) => (
                      <Badge
                        key={s.name}
                        tone={!s.supported ? "warning" : s.verified ? "success" : "danger"}
                        className="text-[9.5px]"
                      >
                        {s.name}: {!s.supported ? "UNSUPPORTED" : s.verified ? "VERIFIED" : "MISMATCH"}
                      </Badge>
                    ))}
                  </div>
                </div>
              )}
            </Card>

            {/* All Supported Power Plans */}
            <Card>
              <div className="mb-3 flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <Label className="text-foreground">SUPPORTED POWER PLANS</Label>
                  <GuideTip id="ai.power" />
                  <Badge tone={connected && autoPowerPlan ? "cyan" : "muted"}>
                    {connected && autoPowerPlan ? "AUTO GOVERNOR ACTIVE" : "AWAITING BACKEND"}
                  </Badge>
                </div>
                <span className="text-xs text-muted-foreground">
                  Select any plan to manually enforce
                </span>
              </div>
              <div className="space-y-1.5">
                {powerPlans.map((p) => {
                  const isActive = isPlanActive(p);
                  const isActivating = activatingPlan === p.guid || activatingPlan === p.name;
                  const isRecommended =
                    (powerRecommendation?.planName && p.name.toLowerCase().includes(powerRecommendation.planName.toLowerCase())) ||
                    (powerRecommendation?.planGuid && p.guid.toLowerCase() === powerRecommendation.planGuid.toLowerCase());
                  return (
                    <div
                      key={p.guid || p.name}
                      className={cn(
                        "flex items-center justify-between eo-list-row rounded-lg border bg-window px-3 py-2.5 text-[12.5px]",
                        isActive
                          ? "border-cyan/60 bg-cyan/5 shadow-sm shadow-cyan/10"
                          : "border-border",
                      )}
                    >
                      <div>
                        <div className="flex items-center gap-2">
                          <span className="font-semibold">{p.name}</span>
                          <Badge
                            tone={
                              p.type === "AI_CUSTOM"
                                ? "cyan"
                                : p.type === "WINDOWS_ULTIMATE"
                                  ? "primary"
                                  : p.type === "CUSTOM_USER"
                                    ? "warning"
                                    : "muted"
                            }
                          >
                            {p.type.replace("_", " ")}
                          </Badge>
                          {isRecommended && (
                            <Badge tone="cyan">RECOMMENDED</Badge>
                          )}
                        </div>
                        <p className="text-[11px] text-muted-foreground mt-0.5">{p.description}</p>
                      </div>
                      {isActive ? (
                        <StatusIndicator tone="pos" size="sm">
                          ACTIVE
                        </StatusIndicator>
                      ) : (
                        <Btn
                          size="sm"
                          variant="ghost"
                          loading={isActivating}
                          onClick={() => handleActivatePowerPlan(p.guid || p.name)}
                        >
                          Activate
                        </Btn>
                      )}
                    </div>
                  );
                })}
              </div>
            </Card>
          </>
        )}
      </div>

      {/* Modal: Create Power Plan */}
      <Modal
        open={planModalOpen}
        onClose={() => setPlanModalOpen(false)}
        title="Create new custom Windows power plan"
        footer={
          <>
            <Btn variant="ghost" onClick={() => setPlanModalOpen(false)}>
              Cancel
            </Btn>
            <Btn variant="primary" onClick={handleCreatePowerPlan}>
              Create & verify in Windows
            </Btn>
          </>
        }
      >
        <div className="space-y-3">
          <Cols cols="1fr 1fr">
            <div>
              <Label className="mb-1">PLAN NAME</Label>
              <input
                className="h-9 w-full rounded-lg border border-border bg-window px-3 text-sm focus:outline-none focus:border-cyan"
                value={newPlanName}
                onChange={(e) => setNewPlanName(e.target.value)}
                placeholder="e.g. EO Custom Gaming Plan"
              />
            </div>
            <div>
              <Label className="mb-1">DUPLICATE REAL BASE SCHEME</Label>
              <PremiumSelect
                label="Duplicate real base scheme"
                value={newPlanBaseScheme}
                onChange={setNewPlanBaseScheme}
                options={["High performance", "Balanced", "Ultimate Performance"]}
              />
            </div>
          </Cols>

          <Cols cols="1fr 1fr">
            <div>
              <div className="flex items-center justify-between mb-1">
                <Label>MIN PROCESSOR STATE</Label>
                <span className="font-mono text-xs text-cyan">{newPlanCpuMin}%</span>
              </div>
              <RangeSlider
                id="eo-new-plan-min"
                label="Minimum processor state"
                value={newPlanCpuMin}
                onChange={setNewPlanCpuMin}
                min={5}
                max={100}
                step={5}
                valueText={`${newPlanCpuMin}%`}
              />
            </div>
            <div>
              <div className="flex items-center justify-between mb-1">
                <Label>MAX PROCESSOR STATE</Label>
                <span className="font-mono text-xs text-cyan">{newPlanCpuMax}%</span>
              </div>
              <RangeSlider
                id="eo-new-plan-max"
                label="Maximum processor state"
                value={newPlanCpuMax}
                onChange={setNewPlanCpuMax}
                min={50}
                max={100}
                step={5}
                valueText={`${newPlanCpuMax}%`}
              />
            </div>
          </Cols>

          <div>
            <Label className="mb-1">SYSTEM COOLING POLICY</Label>
            <PremiumSelect
              label="System Cooling Policy"
              value={newPlanCooling}
              onChange={(v) => setNewPlanCooling(v as "Active" | "Passive")}
              options={["Active", "Passive"]}
            />
            <p className="text-[11px] text-muted-foreground mt-1">
              Active cooling increases fan speed before slowing the processor. Supported natively
              via Windows powercfg.
            </p>
          </div>
        </div>
      </Modal>
    </div>
  );
}

/* ───────── BACKUP & RESTORE ───────── */
interface LiveTxItem {
  id: string;
  name: string;
  cat: string;
  module: string;
  at: string;
  restorable: boolean;
  statusText: string;
  beforeState: string;
  afterState: string;
  targetKey: string;
  rollbackMethod: string;
  isV4Snapshot?: boolean;
  v4BatchId?: string;
}

interface ProfileContainerItem {
  profileName: string;
  displayName: string;
  totalTransactions: number;
  restorableTransactions: number;
  lastBackupTime: string | null;
}

const TX_FILTERS = [
  "ALL",
  "REGISTRY",
  "POWER",
  "INPUT",
  "NETWORK",
  "STORAGE",
  "MEMORY",
  "SERVICES",
  "REPAIR",
  "✓ RESTORABLE",
  "NON-REVERSIBLE",
];

export type BackupPageState =
  | "INITIAL_LOADING"
  | "LOADING_METADATA"
  | "LOADING_MORE"
  | "READY"
  | "EMPTY"
  | "PARTIAL_RESULTS"
  | "ACCESS_DENIED"
  | "CORRUPTED_METADATA"
  | "BACKEND_UNAVAILABLE"
  | "FAILED"
  | "CANCELLED";

function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  return `${(bytes / Math.pow(1024, i)).toFixed(1)} ${units[i]}`;
}

export function BackupRestorePage() {
  const connected = useIsBackendConnected();
  const [pageState, setPageState] = useState<BackupPageState>("INITIAL_LOADING");
  const [query, setQuery] = useState("");
  const [f, setF] = useState("ALL");
  const [displayLimit, setDisplayLimit] = useState(40);
  const [d, setD] = useState<LiveTxItem | null>(null);
  const [restore, setRestore] = useState(false);
  const [restoreTarget, setRestoreTarget] = useState<LiveTxItem | null>(null);
  const [restoreProfileTarget, setRestoreProfileTarget] = useState<ProfileContainerItem | null>(null);
  const [createRp, setCreateRp] = useState(false);
  const [transactions, setTransactions] = useState<LiveTxItem[]>([]);
  const [profileContainers, setProfileContainers] = useState<ProfileContainerItem[]>([]);
  const [statsView, setStatsView] = useState<{
    totalBackups: number | null;
    profiles: number | null;
    optimizations: number | null;
    registryValues: number | null;
    restorable: number | null;
    nonReversible: number | null;
  }>({
    totalBackups: null,
    profiles: null,
    optimizations: null,
    registryValues: null,
    restorable: null,
    nonReversible: null,
  });
  const [rpCount, setRpCount] = useState<number | null>(null);
  const [latestRp, setLatestRp] = useState<{ name: string; at: string } | null>(null);
  const [protectionOn, setProtectionOn] = useState<boolean | null>(null);
  const [protDrive, setProtDrive] = useState<string>(SYSTEM_DRIVE.label);
  const [protDetail, setProtDetail] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Multi-selection state
  const [selectionMode, setSelectionMode] = useState(false);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());

  // Deletion modals & progress state
  const [confirmDeleteSelected, setConfirmDeleteSelected] = useState(false);
  const [confirmDeleteAll, setConfirmDeleteAll] = useState(false);
  const [isDeleting, setIsDeleting] = useState(false);
  const [deleteProgress, setDeleteProgress] = useState<{
    stage: string;
    percent?: number;
  } | null>(null);

  const rpState = useRestoreState();
  const activeReqId = useRef(0);

  useEffect(() => {
    return () => {
      activeReqId.current = 0;
    };
  }, []);

  useEffect(() => {
    setDisplayLimit(40);
  }, [query, f]);

  const loadBackups = async (notify = false) => {
    if (!hasNativeBackend()) {
      setPageState("BACKEND_UNAVAILABLE");
      return;
    }
    const currentReqId = ++activeReqId.current;
    setBusy(true);
    setLoadError(null);
    setPageState((cur) => (cur === "READY" || cur === "EMPTY" || cur === "PARTIAL_RESULTS" ? "LOADING_METADATA" : "INITIAL_LOADING"));

    const tStart = performance.now();

    // Query Windows System Restore points asynchronously without blocking backup manifest enumeration
    void sendHostRequest<Array<Record<string, unknown>>>("restorepoint:list", undefined, 8000)
      .then((rpRes) => {
        if (activeReqId.current !== currentReqId) return;
        if (Array.isArray(rpRes)) {
          setRpCount(rpRes.length);
          const first = rpRes[0];
          if (first && typeof first === "object") {
            setLatestRp({
              name: String(first["description"] ?? first["Description"] ?? "Restore Point"),
              at: String(first["creationTime"] ?? first["CreationTime"] ?? ""),
            });
          } else {
            setLatestRp(null);
          }
        }
      })
      .catch(() => {
        /* non-fatal: System Restore query timed out or disabled */
      });

    void sendHostRequest<Record<string, unknown>>("restorepoint:status", undefined, 6000)
      .then((statusRes) => {
        if (activeReqId.current !== currentReqId) return;
        if (statusRes) {
          const enabled = statusRes["isEnabled"] ?? statusRes["protectionEnabled"] ?? statusRes["enabled"];
          if (typeof enabled === "boolean") setProtectionOn(enabled);
          const drv = statusRes["drive"] ?? statusRes["SystemDrive"];
          if (typeof drv === "string" && drv.trim()) setProtDrive(drv.trim());
          const rsn = statusRes["reason"] ?? statusRes["StatusReason"];
          if (typeof rsn === "string" && rsn.trim()) setProtDetail(rsn.trim());
        }
      })
      .catch(() => {
        /* non-fatal: protection status query timed out */
      });

    try {
      const bkRes = await sendHostRequest<Record<string, unknown>>("backup:list", undefined, 15000);
      if (activeReqId.current !== currentReqId) return;
      const tIpc = performance.now();

      const rawTx = (
        Array.isArray(bkRes?.["transactions"])
          ? (bkRes["transactions"] as Array<Record<string, unknown>>)
          : []
      ).filter((t) => {
        const op = String(t["operationType"] ?? t["OperationType"] ?? "").toLowerCase();
        const rb = String(t["rollbackMethod"] ?? t["RollbackMethod"] ?? "").toLowerCase();
        return op !== "systemrestorepoint" && rb !== "systemrestorepoint";
      });
      const rawV4 = Array.isArray(bkRes?.["v4Backups"])
        ? (bkRes["v4Backups"] as Array<Record<string, unknown>>)
        : [];
      const rawV4Tx = Array.isArray(bkRes?.["v4Transactions"])
        ? (bkRes["v4Transactions"] as Array<Record<string, unknown>>)
        : [];

      const mapped: LiveTxItem[] = rawTx.map((t, i) => {
        const canRb = Boolean(t["canRollback"] ?? t["CanRollback"]);
        const rbStatus = String(t["rollbackStatus"] ?? t["RollbackStatus"] ?? (canRb ? "AVAILABLE" : "NOT_SUPPORTED")).toUpperCase();
        return {
          id: String(t["transactionId"] ?? t["TransactionId"] ?? `tx-${i}`),
          name: String(t["optimizationName"] ?? t["OptimizationName"] ?? "Optimization"),
          cat: String(t["category"] ?? t["Category"] ?? "REGISTRY").toUpperCase(),
          module: String(t["profileName"] ?? t["ProfileName"] ?? "Manual"),
          at: String(t["displayTimestamp"] ?? t["DisplayTimestamp"] ?? t["timestamp"] ?? ""),
          restorable: canRb && rbStatus !== "RESTORED" && rbStatus !== "NOT_SUPPORTED",
          statusText: rbStatus,
          beforeState: String(t["beforeState"] ?? t["BeforeState"] ?? "Captured value"),
          afterState: String(t["afterState"] ?? t["AfterState"] ?? "Applied value"),
          targetKey: String(t["targetKey"] ?? t["TargetKey"] ?? ""),
          rollbackMethod: String(t["rollbackMethod"] ?? t["RollbackMethod"] ?? "Snapshot restore"),
        };
      });

      for (const v4 of rawV4) {
        const snapId = String(v4["id"] ?? v4["Id"] ?? "");
        if (!snapId) continue;
        const targetName = String(v4["target"] ?? v4["Target"] ?? v4["component"] ?? v4["Component"] ?? `V4 Snapshot ${snapId}`);
        const comp = String(v4["component"] ?? v4["Component"] ?? "V4 Emulator");
        const createdRaw = String(v4["createdUtc"] ?? v4["CreatedUtc"] ?? "");
        const atStr = createdRaw ? new Date(createdRaw).toLocaleString() : "";
        mapped.push({
          id: snapId,
          name: targetName,
          cat: "SNAPSHOT",
          module: comp,
          at: atStr,
          restorable: true,
          statusText: "AVAILABLE",
          beforeState: "Pre-optimization file/config state",
          afterState: "Modified",
          targetKey: String(v4["sourcePath"] ?? v4["SourcePath"] ?? "V4 Snapshot Container"),
          rollbackMethod: "BackupStore File Snapshot",
          isV4Snapshot: true,
        });
      }

      let v4RegCount = 0;
      let v4RestorableTxCount = 0;
      let v4NonRevTxCount = 0;
      for (const vtx of rawV4Tx) {
        const txId = String(vtx["transactionId"] ?? vtx["TransactionId"] ?? "");
        const batchId = String(vtx["batchId"] ?? vtx["BatchId"] ?? "");
        const restored = Boolean(vtx["restored"] ?? vtx["Restored"]);
        if (!txId) continue;
        const cat = String(vtx["targetType"] ?? vtx["TargetType"] ?? "REGISTRY").toUpperCase();
        const isRestorable = !restored && Boolean(batchId);
        if (cat === "REGISTRY") v4RegCount++;
        if (isRestorable) v4RestorableTxCount++;
        else v4NonRevTxCount++;
        mapped.push({
          id: `v4tx-${txId}`,
          name: String(vtx["targetIdentifier"] ?? vtx["TargetIdentifier"] ?? "V4 Transaction"),
          cat,
          module: batchId ? `V4 Batch (${batchId})` : "V4 Engine",
          at: vtx["timestamp"] ? new Date(String(vtx["timestamp"])).toLocaleString() : "",
          restorable: isRestorable,
          statusText: restored ? "RESTORED" : "AVAILABLE",
          beforeState: String(vtx["previousState"] ?? vtx["PreviousState"] ?? "Previous state"),
          afterState: String(vtx["newState"] ?? vtx["NewState"] ?? "New state"),
          targetKey: String(vtx["targetIdentifier"] ?? vtx["TargetIdentifier"] ?? ""),
          rollbackMethod: "V4 Transaction Log",
          isV4Snapshot: Boolean(batchId),
          ...(batchId ? { v4BatchId: batchId } : {}),
        });
      }

      const tMap = performance.now();
      console.debug(`[BackupAudit] Backups ready in ${(tMap - tStart).toFixed(1)}ms (IPC: ${(tIpc - tStart).toFixed(1)}ms, Map: ${(tMap - tIpc).toFixed(1)}ms) - total items: ${mapped.length}`);

      setTransactions(mapped);

      const rawGroups = Array.isArray(bkRes?.["profileGroups"])
        ? (bkRes["profileGroups"] as Array<Record<string, unknown>>)
        : [];
      const activeContainers: ProfileContainerItem[] = rawGroups
        .map((g) => ({
          profileName: String(g["profileName"] ?? g["ProfileName"] ?? ""),
          displayName: String(g["displayName"] ?? g["DisplayName"] ?? g["profileName"] ?? "Profile"),
          totalTransactions: Number(g["totalTransactions"] ?? g["TotalTransactions"] ?? 0),
          restorableTransactions: Number(g["restorableTransactions"] ?? g["RestorableTransactions"] ?? 0),
          lastBackupTime: g["lastBackupTime"]
            ? new Date(String(g["lastBackupTime"])).toLocaleString()
            : g["LastBackupTime"]
              ? new Date(String(g["LastBackupTime"])).toLocaleString()
              : null,
        }))
        .filter((g) => g.totalTransactions > 0);
      setProfileContainers(activeContainers);

      const stats = (bkRes?.["stats"] ?? {}) as Record<string, unknown>;
      const numOr = (k1: string, k2: string, fb: number) => {
        const v = stats[k1] ?? stats[k2];
        return typeof v === "number" && Number.isFinite(v) ? v : fb;
      };
      const baseTotal = numOr("totalBackups", "TotalBackups", rawTx.length);
      const baseRestorable = numOr("restorableCount", "RestorableCount", mapped.filter((t) => !t.isV4Snapshot && t.restorable).length);
      const baseNonRev = numOr("nonReversibleCount", "NonReversibleCount", mapped.filter((t) => !t.isV4Snapshot && !t.restorable).length);
      const baseOptimizations = numOr("optimizationBackups", "OptimizationBackups", rawTx.length);
      const baseRegistry = numOr("registryBackups", "RegistryBackups", 0);

      setStatsView({
        totalBackups: baseTotal + rawV4.length + rawV4Tx.length,
        profiles: activeContainers.length > 0 ? activeContainers.length : numOr("profileBackups", "ProfileBackups", 0),
        optimizations: baseOptimizations + rawV4Tx.length,
        registryValues: baseRegistry + v4RegCount,
        restorable: baseRestorable + rawV4.length + v4RestorableTxCount,
        nonReversible: baseNonRev + v4NonRevTxCount,
      });

      if (mapped.length === 0) {
        setPageState("EMPTY");
      } else {
        setPageState("READY");
      }

      if (notify) toast.success("Backups refreshed");
    } catch (err) {
      if (activeReqId.current !== currentReqId) return;
      const msg = err instanceof Error ? err.message : String(err);
      setLoadError(msg);
      const lower = msg.toLowerCase();
      if (lower.includes("access") || lower.includes("denied") || lower.includes("unauthorized")) {
        setPageState("ACCESS_DENIED");
      } else if (lower.includes("corrupt") || lower.includes("json") || lower.includes("deserial")) {
        setPageState("CORRUPTED_METADATA");
      } else if (
        lower.includes("disconnect") ||
        lower.includes("pipe") ||
        lower.includes("refused") ||
        lower.includes("unavailable") ||
        lower.includes("not connected")
      ) {
        setPageState("BACKEND_UNAVAILABLE");
      } else {
        setPageState("FAILED");
      }
      if (notify) toast.error("Failed to load backups", { description: msg });
    } finally {
      if (activeReqId.current === currentReqId) {
        setBusy(false);
      }
    }
  };

  const openTransactionDetails = (t: LiveTxItem) => {
    setD(t);
    if (!hasNativeBackend() || t.v4BatchId) return;
    const payload = t.isV4Snapshot ? { backupId: t.id } : { transactionId: t.id };
    void sendHostRequest<{ valid?: boolean; Valid?: boolean; reason?: string; Reason?: string }>(
      "backup:validate",
      payload,
      15000,
    )
      .then((res) => {
        const isValid = res?.valid === true || res?.Valid === true;
        const reason = String(res?.reason ?? res?.Reason ?? "").trim();
        if (!isValid) {
          const failLabel = reason ? `INTEGRITY FAILED: ${reason}` : "INTEGRITY FAILED";
          setD((cur) => (cur && cur.id === t.id ? { ...cur, restorable: false, statusText: failLabel } : cur));
          setTransactions((prev) =>
            prev.map((row) => (row.id === t.id ? { ...row, restorable: false, statusText: failLabel } : row)),
          );
        }
      })
      .catch(() => {
        /* preserve loaded state if validation RPC is transient */
      });
  };

  const scan = usePageScan("backup-restore", () => loadBackups(false));

  const executeRestore = async () => {
    setRestore(false);
    setBusy(true);
    try {
      if (restoreProfileTarget) {
        const res = (await sendHostRequest(
          "backup:restore-profile",
          { profileName: restoreProfileTarget.profileName },
          90000,
        )) as { success?: boolean; Success?: boolean; message?: string; Message?: string };
        if (res?.success === false || res?.Success === false) {
          throw new Error(res.message ?? res.Message ?? "Profile restore failed.");
        }
        toast.success("Profile container restored & verified", {
          description: `${restoreProfileTarget.displayName} (${res?.message ?? res?.Message ?? "completed"})`,
        });
      } else if (restoreTarget) {
        if (!restoreTarget.v4BatchId) {
          const valPayload = restoreTarget.isV4Snapshot
            ? { backupId: restoreTarget.id }
            : { transactionId: restoreTarget.id };
          const valRes = (await sendHostRequest("backup:validate", valPayload, 20000)) as {
            valid?: boolean;
            Valid?: boolean;
            reason?: string;
            Reason?: string;
          };
          if (valRes?.valid !== true && valRes?.Valid !== true) {
            throw new Error(
              valRes?.reason ?? valRes?.Reason ?? "Pre-restore backup integrity validation failed.",
            );
          }
        }
        if (restoreTarget.isV4Snapshot) {
          const payload = restoreTarget.v4BatchId
            ? { batchId: restoreTarget.v4BatchId }
            : { backupId: restoreTarget.id };
          const snapRes = (await sendHostRequest("backup:restore-snapshot", payload, 60000)) as {
            success?: boolean;
            Success?: boolean;
            message?: string;
            Message?: string;
          };
          if (snapRes?.success === false || snapRes?.Success === false) {
            throw new Error(snapRes.message ?? snapRes.Message ?? "Snapshot restore failed.");
          }
        } else {
          const res = (await sendHostRequest(
            "backup:restore-transaction",
            { transactionId: restoreTarget.id },
            60000,
          )) as { success?: boolean; Success?: boolean; message?: string; Message?: string };
          if (res?.success === false || res?.Success === false) {
            throw new Error(res.message ?? res.Message ?? "Transaction restore failed.");
          }
        }
        toast.success("Backup restored & verified", { description: restoreTarget.name });
      } else {
        const res = (await sendHostRequest("backup:restore-full", undefined, 120000)) as {
          success?: boolean;
          Success?: boolean;
          message?: string;
          Message?: string;
        };
        if (res?.success === false || res?.Success === false) {
          throw new Error(res.message ?? res.Message ?? "Full system restore failed.");
        }
        toast.success("System rollback completed & verified", {
          description: res?.message ?? res?.Message ?? "All restorable transactions reverted.",
        });
      }
      await loadBackups(false);
    } catch (err) {
      toast.error("Backup restore failed", { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setRestoreTarget(null);
      setRestoreProfileTarget(null);
      setBusy(false);
    }
  };

  const toggleSelect = (id: string, e?: React.MouseEvent) => {
    if (e) e.stopPropagation();
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const vis = useMemo(() => {
    const q = query.trim().toLowerCase();
    return transactions.filter(
      (t) =>
        (!q ||
          t.name.toLowerCase().includes(q) ||
          t.module.toLowerCase().includes(q) ||
          t.targetKey.toLowerCase().includes(q)) &&
        (f === "ALL" ||
          t.cat === f ||
          (f === "✓ RESTORABLE" && t.restorable) ||
          (f === "NON-REVERSIBLE" && !t.restorable)),
    );
  }, [transactions, query, f]);

  const handleSelectAllVisible = () => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      vis.forEach((t) => next.add(t.id));
      return next;
    });
  };

  const handleSelectAll = () => {
    setSelectedIds(new Set(transactions.map((t) => t.id)));
  };

  const handleDeselectAll = () => {
    setSelectedIds(new Set());
  };

  const isAllVisibleSelected = vis.length > 0 && vis.every((t) => selectedIds.has(t.id));
  const isSomeVisibleSelected = !isAllVisibleSelected && vis.some((t) => selectedIds.has(t.id));

  const selectedTransactions = useMemo(() => {
    return transactions.filter((t) => selectedIds.has(t.id));
  }, [transactions, selectedIds]);

  const selectedCategoryCounts = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const t of selectedTransactions) {
      counts[t.cat] = (counts[t.cat] || 0) + 1;
    }
    return counts;
  }, [selectedTransactions]);

  const selectedRestorableCount = useMemo(() => {
    return selectedTransactions.filter((t) => t.restorable).length;
  }, [selectedTransactions]);

  const executeDeleteSelected = async () => {
    if (selectedIds.size === 0) return;
    setIsDeleting(true);
    setDeleteProgress({ stage: `Deleting ${selectedIds.size} selected backup(s)...`, percent: 20 });
    try {
      const idsToDelete = Array.from(selectedIds);
      setDeleteProgress({ stage: "Communicating with backend storage...", percent: 50 });
      const res = (await sendHostRequest("backup:delete-bulk", { ids: idsToDelete }, 60000)) as {
        success?: boolean;
        deleted?: boolean;
        deletedCount?: number;
        skippedCount?: number;
        failedCount?: number;
        freedBytes?: number;
        message?: string;
      };
      setDeleteProgress({ stage: "Reconciling manifests and updating dashboard...", percent: 90 });

      const deletedCount = res?.deletedCount ?? idsToDelete.length;
      const skippedCount = res?.skippedCount ?? 0;
      const failedCount = res?.failedCount ?? 0;
      const freedBytes = res?.freedBytes ?? 0;

      toast.success(`Deleted ${deletedCount} backup(s)`, {
        description: `${skippedCount > 0 ? `${skippedCount} protected/skipped · ` : ""}${failedCount > 0 ? `${failedCount} failed · ` : ""}${freedBytes > 0 ? `Freed ${formatBytes(freedBytes)} · ` : ""}Windows System Restore points untouched.`,
      });

      setSelectedIds(new Set());
      setConfirmDeleteSelected(false);
      setSelectionMode(false);
      await loadBackups(false);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      toast.error("Failed to delete selected backups", { description: msg });
    } finally {
      setIsDeleting(false);
      setDeleteProgress(null);
    }
  };

  const executeDeleteAll = async () => {
    setIsDeleting(true);
    setDeleteProgress({ stage: "Purging all application backups...", percent: 25 });
    try {
      setDeleteProgress({ stage: "Removing transaction records and snapshots...", percent: 60 });
      const res = (await sendHostRequest("backup:delete-all", { confirmation: true, deleteAll: true }, 90000)) as {
        success?: boolean;
        deleted?: boolean;
        deletedCount?: number;
        skippedCount?: number;
        failedCount?: number;
        freedBytes?: number;
        message?: string;
      };
      setDeleteProgress({ stage: "Refreshing backup inventory...", percent: 90 });

      const deletedCount = res?.deletedCount ?? transactions.length;
      const skippedCount = res?.skippedCount ?? 0;
      const freedBytes = res?.freedBytes ?? 0;

      toast.success(`All application backups deleted (${deletedCount} purged)`, {
        description: `Windows System Restore points preserved.${freedBytes > 0 ? ` Freed ${formatBytes(freedBytes)}.` : ""}`,
      });

      setSelectedIds(new Set());
      setConfirmDeleteAll(false);
      setSelectionMode(false);
      await loadBackups(false);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      toast.error("Failed to purge application backups", { description: msg });
    } finally {
      setIsDeleting(false);
      setDeleteProgress(null);
    }
  };

  const displayedRows = useMemo(() => vis.slice(0, displayLimit), [vis, displayLimit]);
  const protTone: StatusTone = loadError
    ? "neg"
    : !connected
      ? "warn"
      : protectionOn === false
        ? "warn"
        : "pos";
  const protLabel = !connected
    ? "NOT CONNECTED"
    : loadError
      ? "ERROR"
      : protectionOn === true
        ? "PROTECTION ON"
        : protectionOn === false
          ? "PROTECTION OFF"
          : "CHECKING…";
  const effectiveRp = latestRp ?? rpState.last;
  const filtered = query.trim() !== "" || f !== "ALL";

  return (
    <div className="space-y-3">
      <PageHeader
        className="eo-bk-header"
        title="Backup & Restore"
        badges={<Badge tone="cyan">UNIVERSAL TRANSACTION ENGINE</Badge>}
        sub="Transaction backups, profile containers and Windows System Restore points."
        actions={
          <>
            <Btn size="sm" loading={busy} onClick={() => void loadBackups(true)}>
              <RotateCcw aria-hidden="true" className="h-3.5 w-3.5" />
              Refresh
            </Btn>
            <Btn size="sm" onClick={() => setCreateRp(true)}>
              <ShieldCheck aria-hidden="true" className="h-3.5 w-3.5" />
              Create restore point
            </Btn>
            <Btn
              size="sm"
              variant={selectionMode ? "primary" : "ghost"}
              disabled={transactions.length === 0 || busy}
              onClick={() => {
                setSelectionMode((prev) => !prev);
                if (selectionMode) setSelectedIds(new Set());
              }}
            >
              <CheckSquare aria-hidden="true" className="h-3.5 w-3.5" />
              {selectionMode ? "Exit selection" : "Select"}
            </Btn>
            <Btn
              size="sm"
              variant="danger"
              disabled={transactions.length === 0 || busy}
              onClick={() => setConfirmDeleteAll(true)}
            >
              <Trash2 aria-hidden="true" className="h-3.5 w-3.5" />
              Delete all backups
            </Btn>
            <Btn
              size="sm"
              disabled={!transactions.some((t) => t.restorable) || busy}
              onClick={() => {
                setRestoreTarget(null);
                setRestoreProfileTarget(null);
                setRestore(true);
              }}
            >
              Restore entire system
            </Btn>
          </>
        }
      />
      <StatTiles
        items={[
          { label: "TOTAL BACKUPS", value: statsView.totalBackups ?? "—" },
          { label: "PROFILES", value: statsView.profiles ?? "—" },
          { label: "OPTIMIZATIONS", value: statsView.optimizations ?? "—" },
          { label: "REGISTRY VALUES", value: statsView.registryValues ?? "—" },
          { label: "SYSTEM RESTORE", value: rpCount ?? "—", tone: "cyan" },
          { label: "RESTORABLE", value: statsView.restorable ?? "—" },
          { label: "NON-REVERSIBLE", value: statsView.nonReversible ?? "—", tone: "warning" },
        ]}
      />
      <div className="eo-bk-top">
        <Card className="eo-bk-protect">
          <SectionTitle
            title="SYSTEM PROTECTION"
            guide="backup.restore-point"
            badge={<Badge tone="muted">{connected ? "WINDOWS HOST" : "AWAITING BACKEND"}</Badge>}
          />
          <dl className="eo-bk-facts">
            <div>
              <dt>Status</dt>
              <dd>
                <StatusIndicator tone={protTone} size="sm" live>
                  {protLabel}
                </StatusIndicator>
              </dd>
            </div>
            <div>
              <dt>Drive</dt>
              <dd title={protDrive}>{protDrive}</dd>
            </div>
            <div>
              <dt>Last restore point</dt>
              <dd title={effectiveRp ? `${effectiveRp.name} · ${effectiveRp.at}` : undefined}>
                {effectiveRp
                  ? `“${effectiveRp.name}” · ${effectiveRp.at}`
                  : "None created"}
              </dd>
            </div>
          </dl>
          <p className="eo-bk-desc">
            {loadError
              ? `Backend query error: ${loadError}`
              : connected
                ? (protDetail ?? "System Protection protects system files and registry settings on the Windows system drive.")
                : "Connect the ERROR OPTIMIZER background service to query Windows System Protection and restore points."}
          </p>
        </Card>
        <Card className="eo-bk-profiles">
          <SectionTitle
            title="PROFILE CONTAINERS"
            sub="Restore every change a profile made in one step."
          />
          {profileContainers.length > 0 ? (
            <ul className="divide-y divide-border/60">
              {profileContainers.map((p) => (
                <li key={p.profileName} className="flex items-center justify-between gap-2 py-2 text-xs">
                  <div className="min-w-0">
                    <div className="font-semibold text-foreground">{p.displayName}</div>
                    <div className="truncate text-[11px] text-muted-foreground">
                      {p.restorableTransactions}/{p.totalTransactions} restorable
                      {p.lastBackupTime ? ` · ${p.lastBackupTime}` : ""}
                    </div>
                  </div>
                  <Btn
                    size="sm"
                    disabled={busy || p.restorableTransactions === 0}
                    onClick={() => {
                      setRestoreTarget(null);
                      setRestoreProfileTarget(p);
                      setRestore(true);
                    }}
                  >
                    Restore
                  </Btn>
                </li>
              ))}
            </ul>
          ) : (
            <div className="py-4 text-xs text-muted-foreground">
              No profile backup containers recorded yet. Profile snapshots appear here after optimization runs.
            </div>
          )}
        </Card>
      </div>
      <Card className="eo-bk-history">
        <SectionTitle
          title="TRANSACTION HISTORY"
          sub={
            loadError
              ? "Failed to load transaction history from backend"
              : filtered
                ? `${vis.length} of ${transactions.length} transactions`
                : `${transactions.length} transactions`
          }
        />
        <div className="eo-bk-toolbar">
          <SearchBox placeholder="Search transactions…" value={query} onChange={setQuery} />
          <PremiumSelect
            size="sm"
            className="eo-bk-filter"
            label="Transaction filter"
            value={f}
            onChange={setF}
            options={TX_FILTERS}
          />
          <Btn
            size="sm"
            disabled={!filtered}
            onClick={() => {
              setF("ALL");
              setQuery("");
            }}
          >
            Reset filters
          </Btn>
        </div>
        {selectionMode && (
          <div className="flex flex-wrap items-center justify-between gap-2.5 p-2.5 mb-2.5 rounded-lg border border-primary/20 bg-primary/5">
            <div className="flex items-center gap-2 text-xs font-semibold">
              <Badge tone="cyan">SELECTION MODE</Badge>
              <span className="text-foreground">
                {selectedIds.size} of {transactions.length} selected
              </span>
              {vis.length !== transactions.length && (
                <span className="text-muted-foreground text-[11px]">
                  ({vis.length} visible in filter)
                </span>
              )}
            </div>
            <div className="flex items-center gap-1.5">
              <Btn
                size="sm"
                variant="ghost"
                onClick={handleSelectAllVisible}
                disabled={vis.length === 0}
              >
                Select visible ({vis.length})
              </Btn>
              <Btn
                size="sm"
                variant="ghost"
                onClick={handleSelectAll}
                disabled={transactions.length === 0}
              >
                Select all ({transactions.length})
              </Btn>
              <Btn
                size="sm"
                variant="ghost"
                onClick={handleDeselectAll}
                disabled={selectedIds.size === 0}
              >
                Deselect all
              </Btn>
              <Btn
                size="sm"
                variant="danger"
                disabled={selectedIds.size === 0 || isDeleting || busy}
                onClick={() => setConfirmDeleteSelected(true)}
              >
                <Trash2 className="h-3.5 w-3.5" />
                Delete selected ({selectedIds.size})
              </Btn>
              <Btn
                size="sm"
                variant="ghost"
                onClick={() => {
                  setSelectionMode(false);
                  setSelectedIds(new Set());
                }}
              >
                Cancel
              </Btn>
            </div>
          </div>
        )}
        {loadError || ["ACCESS_DENIED", "CORRUPTED_METADATA", "BACKEND_UNAVAILABLE", "FAILED"].includes(pageState) ? (
          <EmptyState
            title={
              pageState === "ACCESS_DENIED"
                ? "BACKUP ACCESS DENIED"
                : pageState === "CORRUPTED_METADATA"
                  ? "CORRUPTED BACKUP METADATA"
                  : pageState === "BACKEND_UNAVAILABLE"
                    ? "BACKEND SERVICE UNAVAILABLE"
                    : "FAILED TO LOAD BACKUP INVENTORY"
            }
            sub={
              pageState === "ACCESS_DENIED"
                ? "Administrative privileges are required to access Windows backup storage and System Restore."
                : pageState === "CORRUPTED_METADATA"
                  ? "The backup manifest or index could not be parsed. The automatic recovery rebuild may be needed."
                  : pageState === "BACKEND_UNAVAILABLE"
                    ? "The ERROR OPTIMIZER background service is currently unreachable over named pipe."
                    : (loadError ?? "An unexpected error occurred while querying the backup catalog.")
            }
            action={
              <Btn size="sm" onClick={() => void loadBackups(true)}>
                Retry
              </Btn>
            }
          />
        ) : vis.length ? (
          <div className="eo-bk-table" role="table" aria-label="Transaction history">
            <div
              className="eo-bk-row eo-bk-head"
              role="row"
              style={{
                gridTemplateColumns: selectionMode
                  ? "36px minmax(0, 1fr) 96px minmax(0, 150px) 132px 140px"
                  : undefined,
              }}
            >
              {selectionMode && (
                <span role="columnheader" className="flex items-center justify-center">
                  <PremiumCircularCheckbox
                    on={isAllVisibleSelected}
                    mixed={isSomeVisibleSelected}
                    onChange={() => {
                      if (isAllVisibleSelected) {
                        setSelectedIds((prev) => {
                          const next = new Set(prev);
                          vis.forEach((t) => next.delete(t.id));
                          return next;
                        });
                      } else {
                        setSelectedIds((prev) => {
                          const next = new Set(prev);
                          vis.forEach((t) => next.add(t.id));
                          return next;
                        });
                      }
                    }}
                    label="Toggle select all visible transactions"
                  />
                </span>
              )}
              <span role="columnheader">Transaction</span>
              <span role="columnheader">Category</span>
              <span role="columnheader">Profile / Module</span>
              <span role="columnheader">Timestamp</span>
              <span role="columnheader">Status</span>
            </div>
            {displayedRows.map((t) => {
              const isSelected = selectedIds.has(t.id);
              return (
                <button
                  key={t.id}
                  type="button"
                  role="row"
                  onClick={() => {
                    if (selectionMode) {
                      toggleSelect(t.id);
                    } else {
                      openTransactionDetails(t);
                    }
                  }}
                  style={{
                    gridTemplateColumns: selectionMode
                      ? "36px minmax(0, 1fr) 96px minmax(0, 150px) 132px 140px"
                      : undefined,
                  }}
                  className={cn(
                    "eo-bk-row eo-bk-tx",
                    selectionMode && isSelected && "bg-destructive/10 border-destructive/40 text-foreground",
                  )}
                >
                  {selectionMode && (
                    <span
                      role="cell"
                      className="flex items-center justify-center"
                      onClick={(e) => toggleSelect(t.id, e)}
                    >
                      <PremiumCircularCheckbox
                        on={isSelected}
                        onChange={() => toggleSelect(t.id)}
                        label={`Select ${t.name}`}
                      />
                    </span>
                  )}
                  <span role="cell" className="eo-bk-name" title={t.name}>
                    {t.name}
                  </span>
                  <span role="cell">
                    <Badge>{t.cat}</Badge>
                  </span>
                  <span role="cell" className="eo-bk-mod" title={t.module}>
                    {t.module}
                  </span>
                  <span role="cell" className="eo-bk-time">
                    {t.at}
                  </span>
                  <span role="cell">
                    <StatusIndicator tone={t.restorable ? "pos" : "warn"} size="sm">
                      {t.restorable ? "RESTORABLE" : "NON-REVERSIBLE"}
                    </StatusIndicator>
                  </span>
                </button>
              );
            })}
            {vis.length > displayLimit && (
              <div className="flex justify-center p-3 border-t border-border/40">
                <Btn
                  size="sm"
                  variant="ghost"
                  onClick={() => setDisplayLimit((prev) => prev + 40)}
                >
                  Load more ({vis.length - displayLimit} remaining)
                </Btn>
              </div>
            )}
          </div>
        ) : (
          <EmptyState
            title={
              filtered
                ? "NO TRANSACTIONS MATCH THESE FILTERS"
                : pageState === "INITIAL_LOADING"
                  ? "RETRIEVING BACKUP CATALOG…"
                  : "NO BACKUP TRANSACTIONS RECORDED YET"
            }
            sub={
              filtered
                ? "Try another search or reset the filters."
                : pageState === "INITIAL_LOADING"
                  ? "Reading transaction catalog and verification states from backend storage."
                  : "Universal transaction records and state backups will appear here automatically."
            }
            action={
              filtered ? (
                <Btn
                  size="sm"
                  onClick={() => {
                    setF("ALL");
                    setQuery("");
                  }}
                >
                  Reset filters
                </Btn>
              ) : undefined
            }
          />
        )}
      </Card>
      <Modal
        open={!!d}
        onClose={() => setD(null)}
        title="Transaction details"
        footer={
          <>
            <Btn variant="ghost" onClick={() => setD(null)}>
              Close
            </Btn>
            {d?.restorable && (
              <Btn
                variant="primary"
                onClick={() => {
                  setRestoreTarget(d);
                  setD(null);
                  setRestore(true);
                }}
              >
                Restore
              </Btn>
            )}
          </>
        }
      >
        {d && (
          <div className="space-y-4">
            <div>
              <KV k="Optimization:" v={d.name} />
              <KV k="Profile / Module:" v={d.module} />
              <KV k="Category & Risk:" v={d.cat} />
              <KV k="Timestamp:" v={d.at} />
              <KV k="Machine Identity:" v="Local PC" />
            </div>
            <Details title="TARGET KEY / PATH & STATE TRANSITION">
              <KV k="Before State:" v={d.beforeState} />
              <KV k="After State:" v={d.afterState} />
            </Details>
            <div>
              <Label className="mb-1 text-cyan">ROLLBACK DIAGNOSTICS</Label>
              <KV k="Rollback Method:" v={d.rollbackMethod} />
              <KV k="Status:" v={d.restorable ? "Available" : d.statusText} />
            </div>
          </div>
        )}
      </Modal>
      <Modal
        open={restore}
        onClose={() => {
          setRestore(false);
          setRestoreProfileTarget(null);
        }}
        title="Proceed with restore?"
        footer={
          <>
            <Btn
              variant="ghost"
              onClick={() => {
                setRestore(false);
                setRestoreProfileTarget(null);
              }}
            >
              Cancel
            </Btn>
            <Btn
              variant="danger"
              disabled={busy}
              onClick={() => void executeRestore()}
            >
              Proceed with restore
            </Btn>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">
          {restoreProfileTarget
            ? `All recorded changes for the ${restoreProfileTarget.displayName} profile container will be reverted and verified.`
            : restoreTarget
              ? `Transaction "${restoreTarget.name}" will be rolled back to its captured pre-optimization state.`
              : "All restorable transactions across profiles and modules will be reverted to baseline and verified."}
        </p>
      </Modal>
      <Modal
        open={confirmDeleteSelected}
        onClose={() => !isDeleting && setConfirmDeleteSelected(false)}
        title={`Delete ${selectedIds.size} selected backup${selectedIds.size === 1 ? "" : "s"}?`}
        footer={
          <>
            <Btn
              variant="ghost"
              disabled={isDeleting}
              onClick={() => setConfirmDeleteSelected(false)}
            >
              Cancel
            </Btn>
            <Btn
              variant="danger"
              loading={isDeleting}
              onClick={() => void executeDeleteSelected()}
            >
              <Trash2 className="h-3.5 w-3.5" />
              Delete {selectedIds.size} {selectedIds.size === 1 ? "backup" : "backups"}
            </Btn>
          </>
        }
      >
        <div className="space-y-4">
          <div className="flex items-start gap-3 p-3 rounded-lg border border-destructive/30 bg-destructive/5 text-xs">
            <AlertCircle className="h-5 w-5 text-destructive shrink-0 mt-0.5" />
            <div className="space-y-1">
              <div className="font-semibold text-destructive">Permanent Application Backup Deletion</div>
              <p className="text-muted-foreground leading-relaxed">
                The selected backup transactions will be permanently deleted from local disk storage and removed from the active manifest. This operation cannot be undone.
              </p>
            </div>
          </div>

          <div className="grid grid-cols-2 gap-2 text-xs">
            <div className="p-2.5 rounded-lg border border-border bg-window/50">
              <div className="text-[10px] font-bold text-muted-foreground uppercase">Selected Backups</div>
              <div className="text-lg font-bold text-foreground mt-0.5">{selectedIds.size}</div>
            </div>
            <div className="p-2.5 rounded-lg border border-border bg-window/50">
              <div className="text-[10px] font-bold text-muted-foreground uppercase">Restorable Count</div>
              <div className="text-lg font-bold text-foreground mt-0.5">{selectedRestorableCount}</div>
            </div>
          </div>

          {Object.keys(selectedCategoryCounts).length > 0 && (
            <div>
              <Label className="text-[10px] font-bold text-muted-foreground uppercase mb-1.5 block">
                Affected Categories
              </Label>
              <div className="flex flex-wrap gap-1.5">
                {Object.entries(selectedCategoryCounts).map(([cat, count]) => (
                  <Badge key={cat} tone="cyan">
                    {cat} ({count})
                  </Badge>
                ))}
              </div>
            </div>
          )}

          <div className="p-2.5 rounded-lg border border-cyan/30 bg-cyan/5 text-xs text-muted-foreground">
            <div className="flex items-center gap-1.5 text-cyan font-semibold text-[11px] mb-1">
              <ShieldCheck className="h-4 w-4 shrink-0" />
              WINDOWS SYSTEM RESTORE SAFEGUARD
            </div>
            Windows System Restore points and System Protection will NOT be affected.
          </div>

          {deleteProgress && (
            <div className="space-y-1.5 pt-2 border-t border-border/40">
              <div className="flex justify-between text-xs font-semibold text-foreground">
                <span>{deleteProgress.stage}</span>
                {deleteProgress.percent !== undefined && <span>{deleteProgress.percent}%</span>}
              </div>
              <Bar value={deleteProgress.percent ?? 50} tone="danger" />
            </div>
          )}
        </div>
      </Modal>
      <Modal
        open={confirmDeleteAll}
        onClose={() => !isDeleting && setConfirmDeleteAll(false)}
        title="Delete all application backups?"
        footer={
          <>
            <Btn
              variant="ghost"
              disabled={isDeleting}
              onClick={() => setConfirmDeleteAll(false)}
            >
              Cancel
            </Btn>
            <Btn
              variant="danger"
              loading={isDeleting}
              onClick={() => void executeDeleteAll()}
            >
              <Trash2 className="h-3.5 w-3.5" />
              Delete all application backups
            </Btn>
          </>
        }
      >
        <div className="space-y-4">
          <div className="flex items-start gap-3 p-3 rounded-lg border border-destructive/30 bg-destructive/5 text-xs">
            <AlertCircle className="h-5 w-5 text-destructive shrink-0 mt-0.5" />
            <div className="space-y-1">
              <div className="font-semibold text-destructive">Permanent System Rollback Manifest Purge</div>
              <p className="text-muted-foreground leading-relaxed">
                This will permanently delete all application-managed optimization transactions, profile rollback containers, registry snapshots, and V4 configuration backups. All history and restorable snapshots will be purged.
              </p>
            </div>
          </div>

          <div className="grid grid-cols-3 gap-2 text-xs">
            <div className="p-2.5 rounded-lg border border-border bg-window/50">
              <div className="text-[10px] font-bold text-muted-foreground uppercase">Total Backups</div>
              <div className="text-lg font-bold text-foreground mt-0.5">{transactions.length}</div>
            </div>
            <div className="p-2.5 rounded-lg border border-border bg-window/50">
              <div className="text-[10px] font-bold text-muted-foreground uppercase">Profile Containers</div>
              <div className="text-lg font-bold text-foreground mt-0.5">{profileContainers.length}</div>
            </div>
            <div className="p-2.5 rounded-lg border border-border bg-window/50">
              <div className="text-[10px] font-bold text-muted-foreground uppercase">Restorable</div>
              <div className="text-lg font-bold text-foreground mt-0.5">{statsView.restorable ?? 0}</div>
            </div>
          </div>

          <div className="p-2.5 rounded-lg border border-cyan/30 bg-cyan/5 text-xs text-muted-foreground">
            <div className="flex items-center gap-1.5 text-cyan font-semibold text-[11px] mb-1">
              <ShieldCheck className="h-4 w-4 shrink-0" />
              WINDOWS SYSTEM RESTORE SAFEGUARD
            </div>
            Windows System Restore points and System Protection will NOT be affected. Only Error Optimizer's application-level backup storage will be cleared.
          </div>

          {deleteProgress && (
            <div className="space-y-1.5 pt-2 border-t border-border/40">
              <div className="flex justify-between text-xs font-semibold text-foreground">
                <span>{deleteProgress.stage}</span>
                {deleteProgress.percent !== undefined && <span>{deleteProgress.percent}%</span>}
              </div>
              <Bar value={deleteProgress.percent ?? 50} tone="danger" />
            </div>
          )}
        </div>
      </Modal>
      <RestorePointDialog
        open={createRp}
        onClose={() => {
          setCreateRp(false);
          void loadBackups(false);
        }}
        defaultName="Manual restore point"
      />
    </div>
  );
}

/* ───────── SETTINGS ───────── */
export function SettingsPage() {
  return <SettingsCenter />;
}

/* ───────── ABOUT ───────── */
export function AboutPage() {
  const connected = useIsBackendConnected();
  const [q, setQ] = useState("");
  const [cat, setCat] = useState("ALL");
  const [d, setD] = useState<(typeof CATALOG)[number] | null>(null);
  const [audit, setAudit] = useState<Record<string, unknown> | null>(null);
  const [readings, setReadings] = useState<ScanReadings | null>(null);
  const [scanningHw, setScanningHw] = useState(false);

  const refreshAbout = async (notify = false) => {
    if (!hasNativeBackend()) return;
    setScanningHw(true);
    try {
      const [auditRes, scanRes] = await Promise.all([
        sendHostRequest<Record<string, unknown>>("about:audit", undefined, 15000).catch(() => null),
        sendHostRequest<{ readings?: unknown }>("system:scan", undefined, 25000).catch(() => null),
      ]);
      if (auditRes) setAudit(auditRes);
      if (scanRes) {
        const raw = scanRes.readings !== undefined ? scanRes.readings : scanRes;
        const parsed = parseReadings(raw);
        if (parsed) setReadings(parsed);
      }
      if (notify) toast.success("Hardware & catalog audit refreshed");
    } catch (err) {
      if (notify) {
        toast.error("Hardware rescan failed", {
          description: err instanceof Error ? err.message : String(err),
        });
      }
    } finally {
      setScanningHw(false);
    }
  };

  const scan = usePageScan("about", () => refreshAbout(false));

  const strVal = (v: unknown): string | null =>
    typeof v === "string" && v.trim() ? v.trim() : null;
  const numVal = (v: unknown): number | null =>
    typeof v === "number" && Number.isFinite(v) ? v : null;
  const auditCatalog =
    audit?.["catalog"] && typeof audit["catalog"] === "object"
      ? (audit["catalog"] as Record<string, unknown>)
      : null;
  const backupSummary =
    audit?.["backupSummary"] && typeof audit["backupSummary"] === "object"
      ? (audit["backupSummary"] as Record<string, unknown>)
      : null;
  const ramGb = readings?.ram.totalGb ?? numVal(audit?.["ramTotalGb"]);
  const backupCount = numVal(backupSummary?.["totalBackups"] ?? backupSummary?.["TotalBackups"]);
  const restorableCount = numVal(backupSummary?.["restorableCount"] ?? backupSummary?.["RestorableCount"]);
  const serviceConnected = Boolean(audit?.["serviceConnected"]);
  const serviceVersion = strVal(audit?.["serviceVersion"]);
  const integrityText = audit
    ? serviceConnected
      ? `Service verified${serviceVersion ? ` (${serviceVersion})` : ""}`
      : audit["isElevated"]
        ? "Host verified (Elevated)"
        : "Host verified"
    : "Awaiting service check";

  const cats = ["ALL", ...Array.from(new Set(CATALOG.map((c) => c.cat)))];
  const vis = CATALOG.filter(
    (c) => (cat === "ALL" || c.cat === cat) && c.name.toLowerCase().includes(q.toLowerCase()),
  ).sort((a, b) => a.name.localeCompare(b.name));

  return (
    <div className="space-y-3">
      <PageHeader
        title="About Error Optimizer"
        sub="Application details, optimization profiles, hardware summary, and safety catalog."
      />
      <section className="eo-card eo-about-hero" aria-label="Application identity">
        <img
          src="/favicon.png"
          alt=""
          aria-hidden="true"
          className="eo-about-logo"
          width={44}
          height={44}
        />
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="eo-about-name">ERROR OPTIMIZER</h2>
            <Badge tone="cyan">DESKTOP EDITION</Badge>
          </div>
          <p className="text-[12px] text-muted-foreground">
            VERSION V4 · Unified Windows Optimization Suite
          </p>
        </div>
        <dl className="eo-about-facts">
          <div>
            <dt>Version</dt>
            <dd>V4</dd>
          </div>
          <div>
            <dt>Build</dt>
            <dd>{strVal(audit?.["appVersion"]) ?? "desktop-ui"}</dd>
          </div>
          <div>
            <dt>Integrity</dt>
            <dd className={audit ? "text-success" : "text-muted-foreground"}>{integrityText}</dd>
          </div>
        </dl>
      </section>
      <div className="eo-about-grid">
        <Card>
          <SectionTitle
            title="CONNECT WITH ERROR OPTIMIZER"
            sub="Official community, content and social channels."
          />
          <ul className="eo-about-links">
            {[
              ["DISCORD", "Community Server", "JOIN DISCORD", "https://discord.gg/QxfMNGbm54"],
              ["YOUTUBE", "@Errorfftamil", "WATCH YOUTUBE", "https://youtube.com/@errorfftamil?si=Jwgv22YRaXKzv7Ve"],
              ["INSTAGRAM", "@errorexeff", "OPEN INSTAGRAM", "https://www.instagram.com/errorexeff?utm_source=ig_web_button_share_sheet&xtok=ZDNlZDc0MzIxNw=="],
              ["KICK", "Live Streaming", "WATCH ON KICK", "https://kick.com/errorexeff"],
            ].map(([a, b, c, url]) => (
              <li key={a}>
                <div className="min-w-0">
                  <Label className="text-cyan">{a}</Label>
                  <div className="truncate text-[12.5px] font-bold text-foreground" title={b}>
                    {b}
                  </div>
                </div>
                <Btn
                  size="sm"
                  label={c}
                  title={c}
                  onClick={async () => {
                    try {
                      await sendHostRequest("window:open-external", { channel: a, url }, 10000);
                    } catch (err) {
                      if (typeof window !== "undefined" && url) {
                        window.open(url, "_blank", "noopener,noreferrer");
                      } else {
                        toast.error("Unable to open link", { description: err instanceof Error ? err.message : String(err) });
                      }
                    }
                  }}
                >
                  <span>{c}</span>
                  <span aria-hidden="true"> →</span>
                </Btn>
              </li>
            ))}
          </ul>
        </Card>
        <Card>
          <SectionTitle
            title="HARDWARE & ENGINE STATUS"
            sub="Verified machine diagnostics from the background service."
            right={
              <Btn
                size="sm"
                loading={scanningHw}
                disabled={scanningHw}
                onClick={() => void refreshAbout(true)}
              >
                <RotateCcw aria-hidden="true" className="h-3.5 w-3.5" />
                Rescan hardware
              </Btn>
            }
          />
          <dl className="eo-about-specs">
            {[
              ["LOCAL MACHINE", strVal(audit?.["machineName"]) ?? "Not detected"],
              ["PLATFORM ARCHITECTURE", strVal(audit?.["architecture"]) ?? "x64"],
              ["CURRENT OS", strVal(audit?.["osVersion"]) ?? "Not detected"],
              ["WINDOWS COMPATIBILITY", "Windows 10 / 11"],
              ["CPU / PROCESSOR", readings?.cpu.model ?? strVal(audit?.["cpuName"]) ?? "Data unavailable"],
              ["GRAPHICS ADAPTER (GPU)", readings?.gpu.model ?? strVal(audit?.["gpuName"]) ?? "Data unavailable"],
              [
                "PHYSICAL MEMORY (RAM)",
                ramGb != null ? `${ramGb} GB Physical Memory` : "Data unavailable",
              ],
              ["POWER SOURCE", strVal(audit?.["powerSource"]) ?? "Unavailable"],
              ["OPTIMIZATION ENGINE", "Ready"],
              [
                "BACKEND SERVICE",
                audit
                  ? serviceConnected
                    ? `Connected${serviceVersion ? ` (${serviceVersion})` : ""}`
                    : "Host IPC fallback"
                  : "Not connected",
              ],
              ["PROFILE ENGINE", "7 profiles"],
              [
                "TRANSACTIONAL BACKUP",
                backupCount != null
                  ? `${restorableCount ?? backupCount} restorable (${backupCount} total)`
                  : "Standby",
              ],
            ].map(([k, v]) => (
              <div key={k}>
                <dt>{k}</dt>
                <dd title={v}>{v}</dd>
              </div>
            ))}
          </dl>
        </Card>
      </div>
      <StatTiles
        items={[
          { label: "TOTAL CATALOG", value: numVal(auditCatalog?.["total"]) ?? CATALOG.length },
          { label: "APPLICABLE", value: numVal(auditCatalog?.["applicable"]) ?? "—", tone: "cyan" },
          { label: "ALREADY OPT.", value: numVal(auditCatalog?.["alreadyOptimized"]) ?? "—" },
          { label: "PENDING", value: numVal(auditCatalog?.["pending"]) ?? "—", tone: "warning" },
          { label: "SAFE", value: CATALOG.filter((c) => c.risk === "LOW").length, tone: "success" },
          {
            label: "MEDIUM RISK",
            value: CATALOG.filter((c) => c.risk === "MEDIUM").length,
            tone: "warning",
          },
          { label: "HIGH RISK", value: 0, tone: "danger" },
        ]}
      />
      <Card className="eo-about-profcard">
        <SectionTitle
          title="SYSTEM OPTIMIZATION PROFILES"
          sub={`${PROFILES.length} Specialized Engine Profiles`}
        />
        <ul className="eo-about-profiles" aria-label="System optimization profiles">
          {PROFILES.map((p) => (
            <li key={p}>{p}</li>
          ))}
        </ul>
      </Card>
      <Card>
        <SectionTitle
          title="OPTIMIZATION CATALOG & KNOWLEDGE BASE"
          sub="Registered catalog metadata (not the complete operation inventory)."
          right={
            <span className="eo-about-count">
              {vis.length} of {CATALOG.length}
            </span>
          }
        />
        <div className="eo-about-toolbar">
          <SearchBox placeholder="Search the catalog…" value={q} onChange={setQ} />
          <Chips
            label="Catalog category"
            options={cats}
            value={cat}
            onChange={setCat}
            className="eo-about-filter"
          />
          <span className="eo-about-sort">SORT: NAME</span>
        </div>
        {vis.length ? (
          <div className="eo-about-catalog">
            {vis.map((c) => (
              <button key={c.id} type="button" onClick={() => setD(c)} className="eo-about-entry">
                <span className="eo-about-entry-name" title={c.name}>
                  {c.name}
                </span>
                <span className="eo-about-entry-tags">
                  <Badge>{c.cat}</Badge>
                  <RiskBadge risk={c?.risk || "LOW"} />
                </span>
              </button>
            ))}
          </div>
        ) : (
          <EmptyState title="NO CATALOG ENTRIES MATCH" sub="Try another search or category." />
        )}
      </Card>
      <Modal
        open={!!d}
        onClose={() => setD(null)}
        title={d?.name ?? ""}
        width="max-w-3xl"
        footer={<Btn onClick={() => setD(null)}>Close details</Btn>}
      >
        {d &&
          [
            "SECTION 1: OVERVIEW & PURPOSE",
            "SECTION 2: WHAT DOES IT DO?",
            "SECTION 3: WHY IS IT USED?",
            "SECTION 4: WHAT IT CHANGES",
            "SECTION 5: CURRENT STATE vs TARGET OPTIMIZED STATE",
            "SECTION 6: APPLICABILITY & HARDWARE ENVIRONMENT",
            "SECTION 7: RISK CLASSIFICATION & SAFETY GUARANTEES",
            "SECTION 8: BACKUP & TRANSACTIONAL ROLLBACK",
            "SECTION 9: POST-EXECUTION READBACK & VERIFICATION",
          ].map((s) => (
            <div key={s} className="mb-3 rounded-lg border border-border bg-window p-3">
              <Label className="text-cyan">{s}</Label>
              <p className="mt-1 text-xs text-muted-foreground">
                {s.includes("RISK") ? (
                  <RiskBadge risk={d?.risk || "LOW"} />
                ) : (
                  "Populated from OptimizationCatalog metadata in the native app."
                )}
              </p>
            </div>
          ))}
      </Modal>
    </div>
  );
}

/* ───────── PERFORMANCE TUNING ───────── */
interface TuningProbeView {
  cpuName?: string;
  cpuAvailability?: string;
  cpuRawOverclockSupported?: boolean;
  cpuSupportDetail?: string;
  cpuTempC?: number | null;
  gpuName?: string;
  gpuAvailability?: string;
  gpuRawVoltageOverclockSupported?: boolean;
  gpuSupportDetail?: string;
  gpuTempC?: number | null;
  powerSource?: string;
  activePowerSchemeName?: string;
  hasActiveTunedState?: boolean;
  lastTransactionId?: string | null;
}

export function PerformanceTuningPage() {
  const [active, setActive] = useState<string | null>(null);
  const [probe, setProbe] = useState<TuningProbeView | null>(null);
  const [stageIdx, setStageIdx] = useState<number>(-1);
  const [progressVal, setProgressVal] = useState(0);
  const [statusMsg, setStatusMsg] = useState<string | null>(null);
  const [lastTxId, setLastTxId] = useState<string | null>(null);

  const probeHardware = async () => {
    if (!hasNativeBackend()) return;
    try {
      const p = (await sendHostRequest("tuning:probe", undefined, 25000)) as TuningProbeView;
      setProbe(p);
      if (p?.lastTransactionId) setLastTxId(p.lastTransactionId);
    } catch {
      /* keep unprobed state cleanly */
    }
  };

  const scan = usePageScan("performance-tuning", probeHardware);

  const runTuning = async (targetLabel: string) => {
    if (active) return;
    const target = targetLabel.toLowerCase().startsWith("gpu") ? "gpu" : "cpu";
    setActive(targetLabel);
    setStageIdx(0);
    setProgressVal(20);
    setStatusMsg(`Probing ${target.toUpperCase()} capabilities and baseline thermals...`);
    try {
      const res = (await sendHostRequest(
        "tuning:execute",
        { target, confirmed: true },
        90000,
      )) as {
        success?: boolean;
        status?: string;
        message?: string;
        transactionId?: string;
        stagesCompleted?: string[];
      };
      setStageIdx(4);
      setProgressVal(100);
      setStatusMsg(res?.message ?? "Tuning evaluation finished.");
      if (res?.transactionId) setLastTxId(res.transactionId);
      if (res?.success) {
        toast.success(`${target.toUpperCase()} safe tuning applied`, { description: res?.message });
      } else {
        toast.info(`${target.toUpperCase()} tuning status: ${res?.status ?? "Notice"}`, { description: res?.message });
      }
      await probeHardware();
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setStatusMsg(msg);
      toast.error(`${target.toUpperCase()} tuning failed`, { description: msg });
    } finally {
      setActive(null);
    }
  };

  const stopOrRollback = async () => {
    setActive(null);
    if (lastTxId) {
      try {
        await sendHostRequest("tuning:rollback", { transactionId: lastTxId }, 30000);
        toast.success("Rolled back performance tuning changes");
        setLastTxId(null);
        await probeHardware();
      } catch (err) {
        toast.error("Rollback failed", { description: err instanceof Error ? err.message : String(err) });
      }
    }
  };

  const connected = useIsBackendConnected();
  return (
    <div className="space-y-4">
      <PageHeader
        title="Performance Tuning"
        sub="Hardware-safe CPU and GPU tuning with continuous thermal and stability monitoring."
        scan={scan}
      />
      <Cols cols="1fr 1fr" gap={14} className="items-stretch">
        {[
          [
            "CPU PERFORMANCE",
            probe?.cpuName || "Waiting for CPU detection",
            probe?.cpuTempC != null ? `${probe.cpuTempC.toFixed(0)}°C` : "—",
            probe?.cpuAvailability ?? "NOT SCANNED",
            probe?.cpuSupportDetail ?? "Unavailable",
          ],
          [
            "GPU PERFORMANCE",
            probe?.gpuName || "Waiting for GPU detection",
            probe?.gpuTempC != null ? `${probe.gpuTempC.toFixed(0)}°C` : "—",
            probe?.gpuAvailability ?? "NOT SCANNED",
            probe?.gpuSupportDetail ?? "Unavailable",
          ],
        ].map(([t, n, temp, avail, detail]) => (
          <Card key={t}>
            <div className="mb-3 flex items-center justify-between">
              <Label className="text-foreground">{t}</Label>
              <Badge tone={avail === "Supported" ? "success" : avail === "Limited" ? "warning" : "muted"}>
                {(avail ?? "NOT SCANNED").toUpperCase()}
              </Badge>
            </div>
            <div className="text-sm font-semibold text-muted-foreground">{n}</div>
            <div className="mt-3 grid grid-cols-2 gap-2">
              <KV k="CURRENT STATE" v={detail} />
              <KV k="TEMPERATURE" v={temp} />
            </div>
            <Btn
              variant="primary"
              disabled={!!active}
              className="mt-4 w-full"
              onClick={() => void runTuning(t ?? "CPU PERFORMANCE")}
            >
              Start {t?.split(" ")[0]} tuning
            </Btn>
          </Card>
        ))}
      </Cols>
      <StatTiles
        items={[
          { label: "THERMAL PROTECTION", value: active ? "Monitoring" : probe ? "Armed" : "Standby" },
          { label: "STABILITY MONITORING", value: probe ? (probe.hasActiveTunedState ? "Tuned" : "Baseline") : "Awaiting backend" },
          { label: "POWER SOURCE", value: probe?.powerSource ?? "Unavailable" },
        ]}
      />
      <Card>
        <SectionTitle
          title="SESSION FLOW"
          sub={
            active
              ? `${active} · compatibility and stability evaluation`
              : (statusMsg ?? "Start CPU or GPU tuning to begin")
          }
          right={
            <Btn
              variant="danger"
              size="sm"
              disabled={!active && !lastTxId}
              onClick={() => void stopOrRollback()}
            >
              {active ? "Stop" : "Rollback"}
            </Btn>
          }
        />
        <Stepper
          label="Tuning session flow"
          steps={["Check", "Test", "Tune", "Verify", "Decide"]}
          current={stageIdx}
        />
        {(active || progressVal > 0) && (
          <>
            <Bar value={progressVal} className="mt-3" />
            <p className="mt-2 text-xs text-cyan">{statusMsg ?? "Waiting for hardware validation telemetry."}</p>
          </>
        )}
      </Card>
      <Details title="ADVANCED DETAILS — HARDWARE ENVELOPE & VERIFICATION DETAILS">
        <p className="text-xs text-muted-foreground">
          Evaluation Policy: evaluates maximum verified stable performance — never unverified
          overclocking or unsigned ring-0 drivers.
        </p>
      </Details>
    </div>
  );
}

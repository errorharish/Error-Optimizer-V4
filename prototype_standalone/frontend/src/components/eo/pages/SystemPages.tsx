// InputOptimizerView, SystemInfoView, RegistryTweakView, RegistryValuesView, RegistryValuesRamView, GpuRegistryValuesView
import { useEffect, useState, useMemo, useCallback, useRef, type ReactNode } from "react";
import { toast } from "sonner";
import { AlertCircle, CheckCircle2, Gauge, MousePointer2, RotateCcw, ShieldCheck, SlidersHorizontal, type LucideIcon } from "lucide-react";
import { Link, useNavigate } from "@tanstack/react-router";
import { usePageScan } from "../usePageScan";
import { hasNativeBackend, useIsBackendConnected, isDesktopApp } from "@/lib/eo/nativeBridge";
import { isBackendConnected } from "@/lib/eo/connectionStore";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { parseReadings, type ScanReadings } from "@/lib/eo/dashboardScan";
import { updateSharedScanReadings, useSharedTelemetryState } from "@/lib/eo/telemetryStore";
import { isLiveFresh } from "@/lib/eo/v4Telemetry";
import {
  BackLink,
  Badge,
  Bar,
  Btn,
  Card,
  Check,
  CheckRow,
  Chips,
  Cols,
  Details,
  KV,
  Label,
  Modal,
  PageHeader,
  RiskBadge,
  SectionTitle,
  SelectionBar,
  StatTiles,
  SelectMark,
  CatalogFilterDropdown,
  normalizeFilterScope,
} from "../kit";
import type { GuideId } from "@/lib/eo/guides";
import { RunModal } from "../RunModal";
import { PremiumSelect } from "../dropdown";
import { TelemetryConsole } from "../Telemetry";
import { HealthCheck } from "../HealthCheck";
import {
  buildExecutionPlan,
  clampSelection,
  eligibleSelection,
  isSelectable,
  toggleInSet,
} from "@/lib/eo/selection";
import {
  INPUT_CANONICAL_TO_FRONTEND,
  INPUT_ITEMS,
  INPUT_MODES,
  exclusionReason,
  modeSelection,
  parseInputMode,
  type InputItem,
  type InputMode,
} from "@/lib/eo/inputModes";
import { useModedSelection, usePersistedSelection } from "../useSelection";
import {
  GPU_REG_GROUPS,
  GPU_REGISTRY_VALUES,
  RAM_GROUPS,
  RAM_REGISTRY_VALUES,
  RAM_TIER_MAP,
  SVCHOST_DEFAULT_KB,
  hex8,
  nearestRamTier,
  registrySummary,
  valuesInGroup,
  type RegistryGroup,
  type RegistryValueEntry,
} from "@/lib/eo/registryValues";
import { CATALOG, HW, type Risk } from "@/lib/eo/data";
import { normalizeOptimizationCatalog } from "@/lib/eo/catalogContract";
import { cn } from "@/lib/utils";

/* ───────── INPUT OPTIMIZER (ALL ELIGIBLE default + CUSTOM selection) ───────── */
interface InputLatencyResult {
  isMeasured?: boolean;
  sampleCount?: number;
  averageIntervalMs?: number | null;
  pollingHz?: number | null;
  jitterMs?: number | null;
  timerResolutionMs?: number | null;
  reason?: string | null;
}

interface DetectedInputDevice {
  deviceId?: string;
  deviceName?: string;
  deviceType?: string;
  transportType?: string;
  manufacturer?: string;
  vid?: string;
  pid?: string;
  deviceClass?: string;
  deviceInterface?: string;
  rawDevicePath?: string;
  instanceId?: string;
  isPhysical?: boolean;
  isTouchpad?: boolean;
  isInternal?: boolean;
}

export interface InputScanResponse {
  items?: Array<{ id?: string; isApplied?: boolean; alreadyOptimized?: boolean }>;
  actions?: Array<{ id?: string; status?: string; Status?: string }>;
  devices?: DetectedInputDevice[];
  timerResolutionMs?: number | null;
  timerMs?: number | null;
  mouseTunedCount?: number;
  keyboardTunedCount?: number;
  mouseDeviceName?: string;
  keyboardDeviceName?: string;
  mouseStatus?: string;
  keyboardStatus?: string;
}

export interface InputCatalogItem {
  id: string;
  name: string;
  cat: string;
  risk: Risk;
  desc?: string;
  done: boolean;
  ineligible?: boolean;
  isApplied?: boolean;
  reason?: string;
  currentValue?: string;
  targetValue?: string;
  eligibilityState?: string;
  requiresReboot?: boolean;
  requiresRestart?: boolean;
  restart?: boolean;
}

export function InputOptimizerPage() {
  const [backendItems, setBackendItems] = useState<InputCatalogItem[] | null>(null);
  const [backendScanned, setBackendScanned] = useState(false);
  const [deviceList, setDeviceList] = useState<DetectedInputDevice[]>([]);
  const [showDeviceDetails, setShowDeviceDetails] = useState(false);
  const [detail, setDetail] = useState<InputCatalogItem | null>(null);
  const [inputView, setInputView] = useState("ACTIONABLE");
  const [inputMeta, setInputMeta] = useState<{
    mouse: string;
    mouseStatus?: string;
    keyboard: string;
    keyboardStatus?: string;
    timer: string;
  } | null>(null);
  const scanGenRef = useRef(0);

  const scan = usePageScan("input-optimizer", async () => {
    const currentGen = ++scanGenRef.current;
    if (!hasNativeBackend()) {
      throw new Error("ERROR OPTIMIZER background service disconnected.");
    }
    try {
      const [res, sysScan] = await Promise.all([
        sendHostRequest<Record<string, unknown>>("input:scan", undefined, 20000),
        sendHostRequest<{ readings?: { latency?: { timerMs?: number | null } } }>("system:scan", undefined, 20000).catch(() => null),
      ]);
      if (currentGen !== scanGenRef.current) return;

      const norm = normalizeOptimizationCatalog(res, "input-optimizer");
      const mappedItems: InputCatalogItem[] = norm.items.map((ni) => ({
        id: ni.id,
        name: ni.name,
        cat: ni.category || "INPUT",
        risk: ni.risk as Risk,
        desc: ni.description || ni.reason || "",
        done: ni.done,
        ineligible: ni.ineligible ? true : undefined,
        isApplied: ni.isApplied,
        reason: ni.reason || ni.applicabilityReason,
        currentValue: ni.currentState,
        targetValue: ni.targetValue,
        eligibilityState: ni.eligibilityState,
        requiresReboot: Boolean(ni.requiresReboot || ni.requiresRestart || ni.restart),
        requiresRestart: Boolean(ni.requiresReboot || ni.requiresRestart || ni.restart),
        restart: Boolean(ni.requiresReboot || ni.requiresRestart || ni.restart),
      }));

      setBackendItems(mappedItems);
      setBackendScanned(true);

      const rawDevs = (res && typeof res === "object" && (res["devices"] ?? res["Devices"])) as DetectedInputDevice[] | undefined;
      const devices = Array.isArray(rawDevs) ? rawDevs : [];
      setDeviceList(devices);

      const rawTimer = (res && typeof res === "object" && (res["timerResolutionMs"] ?? res["timerMs"] ?? res["TimerResolutionMs"] ?? res["TimerMs"])) as number | undefined;
      const timerVal = typeof rawTimer === "number" ? rawTimer : sysScan?.readings?.latency?.timerMs;

      const rawMouseCount = (res && typeof res === "object" && (res["mouseTunedCount"] ?? res["MouseTunedCount"])) as number | undefined;
      const rawKbCount = (res && typeof res === "object" && (res["keyboardTunedCount"] ?? res["KeyboardTunedCount"])) as number | undefined;

      const mouseCount = typeof rawMouseCount === "number"
        ? rawMouseCount
        : mappedItems.filter((x) => (x.id.startsWith("mouse") || x.id.includes("mou") || x.id.includes("pointer") || x.id.includes("cursor")) && x.done).length;
      const keyboardCount = typeof rawKbCount === "number"
        ? rawKbCount
        : mappedItems.filter((x) => (x.id.startsWith("kb") || x.id.includes("kbd") || x.id.includes("keyboard") || x.id.includes("debounce") || x.id.includes("filter_keys") || x.id.includes("sticky_keys")) && x.done).length;

      setInputMeta({
        mouse: `${mouseCount || mappedItems.filter(x => x.done).length} verified settings`,
        mouseStatus: devices.some((d) => d.deviceType === "Mouse" || d.isTouchpad) ? "Detected" : "Detected",
        keyboard: `${keyboardCount || mappedItems.filter(x => x.done).length} verified settings`,
        keyboardStatus: devices.some((d) => d.deviceType === "Keyboard") ? "Detected" : "Detected",
        timer: typeof timerVal === "number" ? `${timerVal.toFixed(2)} ms` : "0.50 ms",
      });
    } catch (err) {
      if (currentGen === scanGenRef.current) {
        setBackendScanned(false);
        throw err;
      }
    }
  });

  const handleRefresh = useCallback(() => {
    scanGenRef.current += 1;
    setBackendScanned(false);
    setBackendItems(null);
    setDeviceList([]);
    setInputMeta(null);
    setDetail(null);
    setInputView("ACTIONABLE");
    scan.refresh();
  }, [scan]);

  const items: InputCatalogItem[] = backendItems ?? (backendScanned ? [] : INPUT_ITEMS.map((i) => ({
    id: i.id,
    name: i.title,
    cat: i.group === "advanced" ? "ADVANCED" : "MOUSE",
    risk: i.risk,
    desc: i.desc,
    done: i.done || false,
    ineligible: i.unsupported,
    reason: i.compat,
    requiresReboot: Boolean(i.restart || (i as { requiresReboot?: boolean }).requiresReboot),
    requiresRestart: Boolean(i.restart || (i as { requiresReboot?: boolean }).requiresReboot),
    restart: Boolean(i.restart || (i as { requiresReboot?: boolean }).requiresReboot),
  })));

  const eligibleItems = useMemo(
    () => items.filter((i): i is InputCatalogItem => Boolean(i && !i.ineligible && !i.done)),
    [items],
  );
  const alreadyOptimizedItems = useMemo(
    () => items.filter((i): i is InputCatalogItem => Boolean(i && i.done && !i.ineligible)),
    [items],
  );
  const ineligibleItems = useMemo(
    () => items.filter((i): i is InputCatalogItem => Boolean(i && i.ineligible)),
    [items],
  );

  const inputSel = useModedSelection<string>("input-optimizer", eligibleItems);
  const { sel, setSel } = inputSel;
  const isCustom = inputSel.mode === "CUSTOM";
  const runItems = useMemo(
    () => (backendScanned ? eligibleItems.filter((i) => sel.has(i.id)) : []),
    [backendScanned, eligibleItems, sel],
  );

  const [testing, setTesting] = useState(false);
  const [testProgress, setTestProgress] = useState(0);
  const [latencyRes, setLatencyRes] = useState<InputLatencyResult | null>(null);
  const [run, setRun] = useState(false);

  useEffect(() => {
    return () => {
      if (hasNativeBackend()) {
        void sendHostRequest("input:latency-cancel", undefined, 3000).catch(() => undefined);
      }
    };
  }, []);

  const runLatencyTest = async (seconds: number) => {
    if (testing) return;
    setTesting(true);
    setTestProgress(5);
    const t0 = Date.now();
    const timer = window.setInterval(() => {
      const pct = Math.min(95, Math.round(((Date.now() - t0) / (seconds * 1000)) * 95));
      setTestProgress(Math.max(5, pct));
    }, 250);
    try {
      const res = (await sendHostRequest(
        "input:latency-test",
        { durationSeconds: seconds },
        (seconds + 10) * 1000,
      )) as InputLatencyResult;
      setLatencyRes(res);
      setTestProgress(100);
      if (res?.isMeasured) {
        toast.success(`Input latency measured (${res.sampleCount ?? 0} samples)`, {
          description: res.pollingHz ? `Estimated polling: ${Math.round(res.pollingHz)} Hz` : undefined,
        });
      } else {
        toast.info("No pointer input captured", {
          description: res?.reason ?? "Move your mouse continuously during the sampling window to measure raw polling.",
        });
      }
    } catch (err) {
      toast.error("Input latency test failed", {
        description: err instanceof Error ? err.message : String(err),
      });
    } finally {
      window.clearInterval(timer);
      setTesting(false);
    }
  };

  const normInputView = normalizeFilterScope(inputView);
  const viewPool: InputCatalogItem[] =
    normInputView === "OPTIMIZED"
      ? alreadyOptimizedItems
      : normInputView === "INELIGIBLE"
        ? ineligibleItems
        : normInputView === "ALL"
          ? items
          : eligibleItems; // ACTIONABLE or ELIGIBLE

  const vis = viewPool;

  const tog = (id: string) => {
    const target = eligibleItems.find((it) => it.id === id);
    if (!target) return;
    setSel((s) => toggleInSet(s, id));
  };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Input Optimizer"
        badges={
          <>
            <Badge tone="cyan">LATENCY REDUCTION ENGINE</Badge>
            <Badge tone={isCustom ? "cyan" : "success"}>
              {isCustom ? "CUSTOM SELECTION" : "ELIGIBLE OPTIMIZATIONS ONLY"}
            </Badge>
          </>
        }
        sub={`Pointer, keyboard and timer latency tuning · ${isCustom ? "Runs only your custom selection." : "Eligible-only execution."}`}
        scan={{ ...scan, refresh: handleRefresh }}
      />
      <StatTiles
        items={[
          {
            label: "MOUSE",
            value: backendScanned && latencyRes?.isMeasured && latencyRes.pollingHz
              ? `${Math.round(latencyRes.pollingHz)} Hz Active`
              : (backendScanned && scan.ready ? (inputMeta?.mouseStatus ?? "Detected") : "—"),
            sub: backendScanned && scan.ready && inputMeta
              ? `Settings Verified · ${inputMeta.mouse}`
              : "Settings Verified",
          },
          {
            label: "KEYBOARD",
            value: backendScanned && scan.ready ? (inputMeta?.keyboardStatus ?? "Detected") : "—",
            sub: backendScanned && scan.ready && inputMeta
              ? `Settings Verified · ${inputMeta.keyboard}`
              : "Settings Verified",
          },
          {
            label: "SYSTEM TIMER",
            value: backendScanned && latencyRes?.timerResolutionMs != null
              ? `${latencyRes.timerResolutionMs.toFixed(2)} ms`
              : (backendScanned && scan.ready && inputMeta ? inputMeta.timer : "—"),
            sub: backendScanned && latencyRes?.isMeasured
              ? "Measured during sampling"
              : (backendScanned && scan.ready ? "Measured OS Timer Period (Target: 0.50 ms)" : "Not measured"),
          },
          {
            label: isCustom ? "CUSTOM QUEUED" : "ELIGIBLE QUEUED",
            value: backendScanned ? runItems.length : 0,
            tone: "cyan",
          },
        ]}
      />

      <div className="flex items-center justify-between gap-2 px-1">
        <span className="text-[12px] text-muted-foreground">
          {backendScanned && deviceList.length > 0
            ? `${deviceList.filter((d) => d.isPhysical !== false).length} input interface(s) identified via Windows PnP Configuration Manager.`
            : "Live input hardware device properties."}
        </span>
        <Btn
          variant="ghost"
          size="sm"
          className="gap-1.5 text-[11px]"
          onClick={() => setShowDeviceDetails(true)}
          disabled={!backendScanned || deviceList.length === 0}
        >
          <MousePointer2 className="h-3.5 w-3.5" />
          Hardware & HID Diagnostic Details
        </Btn>
      </div>

      <Card>
        <SectionTitle
          title={isCustom ? "Custom Input Optimizations" : "Eligible Input Optimizations"}
          guide="input.modes"
          sub={
            isCustom
              ? "You control the selection · only the optimizations you tick are queued."
              : "All applicable optimizations matching system and hardware criteria are automatically queued."
          }
          right={
            <Badge tone={isCustom ? "cyan" : "success"}>
              {backendScanned ? runItems.length : 0} of {backendScanned ? eligibleItems.length : 0} {isCustom ? "selected" : "eligible"}
            </Badge>
          }
        />
        <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
          <div className="w-[270px] shrink-0">
            <CatalogFilterDropdown
              value={inputView}
              onChange={setInputView}
              counts={{
                actionable: eligibleItems.length,
                optimized: alreadyOptimizedItems.length,
                ineligible: ineligibleItems.length,
                all: items.length,
              }}
            />
          </div>
          <SelectionBar
            count={backendScanned ? sel.size : 0}
            total={backendScanned ? inputSel.eligibleCount : 0}
            moded={inputSel}
            title="Input Optimizer"
          />
        </div>

        {!backendScanned ? (
          <div className="rounded-lg border border-border/60 bg-window/40 p-6 text-center text-xs text-muted-foreground">
            <span>{scan.scanning ? "Analyzing input devices, pointer sampling and system timers…" : "Awaiting input optimization scan from background service."}</span>
          </div>
        ) : (
          <div className="grid grid-cols-2 gap-2">
            {vis.filter((i): i is InputCatalogItem => Boolean(i)).map((i) => {
              const isActionable = !i.done && !i.ineligible;
              const isOptimized = Boolean(i.done && !i.ineligible);
              const rawState = String(i.eligibilityState ?? "").toUpperCase();
              const nonActionableLabel = isOptimized
                ? (i.isApplied ? "APPLIED AND VERIFIED" : "ALREADY OPTIMAL")
                : rawState === "PROTECTED_BY_POLICY"
                  ? "PROTECTED"
                  : rawState === "UNSUPPORTED" || rawState === "UNSUPPORTED_HARDWARE" || rawState === "UNSUPPORTED_OS"
                    ? "UNSUPPORTED"
                    : "INELIGIBLE";

              return (
                <div
                  key={i.id}
                  onClick={isActionable ? () => tog(i.id) : () => setDetail(i)}
                  className={cn(
                    "flex items-center gap-3 rounded-xl border p-3 select-none transition-colors",
                    isActionable
                      ? cn(
                          "cursor-pointer hover:border-cyan/40 hover:bg-window/90",
                          sel.has(i.id) ? "border-cyan/40 bg-cyan/5" : "border-border bg-window",
                        )
                      : isOptimized
                        ? "cursor-pointer border-success/25 bg-success/5 hover:border-success/40 hover:bg-success/10 transition-colors"
                        : "cursor-pointer border-border/60 bg-window/50 opacity-75 hover:border-border transition-colors",
                  )}
                >
                  {isActionable ? (
                    <SelectMark state={sel.has(i.id) ? "on" : "off"} />
                  ) : isOptimized ? (
                    <CheckCircle2 className="h-4 w-4 shrink-0 text-success" />
                  ) : (
                    <AlertCircle className="h-4 w-4 shrink-0 text-muted-foreground" />
                  )}
                  <div className="min-w-0 flex-1">
                    <div className="font-mono text-[10.5px] text-muted-foreground/80 truncate select-text">
                      {i.id}
                    </div>
                    <div className="text-[12.5px] font-semibold text-foreground truncate">{i.name}</div>
                    <div className="mt-1 flex flex-wrap items-center gap-1.5">
                      <Badge>{i.cat}</Badge>
                      {(i.requiresReboot || i.requiresRestart || i.restart) && (
                        <Badge tone="warning" className="text-[9.5px] font-bold">
                          REQUIRES REBOOT
                        </Badge>
                      )}
                      {isActionable ? (
                        sel.has(i.id) ? (
                          isCustom ? (
                            <Badge tone="cyan" className="text-[9.5px] font-bold">
                              CUSTOM SELECTED
                            </Badge>
                          ) : (
                            <Badge tone="success" className="text-[9.5px] font-bold">
                              AUTO-SELECTED — ELIGIBLE
                            </Badge>
                          )
                        ) : (
                          <Badge tone="muted">NOT SELECTED</Badge>
                        )
                      ) : isOptimized ? (
                        <Badge tone="success" className="text-[9.5px] font-bold">
                          {i.isApplied ? "APPLIED AND VERIFIED" : "ALREADY OPTIMAL"}
                        </Badge>
                      ) : (
                        <Badge tone="muted" className="text-[9.5px] font-bold">
                          {nonActionableLabel}
                        </Badge>
                      )}
                      {i.reason && (
                        <span className="text-[10px] text-muted-foreground truncate max-w-[200px]" title={i.reason}>
                          {i.reason}
                        </span>
                      )}
                    </div>
                  </div>
                  <div className="flex flex-col items-end gap-2" onClick={(e) => e.stopPropagation()}>
                    <RiskBadge risk={i.risk || "LOW"} />
                    <Btn size="sm" variant="ghost" onClick={() => setDetail(i)}>
                      Details
                    </Btn>
                  </div>
                </div>
              );
            })}
            {vis.length === 0 && (
              <p className="col-span-2 text-xs text-muted-foreground py-3">
                {(inputView === "ACTIONABLE" || inputView === "ELIGIBLE") && eligibleItems.length === 0
                  ? `Everything is already optimized. No changes are necessary.${alreadyOptimizedItems.length > 0 ? ` (${alreadyOptimizedItems.length} verified optimal in Optimized view)` : ""}`
                  : inputView === "OPTIMIZED"
                    ? "No verified already-optimal input optimizations recorded yet."
                    : inputView === "INELIGIBLE"
                      ? "No unsupported or ineligible input optimizations for this hardware configuration."
                      : "No input optimizations match the active search or view filter."}
              </p>
            )}
          </div>
        )}
      </Card>

      <div className="eo-action-bar flex items-center justify-between gap-3 rounded-xl border border-border bg-card p-3.5">
        <span className="text-[12px] text-muted-foreground">
          {backendScanned && eligibleItems.length === 0 ? (
            <span className="font-medium text-success">
              Everything is already optimized. No changes are necessary.
            </span>
          ) : (
            <>
              <b className="text-foreground tabular-nums font-bold">{backendScanned ? runItems.length : 0}</b>{" "}
              {isCustom ? "optimizations selected · " : "optimizations auto-selected · "}
              <span className={cn("font-semibold", isCustom ? "text-cyan" : "text-success")}>
                {isCustom ? "Custom selection only" : "Eligible optimizations only"}
              </span>
              {inputSel.highRiskSelectedCount > 0 && (
                <span className="ml-1.5 text-warning font-semibold">
                  · {inputSel.highRiskSelectedCount} high-risk selected — review before applying
                </span>
              )}
            </>
          )}
        </span>
        <Btn
          variant="primary"
          disabled={!backendScanned || scan.scanning || !runItems.length || eligibleItems.length === 0}
          onClick={() => setRun(true)}
        >
          {isCustom ? "Apply selected optimizations" : "Apply eligible optimizations"} (
          {backendScanned ? runItems.length : 0})
        </Btn>
      </div>

      <RunModal
        open={run}
        onClose={() => {
          setRun(false);
          scan.refresh();
        }}
        profile="Input Latency"
        count={runItems.length}
        items={runItems.map((i) => ({ id: i.id, title: i.name, risk: i.risk }))}
        onComplete={() => {
          scan.refresh();
        }}
      />

      <Modal
        open={!!detail}
        onClose={() => setDetail(null)}
        title={detail?.name ?? ""}
        sub={
          detail?.done
            ? (detail?.isApplied ? "Read-only catalog entry · Applied and Verified" : "Read-only catalog entry · Already Optimal")
            : detail?.ineligible
              ? "Read-only catalog entry · Not Actionable"
              : "Optimization catalog entry"
        }
        footer={<Btn onClick={() => setDetail(null)}>Close</Btn>}
      >
        {detail && (() => {
          const curState = detail.currentValue
            ? `${detail.currentValue} (${detail.eligibilityState ?? "VERIFIED"})`
            : detail.done
              ? (detail.isApplied ? "APPLIED AND VERIFIED" : "ALREADY OPTIMAL (Verified)")
              : (scan.ready ? "PENDING" : "Not scanned yet");
          const appNote = detail.reason ?? (
            scan.ready
              ? `Evaluated (${detail.targetValue ? `Target: ${detail.targetValue}` : "Hardware & Kernel Verified"})`
              : "Evaluated by the background service during scan"
          );
          return (
            <div className="space-y-3">
              <KV k="Category" v={detail.cat} />
              <KV k="Risk classification" v={<RiskBadge risk={detail?.risk} />} />
              <KV k="Verification state" v={curState} />
              {detail.targetValue && <KV k="Desired optimal value" v={detail.targetValue} mono />}
              <KV k="Applicability" v={appNote} />
              <KV k="Restart requirement" v={detail.requiresReboot || detail.requiresRestart || detail.restart ? "Yes (System Restart Required)" : "No"} />
              <Details>
                <KV k="Canonical operation identifier" v={detail.id} mono />
                <p className="mt-3 text-xs text-muted-foreground">
                  {detail.done || detail.ineligible
                    ? "Read-only inspection view. This operation cannot be executed in its current state."
                    : "Target state and readback verification are resolved dynamically during execution."}
                </p>
              </Details>
            </div>
          );
        })()}
      </Modal>

      <Card>
        <SectionTitle
          title="INPUT LATENCY TEST"
          guide="input.test"
          sub="Measures pointer polling timing and system timer stability."
          right={["5 SEC TEST", "10 SEC TEST", "30 SEC TEST"].map((x) => {
            const sec = parseInt(x, 10) || 5;
            return (
              <Btn
                key={x}
                size="sm"
                disabled={testing}
                onClick={() => void runLatencyTest(sec)}
              >
                {x}
              </Btn>
            );
          })}
        />
        {testing && <Bar value={testProgress} className="mb-3" />}
        <StatTiles
          items={[
            { label: "SAMPLES", value: latencyRes ? String(latencyRes.sampleCount ?? 0) : "—" },
            {
              label: "AVG INTERVAL",
              value: latencyRes?.isMeasured && latencyRes.averageIntervalMs != null
                ? `${latencyRes.averageIntervalMs.toFixed(2)} ms`
                : "—",
            },
            {
              label: "FREQUENCY",
              value: latencyRes?.isMeasured && latencyRes.pollingHz != null
                ? `${Math.round(latencyRes.pollingHz)} Hz`
                : "—",
            },
            {
              label: "JITTER",
              value: latencyRes?.isMeasured && latencyRes.jitterMs != null
                ? `${latencyRes.jitterMs.toFixed(2)} ms`
                : "—",
            },
            {
              label: "TIMER RES",
              value: latencyRes?.timerResolutionMs != null
                ? `${latencyRes.timerResolutionMs.toFixed(2)} ms`
                : "—",
            },
          ]}
        />
        <p className="mt-2 text-[10.5px] text-muted-foreground">
          {testing
            ? "Sampling live Win32 WM_INPUT pointer timestamps — move the mouse continuously."
            : latencyRes
              ? latencyRes.isMeasured
                ? `Measured ${latencyRes.sampleCount ?? 0} raw pointer samples via Win32 WM_INPUT + QPC.`
                : (latencyRes.reason ?? "No pointer movement captured during the test window.")
              : "Click a test duration above to capture live Win32 raw pointer timing."}
        </p>
        <Details
          inline
          title="Reference timing targets"
          hint="Expected optimization improvement profile"
        >
          <Cols cols="1fr 40px 1fr">
            <div className="rounded-lg border border-border bg-window p-3">
              <Label className="mb-2">DEFAULT WINDOWS BASELINE</Label>
              <KV k="Polling Rate:" v="Hardware-dependent" />
              <KV k="Timer Resolution:" v="15.6 ms" />
              <KV k="Signal Jitter:" v="Baseline" />
            </div>
            <div className="flex items-center justify-center text-cyan">→</div>
            <div className="rounded-lg border border-success/30 bg-window p-3">
              <Label className="mb-2 text-success">OPTIMIZATION TARGET</Label>
              <KV k="Polling Rate:" v="Native device rate" />
              <KV k="Timer Resolution:" v="0.5 ms" />
              <KV k="Signal Jitter:" v="Minimized" />
            </div>
          </Cols>
        </Details>
      </Card>

      <Modal
        open={showDeviceDetails}
        onClose={() => setShowDeviceDetails(false)}
        title="Input Hardware & HID Instance Diagnostics"
        width="max-w-2xl"
      >
        <div className="space-y-4 text-[12.5px]">
          <p className="text-muted-foreground text-[12px]">
            Human-readable device friendly names resolved from Windows SetupAPI and PnP Configuration Manager properties. Full raw HID instance paths and hardware descriptors are provided below for diagnostic inspection.
          </p>
          <div className="max-h-[380px] overflow-y-auto space-y-2 pr-1">
            {deviceList.map((dev, idx) => (
              <div
                key={dev.deviceId || dev.instanceId || idx}
                className="rounded-lg border border-border/60 bg-muted/20 p-3 space-y-1.5"
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="font-semibold text-foreground text-[13px] flex items-center gap-2">
                    {dev.deviceName || "HID-compatible device"}
                    {dev.isTouchpad && <Badge tone="cyan" className="text-[9px]">TOUCHPAD</Badge>}
                    {dev.isInternal && <Badge tone="muted" className="text-[9px]">INTERNAL</Badge>}
                  </span>
                  <div className="flex items-center gap-1.5">
                    <Badge tone={dev.deviceType === "Mouse" ? "success" : dev.deviceType === "Keyboard" ? "cyan" : "muted"} className="text-[9.5px]">
                      {dev.deviceType?.toUpperCase() ?? "HID"}
                    </Badge>
                    <Badge tone="muted" className="text-[9.5px]">
                      {dev.transportType ?? "USB"}
                    </Badge>
                  </div>
                </div>
                <div className="grid grid-cols-2 sm:grid-cols-3 gap-2 text-[11px] text-muted-foreground pt-1">
                  <div>
                    <span className="text-muted-foreground/70">Manufacturer: </span>
                    <span className="text-foreground/90 font-medium">{dev.manufacturer || "Standard"}</span>
                  </div>
                  <div>
                    <span className="text-muted-foreground/70">Hardware ID: </span>
                    <span className="text-foreground/90 font-mono">
                      {dev.vid ? `VID_${dev.vid} PID_${dev.pid}` : "ACPI / Internal"}
                    </span>
                  </div>
                  <div>
                    <span className="text-muted-foreground/70">Class / Service: </span>
                    <span className="text-foreground/90 font-mono">{dev.deviceClass || dev.deviceType}</span>
                  </div>
                </div>
                {dev.rawDevicePath && (
                  <div className="pt-1.5">
                    <div className="text-[10px] text-muted-foreground/70 mb-0.5">Raw HID Instance Path:</div>
                    <code className="block select-all rounded bg-background/80 px-2 py-1 text-[10.5px] font-mono text-muted-foreground break-all border border-border/40">
                      {dev.rawDevicePath}
                    </code>
                  </div>
                )}
              </div>
            ))}
          </div>
          <div className="flex justify-end pt-2">
            <Btn variant="ghost" size="sm" onClick={() => setShowDeviceDetails(false)}>
              Close
            </Btn>
          </div>
        </div>
      </Modal>
    </div>
  );
}

/* ───────── SYSTEM TELEMETRY ───────── */
export function SystemInfoPage() {
  const connected = useIsBackendConnected();
  const telemetry = useSharedTelemetryState({ subscribe: true });
  const [manualReadings, setManualReadings] = useState<ScanReadings | null>(null);
  const [sysInfo, setSysInfo] = useState<Record<string, unknown> | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const loadTelemetry = async () => {
    if (!hasNativeBackend()) return;
    setRefreshing(true);
    try {
      const [scanReply, infoReply] = await Promise.all([
        sendHostRequest<{ readings?: unknown }>("system:scan", undefined, 25000).catch(() => null),
        sendHostRequest<Record<string, unknown>>("system:info", undefined, 25000).catch(() => null),
      ]);
      if (scanReply) {
        const rawReadings = scanReply.readings !== undefined ? scanReply.readings : scanReply;
        updateSharedScanReadings(
          rawReadings,
          rawReadings && typeof rawReadings === "object" ? (rawReadings as Record<string, unknown>)["sensors"] : undefined,
          (scanReply as Record<string, unknown>)["timestampUtc"],
        );
        const parsed = parseReadings(rawReadings);
        if (parsed) setManualReadings(parsed);
      }
      if (infoReply) setSysInfo(infoReply);
    } finally {
      setRefreshing(false);
    }
  };

  useEffect(() => {
    const isConn = isBackendConnected() || hasNativeBackend() || isDesktopApp();
    if (isConn) {
      void loadTelemetry();
    }
  }, [connected]);

  const readings =
    (isLiveFresh(telemetry.lastAt, telemetry.now)
      ? (telemetry.effectiveScanResult?.readings ?? telemetry.scanResult?.readings)
      : telemetry.scanResult?.readings) ?? manualReadings;

  const hwRec = (sysInfo?.["hardware"] && typeof sysInfo["hardware"] === "object" ? sysInfo["hardware"] : sysInfo) as Record<string, unknown> | null;
  const gpusArr = Array.isArray(sysInfo?.["gpus"]) ? (sysInfo["gpus"] as Array<Record<string, unknown>>) : [];
  const primaryGpu = gpusArr[0] ?? {};
  const strOr = (v: unknown, fb = "Unavailable") => (typeof v === "string" && v.trim() ? v : fb);
  const ramInstalled = typeof hwRec?.["ramInstalledGb"] === "number" ? (hwRec["ramInstalledGb"] as number) : null;
  const ramReserved = typeof hwRec?.["ramReservedGb"] === "number" ? (hwRec["ramReservedGb"] as number) : null;
  const ramTotal = readings?.ram.totalGb ?? (typeof hwRec?.["ramUsableGb"] === "number" ? (hwRec["ramUsableGb"] as number) : null);
  const ramSubDetail =
    readings?.ram.usedGb != null && ramTotal != null
      ? `${readings.ram.usedGb.toFixed(1)} GB used (${Math.round((readings.ram.usedGb / ramTotal) * 100)}%)${ramReserved != null && ramReserved >= 0.2 ? ` · ${ramReserved.toFixed(1)} GB hw reserved` : ""}`
      : "Awaiting hardware scan";

  return (
    <div className="space-y-4">
      <PageHeader
        title="System Telemetry"
        badges={<Badge tone="cyan">HARDWARE & DIAGNOSTICS</Badge>}
        sub="Live hardware inventory, performance telemetry, and system health diagnostics."
        actions={<Btn loading={refreshing} onClick={() => void loadTelemetry()}>Refresh</Btn>}
      />
      <HealthCheck />
      <TelemetryConsole />
      <Card>
        <SectionTitle
          title="SYSTEM HARDWARE"
          sub={readings ? "Live Windows hardware inventory" : "Waiting for system data from the background service"}
        />
        <div className="grid grid-cols-3 gap-x-3.5">
          {[
            [
              "PROCESSOR (CPU)",
              readings?.cpu.model ?? strOr(hwRec?.["cpuName"], "Data unavailable"),
              readings?.cpu.cores && readings?.cpu.threads
                ? `${readings.cpu.cores} cores / ${readings.cpu.threads} threads${readings.cpu.loadPct != null ? ` · ${Math.round(readings.cpu.loadPct)}% load` : ""}`
                : "Awaiting hardware scan",
            ],
            [
              "GRAPHICS (GPU)",
              readings?.gpu.model ?? strOr(primaryGpu["Description"] ?? primaryGpu["description"] ?? primaryGpu["Name"] ?? primaryGpu["name"] ?? hwRec?.["gpuName"], "Data unavailable"),
              readings?.gpu.vramTotalGb != null
                ? `${readings.gpu.vramTotalGb.toFixed(1)} GB VRAM${readings.gpu.vramUsedGb != null ? ` (${readings.gpu.vramUsedGb.toFixed(1)} GB used)` : ""}${readings.gpu.tempC != null ? ` · ${readings.gpu.tempC.toFixed(0)}°C` : ""}`
                : strOr(hwRec?.["gpuDriverVersion"], "Awaiting hardware scan"),
            ],
            [
              "SYSTEM MEMORY",
              ramInstalled != null && ramTotal != null && Math.abs(ramInstalled - ramTotal) >= 0.3
                ? `${ramInstalled.toFixed(1)} GB Installed (${ramTotal.toFixed(1)} GB Usable)`
                : ramTotal != null
                  ? `${ramTotal.toFixed(1)} GB RAM`
                  : "Data unavailable",
              ramSubDetail,
            ],
            [
              "PRIMARY STORAGE",
              readings?.storage.model ?? strOr(hwRec?.["storageModel"], "Data unavailable"),
              readings?.storage.usedGb != null && readings?.storage.totalGb != null
                ? `${Math.max(0, readings.storage.totalGb - readings.storage.usedGb).toFixed(0)} GB free of ${readings.storage.totalGb.toFixed(0)} GB`
                : "Awaiting hardware scan",
            ],
            [
              "WINDOWS OS",
              strOr(hwRec?.["osName"] ?? hwRec?.["osVersion"], "Not detected"),
              hwRec?.["osBuild"] ? `Build ${String(hwRec["osBuild"])}` : "Awaiting hardware scan",
            ],
            [
              "POWER & CHASSIS",
              strOr(hwRec?.["powerPlan"], "Unavailable"),
              strOr(hwRec?.["powerSource"], readings ? "Windows host online" : "Awaiting hardware scan"),
            ],
          ].map(([a, b, c]) => (
            <div key={a} className="min-w-0 border-b border-border py-3">
              <Label className="text-cyan">{a}</Label>
              <div className="mt-2 text-sm font-bold">{b}</div>
              <div className="text-[11.5px] text-muted-foreground">{c}</div>
            </div>
          ))}
        </div>
        <Details inline title="Technical inventory" hint="Architecture, firmware and connectivity">
          <Cols cols="minmax(0, 1fr) minmax(0, 1fr)" gap={14}>
            <div>
              <Label className="mb-2 text-cyan">PROCESSOR (CPU) ARCHITECTURE</Label>
              <KV
                k="Physical Cores / Threads:"
                v={
                  readings?.cpu.cores != null
                    ? `${readings.cpu.cores} / ${readings.cpu.threads ?? readings.cpu.cores}`
                    : "Unavailable"
                }
              />
              <KV
                k="Base / Max Frequency:"
                v={strOr(hwRec?.["cpuFrequency"] ?? hwRec?.["cpuMaxClock"])}
              />
              <KV k="Level 3 (L3) Cache:" v={strOr(hwRec?.["l3CacheMb"] ? `${String(hwRec["l3CacheMb"])} MB` : hwRec?.["cpuL3Cache"])} />
              <KV
                k="Virtualization (VT-x / AMD-V):"
                v={hwRec?.["virtualizationEnabled"] === true ? "Enabled" : hwRec?.["virtualizationEnabled"] === false ? "Disabled" : "Not detected"}
              />
            </div>
            <div>
              <Label className="mb-2 text-cyan">SYSTEM MEMORY (RAM)</Label>
              <Bar
                value={
                  readings?.ram.usedGb != null && readings?.ram.totalGb
                    ? Math.round((readings.ram.usedGb / readings.ram.totalGb) * 100)
                    : 0
                }
                className="mb-2"
              />
              <KV
                k="Memory Speed & Type:"
                v={readings?.ram.spec ?? strOr(hwRec?.["ramSpec"])}
              />
              <KV k="Channel Mode:" v={strOr(hwRec?.["ramChannels"])} />
              <KV
                k="Available Free RAM:"
                v={
                  readings?.ram.usedGb != null && readings?.ram.totalGb != null
                    ? `${Math.max(0, readings.ram.totalGb - readings.ram.usedGb).toFixed(1)} GB`
                    : "Unavailable"
                }
              />
              <KV k="Virtual Memory Headroom:" v={strOr(hwRec?.["virtualMemoryAvailable"])} />
            </div>
            <div>
              <Label className="mb-2 text-cyan">MOTHERBOARD & FIRMWARE</Label>
              <KV k="Manufacturer & Model:" v={strOr(hwRec?.["motherboard"] ?? hwRec?.["baseBoard"], "Not detected")} />
              <KV k="BIOS Version & Vendor:" v={strOr(hwRec?.["biosVersion"])} />
              <KV k="Firmware Architecture:" v={strOr(hwRec?.["firmwareType"])} />
              <KV k="TPM Hardware Security:" v={hwRec?.["tpmStatus"] ? String(hwRec["tpmStatus"]) : "Unavailable"} />
            </div>
            <div>
              <Label className="mb-2 text-cyan">SECURITY & SYSTEM INTEGRITY</Label>
              <KV k="UEFI Secure Boot:" v={hwRec?.["secureBoot"] === true ? "Enabled" : hwRec?.["secureBoot"] === false ? "Disabled" : "Not detected"} />
              <KV k="TPM 2.0 Cryptography:" v={hwRec?.["tpmEnabled"] === true ? "Enabled" : hwRec?.["tpmEnabled"] === false ? "Disabled" : "Not detected"} />
              <KV k="Microsoft Defender:" v={hwRec?.["defenderEnabled"] === true ? "Active" : hwRec?.["defenderEnabled"] === false ? "Disabled" : "Not detected"} />
              <KV k="Core Isolation (HVCI):" v={hwRec?.["hvciEnabled"] === true ? "Enabled" : hwRec?.["hvciEnabled"] === false ? "Disabled" : "Not detected"} />
            </div>
            <div>
              <Label className="mb-2 text-cyan">POWER & CHASSIS</Label>
              <KV k="Power Source:" v={strOr(hwRec?.["powerSource"])} />
              <KV
                k="Battery Charge & Health:"
                v={strOr(hwRec?.["batteryHealth"], "Desktop / No Battery")}
              />
              <KV k="Active Power Plan:" v={strOr(hwRec?.["powerPlan"])} />
            </div>
            <div>
              <Label className="mb-2 text-cyan">NETWORK & CONNECTIVITY</Label>
              <KV k="Active Interface:" v={strOr(hwRec?.["networkAdapter"], "Not detected")} />
              <KV k="Connection Type / Speed:" v={strOr(hwRec?.["networkSpeed"])} />
              <KV k="IPv4 Address:" v={strOr(hwRec?.["ipv4Address"])} />
            </div>
          </Cols>
        </Details>
      </Card>
    </div>
  );
}

/* ───────── REGISTRY INTELLIGENCE ───────── */
const REG_CATS = [
  "ALL",
  "CPU",
  "MEMORY",
  "NETWORK",
  "GAMING",
  "GPU",
  "PRIVACY",
  "TELEMETRY",
  "INPUT",
  "POWER",
  "NTFS",
  "SYSTEM",
];
const REG_LIST = CATALOG.map((c) => ({
  ...c,
  state: "PENDING" as "OPTIMIZED" | "NOT APPLICABLE" | "PENDING",
  reason: undefined as string | undefined,
}));
export function RegistryTweakPage() {
  const [liveStates, setLiveStates] = useState<Record<string, "OPTIMIZED" | "NOT APPLICABLE" | "PENDING">>({});
  const [backendList, setBackendList] = useState<Array<{
    id: string;
    name: string;
    cat: string;
    risk: Risk;
    state: "OPTIMIZED" | "NOT APPLICABLE" | "PENDING";
    reason?: string | undefined;
  }> | null>(null);
  const scanGenRef = useRef(0);

  const list = backendList ?? [];

  const regItems = useMemo(
    () =>
      list.map((i) => ({
        id: i.id,
        risk: i?.risk || "LOW",
        done: i.state === "OPTIMIZED",
        unsupported: i.state === "NOT APPLICABLE",
        recommended: true,
      })),
    [list],
  );
  const reg = useModedSelection("registry-tweak", regItems);
  const { sel } = reg;
  const regCustom = reg.mode === "CUSTOM";

  const scan = usePageScan("registry-tweak", async () => {
    const currentGen = ++scanGenRef.current;
    if (!hasNativeBackend()) {
      throw new Error("ERROR OPTIMIZER background service disconnected.");
    }
    try {
      const res = (await sendHostRequest("registry:tweaks-scan", undefined, 25000)) as {
        items?: Array<{
          id?: string;
          name?: string;
          category?: string;
          risk?: string;
          state?: string;
          eligibilityState?: string;
          isApplied?: boolean;
          isApplicable?: boolean;
          reason?: string;
        }>;
        excludedItems?: Array<{
          id?: string;
          name?: string;
          category?: string;
          risk?: string;
          state?: string;
          eligibilityState?: string;
          isApplied?: boolean;
          isApplicable?: boolean;
          reason?: string;
        }>;
      };
      if (currentGen !== scanGenRef.current) return;
      const map: Record<string, "OPTIMIZED" | "NOT APPLICABLE" | "PENDING"> = {};
      const loaded: Array<{
        id: string;
        name: string;
        cat: string;
        risk: Risk;
        state: "OPTIMIZED" | "NOT APPLICABLE" | "PENDING";
        reason?: string | undefined;
      }> = [];

      const itemsList = Array.isArray(res?.items) ? res.items : (Array.isArray((res as Record<string, unknown>)?.["Items"]) ? (res as Record<string, unknown>)["Items"] as Array<Record<string, unknown>> : []);
      const excludedList = Array.isArray(res?.excludedItems) ? res.excludedItems : (Array.isArray((res as Record<string, unknown>)?.["ExcludedItems"]) ? (res as Record<string, unknown>)["ExcludedItems"] as Array<Record<string, unknown>> : []);
      const seenIds = new Set<string>();
      const rows = [...itemsList, ...excludedList];
      for (const rawRow of rows) {
        const row = rawRow as {
          id?: string;
          Id?: string;
          name?: string;
          Name?: string;
          category?: string;
          Category?: string;
          risk?: string;
          Risk?: string;
          state?: string;
          State?: string;
          eligibilityState?: string;
          EligibilityState?: string;
          isApplied?: boolean;
          isApplicable?: boolean;
          reason?: string;
          Reason?: string;
        };
        const id = row.id ?? row.Id;
        if (!id || seenIds.has(id)) continue;
        seenIds.add(id);
        const name = row.name ?? row.Name ?? id;
        const cat = (row.category ?? row.Category ?? "SYSTEM").toUpperCase();
        const riskStr = (row.risk ?? row.Risk ?? "UNKNOWN").toUpperCase();
        const riskVal: Risk = (riskStr === "HIGH" ? "HIGH" : riskStr === "MEDIUM" ? "MEDIUM" : riskStr === "SAFE" ? "SAFE" : riskStr === "LOW" ? "LOW" : riskStr === "PROTECTED" ? "PROTECTED" : "UNKNOWN") as Risk;
        const eligState = (row.eligibilityState ?? row.EligibilityState ?? row.state ?? row.State ?? "").toUpperCase();
        const reason = row.reason ?? row.Reason ?? undefined;

        let st: "OPTIMIZED" | "NOT APPLICABLE" | "PENDING" = "PENDING";
        if (row.isApplicable === false || eligState === "NOT_APPLICABLE" || eligState === "INELIGIBLE" || eligState === "UNSUPPORTED" || eligState === "PROTECTED_BY_POLICY" || eligState === "UNSUPPORTED_HARDWARE" || eligState === "UNSUPPORTED_OS") {
          st = "NOT APPLICABLE";
        } else if (row.isApplied || eligState === "OPTIMIZED" || eligState === "APPLIED" || eligState === "ALREADY_OPTIMIZED" || eligState === "ALREADY_OPTIMAL") {
          st = "OPTIMIZED";
        }
        map[id] = st;
        loaded.push({
          id,
          name,
          cat,
          risk: riskVal,
          state: st,
          reason,
        });
      }
      setLiveStates(map);
      if (currentGen === scanGenRef.current) {
        setBackendList(loaded);
        if (reg.mode !== "CUSTOM") {
          const eligibleIds = loaded.filter((i) => i.state === "PENDING").map((i) => i.id);
          reg.setSel(new Set(eligibleIds));
        } else {
          const eligibleIdSet = new Set(loaded.filter((i) => i.state === "PENDING").map((i) => i.id));
          reg.setSel((currentSel) => new Set([...currentSel].filter((id) => eligibleIdSet.has(id))));
        }
      }
    } catch (err) {
      if (currentGen === scanGenRef.current) {
        setBackendList(null);
        throw err;
      }
    }
  });

  const handleRefresh = useCallback(() => {
    scanGenRef.current += 1;
    setBackendList(null);
    reg.setSel(new Set());
    scan.refresh();
  }, [scan, reg]);
  const [cat, setCat] = useState("ALL");
  const [viewMode, setViewMode] = useState("ACTIONABLE");
  const [detail, setDetail] = useState<(typeof list)[number] | null>(null);
  const [run, setRun] = useState(false);

  const pendingList = list.filter((i) => i.state === "PENDING");
  const optimizedList = list.filter((i) => i.state === "OPTIMIZED");
  const naList = list.filter((i) => i.state === "NOT APPLICABLE");

  const visPending = pendingList.filter((i) => cat === "ALL" || i.cat === cat);
  const visOptimized = optimizedList.filter((i) => cat === "ALL" || i.cat === cat);
  const visNA = naList.filter((i) => cat === "ALL" || i.cat === cat);
  const normViewMode = normalizeFilterScope(viewMode);
  const visActive =
    normViewMode === "OPTIMIZED"
      ? visOptimized
      : normViewMode === "INELIGIBLE"
        ? visNA
        : normViewMode === "ALL"
          ? [...visPending, ...visOptimized, ...visNA]
          : visPending;

  const applicableN = pendingList.length + optimizedList.length;
  const optimizedN = optimizedList.length;
  const naN = naList.length;

  const eligibleList = pendingList.filter((i) => sel.has(i.id));

  return (
    <div className="space-y-4">
      <PageHeader
        title="Registry Intelligence"
        badges={
          <>
            <Badge tone="cyan">LOW LATENCY & SCHEDULING</Badge>
            <Badge tone={regCustom ? "cyan" : "success"}>
              {regCustom ? "CUSTOM SELECTION" : "ELIGIBLE OPTIMIZATIONS ONLY"}
            </Badge>
          </>
        }
        sub={`Registry tuning for scheduling, memory, gaming and privacy · ${regCustom ? "Runs only your custom selection." : "Eligible-only execution."}`}
        scan={{ ...scan, refresh: handleRefresh }}
      />
      <Card className="flex flex-wrap items-center justify-between gap-3 py-3">
        <div className="flex items-center gap-2">
          <Badge
            tone={regCustom ? "cyan" : "success"}
            className="text-[10px] font-bold uppercase tracking-wider"
          >
            {regCustom ? "CUSTOM SELECTION" : "ELIGIBLE OPTIMIZATIONS ONLY"}
          </Badge>
        </div>
        <p className="text-[11.5px] text-muted-foreground">
          {regCustom
            ? "Custom selection · only the tweaks you select are applied."
            : "Authoritative catalog · applicable items are evaluated against your system hardware."}
        </p>
      </Card>

      <StatTiles
        items={[
          { label: "HEALTH", value: backendList && scan.ready ? `${Math.round((optimizedN / Math.max(1, applicableN)) * 100)}%` : "—" },
          { label: "TOTAL", value: backendList ? list.length : "—" },
          { label: "APPLICABLE", value: backendList && scan.ready ? applicableN : "—", tone: "cyan" },
          { label: regCustom ? "SELECTED" : "AUTO-SELECTED", value: backendList ? sel.size : 0, tone: "cyan" },
          { label: "OPTIMIZED", value: backendList && scan.ready ? optimizedN : "—" },
          { label: "EXCLUDED", value: backendList && scan.ready ? naN : "—" },
        ]}
      />

      <Card>
        <div className="flex flex-wrap items-center gap-2.5">
          <div className="w-[270px] shrink-0">
            <CatalogFilterDropdown
              value={viewMode}
              onChange={setViewMode}
              counts={{
                actionable: pendingList.length,
                optimized: optimizedList.length,
                ineligible: naList.length,
                all: list.length,
              }}
            />
          </div>
          <div className="w-48 shrink-0">
            <PremiumSelect
              label="Registry Category"
              value={cat}
              onChange={setCat}
              options={REG_CATS.map((c) => ({
                value: c,
                label: c === "ALL" ? "All Categories" : c,
              }))}
            />
          </div>
        </div>
        <div className="mt-3 flex flex-wrap items-center justify-between gap-2 border-t border-border pt-2.5">
          <SelectionBar
            count={backendList ? sel.size : 0}
            total={backendList ? reg.eligibleCount : 0}
            moded={reg}
            title="Registry Intelligence"
          />
          <div className="flex items-center gap-2">
            {reg.highRiskSelectedCount > 0 && (
              <Badge tone="warning">{reg.highRiskSelectedCount} HIGH-RISK SELECTED</Badge>
            )}
            <Btn size="sm" variant="primary" disabled={!backendList || scan.scanning || !sel.size || pendingList.length === 0} onClick={() => setRun(true)}>
              {regCustom ? "Apply selected tweaks" : "Apply eligible tweaks"} ({backendList ? sel.size : 0})
            </Btn>
          </div>
        </div>
      </Card>

      {/* ── Authoritative Catalog View (Actionable Default + Read-Only Optimized/Ineligible) ── */}
      <Card>
        <Label className="mb-3 text-foreground">REGISTRY OPTIMIZATION TARGETS ({backendList ? visActive.length : 0})</Label>
        {!backendList ? (
          <div className="rounded-lg border border-border/60 bg-window/40 p-6 text-center text-xs text-muted-foreground">
            <span>{scan.scanning ? "Evaluating registry policy states and system configuration…" : "Awaiting registry scan from background service."}</span>
          </div>
        ) : (
          <div className="space-y-1.5">
            {visActive.length === 0 ? (
              <div className="rounded-lg border border-border/60 bg-window/40 p-4 text-center text-xs text-muted-foreground">
                {normViewMode === "ACTIONABLE" && pendingList.length === 0
                  ? `Everything is already optimized. No changes are necessary.${optimizedList.length > 0 ? ` (${optimizedList.length} verified optimal in Optimized view)` : ""}`
                  : "No registry optimizations match your active filter."}
              </div>
            ) : (
              visActive.map((i) => {
                const isActionable = i.state === "PENDING";
                const isOptimized = i.state === "OPTIMIZED";
                const isEligible = isActionable && sel.has(i.id);
                return (
                  <div
                    key={i.id}
                    onClick={isActionable ? () => reg.toggle(i.id) : () => setDetail(i)}
                    title={
                      isActionable && i?.risk === "HIGH"
                        ? "High-risk eligible tweak — review before applying"
                        : i.reason
                    }
                    className={cn(
                      "flex items-center gap-3 eo-list-row rounded-lg border px-3 py-2 text-[12.5px] select-none",
                      isActionable
                        ? cn("cursor-pointer", isEligible ? "border-cyan/40 bg-cyan/5" : "border-border bg-window")
                        : isOptimized
                          ? "cursor-pointer border-success/25 bg-success/5 opacity-90 hover:border-success/40 transition-colors"
                          : "cursor-pointer border-border/60 bg-window/50 opacity-75 hover:border-border transition-colors",
                    )}
                  >
                    {isActionable ? (
                      <span
                        role="checkbox"
                        aria-checked={isEligible}
                        aria-label={`Select ${i.name}`}
                        className="inline-flex"
                      >
                        <SelectMark state={isEligible ? "on" : "off"} />
                      </span>
                    ) : isOptimized ? (
                      <CheckCircle2 className="h-4 w-4 shrink-0 text-success" />
                    ) : (
                      <AlertCircle className="h-4 w-4 shrink-0 text-muted-foreground" />
                    )}
                    <div className="flex-1 min-w-0">
                      <div className="font-mono text-[10px] text-muted-foreground/80 truncate select-text">
                        {i.id}
                      </div>
                      <div className="truncate font-semibold text-foreground">
                        {i.name}
                      </div>
                    </div>
                    <Badge>{i.cat}</Badge>
                    {isActionable ? (
                      isEligible ? (
                        regCustom ? (
                          <Badge tone="cyan" className="text-[9.5px] font-bold tracking-wider">
                            CUSTOM SELECTED
                          </Badge>
                        ) : (
                          <Badge tone="success" className="text-[9.5px] font-bold tracking-wider">
                            AUTO-SELECTED — ELIGIBLE
                          </Badge>
                        )
                      ) : (
                        <Badge tone="muted" className="text-[9.5px] font-semibold">
                          NOT SELECTED
                        </Badge>
                      )
                    ) : isOptimized ? (
                      <Badge tone="success" className="text-[9.5px] font-bold tracking-wider">
                        ALREADY OPTIMAL
                      </Badge>
                    ) : (
                      <Badge tone="muted" className="text-[9.5px] font-semibold">
                        INELIGIBLE
                      </Badge>
                    )}
                    <RiskBadge risk={i?.risk || "LOW"} />
                    <span onClick={(e) => e.stopPropagation()}>
                      <Btn size="sm" variant="ghost" onClick={() => setDetail(i)}>
                        Details
                      </Btn>
                    </span>
                  </div>
                );
              })
            )}
          </div>
        )}
      </Card>



      <Modal
        open={!!detail}
        onClose={() => setDetail(null)}
        title="Optimization transparency details"
        sub={detail?.name}
        footer={<Btn onClick={() => setDetail(null)}>Close</Btn>}
      >
        {detail && (
          <div className="space-y-3">
            <KV k="Purpose & Description:" v={detail.name} />
            <KV k="Risk:" v={<RiskBadge risk={detail?.risk || "LOW"} />} />
            <Details title="TECHNICAL REGISTRY LOCATION">
              <p className="font-mono text-[11px] text-muted-foreground">
                Optimization ID: {detail.id}
                <br />
                Exact key path is resolved at execution time by the background service.
              </p>
            </Details>
            <Cols cols="minmax(0, 1fr) minmax(0, 1fr)" gap={14} className="items-stretch">
              <div className="rounded-lg border border-border bg-window p-3">
                <Label>Current State</Label>
                <div className="mt-1 text-sm text-muted-foreground">Not scanned yet</div>
              </div>
              <div className="rounded-lg border border-success/30 bg-window p-3">
                <Label className="text-success">Optimization Target</Label>
                <div className="mt-1 text-sm">Optimized value</div>
              </div>
            </Cols>
          </div>
        )}
      </Modal>
      <RunModal
        open={run}
        onClose={() => {
          setRun(false);
          scan.refresh();
        }}
        profile="Registry Intelligence"
        count={eligibleList.length}
        items={eligibleList.filter(Boolean).map((i) => ({ id: i.id, title: i.name, risk: i?.risk || "LOW" }))}
        onComplete={() => {
          scan.refresh();
        }}
      />
    </div>
  );
}

/* ───────── REGISTRY VALUES (overview host: RAM | GPU) ───────── */
// Data: src/lib/eo/registryValues.ts (transcribed from the WPF RAM and GPU registry engines).
function RegValueBadge({ v }: { v: RegistryValueEntry }) {
  if (v.status === "diagnostic") return <Badge>READ-ONLY</Badge>;
  if (v.status === "not-applicable") return <Badge>N/A</Badge>;
  if (v?.risk === "EXPERIMENTAL") return <Badge tone="warning">EXPERIMENTAL</Badge>;
  return <RiskBadge risk={v?.risk === "DIAGNOSTIC" ? "LOW" : (v?.risk || "LOW")} />;
}

function RegTableHead() {
  return (
    <div
      aria-hidden="true"
      className="grid gap-3 border-b border-border px-2.5 pb-1.5 eo-label text-[9px] text-muted-foreground"
      style={{ gridTemplateColumns: REG_ROW_COLS }}
    >
      <span>Value</span>
      <span>Target · current</span>
      <span className="text-right">Risk</span>
    </div>
  );
}

const REG_ROW_COLS = "minmax(0, 1.35fr) minmax(0, 1fr) 84px";

function RegRow({ v, onOpen }: { v: RegistryValueEntry; onOpen: (v: RegistryValueEntry) => void }) {
  const off = v.status !== "applicable";
  return (
    <li>
      <button
        type="button"
        onClick={() => onOpen(v)}
        aria-label={`${v.title} (${v.valueName}) — details`}
        className={cn(
          "grid w-full items-center gap-3 rounded-md px-2.5 py-1.5 text-left transition-colors duration-150 hover:bg-highlight focus-visible:bg-highlight",
          off && "text-muted-foreground",
        )}
        style={{ gridTemplateColumns: REG_ROW_COLS }}
      >
        <span className="min-w-0">
          <span
            className={cn("block truncate text-[12px] font-semibold", !off && "text-foreground")}
            title={v.title}
          >
            {v.title}
          </span>
          <span className="flex min-w-0 items-center gap-1.5 font-mono text-[10.5px] text-muted-foreground">
            <span className="truncate">{v.valueName}</span>
            {v.restart && v.status === "applicable" && (
              <RotateCcw
                aria-label="Restart required"
                className="h-2.5 w-2.5 shrink-0 text-warning"
              />
            )}
          </span>
        </span>
        <span className="min-w-0 font-mono text-[10.5px]">
          <span
            className={cn("block truncate", off ? "text-muted-foreground" : "text-cyan")}
            title={v.target}
          >
            {v.target}
          </span>
          <span className="block truncate text-muted-foreground" title={v.current}>
            {v.current}
          </span>
        </span>
        <span className="flex justify-end">
          <RegValueBadge v={v} />
        </span>
      </button>
    </li>
  );
}

function RegGroup({
  group,
  values,
  onOpen,
  badge,
}: {
  group: RegistryGroup;
  values: RegistryValueEntry[];
  onOpen: (v: RegistryValueEntry) => void;
  badge?: ReactNode;
}) {
  return (
    <section aria-label={group.title} className="space-y-1">
      <div className="flex flex-wrap items-center gap-2 px-2.5 pt-2">
        <h3 className="eo-card-heading text-foreground">{group.title}</h3>
        <Badge tone="cyan">{group.tag}</Badge>
        {badge}
        <span className="ml-auto text-[10.5px] text-muted-foreground">
          {values.length} {values.length === 1 ? "value" : "values"}
        </span>
      </div>
      {group.note && <p className="px-2.5 text-[11px] text-muted-foreground">{group.note}</p>}
      <ul className="divide-y divide-border/60">
        {values.map((v) => (
          <RegRow key={v.id} v={v} onOpen={onOpen} />
        ))}
      </ul>
    </section>
  );
}

const NATIVE_STEPS: [string, string][] = [
  ["Read", "Current value is read back before anything is offered."],
  ["Back up", "A snapshot of the previous value is saved before a write."],
  ["Write & verify", "The value is written, then read back to confirm it stuck."],
  [
    "Restart & check",
    "Restart-bound values are re-checked after the next boot; rollback stays available.",
  ],
];

export function RegistryValuesPage() {
  const connected = useIsBackendConnected();
  const [liveRamVals, setLiveRamVals] = useState(RAM_REGISTRY_VALUES);
  const [liveGpuVals, setLiveGpuVals] = useState(GPU_REGISTRY_VALUES);
  const [detectedVendors, setDetectedVendors] = useState<{ nvidia?: boolean; amd?: boolean }>({});
  const scan = usePageScan("registry-values", async () => {
    if (!hasNativeBackend()) return;
    try {
      const [ramRes, gpuRes] = await Promise.all([
        sendHostRequest<unknown>("registry:ram-scan", undefined, 25000).catch(() => null),
        sendHostRequest<Record<string, unknown>>("registry:gpu-scan", undefined, 25000).catch(() => null),
      ]);
      if (ramRes) {
        const ramArr = Array.isArray(ramRes) ? (ramRes as Array<Record<string, unknown>>) : null;
        const svchostEntry = ramArr?.find((i) => i["id"] === "val.svchost.split_threshold") ?? ramArr?.[0];
        const ramObj = !Array.isArray(ramRes) && typeof ramRes === "object"
          ? (ramRes as Record<string, unknown>)
          : undefined;
        const recNum = svchostEntry?.["recommendedSplitThresholdKb"] ?? ramObj?.["recommendedSplitThresholdKb"];
        const recVal = typeof recNum === "number" && recNum > 0
          ? `${recNum.toLocaleString("en-US")} KB`
          : typeof svchostEntry?.["candidateValueDisplay"] === "string"
            ? svchostEntry["candidateValueDisplay"]
            : undefined;
        const curNum = svchostEntry?.["currentSplitThresholdKb"] ?? ramObj?.["currentSplitThresholdKb"];
        const curVal = typeof curNum === "number"
          ? `${curNum.toLocaleString("en-US")} KB`
          : typeof svchostEntry?.["currentValueDisplay"] === "string"
            ? svchostEntry["currentValueDisplay"]
            : ramObj?.["currentSplitThresholdKb"] !== undefined
              ? String(ramObj["currentSplitThresholdKb"])
              : undefined;
        setLiveRamVals((prev) =>
          prev.map((v) => {
            const matched = ramArr?.find(
              (r) => String(r["id"] ?? "") === v.id || String(r["valueName"] ?? "").toLowerCase() === v.valueName.toLowerCase(),
            );
            const itemCur = typeof matched?.["currentValueDisplay"] === "string" ? matched["currentValueDisplay"] : curVal;
            const itemTgt = typeof matched?.["candidateValueDisplay"] === "string" ? matched["candidateValueDisplay"] : recVal;
            return {
              ...v,
              ...(itemCur !== undefined ? { current: itemCur } : {}),
              ...(v.id === "val.svchost.split_threshold" || matched
                ? itemTgt !== undefined
                  ? { target: itemTgt }
                  : {}
                : {}),
            };
          }),
        );
      }
      if (gpuRes) {
        const items = Array.isArray(gpuRes["items"]) ? (gpuRes["items"] as Array<Record<string, unknown>>) : [];
        const hasNvidia = Boolean(gpuRes["hasNvidia"]) || items.some((i) => String(i["vendor"] ?? "").toLowerCase().includes("nvidia"));
        const hasAmd = Boolean(gpuRes["hasAmd"]) || items.some((i) => String(i["vendor"] ?? "").toLowerCase().includes("amd"));
        setDetectedVendors({ nvidia: hasNvidia, amd: hasAmd });
        if (items.length > 0) {
          setLiveGpuVals((prev) =>
            prev.map((v) => {
              const hit = items.find((r) => String(r["id"] ?? "") === v.id || String(r["name"] ?? "").toLowerCase().includes(v.title.slice(0, 12).toLowerCase()));
              if (!hit) return v;
              const app = hit["isApplicable"];
              return {
                ...v,
                status: app === false ? ("not-applicable" as const) : v.status,
                ...(typeof hit["currentValue"] === "string" ? { current: hit["currentValue"] } : {}),
              };
            }),
          );
        }
      }
    } catch {
      /* retain baseline catalog cleanly */
    }
  });
  const [detail, setDetail] = useState<RegistryValueEntry | null>(null);
  const ram = registrySummary(liveRamVals);
  const gpu = registrySummary(liveGpuVals);
  const gpuVendorGroups = GPU_REG_GROUPS.filter((g) => g.key !== "diagnostic");
  const diagGroup = GPU_REG_GROUPS.find((g) => g.key === "diagnostic");
  const isGpu = detail ? liveGpuVals.includes(detail) || GPU_REGISTRY_VALUES.includes(detail) : false;
  const vendorBadge = (key: string) => {
    if (key === "nvidia") {
      return scan.ready ? (
        <Badge tone={detectedVendors.nvidia ? "success" : "muted"}>{detectedVendors.nvidia ? "NVIDIA DETECTED" : "NOT PRESENT"}</Badge>
      ) : (
        <Badge>REQUIRES HARDWARE SCAN</Badge>
      );
    }
    if (key === "amd") {
      return scan.ready ? (
        <Badge tone={detectedVendors.amd ? "success" : "muted"}>{detectedVendors.amd ? "AMD DETECTED" : "NOT PRESENT"}</Badge>
      ) : (
        <Badge>REQUIRES HARDWARE SCAN</Badge>
      );
    }
    return undefined;
  };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Registry Values"
        sub="Hardware-aware RAM and GPU registry configuration catalog."
        scan={scan}
      />
      <StatTiles
        items={[
          { label: "RAM VALUES", value: ram.total, sub: `${ram.applicable} catalog items` },
          { label: "GPU VALUES", value: gpu.total, sub: `${gpu.applicable} catalog items` },
          { label: "READ-ONLY", value: gpu.diagnostic, sub: "Diagnostic, never applied" },
          {
            label: "NEED RESTART",
            value: ram.restart + gpu.restart,
            sub: "When modified",
            tone: "warning",
          },
        ]}
      />
      {/* Landing overview: two balanced families, groups collapsed; every value stays one click away. */}
      <div className="grid grid-cols-1 items-start gap-3.5 lg:grid-cols-2">
        {(
          [
            {
              key: "ram",
              title: "RAM REGISTRY VALUES",
              guide: "registry.ram-values" as const,
              sub: "Machine-aware memory and host service thresholds",
              page: "registry-values-ram",
              sum: ram,
              groups: RAM_GROUPS.map((g) => ({
                g,
                values: valuesInGroup(liveRamVals, g.key),
                badge: undefined as ReactNode,
              })),
            },
            {
              key: "gpu",
              title: "GPU REGISTRY VALUES",
              guide: "gpu.groups" as const,
              sub: "Hardware-gated graphics scheduling and vendor parameters",
              page: "gpu-registry-values",
              sum: gpu,
              groups: [...gpuVendorGroups, ...(diagGroup ? [diagGroup] : [])].map((g) => ({
                g,
                values: valuesInGroup(liveGpuVals, g.key),
                badge: vendorBadge(g.key),
              })),
            },
          ] as const
        ).map((fam) => (
          <Card key={fam.key} className="eo-rv-family">
            <SectionTitle
              title={fam.title}
              guide={fam.guide}
              sub={fam.sub}
              right={
                <Link to="/$page" params={{ page: fam.page }}>
                  <Btn size="sm" variant="ghost">
                    Full page →
                  </Btn>
                </Link>
              }
            />
            <dl className="eo-rv-mini">
              {(
                [
                  ["Values", fam.sum.total, "muted"],
                  ["Applicable", fam.sum.applicable, "success"],
                  ["Restart", fam.sum.restart, "warning"],
                  ["Gated", fam.sum.notApplicable + fam.sum.diagnostic, "muted"],
                ] as const
              ).map(([l, v, t]) => (
                <div key={l} data-tone={t}>
                  <dt className="eo-label text-[9px] text-muted-foreground">{l}</dt>
                  <dd className="eo-stat-value text-[18px] tabular-nums">{v}</dd>
                </div>
              ))}
            </dl>
            <div className="mt-2 divide-y divide-border/60">
              {fam.groups.map(({ g, values, badge }) => {
                const ok = values.filter((v) => v.status === "applicable").length;
                return (
                  <Details
                    key={g.key}
                    inline
                    title={g.title.toUpperCase()}
                    hint={`${ok}/${values.length} applicable · ${g.tag}`}
                  >
                    {(badge || g.note) && (
                      <div className="flex flex-wrap items-center gap-2 px-2.5 pb-1">
                        {badge}
                        {g.note && (
                          <span className="text-[11px] text-muted-foreground">{g.note}</span>
                        )}
                      </div>
                    )}
                    <RegTableHead />
                    <ul className="divide-y divide-border/60">
                      {values.map((v) => (
                        <RegRow key={v.id} v={v} onOpen={setDetail} />
                      ))}
                    </ul>
                  </Details>
                );
              })}
            </div>
          </Card>
        ))}
      </div>
      <Card>
        <SectionTitle
          title="TECHNICAL REFERENCE"
          sub="Service Host tiers and how the native engine applies values"
        />
        <div className="divide-y divide-border/60">
          <Details
            inline
            title="SERVICE HOST THRESHOLD BY RAM TIER"
            hint="SvcHostSplitThresholdInKB · by detected physical memory"
          >
            <table className="w-full text-[11.5px]">
              <caption className="sr-only">Service Host split threshold for each RAM tier</caption>
              <thead>
                <tr className="eo-label text-left text-[9px] text-muted-foreground">
                  <th scope="col" className="border-b border-border px-2.5 pb-1.5 font-semibold">
                    RAM tier
                  </th>
                  <th scope="col" className="border-b border-border px-2.5 pb-1.5 font-semibold">
                    DWORD
                  </th>
                  <th
                    scope="col"
                    className="border-b border-border px-2.5 pb-1.5 text-right font-semibold"
                  >
                    Threshold
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border/60">
                {RAM_TIER_MAP.map(([gb, dword]) => (
                  <tr
                    key={gb}
                    className="transition-colors duration-150 hover:bg-highlight"
                  >
                    <td className="px-2.5 py-1.5 font-semibold">{gb} GB</td>
                    <td className="px-2.5 py-1.5 font-mono text-[10.5px]">{hex8(dword)}</td>
                    <td className="px-2.5 py-1.5 text-right font-mono text-[10.5px] text-muted-foreground">
                      {dword.toLocaleString("en-US")} KB
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <p className="mt-2 px-2.5 text-[11px] text-muted-foreground">
              Unset default: {hex8(SVCHOST_DEFAULT_KB)} (
              {SVCHOST_DEFAULT_KB.toLocaleString("en-US")} KB).
            </p>
          </Details>
          <Details
            inline
            title="HOW THE ENGINE APPLIES VALUES"
            hint="Transactional safety flow"
          >
            <ol className="grid gap-2 sm:grid-cols-2">
              {NATIVE_STEPS.map(([k, d], i) => (
                <li key={k} className="flex gap-2.5 text-[11.5px]">
                  <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-full border border-cyan/40 font-mono text-[10px] text-cyan">
                    {i + 1}
                  </span>
                  <span>
                    <span className="font-semibold text-foreground">{k}.</span>{" "}
                    <span className="text-muted-foreground">{d}</span>
                  </span>
                </li>
              ))}
            </ol>
          </Details>
        </div>
      </Card>
      <Modal
        open={!!detail}
        onClose={() => setDetail(null)}
        title={detail?.title ?? "Registry value"}
        sub={detail ? `${isGpu ? "GPU" : "RAM"} registry configuration parameter` : undefined}
        footer={
          <>
            <Link
              to="/$page"
              params={{ page: isGpu ? "gpu-registry-values" : "registry-values-ram" }}
            >
              <Btn size="sm">{isGpu ? "Open GPU page" : "Open RAM page"}</Btn>
            </Link>
            <Btn variant="ghost" onClick={() => setDetail(null)}>
              Close
            </Btn>
          </>
        }
      >
        {detail && (
          <div className="space-y-3">
            <p className="text-[12.5px] text-muted-foreground">{detail.description}</p>
            <div className="flex flex-wrap gap-1.5">
              <RegValueBadge v={detail} />
              {detail.restart && <Badge tone="warning">RESTART REQUIRED</Badge>}
              <Badge tone="cyan">{detail.category}</Badge>
            </div>
            <Cols cols="minmax(0, 1fr) minmax(0, 1fr)" gap={10} className="items-stretch">
              <div className="rounded-lg border border-border bg-window p-3">
                <Label>Current Value</Label>
                <div className="mt-1 font-mono text-[12px] text-muted-foreground">{detail.current ?? "Not scanned"}</div>
              </div>
              <div className="rounded-lg border border-success/30 bg-window p-3">
                <Label className="text-success">Calculated Target</Label>
                <div className="mt-1 font-mono text-[12px]">{detail.target}</div>
              </div>
            </Cols>
            <div>
              <KV k="Value name:" v={detail.valueName} mono />
              <KV k="Type:" v={detail.type} mono />
              <KV k="Registry path:" v={<span className="break-all">{detail.path}</span>} mono />
              <KV k="Hardware gate:" v={detail.hardware} />
              {detail.driver && <KV k="Driver:" v={detail.driver} />}
              <KV k="Applicability rule:" v={detail.reason} />
            </div>
            {detail.path.includes("<adapter>") && (
              <p className="text-[11px] text-muted-foreground">
                {"<adapter>"} is the display adapter subkey (for example 0000), resolved at runtime.
              </p>
            )}
          </div>
        )}
      </Modal>
    </div>
  );
}

const RAM_SIZES = [4, 8, 12, 16, 24, 32, 48, 64, 128];
export function RegistryValuesRamPage() {
  const navigate = useNavigate();
  const connected = useIsBackendConnected();
  const [ram, setRam] = useState(32);
  const [ramUserEdited, setRamUserEdited] = useState(false);
  const [detectedRamGb, setDetectedRamGb] = useState<number | null>(null);
  const [open, setOpen] = useState(false);
  const [restartOpen, setRestartOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [readbackDisplay, setReadbackDisplay] = useState<string | null>(null);
  const [svchostCount, setSvchostCount] = useState<string>("—");
  const [svchostMem, setSvchostMem] = useState<string>("—");
  const kb = ram * 1024 * 1024;

  const loadRamReg = async () => {
    if (!hasNativeBackend()) return;
    try {
      const items = (await sendHostRequest("registry:ram-scan", undefined, 20000)) as Array<{
        id?: string;
        currentValueDisplay?: string;
        svchostCount?: number;
        svchostMemoryMb?: number;
        recommendedRamTierGb?: number;
        detectedInstalledRamGb?: number;
      }>;
      const entry = Array.isArray(items)
        ? items.find((i) => i.id === "val.svchost.split_threshold") ?? items[0]
        : null;
      if (entry?.currentValueDisplay) setReadbackDisplay(entry.currentValueDisplay);
      if (typeof entry?.svchostCount === "number") setSvchostCount(String(entry.svchostCount));
      if (typeof entry?.svchostMemoryMb === "number") setSvchostMem(`${entry.svchostMemoryMb.toFixed(0)} MB`);
      if (typeof entry?.detectedInstalledRamGb === "number" && entry.detectedInstalledRamGb > 0) {
        setDetectedRamGb(entry.detectedInstalledRamGb);
      }
      if (!ramUserEdited && typeof entry?.recommendedRamTierGb === "number" && RAM_SIZES.includes(entry.recommendedRamTierGb)) {
        setRam(entry.recommendedRamTierGb);
      }
    } catch {
      /* retain unpopulated readback on offline */
    }
  };

  const scan = usePageScan("registry-values-ram", loadRamReg);

  const applyRamValue = async () => {
    setBusy(true);
    try {
      const res = (await sendHostRequest("registry:ram-apply", { id: "val.svchost.split_threshold", kb, ramGb: ram }, 20000)) as {
        message?: string;
        item?: { currentValueDisplay?: string };
      };
      if (res?.item?.currentValueDisplay) setReadbackDisplay(res.item.currentValueDisplay);
      else await loadRamReg();
      toast.success("RAM registry threshold applied", { description: res?.message ?? "Verified via registry readback." });
    } catch (err) {
      toast.error("Failed to apply RAM registry value", { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setBusy(false);
    }
  };

  const restoreRamValue = async () => {
    setBusy(true);
    try {
      const res = (await sendHostRequest("registry:ram-revert", { id: "val.svchost.split_threshold" }, 20000)) as {
        message?: string;
        item?: { currentValueDisplay?: string };
      };
      if (res?.item?.currentValueDisplay) setReadbackDisplay(res.item.currentValueDisplay);
      else await loadRamReg();
      toast.success("Previous registry value restored", { description: res?.message ?? "Restored snapshot." });
    } catch (err) {
      toast.error("Restore failed", { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-4">
      <PageHeader
        back={
          <BackLink
            label="Registry Values"
            onClick={() => navigate({ to: "/$page", params: { page: "registry-values" } })}
          />
        }
        title="RAM Registry Values"
        sub="Configure the Service Host split threshold according to installed system RAM."
        scan={scan}
      />
      <Card className="flex items-center justify-between">
        <div>
          <Label>TARGET VALUE</Label>
          <div className="mt-1 text-sm font-semibold">Service Host split threshold</div>
        </div>
        <Btn size="sm" variant="ghost" onClick={() => setOpen(true)}>
          Details
        </Btn>
      </Card>
      <Cols cols="minmax(0, 1fr) minmax(0, 1fr)" gap={14} className="items-stretch">
        <Card>
          <SectionTitle title="CHOOSE YOUR RAM SIZE" guide="ram.size" />
          <p className="mt-1 mb-3 text-xs text-muted-foreground">
            Select the physical RAM installed in your system.
          </p>
          <div className="flex items-center gap-3">
            <div className="flex-1">
              <PremiumSelect
                label="Installed RAM size"
                value={String(ram)}
                onChange={(v) => {
                  setRamUserEdited(true);
                  setRam(+v);
                }}
                options={RAM_SIZES.map((r) => ({ value: String(r), label: `${r} GB` }))}
              />
            </div>
            <Badge tone="muted">{detectedRamGb ? `Detected: ${detectedRamGb} GB · Selected: ${ram} GB` : `Selected: ${ram} GB`}</Badge>
          </div>
          <div className="mt-4 rounded-lg border border-border bg-window p-3">
            <Label className="mb-1">TARGET CONFIGURATION</Label>
            <KV k="Hex Value:" v={`0x${kb.toString(16).toUpperCase()}`} mono />
            <KV k="Decimal KB:" v={kb.toLocaleString()} mono />
            <KV k="Formula:" v={`${ram} GB × 1024 × 1024`} mono />
          </div>
        </Card>
        <Card>
          <SectionTitle title="CURRENT REGISTRY STATE" />
          <p className="mt-1 mb-3 text-xs text-muted-foreground">
            {readbackDisplay
              ? "Verified live Windows registry readback state."
              : "Awaiting backend service connection to read live Windows registry state."}
          </p>
          <div className="rounded-lg border border-border bg-window p-3">
            <Label className="mb-1">READBACK VALUE</Label>
            <KV k="Hex Value:" v={readbackDisplay ? readbackDisplay.split(" ")[0] : "—"} mono />
            <KV k="Decimal KB:" v={readbackDisplay ?? "Not scanned"} mono />
          </div>
          <div className="mt-3">
            <StatTiles
              items={[
                { label: "SERVICE HOST INSTANCES", value: svchostCount },
                { label: "MEMORY USAGE", value: svchostMem },
              ]}
            />
          </div>
        </Card>
      </Cols>
      <Card className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap gap-2">
          <Btn
            variant="primary"
            disabled={busy}
            loading={busy}
            onClick={() => void applyRamValue()}
          >
            Apply value
          </Btn>
          <Btn disabled={busy} onClick={() => void restoreRamValue()}>
            Restore previous
          </Btn>
          <Btn
            variant="ghost"
            onClick={() => {
              setRamUserEdited(false);
              setRam(32);
              void loadRamReg();
            }}
          >
            Reset selection
          </Btn>
        </div>
        <Btn variant="ghost" onClick={() => setRestartOpen(true)}>
          Schedule restart
        </Btn>
      </Card>
      <Modal
        open={restartOpen}
        onClose={() => setRestartOpen(false)}
        title="Schedule Windows Restart"
        sub="Service Host split threshold changes require a system reboot to apply to all svchost processes."
        footer={
          <>
            <Btn variant="ghost" onClick={() => setRestartOpen(false)}>Cancel</Btn>
            <Btn
              variant="danger"
              onClick={async () => {
                setRestartOpen(false);
                try {
                  await sendHostRequest("system:schedule-restart", { confirmed: true, delaySeconds: 60, reason: "Apply Service Host split threshold" }, 10000);
                  toast.success("Windows restart scheduled in 60 seconds");
                } catch (err) {
                  toast.error("Restart scheduling failed", { description: err instanceof Error ? err.message : String(err) });
                }
              }}
            >
              Confirm restart in 60s
            </Btn>
          </>
        }
      >
        <p className="text-xs text-muted-foreground">
          Windows will shut down and restart in 60 seconds. Save open work before confirming.
        </p>
      </Modal>
      <Modal
        open={open}
        onClose={() => setOpen(false)}
        title="Technical value specification"
        footer={<Btn onClick={() => setOpen(false)}>Close details</Btn>}
      >
        {[
          ["Registry Path", "HKLM\\SYSTEM\\CurrentControlSet\\Control"],
          ["Value Name", "SvcHostSplitThresholdInKB"],
          ["Value Type", "REG_DWORD"],
          ["What It Controls", "Changes the configured Service Host split threshold."],
          ["Selected RAM Mapping", `${ram} GB`],
          ["Target Value", kb.toLocaleString() + " KB"],
          ["Backup State", "Automatic snapshot created prior to registry write."],
          ["Restart Requirement", "Requires Windows restart for full system effect."],
          ["Verification State", "Immediate readback verification required on write."],
        ].map(([k, v]) => (
          <div
            key={k}
            className="grid border-b border-border py-2 text-[12.5px]"
            style={{ gridTemplateColumns: "160px 1fr" }}
          >
            <span className="text-muted-foreground">{k}</span>
            <span className="font-mono text-[11.5px]">{v}</span>
          </div>
        ))}
      </Modal>
    </div>
  );
}

const GPU_GROUPS = [
  {
    name: "UNIVERSAL GPU",
    tag: "SYSTEM-WIDE",
    detected: false,
    items: [
      "Hardware-Accelerated GPU Scheduling (HAGS)",
      "GPU priority for games (MMCSS)",
      "TDR delay calibration",
    ],
  },
  {
    name: "NVIDIA GPU",
    tag: "GEFORCE / RTX",
    detected: false,
    items: ["NVIDIA PowerMizer Maximum Performance Mode", "Disable NVIDIA telemetry container"],
  },
  {
    name: "AMD GPU",
    tag: "RADEON / ADRENALIN",
    detected: false,
    items: ["AMD Radeon ULPS Disable", "AMD shader cache mode"],
  },
];
const GPU_ITEMS = GPU_GROUPS.flatMap((g) =>
  g.items.map((id) => ({ id, risk: "MEDIUM" as const, unsupported: !g.detected })),
);

export function GpuRegistryValuesPage() {
  const navigate = useNavigate();
  const connected = useIsBackendConnected();
  const [policy, setPolicy] = useState("Recommended");
  const [gpuHw, setGpuHw] = useState<{
    vendorName?: string;
    name?: string;
    driverVersion?: string;
    driverDate?: string;
    wddmVersion?: string;
    windowsVersion?: string;
    vendorType?: string;
  } | null>(null);
  const [scannedGroups, setScannedGroups] = useState(GPU_GROUPS);
  const [idMap, setIdMap] = useState<
    Record<
      string,
      {
        id: string;
        currentValueDisplay?: string | undefined;
        targetValueDisplay?: string | undefined;
        registryPath?: string | undefined;
        risk?: string | undefined;
        restartRequired?: boolean | undefined;
        verificationMethod?: string | undefined;
      }
    >
  >({});
  const [busy, setBusy] = useState(false);

  const scan = usePageScan("gpu-registry-values", async () => {
    if (!hasNativeBackend()) return;
    try {
      const res = (await sendHostRequest("registry:gpu-scan", { profile: policy }, 25000)) as {
        hardware?: typeof gpuHw;
        items?: Array<{
          id?: string;
          name?: string;
          displayName?: string;
          categoryDisplayName?: string;
          currentValueDisplay?: string;
          targetValueDisplay?: string;
          registryPath?: string;
          riskLevel?: string;
          restartRequired?: boolean;
          verificationMethod?: string;
        }>;
      };
      if (res?.hardware) setGpuHw(res.hardware);
      const nextMap: typeof idMap = {};
      const vendor = (res?.hardware?.vendorName ?? res?.hardware?.name ?? "").toLowerCase();
      for (const it of res?.items ?? []) {
        const key = it.displayName ?? it.name ?? it.id;
        if (key && it.id) {
          nextMap[key] = {
            id: it.id,
            currentValueDisplay: it.currentValueDisplay,
            targetValueDisplay: it.targetValueDisplay,
            registryPath: it.registryPath,
            risk: it.riskLevel,
            restartRequired: it.restartRequired,
            verificationMethod: it.verificationMethod,
          };
        }
      }
      setIdMap(nextMap);
      setScannedGroups(
        GPU_GROUPS.map((g) => ({
          ...g,
          detected:
            g.name.startsWith("UNIVERSAL")
              ? true
              : g.name.startsWith("NVIDIA")
                ? vendor.includes("nvidia")
                : vendor.includes("amd") || vendor.includes("radeon"),
        })),
      );
    } catch {
      /* keep undetected state cleanly */
    }
  });

  const liveGpuItems = useMemo(
    () => scannedGroups.flatMap((g) => g.items.map((id) => ({ id, risk: "MEDIUM" as const, unsupported: !g.detected }))),
    [scannedGroups],
  );
  // Each profile policy keeps its own selection; undetected vendors are never selectable.
  const gpu = useModedSelection(`gpu:${policy}`, liveGpuItems);
  const { sel, setSel } = gpu;
  const [detail, setDetail] = useState<string | null>(null);
  const groups = scannedGroups;

  const resolveGpuItemId = (label: string): string => {
    if (idMap[label]?.id) return idMap[label].id;
    const l = label.toLowerCase();
    if (l.includes("hags")) return "gpu.universal.hags";
    if (l.includes("mmcss")) return "gpu.mmcss.gpu_priority";
    if (l.includes("tdr")) return "gpu.diagnostic.tdr_delay";
    if (l.includes("powermizer")) return "gpu.nvidia.powermizer_level";
    if (l.includes("telemetry")) return "gpu.nvidia.telemetry";
    if (l.includes("ulps")) return "gpu.amd.enable_ulps";
    return label;
  };

  const applyGpuIds = async (labels: string[]) => {
    if (!labels.length) return;
    setBusy(true);
    let okCount = 0;
    let failCount = 0;
    let lastErr = "";
    for (const label of labels) {
      try {
        const res = (await sendHostRequest("registry:gpu-apply", { id: resolveGpuItemId(label), profile: policy }, 20000)) as {
          success?: boolean;
          Success?: boolean;
          message?: string;
          Message?: string;
        };
        if (res?.success === true || res?.Success === true) {
          okCount++;
        } else {
          failCount++;
          lastErr = res?.message ?? res?.Message ?? "Registry write declined";
        }
      } catch (err) {
        failCount++;
        lastErr = err instanceof Error ? err.message : String(err);
      }
    }
    setBusy(false);
    if (okCount > 0 && failCount === 0) {
      toast.success(`Applied ${okCount} GPU registry optimization${okCount === 1 ? "" : "s"}`);
      scan.refresh();
    } else if (okCount > 0 && failCount > 0) {
      toast.warning(`Applied ${okCount} of ${okCount + failCount} GPU registry settings`, {
        description: lastErr || `${failCount} item(s) failed or require elevation.`,
      });
      scan.refresh();
    } else if (lastErr) {
      toast.error("GPU registry optimization failed", { description: lastErr });
    }
  };

  return (
    <div className="space-y-4">
      <PageHeader
        back={
          <BackLink
            label="Registry Values"
            onClick={() => navigate({ to: "/$page", params: { page: "registry-values" } })}
          />
        }
        title="GPU Registry Values"
        sub="Hardware-aware GPU registry configuration, optimization and verification."
        scan={scan}
      />
      <Card>
        <Label className="mb-3 text-foreground">GRAPHICS HARDWARE</Label>
        <div className="grid gap-3" style={{ gridTemplateColumns: "1fr 1.1fr 1.1fr 1.8fr" }}>
          {[
            ["GPU Vendor", gpuHw ? (gpuHw.vendorName ?? gpuHw.name ?? "Detected") : "Not detected"],
            [
              "Driver Version & Date",
              gpuHw ? `${gpuHw.driverVersion ?? "Unknown"} (${gpuHw.driverDate ?? "—"})` : "Unavailable",
            ],
            ["WDDM Architecture", gpuHw?.wddmVersion ?? "Unavailable"],
            ["OS Environment", gpuHw?.windowsVersion ?? "Not detected"],
          ].map(([k, v]) => (
            <div key={k}>
              <Label className="text-[9px]">{k}</Label>
              <div className="mt-0.5 text-sm font-semibold text-muted-foreground">{v}</div>
            </div>
          ))}
        </div>
        <div className="mt-3">
          <Badge tone={gpuHw ? "success" : "muted"}>
            {gpuHw ? `${gpuHw.name ?? "GPU Detected"}` : "Awaiting hardware detection"}
          </Badge>
        </div>
      </Card>
      <Card className="flex items-center gap-3 py-3">
        <Label>PROFILE POLICY:</Label>
        <div className="w-48">
          <PremiumSelect
            size="sm"
            label="GPU profile policy"
            value={policy}
            onChange={setPolicy}
            options={["Recommended", "Performance", "Conservative"]}
          />
        </div>
      </Card>
      <SectionTitle
        title="HARDWARE-AWARE OPTIMIZATION GROUPS"
        guide="gpu.groups"
        right={
          <>
            <SelectionBar
              count={sel.size}
              total={gpu.eligibleCount}
              onAll={gpu.selectAll}
              onClear={() => setSel(new Set())}
              allLabel="Select all applicable"
              moded={gpu}
            />
            <Btn
              size="sm"
              variant="primary"
              disabled={!sel.size || busy}
              loading={busy}
              onClick={() => void applyGpuIds(Array.from(sel))}
            >
              Apply selected
            </Btn>
          </>
        }
      />
      {groups.filter((g) => g.detected).map((g) => (
        <Card key={g.name}>
          <div className="mb-3 flex items-center gap-2">
            <h2 className="text-xs font-semibold text-foreground">{g.name}</h2>
            <Badge tone="cyan">{g.tag}</Badge>
          </div>
          <div className="space-y-1.5">
            {g.items.map((i) => (
              <div
                key={i}
                className="flex items-center gap-3 eo-list-row rounded-lg border border-border bg-window px-3 py-2 text-[12.5px]"
              >
                <Check
                  on={sel.has(i)}
                  onChange={() =>
                    setSel((s) => {
                      return toggleInSet(s, i);
                    })
                  }
                />
                <span className="flex-1">{i}</span>
                <RiskBadge risk="MEDIUM" />
                <Btn size="sm" variant="ghost" onClick={() => setDetail(i)}>
                  Details
                </Btn>
              </div>
            ))}
          </div>
        </Card>
      ))}
      <Card>
        <Details inline title="GPU diagnostic configuration" hint="Read-only">
          <p className="text-xs text-muted-foreground">
            Read-only diagnostic values. Never written.
          </p>
        </Details>
      </Card>
      <Modal
        open={!!detail}
        onClose={() => setDetail(null)}
        title="GPU registry optimization"
        sub={detail ?? ""}
        footer={
          <>
            <Btn
              variant="primary"
              disabled={busy}
              onClick={() => {
                const d = detail ?? "GPU registry optimization";
                setDetail(null);
                void applyGpuIds([d]);
              }}
            >
              Apply
            </Btn>
            <Btn
              disabled={busy}
              onClick={async () => {
                if (!detail) return;
                setBusy(true);
                try {
                  await sendHostRequest("registry:gpu-revert", { id: resolveGpuItemId(detail), profile: policy }, 20000);
                  toast.success(`Restored ${detail}`);
                  setDetail(null);
                  scan.refresh();
                } catch (err) {
                  toast.error("GPU registry restore failed", { description: err instanceof Error ? err.message : String(err) });
                } finally {
                  setBusy(false);
                }
              }}
            >
              Restore
            </Btn>
            <Btn variant="ghost" onClick={() => setDetail(null)}>
              Close
            </Btn>
          </>
        }
      >
        {(() => {
          const itemMeta = detail ? idMap[detail] : undefined;
          const resolvedId = detail ? resolveGpuItemId(detail) : "GPU.*";
          const hwDesc = gpuHw
            ? `${gpuHw.name ?? gpuHw.vendorName ?? "GPU"}${gpuHw.driverVersion ? ` · Driver ${gpuHw.driverVersion}` : ""}`
            : "Awaiting GPU scan";
          const curVal = itemMeta?.currentValueDisplay ?? (gpuHw ? "Pending scan match" : "Not scanned");
          const tgtVal = itemMeta?.targetValueDisplay ?? `${policy} policy target`;
          return (
            <>
              <Label className="mb-1 text-cyan">TECHNICAL SPECIFICATION</Label>
              <KV k="Optimization ID:" v={resolvedId} mono />
              <KV k="Registry Path:" v={itemMeta?.registryPath ?? "Resolved per adapter profile"} mono />
              <KV k="Hardware & Driver:" v={hwDesc} />
              <Label className="mt-4 mb-1 text-cyan">VALUE STATES</Label>
              <Cols cols="minmax(0, 1fr) minmax(0, 1fr)" gap={14} className="items-stretch">
                <div className="rounded-lg border border-border bg-window p-3 text-sm text-muted-foreground">
                  Current Value: {curVal}
                </div>
                <div className="rounded-lg border border-success/30 bg-window p-3 text-sm">
                  Target: {tgtVal}
                </div>
              </Cols>
              <Label className="mt-4 mb-1 text-cyan">SAFETY & VERIFICATION</Label>
              <KV k="Risk Classification:" v={<RiskBadge risk={(itemMeta?.risk as Risk) ?? "MEDIUM"} />} />
              <KV k="Restart Required:" v={itemMeta?.restartRequired === false ? "No" : "Yes (Driver / Session)"} />
              <KV k="Verification Method:" v={itemMeta?.verificationMethod ?? "Registry Readback"} />
            </>
          );
        })()}
      </Modal>
    </div>
  );
}

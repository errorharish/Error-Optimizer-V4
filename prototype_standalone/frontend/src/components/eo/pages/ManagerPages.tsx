// StartupManagerView, ServiceManagerView, NetworkView, StorageView, ToolsView, ProcessReductionView
import { useEffect, useMemo, useState, useCallback, useRef } from "react";
import { toast } from "sonner";
import type { Risk } from "@/lib/eo/data";
import { hasNativeBackend, useIsBackendConnected, isDesktopApp } from "@/lib/eo/nativeBridge";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { CheckCircle2, ShieldCheck, AppWindow } from "lucide-react";
import {
  Badge,
  Bar,
  Btn,
  Card,
  Check,
  Chips,
  Cols,
  Details,
  EmptyState,
  KV,
  Label,
  Modal,
  PageHeader,
  RiskBadge,
  SearchBox,
  SectionTitle,
  SelectionBar,
  StatTiles,
  Toggle,
  SelectMark,
} from "../kit";
import { PremiumMultiSelect, PremiumSelect, FilterChip } from "../dropdown";
import { usePageScan } from "../usePageScan";
import {
  FILE_TYPES,
  MIN_SIZES,
  SORTS,
  filterFiles,
  type FileType,
  type SampleFile,
} from "@/lib/eo/fileFilters";
import { toggleInSet } from "@/lib/eo/selection";
import { cn } from "@/lib/utils";
import { useModedSelection, usePersistedSelection } from "../useSelection";
import { StatusIndicator } from "../StatusIndicator";

/* ───────── STARTUP MANAGER ───────── */
interface StartupRow {
  id: string;
  name: string;
  pub: string;
  impact: string;
  on: boolean;
  sys: boolean;
  source: string;
  path: string;
  commandLine: string;
  requiresAdmin: boolean;
  fileExists: boolean;
  valueName?: string;
  iconBase64?: string;
}
const STARTUP: StartupRow[] = [];

export function StartupManagerPage() {
  const connected = useIsBackendConnected();
  const [items, setItems] = useState<StartupRow[]>([]);
  const [busyIds, setBusyIds] = useState<Set<string>>(new Set());
  const [f, setF] = useState("ALL");
  const [q, setQ] = useState("");
  const [detail, setDetail] = useState<StartupRow | null>(null);
  const [confirm, setConfirm] = useState<string | null>(null);

  const loadStartup = async () => {
    if (!hasNativeBackend()) return;
    try {
      const res = (await sendHostRequest("startup:scan", undefined, 25000)) as Array<Record<string, unknown>>;
      if (Array.isArray(res)) {
        setItems(
          res.map((r) => {
            const stateRaw = r["state"] ?? r["State"];
            const on =
              stateRaw === 0 ||
              stateRaw === "Enabled" ||
              String(stateRaw).toLowerCase() === "enabled";
            const id = String(r["id"] ?? r["Id"] ?? `${r["source"] ?? ""}|${r["path"] ?? ""}|${r["valueName"] ?? ""}`);
            return {
              id,
              name: String(r["displayName"] ?? r["DisplayName"] ?? r["valueName"] ?? "Startup Item"),
              pub: String(r["publisher"] ?? r["Publisher"] ?? "Unknown"),
              impact: String(r["impact"] ?? r["Impact"] ?? "UNMEASURED").toUpperCase(),
              on,
              sys: Boolean(r["isSystemItem"] ?? r["IsSystemItem"]),
              source: String(r["source"] ?? r["Source"] ?? ""),
              path: String(r["path"] ?? r["Path"] ?? r["valueName"] ?? ""),
              commandLine: String(r["commandLine"] ?? r["CommandLine"] ?? r["exePath"] ?? ""),
              requiresAdmin: Boolean(r["requiresAdmin"] ?? r["RequiresAdmin"]),
              fileExists: Boolean(r["fileExists"] ?? r["FileExists"]),
              valueName: String(r["valueName"] ?? r["ValueName"] ?? ""),
              iconBase64: (r["iconBase64"] ?? r["IconBase64"]) as string | undefined,
            };
          }),
        );
      }
    } catch {
      /* retain previous list if scan fails */
    }
  };

  const scan = usePageScan("startup-manager", loadStartup);

  const vis = items.filter(
    (i) =>
      (i.name.toLowerCase().includes(q.toLowerCase()) ||
        i.commandLine.toLowerCase().includes(q.toLowerCase()) ||
        (i.valueName && i.valueName.toLowerCase().includes(q.toLowerCase()))) &&
      (f === "ALL" ||
        (f === "ENABLED" && i.on) ||
        (f === "DISABLED" && !i.on) ||
        (f === "HIGH IMPACT" && i.impact === "HIGH") ||
        (f === "MEDIUM" && i.impact === "MEDIUM") ||
        (f === "LOW" && i.impact === "LOW") ||
        (f === "USER APPS" && !i.sys) ||
        (f === "SYSTEM" && i.sys) ||
        (f === "UNMEASURED" && (i.impact === "UNMEASURED" || i.impact === "UNKNOWN"))),
  );
  const flip = async (targetId: string) => {
    if (busyIds.has(targetId)) return;
    const entry = items.find((i) => i.id === targetId);
    if (!entry) return;
    const enable = !entry.on;
    setBusyIds((prev) => new Set(prev).add(targetId));
    try {
      const res = (await sendHostRequest(
        "startup:toggle",
        {
          id: entry.id,
          source: entry.source,
          path: entry.path,
          name: entry.valueName || entry.name,
          valueName: entry.valueName || entry.name,
          enable,
        },
        15000,
      )) as { ok?: boolean; success?: boolean; isEnabled?: boolean; message?: string };
      if (res?.ok === false || res?.success === false) {
        throw new Error(res.message ?? `Windows declined to ${enable ? "enable" : "disable"} ${entry.name}.`);
      }
      const finalEnabled = typeof res?.isEnabled === "boolean" ? res.isEnabled : enable;
      setItems((s) => s.map((i) => (i.id === targetId ? { ...i, on: finalEnabled } : i)));
      toast.success(`${entry.name} ${finalEnabled ? "enabled" : "disabled"}`);
    } catch (err) {
      toast.error("Failed to toggle startup entry", { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setBusyIds((prev) => {
        const next = new Set(prev);
        next.delete(targetId);
        return next;
      });
    }
  };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Startup Manager"
        badges={<Badge tone="cyan">BOOT ACCELERATION</Badge>}
        sub="Review Windows startup applications and their boot impact."
        scan={scan}
      />
      <StatTiles
        items={[
          { label: "TOTAL APPS", value: connected ? items.length : "—" },
          { label: "ENABLED", value: connected ? items.filter((i) => i.on).length : "—" },
          { label: "DISABLED", value: connected ? items.filter((i) => !i.on).length : "—" },
        ]}
      />
      <Card>
        <div className="mb-3">
          <SearchBox placeholder="Search startup items…" value={q} onChange={setQ} />
        </div>
        <Chips
          options={[
            "ALL",
            "ENABLED",
            "DISABLED",
          ]}
          value={f}
          onChange={setF}
        />
        <div className="mt-3 overflow-hidden rounded-lg border border-border">
          <div
            className="grid items-center gap-3 bg-window px-4 py-2.5 text-[11px] font-bold tracking-wide text-subtle"
            style={{ gridTemplateColumns: "minmax(0, 1fr) 72px" }}
          >
            <span>APPLICATION</span>
            <span className="text-right">STATE</span>
          </div>
          {vis.map((i) => (
            <div
              key={i.id}
              className="grid items-center gap-3 border-t border-border px-4 py-2.5 text-[13px]"
              style={{ gridTemplateColumns: "minmax(0, 1fr) 72px" }}
            >
              <div className="flex items-center gap-3 min-w-0">
                {i.iconBase64 ? (
                  <img
                    src={i.iconBase64}
                    alt=""
                    className="h-6 w-6 rounded shrink-0 object-contain"
                  />
                ) : (
                  <div className="h-6 w-6 rounded bg-card/60 flex items-center justify-center shrink-0 text-muted-foreground">
                    <AppWindow className="h-3.5 w-3.5" />
                  </div>
                )}
                <span
                  className="min-w-0 font-medium text-foreground truncate select-text"
                  title={i.name}
                >
                  {i.name}
                </span>
              </div>
              <div className="flex justify-end">
                <Toggle
                  label={`Startup for ${i.name}`}
                  on={i.on}
                  disabled={busyIds.has(i.id)}
                  onChange={() => (i.sys && i.on ? setConfirm(i.id) : flip(i.id))}
                />
              </div>
            </div>
          ))}
          {!vis.length && (
            <EmptyState
              title={items.length ? "NO STARTUP ITEMS MATCH CRITERIA" : "NO STARTUP ITEMS SCANNED"}
              sub={
                items.length
                  ? "No startup applications or background entries match your current search."
                  : "Connect the ERROR OPTIMIZER background service to enumerate Windows startup items."
              }
              action={
                items.length ? (
                  <Btn
                    size="sm"
                    onClick={() => {
                      setF("ALL");
                      setQ("");
                    }}
                  >
                    Reset all filters
                  </Btn>
                ) : undefined
              }
            />
          )}
        </div>
      </Card>
      <Modal
        open={!!detail}
        onClose={() => setDetail(null)}
        title={detail?.name ?? ""}
        footer={<Btn onClick={() => setDetail(null)}>Close details</Btn>}
      >
        {detail && (
          <div className="space-y-4">
            <div>
              <Label className="mb-1 text-cyan">
                SECTION 1: APPLICATION IDENTITY & CLASSIFICATION
              </Label>
              <KV k="Category / Type" v={detail.sys ? "System component" : "User app"} />
              <KV k="Publisher" v={detail.pub} />
              <KV
                k="Disk file verification"
                v={detail.fileExists ? "Present on disk" : detail.commandLine ? "Executable missing / virtual" : "Unverified"}
              />
            </div>
            <Details title="SECTION 2–3: STARTUP COMMAND, EXECUTION PATH & REGISTRY SOURCE">
              <p className="font-mono text-[11px] text-muted-foreground">
                {detail.commandLine || detail.path || "Startup command unavailable"}
              </p>
            </Details>
            <div>
              <Label className="mb-1 text-cyan">SECTION 4: SYSTEM IMPACT & REQUIREMENTS</Label>
              <KV k="Boot impact" v={detail.impact} />
              <KV k="Elevated admin" v={detail.requiresAdmin ? "Yes" : "No"} />
              <KV k="Restart required" v="No" />
            </div>
          </div>
        )}
      </Modal>
      <Modal
        open={!!confirm}
        onClose={() => setConfirm(null)}
        title="⚠️ Disable system startup entry?"
        sub="Safety confirmation required"
        footer={
          <>
            <Btn variant="ghost" onClick={() => setConfirm(null)}>
              Cancel
            </Btn>
            <Btn
              variant="danger"
              onClick={() => {
                if (confirm) flip(confirm);
                setConfirm(null);
              }}
            >
              Disable anyway
            </Btn>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">
          This entry is identified as an IMPORTANT SYSTEM OR DRIVER COMPONENT. Disabling it may
          affect hardware features.
        </p>
      </Modal>
    </div>
  );
}

/* ───────── SERVICE MANAGER ───────── */
interface ServiceRow {
  serviceName: string;
  name: string;
  run: boolean;
  safety: string;
  start: string;
  ms: boolean;
  account: string;
  exePath: string;
  dependsOn: string[];
  dependentServices: string[];
}

export function ServiceManagerPage() {
  const connected = useIsBackendConnected();
  const [services, setServices] = useState<ServiceRow[]>([]);
  const [f, setF] = useState("ALL");
  const [q, setQ] = useState("");
  const [d, setD] = useState<ServiceRow | null>(null);
  const [confirm, setConfirm] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const loadServices = async () => {
    if (!hasNativeBackend()) return;
    try {
      const res = (await sendHostRequest("services:scan", undefined, 30000)) as Array<Record<string, unknown>>;
      if (Array.isArray(res)) {
        setServices(
          res.map((s) => {
            const runRaw = s["runningState"] ?? s["RunningState"];
            const runState = String(runRaw ?? "");
            const run = runState.toLowerCase().includes("running") || runRaw === 0 || runRaw === 4;
            const safetyRaw = String(s["actionSafety"] ?? s["ActionSafety"] ?? "SAFE TO STOP").toUpperCase();
            const startRaw = String(s["startupType"] ?? s["StartupType"] ?? "Manual");
            return {
              serviceName: String(s["serviceName"] ?? s["ServiceName"] ?? s["displayName"] ?? ""),
              name: String(s["displayName"] ?? s["DisplayName"] ?? s["serviceName"] ?? "Service"),
              run,
              safety:
                safetyRaw.includes("DO NOT") || s["isCritical"] === true
                  ? "DO NOT STOP"
                  : safetyRaw.includes("CAUTION")
                    ? "WITH CAUTION"
                    : "SAFE TO STOP",
              start: startRaw,
              ms: Boolean(s["isMicrosoft"] ?? s["IsMicrosoft"]),
              account: String(s["account"] ?? s["Account"] ?? "LocalSystem"),
              exePath: String(s["exePath"] ?? s["ExePath"] ?? ""),
              dependsOn: Array.isArray(s["dependsOn"]) ? (s["dependsOn"] as string[]) : [],
              dependentServices: Array.isArray(s["dependentServices"]) ? (s["dependentServices"] as string[]) : [],
            };
          }),
        );
      }
    } catch {
      /* keep current service list cleanly */
    }
  };

  const scan = usePageScan("service-manager", loadServices);

  const mutateServiceState = async (serviceName: string, state: "Start" | "Stop" | "Restart") => {
    setBusy(true);
    try {
      await sendHostRequest("services:change-state", { ServiceName: serviceName, State: state }, 30000);
      toast.success(`Service ${state.toLowerCase()} completed`, { description: serviceName });
      await loadServices();
    } catch (err) {
      toast.error(`Failed to ${state.toLowerCase()} service`, { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setBusy(false);
    }
  };

  const mutateServiceStartup = async (serviceName: string, startupLabel: string) => {
    const mapped =
      startupLabel === "AUTOMATIC"
        ? "Automatic"
        : startupLabel === "AUTOMATIC (DELAYED)"
          ? "Automatic (Delayed)"
          : startupLabel === "MANUAL"
            ? "Manual"
            : "Disabled";
    setBusy(true);
    try {
      await sendHostRequest("services:set-startup", { ServiceName: serviceName, State: mapped }, 30000);
      toast.success(`Startup updated to ${mapped}`, { description: serviceName });
      await loadServices();
    } catch (err) {
      toast.error("Failed to set startup type", { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setBusy(false);
    }
  };

  const vis = services.filter(
    (s) =>
      (s.name.toLowerCase().includes(q.toLowerCase()) || s.serviceName.toLowerCase().includes(q.toLowerCase())) &&
      (f === "ALL" ||
        (f === "RUNNING" && s.run) ||
        (f === "STOPPED" && !s.run) ||
        s.safety === f ||
        (f === "THIRD PARTY" && !s.ms) ||
        (f === "MICROSOFT" && s.ms) ||
        (f === "DISABLED" && s.start.toLowerCase() === "disabled")),
  );
  const tone = (s: string) =>
    (s === "SAFE TO STOP" ? "success" : s === "WITH CAUTION" ? "warning" : "danger") as
      "success" | "warning" | "danger";
  const ready = services.length > 0;

  return (
    <div className="space-y-4">
      <PageHeader
        title="Service Manager"
        sub="Review Windows services, startup modes, and action safety."
        scan={scan}
      />
      <StatTiles
        items={[
          { label: "TOTAL SERVICES", value: ready ? services.length : "—" },
          { label: "RUNNING", value: ready ? services.filter((s) => s.run).length : "—" },
          { label: "STOPPED", value: ready ? services.filter((s) => !s.run).length : "—" },
          { label: "SAFE TO STOP", value: ready ? services.filter((s) => s.safety === "SAFE TO STOP").length : "—", tone: "cyan" },
          { label: "WITH CAUTION", value: ready ? services.filter((s) => s.safety === "WITH CAUTION").length : "—", tone: "warning" },
          { label: "DO NOT STOP", value: ready ? services.filter((s) => s.safety === "DO NOT STOP").length : "—", tone: "danger" },
        ]}
      />
      <Card>
        <Cols cols="1fr auto" className="mb-3 items-center">
          <SearchBox placeholder="Search services…" value={q} onChange={setQ} />
          <span className="text-[10px] font-bold tracking-widest text-subtle">
            SORT: ACTION SAFETY · NAME · STATE · STARTUP · PUBLISHER
          </span>
        </Cols>
        <Chips
          options={[
            "ALL",
            "RUNNING",
            "STOPPED",
            "SAFE TO STOP",
            "WITH CAUTION",
            "THIRD PARTY",
            "MICROSOFT",
            "DISABLED",
          ]}
          value={f}
          onChange={setF}
        />
        <div className="mt-3 overflow-hidden rounded-lg border border-border">
          <div
            className="grid items-center gap-3 bg-window px-3 py-2 text-[10px] font-bold tracking-wide text-subtle"
            style={{ gridTemplateColumns: "minmax(0, 1fr) 80px 130px 115px" }}
          >
            <span>SERVICE</span>
            <span>STATUS</span>
            <span>STARTUP</span>
            <span>SAFETY</span>
          </div>
          {vis.map((s) => (
            <button
              key={s.name}
              onClick={() => setD(s)}
              className="grid w-full items-center gap-3 border-t border-border px-3 py-2 text-left text-[12.5px] hover:bg-window"
              style={{ gridTemplateColumns: "minmax(0, 1fr) 80px 130px 115px" }}
            >
              <span className="font-semibold">{s.name}</span>
              <StatusIndicator tone={s.run ? "pos" : "neg"} size="sm" reserve="RUNNING">
                {s.run ? "RUNNING" : "STOPPED"}
              </StatusIndicator>
              <span className="text-muted-foreground">{s.start}</span>
              <Badge tone={tone(s.safety)}>{s.safety}</Badge>
            </button>
          ))}
          {!vis.length && (
            <EmptyState
              title={connected ? "NO SERVICES MATCH CRITERIA" : "NO SERVICES SCANNED"}
              sub={
                connected
                  ? "No Windows services match your current search query or filter selection."
                  : "Connect the ERROR OPTIMIZER background service to inspect Windows services."
              }
              action={
                connected ? (
                  <Btn
                    size="sm"
                    onClick={() => {
                      setF("ALL");
                      setQ("");
                    }}
                  >
                    Reset all filters
                  </Btn>
                ) : undefined
              }
            />
          )}
        </div>
      </Card>
      <Modal
        open={!!d}
        onClose={() => setD(null)}
        title={d?.name ?? ""}
        width="max-w-3xl"
        footer={
          <>
            <Btn disabled={busy || !d} onClick={() => d && void mutateServiceState(d.serviceName, "Start")}>Start</Btn>
            <Btn
              variant={d?.safety === "DO NOT STOP" ? "ghost" : "danger"}
              disabled={busy || d?.safety === "DO NOT STOP"}
              onClick={() => d && setConfirm(d.serviceName)}
            >
              Stop
            </Btn>
            <Btn disabled={busy || !d} onClick={() => d && void mutateServiceState(d.serviceName, "Restart")}>Restart</Btn>
            <Btn variant="ghost" onClick={() => setD(null)}>
              Close details
            </Btn>
          </>
        }
      >
        {d && (
          <div className="space-y-4">
            <div>
              <Label className="mb-1 text-cyan">SECTION 1: SERVICE IDENTITY & DESCRIPTION</Label>
              <Cols cols="1fr 1fr">
                <KV k="Publisher" v={d.ms ? "Microsoft" : "Third party"} />
                <KV k="Account context" v={d.account || "LocalSystem"} />
              </Cols>
            </div>
            <Details title="SECTION 2: BINARY & EXECUTION PATH">
              <p className="font-mono text-[11px] text-muted-foreground">
                {d.exePath || "Service binary path unavailable"}
              </p>
            </Details>
            <div>
              <Label className="mb-1 text-cyan">SECTION 3: ACTION SAFETY & RISK ANALYSIS</Label>
              <Cols cols="1fr 1fr">
                <KV k="Windows critical service" v={d.safety === "DO NOT STOP" ? "Yes" : "No"} />
                <KV k="Microsoft signature" v={d.ms ? "Verified" : "—"} />
              </Cols>
            </div>
            <div>
              <Label className="mb-1 text-cyan">SECTION 4: DEPENDENCIES & ACTIVE DEPENDENTS</Label>
              <Cols cols="1fr 1fr">
                <KV k="Depends on" v={d.dependsOn.length ? d.dependsOn.join(", ") : "None"} />
                <KV k="Active dependents" v={String(d.dependentServices.length)} />
              </Cols>
            </div>
            <div>
              <Label className="mb-2 text-cyan">
                SECTION 5: STARTUP CONFIGURATION QUICK ACTIONS
              </Label>
              <div className="flex gap-1.5">
                {["AUTOMATIC", "AUTOMATIC (DELAYED)", "MANUAL", "DISABLED"].map((x) => (
                  <Btn key={x} size="sm" disabled={busy} onClick={() => void mutateServiceStartup(d.serviceName, x)}>
                    {x}
                  </Btn>
                ))}
              </div>
            </div>
          </div>
        )}
      </Modal>
      <Modal
        open={!!confirm}
        onClose={() => setConfirm(null)}
        title="⚠️ Action requires explicit user confirmation"
        footer={
          <>
            <Btn variant="ghost" onClick={() => setConfirm(null)}>
              Cancel (safe)
            </Btn>
            <Btn
              variant="danger"
              onClick={() => {
                const target = confirm;
                setConfirm(null);
                if (target) void mutateServiceState(target, "Stop");
              }}
            >
              Confirm & proceed
            </Btn>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">
          Stopping “{confirm}” may affect dependent features.
        </p>
      </Modal>
    </div>
  );
}

/* ───────── NETWORK BOOST ───────── */
interface NetTargetRow {
  id: string;
  name: string;
  cat: string;
  state: string;
}
const NET_FALLBACK: NetTargetRow[] = [];
export function NetworkPage() {
  const connected = useIsBackendConnected();
  const [f, setF] = useState("ALL");
  const [q, setQ] = useState("");
  const [running, setRunning] = useState(false);
  const [diag, setDiag] = useState<Record<string, unknown> | null>(null);
  const [adapter, setAdapter] = useState<Record<string, unknown> | null>(null);
  const [targets, setTargets] = useState<NetTargetRow[]>(NET_FALLBACK);

  const loadNetwork = async () => {
    if (!hasNativeBackend()) return;
    setRunning(true);
    try {
      const [plan, liveDiag] = await Promise.all([
        sendHostRequest<Record<string, unknown>>("network:scan", undefined, 25000).catch(() => null),
        sendHostRequest<Record<string, unknown>>("network:diagnostics", undefined, 25000).catch(() => null),
      ]);
      if (plan) {
        const adapters = Array.isArray(plan["adapters"]) ? (plan["adapters"] as Array<Record<string, unknown>>) : [];
        const active = adapters.find((a) => a["isActivePhysicalAdapter"]) ?? adapters[0] ?? null;
        if (active) setAdapter(active);
        if (plan["diagnostics"] && typeof plan["diagnostics"] === "object") setDiag(plan["diagnostics"] as Record<string, unknown>);
        const acts = Array.isArray(plan["actions"]) ? (plan["actions"] as Array<Record<string, unknown>>) : [];
        if (acts.length > 0) {
          setTargets(
            acts
              .filter((a) => {
                const st = String(a["status"] ?? a["eligibilityState"] ?? "").toUpperCase();
                return st !== "INELIGIBLE" && st !== "UNSUPPORTED" && st !== "NOT_APPLICABLE" && a["isApplicable"] !== false;
              })
              .map((a) => ({
                id: String(a["id"] ?? a["name"] ?? ""),
                name: String(a["name"] ?? a["description"] ?? ""),
                cat: String(a["category"] ?? "TCP/IP").toUpperCase(),
                state: String(a["status"] ?? "PENDING").toUpperCase(),
              })),
          );
        }
      }
      if (liveDiag) setDiag(liveDiag);
    } finally {
      setRunning(false);
    }
  };

  const scan = usePageScan("network", loadNetwork);
  const tested = scan.ready || Boolean(diag);
  const vis = targets.filter(
    (n) =>
      n.name.toLowerCase().includes(q.toLowerCase()) && (f === "ALL" || n.cat === f || n.state === f),
  );

  const optimizePendingNetwork = async () => {
    const pending = targets.filter((t) => t.state === "PENDING" || t.state === "RECOMMENDED");
    if (!pending.length) {
      toast.info("All network targets are already optimized");
      return;
    }
    setRunning(true);
    let ok = 0;
    let failed = 0;
    for (const item of pending) {
      try {
        const res = (await sendHostRequest("network:apply", { id: item.id }, 25000)) as {
          success?: boolean;
          Success?: boolean;
        };
        if (res?.success === true || res?.Success === true) {
          ok++;
        } else {
          failed++;
        }
      } catch {
        failed++;
      }
    }
    await loadNetwork();
    setRunning(false);
    if (ok > 0) {
      toast.success(`Applied ${ok} network optimization${ok === 1 ? "" : "s"}`, {
        description: failed > 0 ? `${failed} failed or declined.` : undefined,
      });
    } else if (failed > 0) {
      toast.error("Network optimizations failed", {
        description: `${failed} operation${failed === 1 ? "" : "s"} could not be applied (administrator privileges may be required).`,
      });
    } else {
      toast.info("No network optimizations applied");
    }
  };

  const flushDnsCache = async () => {
    setRunning(true);
    try {
      const res = (await sendHostRequest("network:flush-dns", undefined, 15000)) as {
        success?: boolean;
        Success?: boolean;
        message?: string;
      };
      if (res?.success !== true && res?.Success !== true) {
        throw new Error(res?.message ?? "DNS flush failed.");
      }
      toast.success("Windows DNS resolver cache flushed");
    } catch (err) {
      toast.error("DNS flush failed", { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setRunning(false);
    }
  };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Network Boost"
        sub="Inspect network diagnostics and optimize TCP/IP, MMCSS, and adapter settings."
        scan={scan}
      />
      {running && <Bar value={65} />}
      <Card>
        <SectionTitle
          title="NETWORK OVERVIEW"
          sub={
            adapter
              ? String(adapter["name"] ?? adapter["description"] ?? "Network adapter scanned")
              : tested
                ? "Network adapter scanned"
                : connected
                  ? "Connected · Ready to scan network adapters"
                  : "Awaiting background service connection"
          }
          right={
            <StatusIndicator tone={tested ? "pos" : connected ? "info" : "warn"}>
              {tested ? String(diag?.["networkHealth"] ?? "ONLINE") : connected ? "AWAITING SCAN" : "NOT CONNECTED"}
            </StatusIndicator>
          }
        />
        <StatTiles
          items={[
            { label: "LATENCY", value: String(diag?.["latencyMs"] ?? diag?.["internetPingMs"] ?? "—") },
            { label: "JITTER", value: String(diag?.["jitterMs"] ?? "—") },
            { label: "LOSS", value: String(diag?.["packetLossPercent"] ?? "—") },
            { label: "DNS TIME", value: String(diag?.["dnsResolutionMs"] ?? "—") },
            { label: "DOWN · Mbps", value: String(diag?.["downloadThroughputMbps"] ?? adapter?.["linkSpeedMbps"] ?? "—"), tone: "cyan" },
            { label: "UP · Mbps", value: String(diag?.["uploadThroughputMbps"] ?? "—"), tone: "cyan" },
          ]}
        />
        <Details inline title="Interface & target inventory" hint={adapter ? String(adapter["status"] ?? "Up") : "Awaiting network detection"}>
          <div className="grid grid-cols-2 gap-x-3.5">
            <KV k="Gateway" v={String(adapter?.["gateway"] || "Not detected")} />
            <KV k="IPv4" v={String(adapter?.["ipv4Address"] || "Unavailable")} />
            <KV k="DNS servers" v={String(adapter?.["dnsServers"] || "Unavailable")} />
            <KV k="Target catalog" v={`${targets.length} targets configured`} />
            <KV k="Target status" v={`${targets.filter((x) => x.state === "OPTIMIZED").length} optimized · ${targets.filter((x) => x.state !== "OPTIMIZED").length} pending`} />
          </div>
        </Details>
      </Card>
      <Card>
        <div className="mb-3 flex flex-wrap items-center gap-3.5">
          <SearchBox placeholder="Search network targets…" value={q} onChange={setQ} />
          <Chips
            options={["ALL", "TCP/IP", "MMCSS", "HARDWARE", "PENDING", "OPTIMIZED"]}
            value={f}
            onChange={setF}
          />
        </div>
        <SectionTitle
          title="PERSISTENT NETWORK TARGETS"
          guide="network.targets"
          sub="Transactional backup, apply, and verified readback."
          right={
            <Btn
              variant="primary"
              disabled={running}
              onClick={() => void optimizePendingNetwork()}
            >
              Optimize all pending
            </Btn>
          }
        />
        <div className="space-y-1.5">
          {vis.map((n) => (
            <div
              key={n.id || n.name}
              className="flex items-center justify-between eo-list-row rounded-lg border border-border bg-window px-3 py-2 text-[12.5px]"
            >
              <span className="font-semibold">{n.name}</span>
              <span className="flex gap-1.5">
                <Badge>{n.cat}</Badge>
                <Badge tone={n.state === "OPTIMIZED" ? "success" : "muted"}>{n.state}</Badge>
              </span>
            </div>
          ))}
        </div>
      </Card>
      <Card>
        <SectionTitle title="ONE-TIME NETWORK ACTIONS & MAINTENANCE" guide="network.actions" />
        <div
          className="grid items-center gap-4 rounded-lg border border-border bg-window p-3"
          style={{ gridTemplateColumns: "auto 1fr auto auto" }}
        >
          <span className="text-xl">🌐</span>
          <div>
            <div className="text-[13px] font-semibold">
              DNS Resolver Cache Flush{" "}
              <span className="text-[11px] text-cyan">• Maintenance Action</span>
            </div>
            <p className="text-[11.5px] text-muted-foreground">
              Flushes and purges the Windows DNS client resolver cache to resolve domain lookup
              issues.
            </p>
          </div>
          <Badge tone="success">READY</Badge>
          <Btn
            size="sm"
            disabled={running}
            onClick={() => void flushDnsCache()}
          >
            🚀 Flush DNS cache
          </Btn>
        </div>
      </Card>
    </div>
  );
}

/* ───────── STORAGE OPTIMIZER (Unified Smart + Deep Clean) ───────── */
export interface StorageCleanItem {
  id: string;
  name: string;
  size: string;
  sizeBytes: number;
  risk: Risk;
  category: "SMART" | "DEEP";
  eligible: boolean;
  status: "ELIGIBLE" | "ALREADY_CLEAN" | "PROTECTED";
  desc: string;
}

const STORAGE_CATALOG: StorageCleanItem[] = [
  // ── Smart Clean: Safe System & App Caches
  {
    id: "win-temp",
    name: "Windows temporary files",
    size: "—",
    sizeBytes: 0,
    risk: "SAFE",
    category: "SMART",
    eligible: true,
    status: "ELIGIBLE",
    desc: "Application %TEMP% files, crash triage files and staging logs.",
  },
  {
    id: "shader-cache",
    name: "DirectX shader cache",
    size: "—",
    sizeBytes: 0,
    risk: "SAFE",
    category: "SMART",
    eligible: true,
    status: "ELIGIBLE",
    desc: "DirectX 11/12 and Vulkan shader compile caches.",
  },
  {
    id: "win-update",
    name: "Windows Update cleanup",
    size: "—",
    sizeBytes: 0,
    risk: "SAFE",
    category: "SMART",
    eligible: true,
    status: "ELIGIBLE",
    desc: "Superseded update components and staging packages.",
  },
  {
    id: "thumb-cache",
    name: "Thumbnail & icon cache",
    size: "—",
    sizeBytes: 0,
    risk: "SAFE",
    category: "SMART",
    eligible: true,
    status: "ELIGIBLE",
    desc: "Windows Explorer icon and thumbnail database.",
  },
  {
    id: "delivery-opt",
    name: "Delivery Optimization cache",
    size: "—",
    sizeBytes: 0,
    risk: "SAFE",
    category: "SMART",
    eligible: true,
    status: "ELIGIBLE",
    desc: "Peer-to-peer distribution cache for updates and Store apps.",
  },

  // ── Deep Clean: Maintenance & Diagnostic Dumps
  {
    id: "crash-dumps",
    name: "Crash dumps & error reports",
    size: "—",
    sizeBytes: 0,
    risk: "LOW",
    category: "DEEP",
    eligible: true,
    status: "ELIGIBLE",
    desc: "Kernel MEMORY.DMP archives and Windows Error Reporting logs.",
  },
  {
    id: "sys-logs",
    name: "System diagnostic log archives",
    size: "—",
    sizeBytes: 0,
    risk: "LOW",
    category: "DEEP",
    eligible: true,
    status: "ELIGIBLE",
    desc: "CBS and DISM log archives from past maintenance cycles.",
  },
  {
    id: "recycle-bin",
    name: "Recycle Bin (system drive)",
    size: "—",
    sizeBytes: 0,
    risk: "MEDIUM",
    category: "DEEP",
    eligible: true,
    status: "ELIGIBLE",
    desc: "User-deleted files staged in $Recycle.Bin on drive C:.",
  },

  {
    id: "branch-cache",
    name: "BranchCache peer staging",
    size: "—",
    sizeBytes: 0,
    risk: "SAFE",
    category: "DEEP",
    eligible: false,
    status: "ALREADY_CLEAN",
    desc: "Local BranchCache folder is already clean.",
  },
];

const SAMPLE_FILES: SampleFile[] = [];

interface LiveDrive {
  name: string;
  letter: string;
  volumeLabel: string;
  driveType: string;
  isFixedLocalDrive: boolean;
  isSystemDrive: boolean;
  mediaType: string;
  totalGb: number;
  usedGb: number;
  freeGb: number;
  physicalDiskTotalGb: number;
  usagePercentage: number;
  healthStatus: string;
}

const TOOL_NAME_TO_ID: Record<string, string> = {
  "System File Checker (SFC)": "sfc",
  "DISM Component Store Repair": "dism-repair",
  "Check Disk (read-only scan)": "chkdsk-scan",
  "Memory Diagnostic Scheduler": "memory-diagnostic",
  "Clear Standby Memory": "standby-memory",
  "Temp & Cache Cleanup": "temp-cleanup",
  "Component Store Cleanup": "dism-cleanup",
  "Reset Windows Update Cache": "reset-windows-update",
  "Rebuild Icon & Thumbnail Cache": "rebuild-icon-cache",
  "Reset Network Stack (Winsock)": "reset-winsock",
  "Flush DNS Resolver Cache": "flush-dns",
  "Restart Graphics Driver": "restart-gpu",
  "Clear DirectX Shader Cache": "shader-cache",
  "Battery & Energy Report": "battery-energy-report",
  "Re-register Store Apps": "reregister-store-apps",
  "Reboot to UEFI Firmware": "uefi-reboot",
};

const FORMAT_BYTES = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
};

const STORAGE_ID_MAP: Record<string, string> = {
  "win-temp": "temp-windows",
  "shader-cache": "shader-cache",
  "win-update": "windows-update",
  "thumb-cache": "thumbnail-cache",
  "delivery-opt": "delivery-opt",
  "crash-dumps": "crash-dumps",
  "sys-logs": "system-logs",
  "recycle-bin": "recycle-bin",
};

export function StoragePage() {
  const connected = useIsBackendConnected();
  const [liveCatalog, setLiveCatalog] = useState<StorageCleanItem[]>(STORAGE_CATALOG);
  const [drives, setDrives] = useState<LiveDrive[]>([]);
  const [scannedOverview, setScannedOverview] = useState(false);
  const scanGenRef = useRef(0);
  const [scannedFiles, setScannedFiles] = useState<(SampleFile & { fullPath?: string })[]>([]);
  const [fileSel, setFileSel] = useState<Set<string>>(new Set());
  const [drive, setDrive] = useState("All drives");
  const [minimum, setMinimum] = useState("100 MB");
  const [sortFiles, setSortFiles] = useState("Size");
  const [fileQuery, setFileQuery] = useState("");
  const [types, setTypes] = useState<Set<FileType>>(new Set());
  const [explorer, setExplorer] = useState(false);
  const [scanned, setScanned] = useState(false);
  const [scanningFiles, setScanningFiles] = useState(false);

  // Unified Storage Optimization Execution State
  const [unifiedModal, setUnifiedModal] = useState(false);
  const [unifiedProgress, setUnifiedProgress] = useState(0);
  const [unifiedRunning, setUnifiedRunning] = useState(false);
  const [unifiedStage, setUnifiedStage] = useState<
    "READY" | "ANALYZING" | "SMART_CLEAN" | "DEEP_CLEAN" | "COMPLETED"
  >("READY");
  const [lastCleanupMsg, setLastCleanupMsg] = useState<string | null>(null);

  const loadStorageOverview = async () => {
    const currentGen = ++scanGenRef.current;
    if (!hasNativeBackend()) return;
    try {
      const [driveRes, catRes] = await Promise.all([
        sendHostRequest<Array<Record<string, unknown>>>("storage:drives", undefined, 25000).catch(() => null),
        sendHostRequest<{ categories?: Array<Record<string, unknown>> } | Array<Record<string, unknown>>>("storage:scan-cleanup", undefined, 30000).catch(() => null),
      ]);
      if (currentGen !== scanGenRef.current) return;
      if (Array.isArray(driveRes)) {
        setDrives(
          driveRes.map((d) => {
            const name = String(d["name"] ?? d["Name"] ?? d["letter"] ?? "C:\\");
            const letter = String(d["letter"] ?? d["Letter"] ?? name).replace(/\\+$/, "");
            const driveType = String(d["driveType"] ?? d["DriveType"] ?? "Fixed");
            const isFixedLocalDrive =
              typeof (d["isFixedLocalDrive"] ?? d["IsFixedLocalDrive"]) === "boolean"
                ? Boolean(d["isFixedLocalDrive"] ?? d["IsFixedLocalDrive"])
                : driveType.toLowerCase() === "fixed";
            return {
              name,
              letter,
              volumeLabel: String(d["volumeLabel"] ?? d["VolumeLabel"] ?? ""),
              driveType,
              isFixedLocalDrive,
              isSystemDrive: Boolean(d["isSystemDrive"] ?? d["IsSystemDrive"]),
              mediaType: String(d["mediaType"] ?? d["MediaType"] ?? "Drive"),
              totalGb: Number(d["totalGb"] ?? d["TotalGb"] ?? 0),
              usedGb: Number(d["usedGb"] ?? d["UsedGb"] ?? 0),
              freeGb: Number(d["freeGb"] ?? d["FreeGb"] ?? 0),
              physicalDiskTotalGb: Number(d["physicalDiskTotalGb"] ?? d["PhysicalDiskTotalGb"] ?? 0),
              usagePercentage: Number(d["usagePercentage"] ?? d["UsagePercentage"] ?? 0),
              healthStatus: String(d["healthStatus"] ?? d["HealthStatus"] ?? "GOOD"),
            };
          }),
        );
      }
      const cats = Array.isArray(catRes) ? catRes : Array.isArray(catRes?.categories) ? catRes.categories : null;
      if (cats && cats.length > 0) {
        setLiveCatalog((prev) =>
          prev.map((item) => {
            const match = cats.find((c) => {
              const cid = String(c["id"] ?? c["Id"] ?? "").toLowerCase();
              const cname = String(c["name"] ?? c["Name"] ?? "").toLowerCase();
              return cid === item.id || cid === STORAGE_ID_MAP[item.id] || cname.includes(item.name.slice(0, 10).toLowerCase());
            });
            if (!match) return item;
            const bytes = Number(match["detectedBytes"] ?? match["DetectedBytes"] ?? item.sizeBytes);
            const appStr = String(match["applicabilityDisplay"] ?? match["ApplicabilityDisplay"] ?? "").toUpperCase();
            return {
              ...item,
              sizeBytes: bytes,
              size: typeof match["formattedSize"] === "string" ? match["formattedSize"] : FORMAT_BYTES(bytes),
              status: item.eligible ? (appStr === "ALREADY CLEAN" || bytes === 0 ? "ALREADY_CLEAN" : "ELIGIBLE") : item.status,
            };
          }),
        );
      }
      setScannedOverview(true);
    } catch {
      /* keep fallback catalog metrics cleanly */
    }
  };

  const handleRefresh = useCallback(() => {
    scanGenRef.current += 1;
    setScannedOverview(false);
    setDrives([]);
    scan.refresh();
  }, []);

  const scan = usePageScan("storage", loadStorageOverview);

  const eligibleCatalog = useMemo(() => (scannedOverview ? liveCatalog : []), [scannedOverview, liveCatalog]);
  const storageSel = useModedSelection("storage:unified", eligibleCatalog);
  const selectedItems = useMemo(
    () => (scannedOverview ? liveCatalog.filter((i) => storageSel.sel.has(i.id)) : []),
    [scannedOverview, liveCatalog, storageSel.sel],
  );
  const smartCleanItems = useMemo(
    () => liveCatalog.filter((i) => i.category === "SMART" && i.status !== "PROTECTED"),
    [liveCatalog],
  );
  const deepCleanItems = useMemo(
    () => liveCatalog.filter((i) => i.category === "DEEP" && i.status !== "PROTECTED"),
    [liveCatalog],
  );

  const totalReclaimableBytes = useMemo(
    () => selectedItems.reduce((acc, cur) => acc + cur.sizeBytes, 0),
    [selectedItems],
  );
  const totalReclaimableGB = (totalReclaimableBytes / (1024 * 1024 * 1024)).toFixed(2);
  const fixedDrives = useMemo(() => {
    const fixed = drives.filter((d) => d.isFixedLocalDrive);
    return fixed.length > 0 ? fixed : drives;
  }, [drives]);
  const scopedDrives = useMemo(() => {
    if (drive === "All drives") return fixedDrives;
    const match = fixedDrives.filter((d) => d.letter.toUpperCase() === drive.toUpperCase());
    return match.length > 0 ? match : fixedDrives;
  }, [fixedDrives, drive]);
  const scopedHealthStatus = useMemo(() => {
    if (scopedDrives.length === 0) return "—";
    const rank: Record<string, number> = { CRITICAL: 4, WARNING: 3, UNKNOWN: 2, GOOD: 1, EXCELLENT: 0 };
    return scopedDrives
      .slice()
      .sort((a, b) => (rank[b.healthStatus.toUpperCase()] ?? 1) - (rank[a.healthStatus.toUpperCase()] ?? 1))[0]
      ?.healthStatus ?? "—";
  }, [scopedDrives]);
  const driveOptions = useMemo(() => {
    const letters = fixedDrives.map((d) => d.letter).filter(Boolean);
    return letters.length > 0 ? ["All drives", ...Array.from(new Set(letters))] : ["All drives", "C:"];
  }, [fixedDrives]);
  const totalDiskFreeGb = scopedDrives.reduce((a, d) => a + d.freeGb, 0);
  const totalDiskUsedGb = scopedDrives.reduce((a, d) => a + d.usedGb, 0);

  const fileRows = scannedFiles.map((f) => [f.name, f.path, `${f.sizeGb} GB`, f.type, f.modified]);
  const visibleFiles = filterFiles(scannedFiles, {
    drive,
    minimum,
    sort: sortFiles,
    types,
    query: fileQuery,
  }).map((f) => [f.name, f.path, `${f.sizeGb} GB`, f.type, f.modified]);

  const scanLargeFilesNow = async () => {
    if (!hasNativeBackend()) return;
    const targetRoots =
      drive === "All drives"
        ? fixedDrives.map((d) => d.name)
        : [drive.endsWith("\\") ? drive : `${drive}\\`];
    if (targetRoots.length === 0) {
      toast.info("No ready fixed volumes detected yet", {
        description: "Run Refresh or wait for the storage scan to detect local drives.",
      });
      return;
    }
    setScanningFiles(true);
    try {
      const minMb = parseInt(minimum, 10) || 100;
      const merged: (SampleFile & { fullPath?: string; drive?: string })[] = [];
      for (const targetDrive of targetRoots) {
        const list = (await sendHostRequest("storage:scan-large", { drive: targetDrive, minSizeMb: minMb }, 60000)) as Array<Record<string, unknown>>;
        if (Array.isArray(list)) {
          for (const f of list) {
            const fullPath = String(f["fullPath"] ?? f["FullPath"] ?? "");
            const sizeBytes = Number(f["sizeBytes"] ?? f["SizeBytes"] ?? 0);
            const extRaw = String(f["extension"] ?? f["Extension"] ?? "").toLowerCase();
            const ext = extRaw && !extRaw.startsWith(".") ? `.${extRaw}` : extRaw;
            const fType: FileType =
              [".mp4", ".mkv", ".avi", ".mov"].includes(ext)
                ? "Videos"
                : [".png", ".jpg", ".jpeg", ".webp"].includes(ext)
                  ? "Images"
                  : [".mp3", ".wav", ".flac"].includes(ext)
                    ? "Audio"
                    : [".zip", ".7z", ".rar", ".tar"].includes(ext)
                      ? "Archives"
                      : [".iso", ".img"].includes(ext)
                        ? "ISO"
                        : [".exe", ".msi"].includes(ext)
                          ? "Applications"
                          : [".pdf", ".docx", ".txt"].includes(ext)
                            ? "Documents"
                            : "Other";
            const dStr = String(f["lastModified"] ?? f["LastModified"] ?? "");
            merged.push({
              name: String(f["fileName"] ?? f["FileName"] ?? fullPath.split("\\").pop() ?? "File"),
              path: fullPath,
              fullPath,
              sizeGb: Math.round((sizeBytes / (1024 * 1024 * 1024)) * 100) / 100,
              type: fType,
              drive: String(f["driveLetter"] ?? f["DriveLetter"] ?? targetDrive.slice(0, 2)),
              modified: dStr ? dStr.slice(0, 10) : "—",
            });
          }
        }
      }
      setScannedFiles(merged.sort((a, b) => b.sizeGb - a.sizeGb));
      setScanned(true);
    } catch (err) {
      toast.error("Large file scan failed", { description: err instanceof Error ? err.message : String(err) });
    } finally {
      setScanningFiles(false);
    }
  };

  const handleStartUnifiedClean = async () => {
    if (!hasNativeBackend()) return;
    setUnifiedRunning(true);
    setUnifiedProgress(25);
    setUnifiedStage("SMART_CLEAN");
    try {
      const categoryIds = selectedItems.map((i) => STORAGE_ID_MAP[i.id] ?? i.id);
      const res = (await sendHostRequest("storage:run-cleanup", { categoryIds }, 90000)) as {
        success?: boolean;
        Success?: boolean;
        message?: string;
        summaryMessage?: string;
        formattedReclaimed?: string;
      };
      if (res?.success !== true && res?.Success !== true) {
        throw new Error(res?.message ?? res?.summaryMessage ?? "Storage cleanup did not succeed.");
      }
      setUnifiedProgress(100);
      setUnifiedStage("COMPLETED");
      setLastCleanupMsg(res?.summaryMessage ?? res?.message ?? (res?.formattedReclaimed ? `Reclaimed ${res.formattedReclaimed}` : "Cleanup completed"));
      await loadStorageOverview();
      toast.success("Unified storage optimization completed", { description: res?.summaryMessage ?? res?.message });
    } catch (err) {
      toast.error("Storage cleanup failed", { description: err instanceof Error ? err.message : String(err) });
      setUnifiedStage("READY");
    } finally {
      setUnifiedRunning(false);
    }
  };

  const handleOpenModal = () => {
    setUnifiedModal(true);
    setUnifiedProgress(0);
    setUnifiedStage("READY");
    setUnifiedRunning(false);
    setLastCleanupMsg(null);
  };

  if (explorer)
    return (
      <div className="space-y-4">
        <div className="grid items-center gap-3" style={{ gridTemplateColumns: "auto 1fr auto" }}>
          <Btn variant="ghost" onClick={() => setExplorer(false)}>
            ◄ Back to overview
          </Btn>
          <div>
            <h1 className="eo-heading">Full File Explorer</h1>
            <p className="text-xs text-muted-foreground">
              Inspect large files and manage drive storage.
            </p>
          </div>
          <Btn
            variant="primary"
            loading={scanningFiles}
            onClick={() => void scanLargeFilesNow()}
          >
            Scan files
          </Btn>
        </div>
        <Card>
          <Label className="mb-2 text-foreground">FILESYSTEM SCAN SUMMARY</Label>
          <div className="flex flex-wrap gap-2 text-[11.5px]">
            {(scanned
              ? (() => {
                  const counts = scannedFiles.reduce<Record<string, number>>((acc, f) => {
                    acc[f.type] = (acc[f.type] ?? 0) + 1;
                    return acc;
                  }, {});
                  return [
                    `📁 ALL: ${scannedFiles.length}`,
                    `🎬 VIDEOS: ${counts["Videos"] ?? 0}`,
                    `🖼️ IMAGES: ${counts["Images"] ?? 0}`,
                    `🎵 AUDIO: ${counts["Audio"] ?? 0}`,
                    `📄 DOCS: ${counts["Documents"] ?? 0}`,
                    `⚙️ APPS: ${counts["Applications"] ?? 0}`,
                    `📦 ARCHIVES: ${counts["Archives"] ?? 0}`,
                    `💿 ISO: ${counts["ISO"] ?? 0}`,
                    `💻 PROJECTS: ${counts["Projects"] ?? 0}`,
                    `📁 OTHER: ${counts["Other"] ?? 0}`,
                  ];
                })()
              : [
                  "📁 ALL: —",
                  "🎬 VIDEOS: —",
                  "🖼️ IMAGES: —",
                  "🎵 AUDIO: —",
                  "📄 DOCS: —",
                  "⚙️ APPS: —",
                  "📦 ARCHIVES: —",
                  "💿 ISO: —",
                  "💻 PROJECTS: —",
                  "📁 OTHER: —",
                ]
            ).map((x) => (
              <span key={x} className="rounded-md border border-border bg-window px-2 py-1">
                {x}
              </span>
            ))}
          </div>
        </Card>
        <Card>
          <div className="grid grid-cols-4 gap-3">
            <div>
              <Label className="mb-1">DRIVE</Label>
              <PremiumSelect
                label="Drive"
                value={drive}
                onChange={setDrive}
                options={driveOptions}
              />
            </div>
            <div>
              <Label className="mb-1">MINIMUM SIZE</Label>
              <PremiumSelect
                label="Minimum size"
                value={minimum}
                onChange={setMinimum}
                options={MIN_SIZES}
              />
            </div>
            <div>
              <Label className="mb-1">SORT BY</Label>
              <PremiumSelect
                label="Sort by"
                value={sortFiles}
                onChange={setSortFiles}
                options={SORTS}
              />
            </div>
            <div>
              <Label className="mb-1">FILE TYPES</Label>
              <PremiumMultiSelect
                label="File types"
                allLabel="All types"
                value={types}
                onChange={setTypes}
                options={FILE_TYPES}
              />
            </div>
          </div>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <div className="min-w-[220px] flex-1">
              <SearchBox
                placeholder="Search scanned files…"
                value={fileQuery}
                onChange={setFileQuery}
              />
            </div>
            {[...types].map((t) => (
              <FilterChip
                key={t}
                onRemove={() =>
                  setTypes((cur) => {
                    const n = new Set(cur);
                    n.delete(t);
                    return n;
                  })
                }
              >
                {t}
              </FilterChip>
            ))}
            <span className="text-[11px] text-subtle tabular-nums">
              {visibleFiles.length} of {scannedFiles.length} files
            </span>
          </div>
        </Card>
        <Card>
          <div className="mb-3 flex items-center justify-between">
            <span className="text-xs text-warning">— User review required before deletion</span>
            <div className="flex gap-1.5">
              <Btn size="sm" onClick={() => setFileSel(new Set(fileRows.map((r) => r[1] ?? "")))}>
                Select all
              </Btn>
              <Btn
                size="sm"
                onClick={() => setFileSel(new Set(visibleFiles.map((r) => r[1] ?? "")))}
              >
                Select filtered
              </Btn>
              <Btn size="sm" variant="ghost" onClick={() => setFileSel(new Set())}>
                Deselect all
              </Btn>
              <Btn
                size="sm"
                variant="danger"
                disabled={!fileSel.size}
                onClick={async () => {
                  const paths = Array.from(fileSel).filter(Boolean);
                  if (!paths.length) return;
                  try {
                    await sendHostRequest("storage:delete-files", { paths, permanent: false }, 30000);
                    setFileSel(new Set());
                    toast.success(`Moved ${paths.length} file${paths.length === 1 ? "" : "s"} to Recycle Bin`);
                    await scanLargeFilesNow();
                  } catch (err) {
                    toast.error("File deletion failed", { description: err instanceof Error ? err.message : String(err) });
                  }
                }}
              >
                Delete selected
              </Btn>
            </div>
          </div>
          {!scanned ? (
            <EmptyState
              title="No files scanned yet."
              sub="Connect the ERROR OPTIMIZER background service to scan drives for large files."
              action={
                <Btn
                  loading={scanningFiles}
                  onClick={() => void scanLargeFilesNow()}
                >
                  Scan files
                </Btn>
              }
            />
          ) : (
            <div className="eo-data-table rounded-lg border border-border">
              <div
                className="grid bg-window px-3 py-2 eo-label text-[10px] text-subtle"
                style={{ gridTemplateColumns: "34px 1fr 72px 72px 120px 90px 95px" }}
              >
                <span />
                <span>FILE & LOCATION</span>
                <span>SIZE</span>
                <span>TYPE</span>
                <span>SAFETY</span>
                <span>MODIFIED</span>
                <span>ACTIONS</span>
              </div>
              {visibleFiles.length === 0 && (
                <div className="border-t border-border px-3 py-6 text-center text-[12px] text-muted-foreground">
                  No files match these filters.
                </div>
              )}
              {visibleFiles.map((r) => (
                <div
                  key={r[1] || r[0]}
                  className="grid items-center border-t border-border px-3 py-2 text-[12px]"
                  style={{ gridTemplateColumns: "34px 1fr 72px 72px 120px 90px 95px" }}
                >
                  <Check
                    label={`Select ${r[0]}`}
                    on={fileSel.has(r[1] ?? "")}
                    onChange={(v) =>
                      setFileSel((current) => {
                        const next = new Set(current);
                        if (v) next.add(r[1] ?? "");
                        else next.delete(r[1] ?? "");
                        return next;
                      })
                    }
                  />
                  <div>
                    <div className="font-semibold">{r[0]}</div>
                    <div className="text-[10.5px] text-subtle">{r[1]}</div>
                  </div>
                  <span>{r[2]}</span>
                  <span>{r[3]}</span>
                  <Badge tone="warning">USER FILE</Badge>
                  <span className="text-subtle">{r[4]}</span>
                  <Btn
                    size="sm"
                    variant="ghost"
                    onClick={async () => {
                      try {
                        await sendHostRequest("storage:reveal-file", { path: r[1] }, 10000);
                      } catch (err) {
                        toast.error("Could not reveal file", { description: err instanceof Error ? err.message : String(err) });
                      }
                    }}
                  >
                    Reveal
                  </Btn>
                </div>
              ))}
            </div>
          )}
        </Card>
      </div>
    );

  const renderCleanRow = (i: StorageCleanItem) => {
    const isChecked = storageSel.sel.has(i.id);
    const isLocked = !i.eligible;
    return (
      <div
        key={i.id}
        onClick={() => !isLocked && storageSel.toggle(i.id)}
        className={cn(
          "flex items-center justify-between gap-3 eo-list-row rounded-lg border border-border bg-window px-3 py-2.5 text-[12.5px] transition-colors",
          !isLocked ? "cursor-pointer hover:border-cyan/40 hover:bg-window/90 select-none" : "cursor-not-allowed opacity-75",
          isChecked ? "border-cyan/40 bg-cyan/5" : "border-border bg-window",
        )}
      >
        <div className="flex items-center gap-2.5 min-w-0 flex-1">
          <SelectMark state={isLocked ? "locked" : isChecked ? "on" : "off"} />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 font-medium">
              <span>{i.name}</span>
              {connected && <span className="text-xs text-subtle">({i.size})</span>}
            </div>
            <div className="text-[11px] text-muted-foreground truncate">{i.desc}</div>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <RiskBadge risk={i?.risk || "LOW"} />
          {!scannedOverview ? (
            <Badge tone="muted" className="text-[9.5px] font-semibold tracking-wide">
              AWAITING SCAN
            </Badge>
          ) : i.status === "ELIGIBLE" ? (
            isChecked ? (
              storageSel.mode === "CUSTOM" ? (
                <Badge tone="cyan" className="text-[9.5px] font-bold tracking-wide">
                  CUSTOM SELECTED
                </Badge>
              ) : (
                <Badge tone="success" className="text-[9.5px] font-bold tracking-wide">
                  AUTO-SELECTED — ELIGIBLE
                </Badge>
              )
            ) : (
              <Badge tone="muted" className="text-[9.5px] font-semibold tracking-wide">
                NOT SELECTED
              </Badge>
            )
          ) : i.status === "ALREADY_CLEAN" ? (
            <Badge tone="muted" className="text-[9.5px] font-semibold">
              ALREADY CLEAN
            </Badge>
          ) : (
            <Badge tone="warning" className="text-[9.5px] font-semibold">
              PROTECTED
            </Badge>
          )}
        </div>
      </div>
    );
  };

  return (
    <div className="space-y-4">
      <PageHeader
        title="Storage Optimizer"
        badges={
          <>
            <Badge tone="cyan">UNIFIED STORAGE CLEAN</Badge>
            <Badge tone="success">ELIGIBLE OPTIMIZATIONS ONLY</Badge>
          </>
        }
        sub="Unified Smart Clean and Deep Clean coordination · Automatic eligibility selection."
        scan={{ ...scan, refresh: handleRefresh }}
      />

      <StatTiles
        items={[
          { label: "STORAGE HEALTH", value: scannedOverview ? scopedHealthStatus : "—" },
          { label: "FREE SPACE", value: scannedOverview && scopedDrives.length ? `${totalDiskFreeGb.toFixed(0)} GB` : "—" },
          { label: "USED SPACE", value: scannedOverview && scopedDrives.length ? `${totalDiskUsedGb.toFixed(0)} GB` : "—" },
          {
            label: "RECLAIMABLE SPACE",
            value: scannedOverview && scopedDrives.length ? `${totalReclaimableGB} GB` : "—",
            sub: scannedOverview && scopedDrives.length ? `${selectedItems.length} selected` : "Awaiting storage scan",
            tone: "cyan",
          },
        ]}
      />

      <Card>
        <Label className="mb-3 text-foreground">LOCAL DRIVES</Label>
        {scannedOverview && drives.length ? (
          <div className="space-y-2">
            {drives.map((d) => (
              <div key={d.name} className="rounded-lg border border-border bg-window p-3 text-xs">
                <div className="flex items-center justify-between font-semibold">
                  <span>{d.name} {d.volumeLabel ? `(${d.volumeLabel})` : ""} · {d.mediaType}</span>
                  <span>{d.freeGb} GB free / {d.totalGb} GB</span>
                </div>
                <Bar value={d.usagePercentage} className="mt-2" />
              </div>
            ))}
          </div>
        ) : (
          <p className="rounded-lg border border-dashed border-border px-3 py-3 text-xs text-muted-foreground">
            Drive capacities and storage health metrics appear here once the background service scans the system.
          </p>
        )}
      </Card>

      {/* ── UNIFIED STORAGE CLEAN CARD (Combining Smart Clean & Deep Clean) ── */}
      <Card>
        <SectionTitle
          title="UNIFIED STORAGE OPTIMIZATION"
          guide="storage.clean"
          sub="Single coordinated workflow combining Smart Clean caches with Deep Clean system logs & dumps."
          right={
            <Btn
              variant="primary"
              onClick={handleOpenModal}
              loading={unifiedRunning}
              disabled={!scannedOverview || unifiedRunning || selectedItems.length === 0 || totalReclaimableBytes === 0}
            >
              {connected && scannedOverview ? `OPTIMIZE STORAGE (${totalReclaimableGB} GB)` : "OPTIMIZE STORAGE"}
            </Btn>
          }
        />

        <div className="mb-4 pb-2 border-b border-border">
          <SelectionBar
            count={scannedOverview ? storageSel.sel.size : 0}
            total={scannedOverview ? storageSel.eligibleCount : 0}
            moded={storageSel}
            title="Storage Optimization"
          />
        </div>

        <div className="space-y-4">
          {/* Smart Clean Section */}
          <div>
            <div className="flex items-center justify-between mb-2">
              <div className="eo-label text-[10.5px] text-cyan font-bold tracking-wider">
                ⚡ SECTION A — SMART CLEAN SCOPE (SAFE TEMPORARY & SYSTEM CACHES)
              </div>
              <span className="text-xs text-muted-foreground">
                {smartCleanItems.length} categories ·{" "}
                {scan.ready
                  ? FORMAT_BYTES(smartCleanItems.reduce((acc, cur) => acc + cur.sizeBytes, 0))
                  : "Pending scan"}
              </span>
            </div>
            <div className="space-y-1.5">
              {smartCleanItems.map(renderCleanRow)}
            </div>
          </div>

          {/* Deep Clean Section */}
          <div className="pt-2 border-t border-border">
            <div className="flex items-center justify-between mb-2">
              <div className="eo-label text-[10.5px] text-cyan font-bold tracking-wider">
                🛡️ SECTION B — DEEP CLEAN SCOPE (DIAGNOSTIC ARCHIVES & RECYCLE BIN)
              </div>
              <span className="text-xs text-muted-foreground">
                {deepCleanItems.length} categories ·{" "}
                {scan.ready
                  ? FORMAT_BYTES(deepCleanItems.reduce((acc, cur) => acc + cur.sizeBytes, 0))
                  : "Pending scan"}
              </span>
            </div>
            <div className="space-y-1.5">
              {deepCleanItems.map(renderCleanRow)}
            </div>
          </div>
        </div>

        <div className="mt-4 border-t border-border pt-3">
          <span className="text-[11px] text-muted-foreground">
            Strict user data protection guarantee: Passwords, cookies, active browser sessions, and personal documents are never touched.
          </span>
        </div>
      </Card>

      <Cols cols="1fr" gap={12}>
        <Card>
          <SectionTitle
            title="FULL FILE EXPLORER"
            guide="storage.explorer"
            sub="Inspect large user files and manage drive space."
          />
          <Btn variant="primary" onClick={() => setExplorer(true)}>
            Open file explorer
          </Btn>
        </Card>
      </Cols>

      {/* ── UNIFIED STORAGE OPTIMIZATION WORKFLOW MODAL ── */}
      <Modal
        open={unifiedModal}
        onClose={() => {
          if (!unifiedRunning) setUnifiedModal(false);
        }}
        title="⚡ Unified Storage Optimization"
        sub="Coordinated Smart Clean & Deep Clean Execution"
        width="max-w-2xl"
        footer={
          unifiedStage === "COMPLETED" ? (
            <Btn variant="primary" onClick={() => setUnifiedModal(false)}>
              Finish & Close
            </Btn>
          ) : (
            <>
              <Btn
                variant="ghost"
                disabled={unifiedRunning}
                onClick={() => setUnifiedModal(false)}
              >
                Cancel
              </Btn>
              <Btn
                variant="primary"
                disabled={unifiedRunning}
                onClick={handleStartUnifiedClean}
              >
                {unifiedRunning ? "Optimizing Storage..." : "Start Unified Storage Optimization"}
              </Btn>
            </>
          )
        }
      >
        <div className="space-y-4">
          {/* Stepper / Progress Status */}
          <div className="rounded-lg border border-border bg-window p-3">
            <div className="flex items-center justify-between text-[12px] font-semibold mb-2">
              <span className="flex items-center gap-1.5">
                <Badge tone="cyan">
                  {unifiedStage === "READY"
                    ? "PREPARED"
                    : unifiedStage === "ANALYZING"
                      ? "ANALYZING ELIGIBILITY"
                      : unifiedStage === "SMART_CLEAN"
                        ? "EXECUTING SMART CLEAN"
                        : unifiedStage === "DEEP_CLEAN"
                          ? "EXECUTING DEEP CLEAN"
                          : "OPTIMIZATION COMPLETE"}
                </Badge>
              </span>
              <span className="tabular-nums text-foreground font-bold">{unifiedProgress}%</span>
            </div>
            <Bar value={unifiedProgress} className="h-2" />
          </div>

          {/* Safety & Exclusion Guarantee Banner */}
          <div className="rounded-lg border border-success/30 bg-success/5 p-3 text-[11.5px] text-foreground">
            <div className="flex items-center gap-2 font-bold text-success mb-1">
              <ShieldCheck className="h-4 w-4" />
              <span>SAFETY & USER DATA PROTECTION GUARANTEE</span>
            </div>
            <p className="text-muted-foreground leading-relaxed">
              The unified storage engine automatically excludes all browser cookies, saved passwords, active authentication tokens, browser history, personal documents, and user projects. Only verified temporary system caches, shader binaries, outdated update staging files, crash dumps, and Recycle Bin items are cleaned.
            </p>
          </div>

          {/* Operations Summary */}
          <div className="grid grid-cols-3 gap-2">
            <div className="rounded-lg border border-border bg-window p-2.5 text-center">
              <div className="text-lg font-bold text-cyan tabular-nums">
                {connected ? `${totalReclaimableGB} GB` : "—"}
              </div>
              <div className="text-[10px] text-muted-foreground font-semibold">RECLAIMABLE SPACE</div>
            </div>
            <div className="rounded-lg border border-border bg-window p-2.5 text-center">
              <div className="text-lg font-bold text-foreground tabular-nums">
                {selectedItems.length} Selected
              </div>
              <div className="text-[10px] text-muted-foreground font-semibold">COORDINATED TASKS</div>
            </div>
            <div className="rounded-lg border border-border bg-window p-2.5 text-center">
              <div className="text-lg font-bold text-success tabular-nums">
                {unifiedStage === "COMPLETED" ? "0 B" : scan.ready ? "Protected" : "—"}
              </div>
              <div className="text-[10px] text-muted-foreground font-semibold">PERSONAL FILES TOUCHED</div>
            </div>
          </div>

          {/* Consolidated Result Report when Completed */}
          {unifiedStage === "COMPLETED" ? (
            <div className="rounded-xl border border-success/40 bg-success/10 p-3.5 space-y-2 text-center animate-in fade-in">
              <div className="flex items-center justify-center gap-2 text-success font-bold text-sm">
                <CheckCircle2 className="h-5 w-5" />
                <span>UNIFIED STORAGE OPTIMIZATION COMPLETED SUCCESSFULLY</span>
              </div>
              <p className="text-xs text-muted-foreground">
                {lastCleanupMsg ?? "Unified Smart Clean and Deep Clean operations completed while preserving all personal data."}
              </p>
            </div>
          ) : (
            <div className="space-y-1.5 max-h-48 overflow-y-auto pr-1">
              <div className="text-[11px] font-bold text-muted-foreground uppercase tracking-wider mb-1">
                Queued Unified Targets ({selectedItems.length} Selected):
              </div>
              {selectedItems.map((item) => (
                <div
                  key={item.id}
                  className="flex items-center justify-between text-xs py-1.5 px-2.5 rounded border border-border/70 bg-window"
                >
                  <span className="font-medium">{item.name}</span>
                  <div className="flex items-center gap-2">
                    <span className="text-subtle">{connected ? item.size : "Pending scan"}</span>
                    <Badge tone="success" className="text-[9px]">ELIGIBLE</Badge>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </Modal>
    </div>
  );
}

/* ───────── SYSTEM TOOLS ───────── */
const TOOLS = [
  ["System File Checker (SFC)", "REPAIR & INTEGRITY", true],
  ["DISM Component Store Repair", "REPAIR & INTEGRITY", true],
  ["Check Disk (read-only scan)", "HARDWARE & BIOS", true],
  ["Memory Diagnostic Scheduler", "MEMORY", true],
  ["Clear Standby Memory", "MEMORY", true],
  ["Temp & Cache Cleanup", "CLEANUP", false],
  ["Component Store Cleanup", "CLEANUP", true],
  ["Reset Windows Update Cache", "WINDOWS OS", true],
  ["Rebuild Icon & Thumbnail Cache", "WINDOWS OS", false],
  ["Reset Network Stack (Winsock)", "NETWORK", true],
  ["Flush DNS Resolver Cache", "NETWORK", false],
  ["Restart Graphics Driver", "GPU & DISPLAY", false],
  ["Clear DirectX Shader Cache", "GPU & DISPLAY", false],
  ["Battery & Energy Report", "HARDWARE & BIOS", true],
  ["Re-register Store Apps", "ADVANCED", true],
  ["Reboot to UEFI Firmware", "ADVANCED", true],
] as const;
const DESTRUCTIVE_TOOL_IDS = new Set([
  "memory-diagnostic",
  "reset-windows-update",
  "reset-winsock",
  "restart-gpu",
  "uefi-reboot",
]);

interface SystemToolJobDto {
  jobId?: string;
  toolId?: string;
  toolName?: string;
  state?: string; // QUEUED, STARTING, RUNNING, WAITING_FOR_ELEVATION, CANCELLING, COMPLETED, COMPLETED_WITH_WARNINGS, FAILED, TIMED_OUT, CANCELLED, REQUIRES_RESTART, REQUIRES_USER_ACTION
  status?: string;
  success?: boolean;
  progress?: number;
  target?: string;
  processName?: string;
  backendOwner?: string;
  startedAtUtc?: string;
  endedAtUtc?: string;
  elapsedMs?: number;
  exitCode?: number | null;
  message?: string;
  outputSummary?: string;
  recentOutputLines?: string[];
  reportPath?: string | null;
  requiresReboot?: boolean;
  requiresUserAction?: boolean;
  reclaimedBytes?: number;
}

export function ToolsPage() {
  const connected = useIsBackendConnected();
  const [c, setC] = useState("ALL TOOLS");
  const [q, setQ] = useState("");
  const [activity, setActivity] = useState<string[]>([]);
  const [runT, setRunT] = useState<string | null>(null);
  const [runDetail, setRunDetail] = useState<string>("");
  const [toolBusy, setToolBusy] = useState(false);
  const [toolProgress, setToolProgress] = useState(0);
  const [toolStatus, setToolStatus] = useState<"running" | "success" | "failed">("running");
  const [confirmTool, setConfirmTool] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const [catalogStatus, setCatalogStatus] = useState<Record<string, string>>({});
  const [activeJob, setActiveJob] = useState<SystemToolJobDto | null>(null);
  const [elapsedSec, setElapsedSec] = useState(0);

  useEffect(() => {
    if (!toolBusy) return;
    const interval = setInterval(() => {
      setElapsedSec((s) => s + 1);
    }, 1000);
    return () => clearInterval(interval);
  }, [toolBusy]);

  const refreshToolsCatalog = async (notify = false) => {
    if (!hasNativeBackend()) return;
    try {
      const res = (await sendHostRequest("tools:catalog", undefined, 15000)) as Array<{ id?: string; name?: string; applicability?: string }>;
      if (Array.isArray(res)) {
        const map: Record<string, string> = {};
        for (const t of res) {
          if (t.name && t.applicability) map[t.name] = t.applicability;
        }
        setCatalogStatus(map);
      }
      if (notify) toast.success("System tools catalog refreshed");
    } catch (err) {
      if (notify) toast.error("Failed to refresh tools", { description: err instanceof Error ? err.message : String(err) });
    }
  };

  const scan = usePageScan("tools", () => refreshToolsCatalog(false));

  const isTerminalState = (state?: string): boolean => {
    if (!state) return false;
    return (
      state === "COMPLETED" ||
      state === "COMPLETED_WITH_WARNINGS" ||
      state === "FAILED" ||
      state === "TIMED_OUT" ||
      state === "CANCELLED" ||
      state === "REQUIRES_RESTART" ||
      state === "REQUIRES_USER_ACTION" ||
      state === "WAITING_FOR_ELEVATION"
    );
  };

  const handleJobCompleted = (name: string, job: SystemToolJobDto) => {
    setToolBusy(false);
    setActiveJob(job);
    setToolProgress(100);
    const ok =
      job.success ||
      job.state === "COMPLETED" ||
      job.state === "COMPLETED_WITH_WARNINGS" ||
      job.state === "REQUIRES_RESTART";
    setToolStatus(ok ? "success" : "failed");
    const summary = job.message || job.outputSummary || (ok ? "Completed" : "Failed");
    setRunDetail(summary);
    setActivity((a) => [`${name} — ${summary}`, ...a]);

    if (!ok) {
      toast.error(`${name} did not succeed`, { description: summary });
    } else if (job.requiresReboot || job.state === "REQUIRES_RESTART") {
      toast.warning(`${name} finished — Restart required`, { description: summary });
    } else {
      toast.success(`${name} completed`, { description: summary });
    }
  };

  const executeTool = async (name: string, confirmed = false) => {
    const toolId = TOOL_NAME_TO_ID[name] ?? name;
    setRunT(name);
    setRunDetail("Starting operation on Windows backend...");
    setToolStatus("running");
    setToolBusy(true);
    setToolProgress(10);
    setElapsedSec(0);
    setActiveJob(null);

    try {
      const initRes = (await sendHostRequest("tools:execute", { toolId, confirmed }, 20000)) as SystemToolJobDto;

      if (initRes.status === "RequiresConfirmation") {
        setConfirmTool(name);
        setToolBusy(false);
        setRunT(null);
        return;
      }

      if (initRes.status === "RequiresAdmin") {
        setToolProgress(100);
        setToolStatus("failed");
        setToolBusy(false);
        setActiveJob(initRes);
        setRunDetail(initRes.message || "Administrator elevation is required.");
        toast.error(`${name} requires Administrator elevation`);
        return;
      }

      if (initRes.status === "Unsupported") {
        setToolProgress(100);
        setToolStatus("failed");
        setToolBusy(false);
        setActiveJob(initRes);
        setRunDetail(initRes.message || "Not supported on this platform.");
        toast.error(`${name} is unsupported`, { description: initRes.message });
        return;
      }

      setActiveJob(initRes);
      setRunDetail(initRes.message || "Executing operation...");
      setToolProgress(initRes.progress || 15);

      if (isTerminalState(initRes.state)) {
        handleJobCompleted(name, initRes);
        return;
      }

      const jobId = initRes.jobId;
      if (!jobId) {
        handleJobCompleted(name, initRes);
        return;
      }

      // Asynchronous status polling loop
      let finished = false;
      while (!finished) {
        await new Promise((resolve) => setTimeout(resolve, 800));
        try {
          const pollRes = (await sendHostRequest("tools:job-status", { jobId }, 10000)) as SystemToolJobDto;
          if (pollRes && pollRes.jobId === jobId) {
            setActiveJob(pollRes);
            if (pollRes.progress !== undefined && pollRes.progress > 0) {
              setToolProgress(pollRes.progress);
            }
            if (pollRes.message) {
              setRunDetail(pollRes.message);
            }

            if (isTerminalState(pollRes.state)) {
              finished = true;
              handleJobCompleted(name, pollRes);
              break;
            }
          }
        } catch (pollErr) {
          console.warn("Tool polling transient warning:", pollErr);
        }
      }
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      setToolProgress(100);
      setToolStatus("failed");
      setRunDetail(msg);
      setActivity((a) => [`${name} — failed: ${msg}`, ...a]);
      toast.error(`${name} failed`, { description: msg });
      setToolBusy(false);
    }
  };

  const cancelActiveJob = async () => {
    if (!activeJob?.jobId) return;
    try {
      setRunDetail("Requesting cancellation...");
      await sendHostRequest("tools:cancel", { jobId: activeJob.jobId }, 10000);
      toast.info("Cancellation requested");
    } catch (err) {
      toast.error("Failed to cancel job", { description: String(err) });
    }
  };

  const requestToolRun = (name: string) => {
    const toolId = TOOL_NAME_TO_ID[name] ?? name;
    if (DESTRUCTIVE_TOOL_IDS.has(toolId)) {
      setConfirmTool(name);
      return;
    }
    void executeTool(name, true);
  };

  const vis = TOOLS.filter(
    (t) => (c === "ALL TOOLS" || t[1] === c) && t[0].toLowerCase().includes(q.toLowerCase()),
  );

  return (
    <div className="space-y-4">
      <PageHeader
        title="System Tools & Health Diagnostics"
        sub="Unified toolbox — 16 advanced Windows diagnostics, repair, and cleanup tools."
        actions={
          <Btn variant="ghost" onClick={() => void refreshToolsCatalog(true)}>
            Refresh
          </Btn>
        }
      />
      <SearchBox placeholder="Search tools, categories, or components…" value={q} onChange={setQ} />
      <Chips
        options={[
          "ALL TOOLS",
          "CLEANUP",
          "MEMORY",
          "REPAIR & INTEGRITY",
          "HARDWARE & BIOS",
          "WINDOWS OS",
          "NETWORK",
          "GPU & DISPLAY",
          "ADVANCED",
        ]}
        value={c}
        onChange={setC}
      />
      <div className="grid grid-cols-2 items-stretch gap-3.5 xl:grid-cols-3">
        {vis.map(([n, cat, elev]) => (
          <div key={n} className="eo-card flex min-w-0 flex-col p-3">
            <Label className="text-cyan">{cat}</Label>
            <div className="mt-1.5 flex-1 text-[13px] font-bold">{n}</div>
            <div className="mt-3 flex items-center justify-between">
              {elev ? (
                <Badge tone="warning">REQUIRES ELEVATION</Badge>
              ) : (
                <Badge tone="success">STANDARD</Badge>
              )}
              <span className="flex gap-1">
                <Btn size="sm" variant="ghost" onClick={() => setInfo(n)}>
                  Info
                </Btn>
                <Btn
                  size="sm"
                  variant={toolBusy && runT === n ? "secondary" : "primary"}
                  onClick={() => {
                    if (toolBusy && runT === n) {
                      setRunT(n);
                    } else if (!toolBusy) {
                      requestToolRun(n);
                    }
                  }}
                >
                  {toolBusy && runT === n ? "Running…" : "Run"}
                </Btn>
              </span>
            </div>
          </div>
        ))}
      </div>
      {!vis.length && (
        <EmptyState
          title="NO TOOLS FOUND"
          sub="No system tools or diagnostics match your current search query or active category."
          action={
            <Btn
              size="sm"
              onClick={() => {
                setC("ALL TOOLS");
                setQ("");
              }}
            >
              Reset all filters
            </Btn>
          }
        />
      )}
      <Card>
        <SectionTitle title="RECENT TOOL ACTIVITY" />
        <p className="text-xs text-muted-foreground">
          {activity.length ? activity.join(" · ") : "No tool actions executed in this session yet."}
        </p>
      </Card>
      <Modal
        open={!!runT}
        onClose={() => setRunT(null)}
        title={`TOOL: ${runT ?? ""}`}
        sub={activeJob?.target || catalogStatus[runT ?? ""] || "Windows Subsystem"}
        footer={
          <>
            <span className="mr-auto text-[11px] text-success flex items-center gap-1.5">
              <span className="inline-block h-1.5 w-1.5 rounded-full bg-success"></span>
              Transactional safety active
            </span>
            {toolBusy && activeJob?.jobId && (
              <Btn
                size="sm"
                variant="danger"
                onClick={() => void cancelActiveJob()}
              >
                Cancel Tool
              </Btn>
            )}
            <Btn
              variant="ghost"
              onClick={() => setRunT(null)}
            >
              {toolBusy ? "Run in background" : "Close"}
            </Btn>
          </>
        }
      >
        <div className="mb-2 flex items-center justify-between text-[11px] font-bold tracking-wider">
          <Badge
            tone={
              toolBusy
                ? "cyan"
                : activeJob?.state === "REQUIRES_RESTART"
                ? "warning"
                : activeJob?.state === "COMPLETED_WITH_WARNINGS"
                ? "warning"
                : activeJob?.success || toolStatus === "success"
                ? "success"
                : "danger"
            }
          >
            {activeJob?.state || (toolBusy ? "RUNNING" : toolStatus === "failed" ? "EXECUTION FAILED" : "EXECUTION COMPLETE")}
          </Badge>
          <span className="font-mono text-cyan">{toolProgress}%</span>
        </div>
        <Bar
          value={toolProgress}
          tone={
            toolBusy
              ? "cyan"
              : activeJob?.state === "REQUIRES_RESTART"
              ? "warning"
              : activeJob?.success || toolStatus === "success"
              ? "success"
              : "danger"
          }
        />
        <p className="mt-2.5 text-xs font-medium text-foreground leading-relaxed">{runDetail}</p>

        {activeJob?.requiresReboot && (
          <div className="mt-3 flex items-start gap-2 rounded border border-warning/30 bg-warning/10 p-2.5 text-xs text-warning">
            <span className="font-bold">⚠️ SYSTEM RESTART REQUIRED:</span>
            <span>This tool made changes that will take full effect after rebooting Windows.</span>
          </div>
        )}

        <div className="mt-4 grid grid-cols-2 gap-2.5 sm:grid-cols-3 lg:grid-cols-5 text-left">
          <div className="rounded border border-border/40 bg-card/40 p-2.5">
            <div className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">TARGET</div>
            <div className="mt-1 text-xs font-bold text-foreground truncate" title={activeJob?.target || "Local PC"}>
              {activeJob?.target || "Local PC"}
            </div>
          </div>
          <div className="rounded border border-border/40 bg-card/40 p-2.5">
            <div className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">PROCESS</div>
            <div className="mt-1 text-xs font-bold text-foreground truncate" title={activeJob?.processName || "System Tool"}>
              {activeJob?.processName || "System Tool"}
            </div>
          </div>
          <div className="rounded border border-border/40 bg-card/40 p-2.5">
            <div className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">ELAPSED</div>
            <div className="mt-1 text-xs font-mono font-bold text-cyan">
              {activeJob?.elapsedMs
                ? `${(activeJob.elapsedMs / 1000).toFixed(1)}s`
                : `${elapsedSec}s`}
            </div>
          </div>
          <div className="rounded border border-border/40 bg-card/40 p-2.5">
            <div className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">BACKEND</div>
            <div className="mt-1 text-xs font-bold text-foreground truncate" title={activeJob?.backendOwner || "Windows Service"}>
              {activeJob?.backendOwner || "Windows Service"}
            </div>
          </div>
          <div className="rounded border border-border/40 bg-card/40 p-2.5">
            <div className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">STATUS</div>
            <div className="mt-1 text-xs font-bold truncate">
              <span className={toolBusy ? "text-cyan" : activeJob?.success ? "text-success" : "text-danger"}>
                {activeJob?.state || (toolBusy ? "RUNNING" : toolStatus.toUpperCase())}
              </span>
            </div>
          </div>
        </div>

        {((activeJob?.recentOutputLines && activeJob.recentOutputLines.length > 0) || activeJob?.outputSummary) && (
          <div className="mt-3.5">
            <div className="mb-1 text-[10px] uppercase font-bold tracking-wider text-cyan">Live Output Stream</div>
            <div className="max-h-44 overflow-y-auto rounded border border-border/40 bg-black/60 p-2.5 font-mono text-[11px] leading-relaxed text-muted-foreground select-text">
              {activeJob?.recentOutputLines && activeJob.recentOutputLines.length > 0 ? (
                activeJob.recentOutputLines.map((line, idx) => (
                  <div key={idx} className="whitespace-pre-wrap break-all">{line}</div>
                ))
              ) : (
                <div className="whitespace-pre-wrap break-all">{activeJob?.outputSummary}</div>
              )}
            </div>
          </div>
        )}
      </Modal>
      <Modal
        open={!!confirmTool}
        onClose={() => setConfirmTool(null)}
        title={`Confirm ${confirmTool ?? "System Action"}`}
        sub="This operation resets system subsystems or schedules an immediate reboot."
        footer={
          <>
            <Btn variant="ghost" onClick={() => setConfirmTool(null)}>
              Cancel
            </Btn>
            <Btn
              variant="danger"
              onClick={() => {
                const target = confirmTool;
                setConfirmTool(null);
                if (target) void executeTool(target, true);
              }}
            >
              Confirm & Run
            </Btn>
          </>
        }
      >
        <p className="text-xs text-muted-foreground">
          You are about to execute <strong className="text-foreground">{confirmTool}</strong>. Ensure open work is saved before proceeding.
        </p>
      </Modal>
      <Modal
        open={!!info}
        onClose={() => setInfo(null)}
        title={info ?? ""}
        footer={<Btn onClick={() => setInfo(null)}>Close</Btn>}
      >
        {[
          "FUNCTION OVERVIEW",
          "TECHNICAL SPECIFICATION & ENGINE",
          "AFFECTED SUBSYSTEMS & COMPONENTS",
        ].map((s) => (
          <div key={s} className="mb-3">
            <Label className="mb-1 text-cyan">{s}</Label>
            <p className="text-xs text-muted-foreground">
              Described in the native app from tool metadata.
            </p>
          </div>
        ))}
        <Label className="mb-1 text-cyan">SECURITY & EXECUTION ARCHITECTURE</Label>
        <Cols cols="1fr 1fr">
          <KV k="SAFETY LEVEL" v="Safe" />
          <KV k="ELEVATION" v="Admin" />
          <KV k="EXECUTION METHOD" v="Native" />
          <KV k="VERIFICATION" v="Exit code + readback" />
        </Cols>
      </Modal>
    </div>
  );
}


/* ───────── PROCESS INTELLIGENCE ───────── */
interface ProcRow {
  n: string;
  mem: number;
  cpu: number;
  s: string;
  why: string;
  pid: number;
  publisher: string;
}

export function ProcessReductionPage() {
  const connected = useIsBackendConnected();
  const [procs, setProcs] = useState<ProcRow[]>([]);
  const [f, setF] = useState("ALL");
  const [sort, setSort] = useState("SAFETY + RAM");
  const [busy, setBusy] = useState(false);

  const loadProcesses = async () => {
    if (!hasNativeBackend()) return;
    try {
      const res = (await sendHostRequest("processes:scan", undefined, 25000)) as Array<Record<string, unknown>>;
      if (Array.isArray(res)) {
        setProcs(
          res.map((p) => {
            const rawSafety = String(p["safetyLevel"] ?? p["SafetyLevel"] ?? "CAUTION").toUpperCase();
            const s = rawSafety.includes("SAFE")
              ? "SAFE TO TERMINATE"
              : rawSafety.includes("CAUTION")
                ? "CAUTION"
                : "PROTECTED / SYSTEM";
            return {
              n: String(p["processName"] ?? p["ProcessName"] ?? p["displayName"] ?? "Process"),
              mem: Math.round(Number(p["ramUsageMb"] ?? p["RamUsageMb"] ?? 0)),
              cpu: Math.round(Number(p["cpuUsagePercent"] ?? p["CpuUsagePercent"] ?? 0) * 10) / 10,
              s,
              why: String(p["reason"] ?? p["Reason"] ?? "Background process"),
              pid: Number(p["processId"] ?? p["ProcessId"] ?? 0),
              publisher: String(p["publisher"] ?? p["Publisher"] ?? "Unknown"),
            };
          }),
        );
      }
    } catch {
      /* retain previous process snapshot cleanly */
    }
  };

  const scan = usePageScan("process-reduction", loadProcesses);
  const procItems = useMemo(
    () =>
      procs.map((p) => ({
        id: p.pid,
        risk: p.s === "SAFE TO TERMINATE" ? ("SAFE" as const) : ("PROTECTED" as const),
      })),
    [procs],
  );
  const procSel = useModedSelection("process-reduction", procItems);
  const { sel, setSel } = procSel;
  const [confirm, setConfirm] = useState(false);
  const [d, setD] = useState<ProcRow | null>(null);
  const vis = procs
    .filter(
      (p) =>
        f === "ALL" ||
        p.s === f ||
        (f === "HIGH MEMORY (>100MB)" && p.mem > 100) ||
        (f === "HIGH CPU (>1%)" && p.cpu > 1),
    )
    .sort((a, b) =>
      sort === "RAM"
        ? b.mem - a.mem
        : sort === "CPU"
          ? b.cpu - a.cpu
          : sort === "NAME"
            ? a.n.localeCompare(b.n)
            : a.s.localeCompare(b.s) || b.mem - a.mem,
    );
  const tone = (s: string) =>
    (s === "SAFE TO TERMINATE" ? "success" : s === "CAUTION" ? "warning" : "cyan") as
      "success" | "warning" | "cyan";
  const ready = procs.length > 0;

  return (
    <div className="space-y-4">
      <PageHeader
        title="Process Intelligence"
        badges={<Badge tone="cyan">PROTECTED SYSTEM PROCESSES</Badge>}
        sub="Analyze running background processes and safely recover memory."
        scan={scan}
      />
      <StatTiles
        items={[
          {
            label: "SAFE TO TERMINATE",
            value: ready ? procs.filter((p) => p.s === "SAFE TO TERMINATE").length : "—",
            sub: "Classified as safe",
            tone: "success",
          },
          { label: "SELECTED", value: ready ? sel.size : "—", sub: "Marked for safe termination", tone: "cyan" },
          {
            label: "RAM RECLAIMABLE",
            value: ready ? `${procs.filter((p) => sel.has(p.pid)).reduce((a, p) => a + p.mem, 0)} MB` : "—",
            sub: "From selected processes",
          },
          {
            label: "SYSTEM STATUS",
            value: ready ? "Healthy" : "Not scanned",
            sub: ready ? "Based on process & memory state" : "Awaiting background service",
            tone: ready ? "success" : "muted",
          },
        ]}
      />
      <Card>
        <div className="mb-3 flex flex-wrap items-center justify-between gap-3.5">
          <Chips
            options={[
              "ALL",
              "SAFE TO TERMINATE",
              "CAUTION",
              "PROTECTED / SYSTEM",
              "HIGH MEMORY (>100MB)",
              "HIGH CPU (>1%)",
            ]}
            value={f}
            onChange={setF}
          />
          <div className="flex gap-1.5">
            <SelectionBar
              count={sel.size}
              total={procSel.eligibleCount}
              onAll={procSel.selectAll}
              onClear={() => setSel(new Set())}
              allLabel="Select all safe"
              autoLabel="ALL SAFE"
              moded={procSel}
            />
          </div>
        </div>
        <div className="mb-3 flex items-center gap-2">
          <Label>SORT:</Label>
          <Chips options={["SAFETY + RAM", "RAM", "CPU", "NAME"]} value={sort} onChange={setSort} />
        </div>
        <Label className="mb-2 text-foreground">BACKGROUND PROCESS CANDIDATES</Label>
        <div className="eo-data-table rounded-lg border border-border">
          <div
            className="grid bg-window px-3 py-2 eo-label text-[10px] text-subtle"
            style={{
              gridTemplateColumns:
                "26px 60px minmax(130px, 1fr) 45px 145px minmax(90px, 1fr) 45px 50px",
            }}
          >
            <span>SEL</span>
            <span>MEMORY</span>
            <span>PROCESS & IDENTITY</span>
            <span>CPU</span>
            <span>SAFETY LEVEL</span>
            <span>REASON</span>
            <span>PID</span>
            <span>ACTION</span>
          </div>
          {vis.map((p) => (
            <div
              key={p.pid}
              className="grid items-center border-t border-border px-3 py-2 text-[12px]"
              style={{
                gridTemplateColumns:
                  "26px 60px minmax(130px, 1fr) 45px 145px minmax(90px, 1fr) 45px 50px",
              }}
            >
              <SelectMark state={p.s === "SAFE TO TERMINATE" ? "on" : "locked"} />
              <span>{p.mem} MB</span>
              <span className="font-semibold">{p.n}</span>
              <span>{p.cpu}%</span>
              <Badge tone={tone(p.s)}>{p.s}</Badge>
              <span className="text-muted-foreground">{p.why}</span>
              <span className="font-mono text-subtle">{p.pid}</span>
              <Btn size="sm" variant="ghost" onClick={() => setD(p)}>
                Info
              </Btn>
            </div>
          ))}
          {!vis.length && (
            <EmptyState
              title="No active process list available"
              sub="Connect the ERROR OPTIMIZER background service to inspect running background processes."
            />
          )}
        </div>
        <div className="mt-3 flex justify-end">
          <Btn variant="danger" disabled={!connected || !sel.size} onClick={() => setConfirm(true)}>
            Terminate selected
          </Btn>
        </div>
      </Card>
      <Modal
        open={confirm}
        onClose={() => setConfirm(false)}
        title="Terminate selected processes?"
        footer={
          <>
            <Btn variant="ghost" onClick={() => setConfirm(false)}>
              Cancel
            </Btn>
            <Btn
              variant="danger"
              disabled={busy}
              onClick={async () => {
                const pids = Array.from(sel).map((x) => Number(x)).filter((n) => n > 0);
                setConfirm(false);
                if (!pids.length) return;
                setBusy(true);
                try {
                  const res = (await sendHostRequest("processes:kill", { pids }, 30000)) as {
                    terminated?: number;
                    accessDenied?: number;
                    skippedProtected?: number;
                  } | null;
                  setSel(new Set());
                  const terminated = typeof res?.terminated === "number" ? res.terminated : pids.length;
                  const denied = typeof res?.accessDenied === "number" ? res.accessDenied : 0;
                  const skippedProt = typeof res?.skippedProtected === "number" ? res.skippedProtected : 0;
                  if (terminated === 0 && (denied > 0 || skippedProt > 0 || pids.length > 0)) {
                    toast.error("No processes terminated", {
                      description: denied > 0
                        ? `${denied} process(es) denied access (Administrator privilege may be required).`
                        : `${skippedProt} protected or already-exited process(es) skipped.`,
                    });
                  } else {
                    const noteParts: string[] = [];
                    if (denied > 0) noteParts.push(`${denied} access denied`);
                    if (skippedProt > 0) noteParts.push(`${skippedProt} protected skipped`);
                    toast.success(`Terminated ${terminated} selected process${terminated === 1 ? "" : "es"}`, {
                      ...(noteParts.length > 0 ? { description: noteParts.join(" · ") } : {}),
                    });
                  }
                  await loadProcesses();
                } catch (err) {
                  toast.error("Process termination failed", { description: err instanceof Error ? err.message : String(err) });
                } finally {
                  setBusy(false);
                }
              }}
            >
              Confirm terminate
            </Btn>
          </>
        }
      >
        <p className="text-sm text-muted-foreground">
          Warning: the selected background processes will be terminated immediately. Unsaved data in
          them may be lost.
        </p>
      </Modal>
      <Modal
        open={!!d}
        onClose={() => setD(null)}
        title={d?.n ?? ""}
        footer={<Btn onClick={() => setD(null)}>Close</Btn>}
      >
        {d && (
          <>
            <KV k="Process ID (PID):" v={d.pid} />
            <KV k="Publisher / Company:" v="Unknown" />
            <KV k="Working Set Memory:" v={`${d.mem} MB`} />
            <KV k="CPU Usage (Approx):" v={`${d.cpu}%`} />
            <KV k="Safety Level:" v={d.s} />
            <KV k="Analysis Reason:" v={d.why} />
          </>
        )}
      </Modal>
    </div>
  );
}

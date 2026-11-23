// TierView.xaml — one shared layout for Normal / Pro / Ultimate / Debloat / BIOS Safe / Max Performance.
// Debloat adds DEBLOAT TOOLBOX + blocklist editor; BIOS Safe adds MANUAL UEFI / FIRMWARE section.
import { useState, useMemo, useRef, useEffect, useCallback } from "react";
import { toast } from "sonner";
import { AlertCircle, CheckCircle2 } from "lucide-react";
import {
  Badge,
  Btn,
  Card,
  Check,
  Chips,
  Label,
  Modal,
  RiskBadge,
  SectionTitle,
  EmptyState,
  Details,
  KV,
  PageHeader,
  StatTiles,
  SelectionBar,
  SelectMark,
  CatalogFilterDropdown,
  normalizeFilterScope,
} from "../kit";
import { RunModal } from "../RunModal";
import { usePageScan } from "../usePageScan";
import { hasNativeBackend, useIsBackendConnected, isDesktopApp } from "@/lib/eo/nativeBridge";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { CATALOG, TIERS, type Risk, type TierInfo } from "@/lib/eo/data";
import { toggleInSet } from "@/lib/eo/selection";
import { useModedSelection, usePersistedSelection } from "../useSelection";
import { normalizeOptimizationCatalog } from "@/lib/eo/catalogContract";
import { cn } from "@/lib/utils";

const TIER_CATS: Record<string, string[]> = {
  normal: ["TELEMETRY", "PRIVACY", "POWER", "INPUT", "NTFS"],
  pro: ["CPU", "MEMORY", "NETWORK", "GAMING", "TELEMETRY"],
  ultimate: ["CPU", "MEMORY", "GPU", "GAMING", "NETWORK", "NTFS"],
  debloat: ["PRIVACY", "TELEMETRY"],
  "bios-safe": ["SYSTEM"],
  "max-performance": ["CPU", "GPU", "POWER", "MEMORY", "GAMING"],
};
interface DebloatToolItem {
  name: string;
  desc: string;
  risk: Risk;
  targetIds: string[];
}

const DEBLOAT_TOOLS: Record<string, DebloatToolItem[]> = {
  BLOATWARE: [
    {
      name: "Consumer Appx packages",
      desc: "Remove pre-installed consumer apps (Candy Crush, TikTok, Facebook, Instagram, Clipchamp, Bing News).",
      risk: "LOW",
      targetIds: [
        "debloat.appx.candycrush",
        "debloat.appx.tiktok",
        "debloat.appx.facebook",
        "debloat.appx.instagram",
        "debloat.appx.clipchamp",
        "debloat.appx.bingnews",
      ],
    },
    {
      name: "Custom bloatware removal",
      desc: "Surgically remove Xbox Game Bar overlay while preserving Game Pass, and process user blocklist.",
      risk: "MEDIUM",
      targetIds: [
        "debloat.appx.xbox_gamebar",
      ],
    },
  ],
  FEATURES: [
    {
      name: "Optional Windows features",
      desc: "Disable unused legacy optional features (OneDrive sync, Cortana, Widgets, Teams taskbar).",
      risk: "MEDIUM",
      targetIds: [
        "debloat.feature.onedrive.startup",
        "debloat.feature.cortana",
        "debloat.feature.widgets",
        "debloat.feature.teams",
      ],
    },
  ],
  PRIVACY: [
    {
      name: "Advertising ID & tailored experiences",
      desc: "Stop personalized ads and tailored diagnostic experiences.",
      risk: "SAFE",
      targetIds: [
        "debloat.privacy.advertising",
        "debloat.privacy.tailored",
      ],
    },
    {
      name: "Activity history",
      desc: "Disable timeline activity collection and cloud tracking.",
      risk: "SAFE",
      targetIds: [
        "debloat.privacy.activityfeed",
      ],
    },
  ],
  REGISTRY: [
    {
      name: "Telemetry policy keys",
      desc: "Apply Windows diagnostic data telemetry policy for current edition.",
      risk: "LOW",
      targetIds: [
        "debloat.privacy.telemetry",
      ],
    },
  ],
  "WINDOWS UI": [
    {
      name: "Widgets & taskbar feed",
      desc: "Hide Windows 11 Widgets taskbar feed and background host.",
      risk: "SAFE",
      targetIds: [
        "debloat.feature.widgets",
      ],
    },
    {
      name: "Start menu suggestions",
      desc: "Remove promoted suggestions, lockscreen ads, and silent consumer app downloads.",
      risk: "SAFE",
      targetIds: [
        "debloat.registry.suggestions",
        "debloat.registry.lockscreen",
        "debloat.registry.silentapps",
      ],
    },
  ],
  ADVANCED: [
    {
      name: "Edge background tasks",
      desc: "Disable Microsoft Edge startup boost & background tasks.",
      risk: "MEDIUM",
      targetIds: [
        "debloat.advanced.edge_background",
      ],
    },
  ],
};
const UEFI = [
  [
    "Enable Resizable BAR / Smart Access Memory",
    "Firmware setting · verify GPU and board support first",
  ],
  ["Enable XMP / EXPO memory profile", "Firmware setting · runs RAM at rated speed"],
  ["Disable unused onboard controllers", "Firmware setting · optional"],
];
const BLOCKLIST = [
  "Microsoft.BingNews",
  "Microsoft.BingWeather",
  "Microsoft.GetHelp",
  "Microsoft.Getstarted",
  "Microsoft.MicrosoftSolitaireCollection",
  "Microsoft.People",
  "Microsoft.WindowsFeedbackHub",
  "Microsoft.ZuneMusic",
  "Microsoft.ZuneVideo",
  "Clipchamp.Clipchamp",
];

export interface TierItem {
  id: string;
  name: string;
  cat: string;
  risk: Risk;
  scope: string;
  done: boolean;
  ineligible?: boolean | undefined;
  isApplied?: boolean | undefined;
  reason?: string | undefined;
  currentValue?: string | undefined;
  targetValue?: string | undefined;
  eligibilityState?: string | undefined;
}

export function TierPage({ slug }: { slug: string }) {
  const t = TIERS[slug];
  // Guard before any hooks run: TierView always calls the same hooks in the same order.
  return t ? <TierView slug={slug} t={t} /> : null;
}

function TierView({ slug, t }: { slug: string; t: TierInfo }) {
  const isBiosSafe = slug === "bios-safe" || t.toolbox === "bios";
  const connected = useIsBackendConnected();
  const mountedRef = useRef(true);
  const scanGenRef = useRef(0);

  const [appliedIds, setAppliedIds] = useState<Set<string>>(() => new Set());
  const [scanMeta, setScanMeta] = useState<
    Record<string, { state: string; currentValue?: string | undefined; targetValue?: string | undefined; reason?: string | undefined }>
  >({});
  const [hwStatus, setHwStatus] = useState<string | null>(null);
  const [runtimeLog, setRuntimeLog] = useState<string | null>(null);
  const [uefiConfirm, setUefiConfirm] = useState(false);

  const [backendItems, setBackendItems] = useState<TierItem[] | null>(null);
  const [authCounts, setAuthCounts] = useState<{
    totalCatalogCount: number;
    eligibleCount: number;
    alreadyOptimizedCount: number;
    ineligibleCount: number;
    safeCount: number;
    medCount: number;
    highCount: number;
  } | null>(null);

  useEffect(() => {
    mountedRef.current = true;
    scanGenRef.current += 1;
    setBackendItems(null);
    setAuthCounts(null);
    setAppliedIds(new Set());
    setScanMeta({});
    setHwStatus(null);
    setRuntimeLog(null);
    setDetail(null);
    setScope("ACTIONABLE");
    return () => {
      mountedRef.current = false;
    };
  }, [slug]);

  const items: TierItem[] = backendItems ?? [];
  const actionableCatalogItems = useMemo(
    () => items.filter((i): i is TierItem => Boolean(i && !i.ineligible && !i.done)),
    [items],
  );
  // Each tier is its own profile. ALL ELIGIBLE runs ONLY genuinely actionable items of this tier; CUSTOM is the only editable mode.
  const tierSel = useModedSelection<string>(`tier:${slug}`, actionableCatalogItems);
  const { sel, setSel } = tierSel;
  const [detail, setDetail] = useState<(typeof items)[number] | null>(null);
  const [logsCleared, setLogsCleared] = useState(false);
  const [scope, setScope] = useState("ACTIONABLE");
  const [toolCat, setToolCat] = useState("BLOATWARE");
  const [block, setBlock] = useState(false);
  const [blockSel, setBlockSel] = useState<Set<string>>(new Set(BLOCKLIST.slice(0, 7)));
  const [run, setRun] = useState(false);
  const [logsOpen, setLogsOpen] = useState(false);

  const scan = usePageScan(`tier:${slug}`, async () => {
    const currentGen = ++scanGenRef.current;
    if (!hasNativeBackend()) {
      if (!mountedRef.current || currentGen !== scanGenRef.current) return;
      throw new Error("ERROR OPTIMIZER background service is disconnected. Connect the service to scan optimization eligibility.");
    }
    try {
      const res = (await sendHostRequest("tier:scan", { tier: t.id }, 30000)) as {
        items?: Array<{
          id?: string;
          name?: string;
          category?: string;
          risk?: string;
          description?: string;
          scope?: string;
          eligibilityState?: string;
          selectionState?: string;
          reason?: string;
          alreadyOptimized?: boolean;
          isApplied?: boolean;
          state?: string;
          currentValue?: string;
          targetValue?: string;
        }>;
        excludedItems?: Array<{
          id?: string;
          name?: string;
          category?: string;
          risk?: string;
          description?: string;
          scope?: string;
          eligibilityState?: string;
          selectionState?: string;
          reason?: string;
          alreadyOptimized?: boolean;
          isApplied?: boolean;
          state?: string;
          currentValue?: string;
          targetValue?: string;
        }>;
        totalCatalogCount?: number;
        eligibleCount?: number;
        ineligibleCount?: number;
        alreadyOptimizedCount?: number;
        selectedCount?: number;
        actions?: Array<{ itemId?: string; displayName?: string; currentValue?: string; targetValue?: string; status?: string }>;
        plannedChanges?: Array<{ id?: string; item?: string; current?: string; target?: string }>;
        inheritedChanges?: Array<{ id?: string; item?: string; current?: string; target?: string }>;
        skippedChanges?: Array<{ id?: string; item?: string; reason?: string; category?: string }>;
        hardwareSummary?: string;
        summary?: string;
      };
      if (!mountedRef.current || currentGen !== scanGenRef.current) return;
      const done = new Set<string>();
      const meta: Record<string, { state: string; currentValue?: string | undefined; targetValue?: string | undefined; reason?: string | undefined }> = {};

      const allBackendRows = [
        ...(Array.isArray(res?.items) ? res.items : []),
        ...(Array.isArray(res?.excludedItems) ? res.excludedItems : []),
      ];

      for (const row of allBackendRows) {
        const key = row.id ?? row.name;
        if (key) {
          const elig = String(row.eligibilityState ?? "").toUpperCase();
          const isDone = Boolean(
            row.alreadyOptimized ||
            row.isApplied ||
            elig === "ALREADY_OPTIMIZED" ||
            elig === "ALREADY_OPTIMAL" ||
            row.state === "OPTIMIZED" ||
            row.state === "Applied",
          );
          if (isDone) done.add(key);
          meta[key] = {
            state: elig || row.state || (isDone ? "ALREADY_OPTIMIZED" : "ELIGIBLE"),
            currentValue: row.currentValue,
            targetValue: row.targetValue,
            reason: row.reason,
          };
        }
      }
      for (const act of res?.actions ?? []) {
        const key = act.itemId ?? act.displayName;
        if (key) {
          const st = String(act.status ?? "");
          const isDone = st.toLowerCase().includes("already");
          if (isDone) done.add(key);
          meta[key] = { state: isDone ? "ALREADY_OPTIMIZED" : st || "RECOMMENDED", currentValue: act.currentValue, targetValue: act.targetValue };
        }
      }
      for (const p of [...(res?.plannedChanges ?? []), ...(res?.inheritedChanges ?? [])]) {
        const key = p.id ?? p.item;
        if (key) {
          const isDone = Boolean(p.current && p.target && p.current.toLowerCase() === p.target.toLowerCase());
          if (isDone) done.add(key);
          meta[key] = { state: isDone ? "ALREADY_OPTIMIZED" : "ELIGIBLE", currentValue: p.current, targetValue: p.target };
        }
      }
      for (const sk of res?.skippedChanges ?? []) {
        const key = sk.id ?? sk.item;
        if (key) {
          meta[key] = { state: sk.category ?? "SKIPPED", reason: sk.reason };
        }
      }

      setScanMeta(meta);
      setAppliedIds(done);

      if (mountedRef.current && currentGen === scanGenRef.current) {
        const norm = normalizeOptimizationCatalog(res, `tier:${slug}`);
        const mappedItems: TierItem[] = norm.items.map((ni) => {
          const isApplied = Boolean(
            ni.isApplied ||
            (ni.done && (done.has(ni.id) || appliedIds.has(ni.id)) && (meta[ni.id]?.state === "ALREADY_APPLIED" || meta[ni.id]?.state === "APPLIED" || meta[ni.id]?.state === "Applied"))
          );
          return {
            id: ni.id,
            name: ni.name,
            cat: ni.category,
            risk: ni.risk as Risk,
            scope: ni.scope,
            done: ni.done,
            ineligible: ni.ineligible ? true : undefined,
            isApplied,
            reason: ni.reason || ni.applicabilityReason || meta[ni.id]?.reason,
            currentValue: ni.currentState || meta[ni.id]?.currentValue,
            targetValue: ni.targetValue || meta[ni.id]?.targetValue,
            eligibilityState: ni.eligibilityState || meta[ni.id]?.state,
          };
        });

        const actionableList = mappedItems.filter((i) => !i.ineligible && !i.done);
        const alreadyList = mappedItems.filter((i) => i.done && !i.ineligible);
        const ineligibleList = mappedItems.filter((i) => i.ineligible);

        const alreadyCount = typeof res?.alreadyOptimizedCount === "number"
          ? res.alreadyOptimizedCount
          : alreadyList.length;
        const ineligCount = typeof res?.ineligibleCount === "number"
          ? res.ineligibleCount
          : ineligibleList.length;
        const eligibleCount = typeof res?.eligibleCount === "number"
          ? res.eligibleCount
          : actionableList.length;
        const totalCount = typeof res?.totalCatalogCount === "number"
          ? res.totalCatalogCount
          : (eligibleCount + alreadyCount + ineligCount);

        setAuthCounts({
          totalCatalogCount: totalCount,
          eligibleCount,
          alreadyOptimizedCount: alreadyCount,
          ineligibleCount: ineligCount,
          safeCount: actionableList.filter((i) => i.risk === "SAFE" || i.risk === "LOW").length,
          medCount: actionableList.filter((i) => i.risk === "MEDIUM").length,
          highCount: actionableList.filter((i) => i.risk === "HIGH").length,
        });

        setBackendItems(mappedItems);

        if (tierSel.mode !== "CUSTOM") {
          setSel(new Set(actionableList.map((i) => i.id)));
        } else {
          const actionableIdSet = new Set(actionableList.map((i) => i.id));
          setSel((currentSel) => new Set([...currentSel].filter((id) => actionableIdSet.has(id))));
        }

        setHwStatus(res?.hardwareSummary ?? res?.summary ?? `${t.name} profile scanned via Windows backend`);
        setRuntimeLog(`${t.id} profile scanned · ${actionableList.length} actionable · ${alreadyCount} already optimized`);
      }
      if (t.toolbox === "debloat") {
        const bl = (await sendHostRequest("tier:debloat-get-blocklist", undefined, 15000)) as { blockedIds?: string[] };
        if (mountedRef.current && currentGen === scanGenRef.current && Array.isArray(bl?.blockedIds) && bl.blockedIds.length > 0) {
          setBlockSel(new Set(bl.blockedIds));
        }
      }
    } catch (err) {
      if (mountedRef.current && currentGen === scanGenRef.current) {
        setHwStatus(err instanceof Error ? err.message : "Backend scan unavailable");
        throw err;
      }
    }
  });

  const handleRefresh = useCallback(() => {
    scanGenRef.current += 1;
    setBackendItems(null);
    setAuthCounts(null);
    setAppliedIds(new Set());
    setScanMeta({});
    setHwStatus(null);
    setRuntimeLog(null);
    setSel(new Set());
    scan.refresh();
  }, [scan, setSel]);
  const accent = {
    "tier-normal": "bg-tier-normal",
    "tier-pro": "bg-tier-pro",
    "tier-ultimate": "bg-tier-ultimate",
    "tier-debloat": "bg-tier-debloat",
    "tier-bios": "bg-tier-bios",
    primary: "bg-primary",
  }[t.color];

  if (scan.error && !backendItems && scan.ready) {
    return (
      <div className="space-y-4 pb-16">
        <PageHeader title={t.name} accent={accent} scan={scan} />
        <Card className="border-danger/30 bg-window/80 p-6">
          <div className="flex items-start gap-3">
            <div className="rounded-md bg-danger/10 p-2 text-danger">
              <AlertCircle className="h-6 w-6" />
            </div>
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2">
                <h2 className="text-base font-bold text-foreground">Profile Scan Error</h2>
                <Badge tone="danger">BACKEND DIAGNOSTICS</Badge>
              </div>
              <p className="mt-1 text-xs text-muted-foreground">{scan.error}</p>
              <div className="mt-4 flex gap-2">
                <Btn variant="primary" size="sm" onClick={handleRefresh}>
                  Retry profile scan
                </Btn>
              </div>
            </div>
          </div>
        </Card>
      </div>
    );
  }
  const eligibleItems = items.filter((i): i is TierItem => Boolean(i && !i.ineligible && !i.done));
  const alreadyOptimizedItems = items.filter((i): i is TierItem => Boolean(i && i.done && !i.ineligible));
  const ineligibleItems = items.filter((i): i is TierItem => Boolean(i && i.ineligible));
  const pending = eligibleItems;
  const normScope = normalizeFilterScope(scope);
  const viewPool: TierItem[] =
    normScope === "OPTIMIZED"
      ? alreadyOptimizedItems
      : normScope === "INELIGIBLE"
        ? ineligibleItems
        : normScope === "ALL"
          ? items
          : eligibleItems;
  const vis = viewPool;
  const tog = (id: string) => {
    const target = eligibleItems.find((it) => it.id === id);
    if (!target) return;
    setSel((s) => toggleInSet(s, id));
  };

  return (
    <div className="space-y-4 pb-16">
      <PageHeader
        title={t.name}
        accent={accent}
        badges={
          <>
            <Badge tone="cyan">TIER PROFILE</Badge>
            <Badge tone={tierSel.mode === "CUSTOM" ? "cyan" : "success"}>
              {tierSel.mode === "CUSTOM" ? "CUSTOM SELECTION" : "ELIGIBLE OPTIMIZATIONS ONLY"}
            </Badge>
            {t.extendsFrom && <Badge>EXTENDS {t.extendsFrom.toUpperCase()}</Badge>}
          </>
        }
        sub={t.desc.split(". ")[0]?.replace(/\.?$/, ".")}
        scan={{ ...scan, refresh: handleRefresh }}
      />

      <div className="space-y-2">
        <StatTiles
          items={[
            {
              label: "TOTAL CATALOG",
              value: authCounts ? authCounts.totalCatalogCount : "—",
              sub: "Authoritative operations",
            },
            { label: "ACTIONABLE", value: authCounts ? authCounts.eligibleCount : "—", tone: "cyan" },
            { label: "SELECTED", value: backendItems ? sel.size : 0, tone: "primary" },
            { label: "OPTIMIZED", value: authCounts ? authCounts.alreadyOptimizedCount : "—", tone: "success" },
            { label: "INELIGIBLE", value: authCounts ? authCounts.ineligibleCount : "—", tone: "muted" },
            {
              label: "SAFE · MEDIUM · HIGH",
              value: authCounts ? `${authCounts.safeCount} · ${authCounts.medCount} · ${authCounts.highCount}` : "—",
            },
          ]}
        />
        <p className="flex flex-wrap items-center gap-x-3 gap-y-1 px-1 text-[11px] text-subtle">
          <Badge tone="muted">HARDWARE</Badge>
          <span>{hwStatus ?? (scan.scanning ? `Evaluating system eligibility and scanning hardware telemetry for ${t.name}…` : scan.ready ? `${t.name} profile ready` : "Waiting for system scan from background service")}</span>
        </p>
      </div>

      <Card>
        <SectionTitle title="TOP RECOMMENDATIONS" />
        {!backendItems ? (
          <div className="rounded-lg border border-border/60 bg-window/40 p-4 text-xs text-muted-foreground flex items-center gap-2.5">
            <span>{connected ? `Evaluating system eligibility and scanning hardware telemetry for ${t.name}…` : `Connect the ERROR OPTIMIZER background service to scan ${t.name} eligibility.`}</span>
          </div>
        ) : pending.length === 0 ? (
          <div className="text-sm font-semibold text-success p-3 rounded-lg bg-success/10 border border-success/30 flex items-center gap-2">
            <CheckCircle2 className="h-4 w-4 shrink-0 text-success" />
            <span>
              Everything is already optimized. No changes are necessary.
              {alreadyOptimizedItems.length > 0 && ` (${alreadyOptimizedItems.length} verified optimal)`}
            </span>
          </div>
        ) : (
          <div className="grid grid-cols-3 gap-2.5">
            {pending.slice(0, 3).filter((i): i is TierItem => Boolean(i)).map((i) => (
              <div
                key={i.id}
                className="eo-list-row rounded-lg border border-border bg-window px-3 py-2.5"
              >
                <div className="flex items-center justify-between">
                  <Badge tone="cyan">{i.cat || "GENERAL"}</Badge>
                  <RiskBadge risk={i.risk || "LOW"} />
                </div>
                <div className="mt-1.5 text-[12.5px] font-semibold">{i.name || i.id}</div>
              </div>
            ))}
          </div>
        )}
      </Card>

      {t.toolbox === "debloat" && (
        <Card>
          <SectionTitle
            title="DEBLOAT TOOLBOX"
            guide="tier.debloat"
            right={
              <Btn size="sm" onClick={() => setBlock(true)}>
                Customize blocklist
              </Btn>
            }
          />
          <Chips options={Object.keys(DEBLOAT_TOOLS)} value={toolCat} onChange={setToolCat} />
          <div className="mt-3 grid grid-cols-2 gap-2.5">
            {(DEBLOAT_TOOLS[toolCat] ?? []).map((d) => (
              <div
                key={d.name}
                className="flex items-start justify-between gap-3 rounded-xl border border-border bg-window p-3.5"
              >
                <div>
                  <div className="text-[13px] font-semibold">{d.name}</div>
                  <p className="text-[11.5px] text-muted-foreground">{d.desc}</p>
                </div>
                <div className="flex flex-col items-end gap-2">
                  <RiskBadge risk={d?.risk} />
                  <Btn
                    size="sm"
                    onClick={async () => {
                      try {
                        const targetIds = d.targetIds ?? [];
                        if (targetIds.length === 0) {
                          toast.info(`${d.name}: No matching operations`, { description: "No targeted operations found in current profile catalog." });
                          return;
                        }

                        // For custom bloatware removal, also include customized blocklist packages if any are queued
                        const executionIds = d.name.toLowerCase().includes("custom bloatware") && blockSel.size > 0
                          ? Array.from(new Set([...targetIds, ...blockSel]))
                          : targetIds;

                        const res = (await sendHostRequest("tier:execute", { tierId: t.id, selectedItemIds: executionIds }, 90000)) as Record<string, unknown>;
                        const rec = (res && typeof res === "object" ? res : {}) as Record<string, unknown>;
                        const rawRows = rec["operations"] ?? rec["Operations"] ?? rec["results"] ?? rec["Results"];
                        const rows = (Array.isArray(res) ? res : (Array.isArray(rawRows) ? rawRows : [])) as Array<Record<string, unknown>>;
                        const statusStr = String(rec["status"] ?? "");
                        const isNoEligible = statusStr.toLowerCase().includes("no_eligible") || statusStr === "NO_ELIGIBLE_OPERATIONS";
                        const isAlready = statusStr.toUpperCase() === "ALREADY_OPTIMAL" || rec["alreadyOptimized"] === true;
                        const failedCount = typeof rec["failedCount"] === "number" ? rec["failedCount"] as number : (typeof rec["FailedCount"] === "number" ? rec["FailedCount"] as number : rows.filter((r) => String(r["status"] ?? r["outcome"] ?? r["ExecutionState"] ?? "").toLowerCase() === "failed" || r["status"] === 2).length);
                        const verifiedCount = typeof rec["verifiedCount"] === "number" ? rec["verifiedCount"] as number : (typeof rec["VerifiedCount"] === "number" ? rec["VerifiedCount"] as number : rows.filter((r) => r["verified"] === true || r["Verified"] === true).length);
                        const alreadyCount = typeof rec["alreadyOptimizedCount"] === "number" ? rec["alreadyOptimizedCount"] as number : (typeof rec["AlreadyOptimizedCount"] === "number" ? rec["AlreadyOptimizedCount"] as number : 0);

                        if (isAlready || (alreadyCount > 0 && verifiedCount === 0 && failedCount === 0)) {
                          toast.success(`${d.name}: Already optimal`, {
                            description: `All ${alreadyCount || executionIds.length} operations are already in their desired optimal state.`,
                          });
                        } else if (isNoEligible) {
                          toast.info(`${d.name}: No eligible operations`, { description: String(rec["message"] ?? "All items are in desired state or unsupported.") });
                        } else if (failedCount > 0 && verifiedCount === 0 && alreadyCount === 0) {
                          throw new Error(String(rec["error"] ?? rec["message"] ?? "All targeted optimizations failed."));
                        } else if (failedCount > 0) {
                          toast.warning(`${d.name} completed with issues`, {
                            description: `${verifiedCount} verified, ${failedCount} failed of ${executionIds.length} targeted.`,
                          });
                        } else {
                          toast.success(`${d.name} completed`, {
                            description: `${verifiedCount} of ${executionIds.length} operations verified${alreadyCount > 0 ? ` · ${alreadyCount} already optimal` : ""}.`,
                          });
                        }
                        scan.refresh();
                      } catch (err) {
                        toast.error(`${d.name} failed`, { description: err instanceof Error ? err.message : String(err) });
                      }
                    }}
                  >
                    Run
                  </Btn>
                </div>
              </div>
            ))}
          </div>
        </Card>
      )}

      <Card>
        <SectionTitle
          title="WHAT WILL CHANGE"
          guide="tier.preview"
          sub={
            tierSel.mode === "CUSTOM"
              ? "You control the selection · only the optimizations you tick will change."
              : "All applicable optimizations matching hardware tier criteria are automatically selected."
          }
        />
        <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
          <div className="w-[270px] shrink-0">
            <CatalogFilterDropdown
              value={scope}
              onChange={setScope}
              counts={{
                actionable: authCounts ? authCounts.eligibleCount : eligibleItems.length,
                optimized: authCounts ? authCounts.alreadyOptimizedCount : alreadyOptimizedItems.length,
                ineligible: authCounts ? authCounts.ineligibleCount : ineligibleItems.length,
                all: authCounts ? authCounts.totalCatalogCount : items.length,
              }}
            />
          </div>
          <div className="flex flex-wrap items-center gap-1.5">
            <SelectionBar
              count={backendItems ? sel.size : 0}
              total={backendItems ? eligibleItems.length : 0}
              moded={tierSel}
              title={t.name}
            />
          </div>
        </div>
        {!backendItems ? (
          <div className="rounded-lg border border-border/60 bg-window/40 p-6 text-center text-xs text-muted-foreground">
            <span>{connected ? `Scanning hardware capabilities and system state for ${t.name}…` : "Background service connection required to evaluate optimization targets."}</span>
          </div>
        ) : (
          <div className="grid grid-cols-2 gap-2">
            {vis.filter((i): i is TierItem => Boolean(i)).map((i) => {
              const isActionable = !i.done && !i.ineligible;
              const isOptimized = Boolean(i.done && !i.ineligible);
              const metaEntry = scanMeta[i.id] ?? scanMeta[i.name];
              const rawState = String(metaEntry?.state ?? i.eligibilityState ?? "").toUpperCase();
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
                      {isActionable ? (
                        sel.has(i.id) ? (
                          tierSel.mode === "CUSTOM" ? (
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
                {(scope === "ACTIONABLE" || scope === "ELIGIBLE") && eligibleItems.length === 0
                  ? `Everything is already optimized. No changes are necessary.${alreadyOptimizedItems.length > 0 ? ` (${alreadyOptimizedItems.length} verified optimal in Optimized view)` : ""}`
                  : scope === "OPTIMIZED"
                    ? "No verified already-optimal operations recorded yet for this profile."
                    : scope === "INELIGIBLE"
                      ? "No ineligible or unsupported operations for this hardware configuration."
                      : "No operations match the active search or view filter."}
              </p>
            )}
          </div>
        )}
      </Card>



      {t.toolbox === "bios" && (
        <Card>
          <SectionTitle
            title="MANUAL UEFI / FIRMWARE OPTIMIZATIONS"
            guide="tier.uefi"
            sub="These must be changed by you in firmware setup. Error Optimizer only guides and verifies."
            right={
              <Btn size="sm" variant="danger" onClick={() => setUefiConfirm(true)}>
                Reboot to UEFI firmware
              </Btn>
            }
          />
          <div className="space-y-2">
            {UEFI.map(([n, d]) => (
              <div key={n} className="rounded-lg border border-border bg-window p-3">
                <div className="text-[13px] font-semibold">{n}</div>
                <div className="text-[11.5px] text-muted-foreground">{d}</div>
              </div>
            ))}
          </div>
        </Card>
      )}

      <Card className="py-3">
        <div className="flex w-full flex-wrap items-center justify-between gap-2">
          <Label>ADVANCED LOGS & DIAGNOSTICS</Label>
          <span className="flex flex-wrap gap-1.5">
            <Btn
              size="sm"
              variant="ghost"
              onClick={async () => {
                try {
                  const res = (await sendHostRequest("settings:logs-copy", undefined, 15000)) as { text?: string; content?: string };
                  const text = res?.text ?? res?.content ?? `${t.id} · ${runtimeLog ?? "No logs yet"}`;
                  await navigator.clipboard?.writeText(text).catch(() => undefined);
                  toast.success("Diagnostic logs copied");
                } catch (err) {
                  toast.error("Could not copy logs", { description: err instanceof Error ? err.message : String(err) });
                }
              }}
            >
              Copy logs
            </Btn>
            <Btn size="sm" variant="ghost" onClick={() => setLogsCleared(true)}>
              Clear
            </Btn>
            <Btn size="sm" variant="ghost" onClick={() => setLogsOpen(!logsOpen)}>
              Expand / Collapse
            </Btn>
          </span>
        </div>
        {logsOpen && !logsCleared && (
          <pre className="mt-3 rounded-lg bg-background p-3 font-mono text-[11px] text-muted-foreground">
            {t.id} profile loaded · {t.entries} entries{"\n"}
            {runtimeLog ?? "Runtime applicability evaluated by background service"}
          </pre>
        )}
      </Card>

      <div className="sticky bottom-0 flex items-center justify-between border-t border-border">
        <span className="text-xs text-muted-foreground">
          {backendItems && eligibleItems.length === 0 ? (
            <span className="font-medium text-success">
              Everything is already optimized. No changes are necessary.
            </span>
          ) : (
            <>
              Transactional execution · Recovery support varies by operation.{" "}
              <b className="text-foreground">{backendItems ? sel.size : 0}</b> selected
            </>
          )}
        </span>
        <Btn
          variant="primary"
          disabled={!backendItems || scan.scanning || !sel.size || eligibleItems.length === 0}
          onClick={() => setRun(true)}
        >
          Apply {t.name}
        </Btn>
      </div>

      <Modal
        open={block}
        onClose={() => setBlock(false)}
        title="Customize bloatware blocklist"
        footer={
          <>
            <Btn variant="ghost" onClick={() => setBlock(false)}>
              Cancel
            </Btn>
            <Btn
              variant="primary"
              onClick={async () => {
                try {
                  const ids = Array.from(blockSel);
                  await sendHostRequest("tier:debloat-save-blocklist", { blockedIds: ids }, 15000);
                  setBlock(false);
                  toast.success("Debloat blocklist saved", { description: `${ids.length} packages protected/queued.` });
                } catch (err) {
                  toast.error("Save blocklist failed", { description: err instanceof Error ? err.message : String(err) });
                }
              }}
            >
              Save blocklist
            </Btn>
          </>
        }
      >
        <div className="mb-3 flex justify-end">
          <Btn
            size="sm"
            variant="ghost"
            onClick={() => setBlockSel(new Set(BLOCKLIST.slice(0, 7)))}
          >
            Reset defaults
          </Btn>
        </div>
        <div className="space-y-1.5">
          {BLOCKLIST.map((b) => (
            <div
              key={b}
              className="flex items-center gap-3 rounded-md border border-border bg-window px-3 py-2 font-mono text-xs"
            >
              <Check
                label={`Include ${b}`}
                on={blockSel.has(b)}
                onChange={() => setBlockSel((s) => toggleInSet(s, b))}
              />
              {b}
            </div>
          ))}
        </div>
        <p className="mt-3 text-[11px] text-muted-foreground">
          Selected packages will be processed during Custom Bloatware Removal.
        </p>
      </Modal>
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
          const meta = scanMeta[detail.id] ?? scanMeta[detail.name];
          const curState = detail.currentValue ?? meta?.currentValue
            ? `${detail.currentValue ?? meta?.currentValue} (${meta?.state ?? detail.eligibilityState ?? "VERIFIED"})`
            : detail.done
              ? (detail.isApplied ? "APPLIED AND VERIFIED" : "ALREADY OPTIMAL (Verified)")
              : meta?.state ?? (scan.ready ? "PENDING" : "Not scanned yet");
          const appNote = detail.reason ?? meta?.reason ?? (
            scan.ready
              ? `Evaluated (${(detail.targetValue ?? meta?.targetValue) ? `Target: ${detail.targetValue ?? meta?.targetValue}` : detail.scope})`
              : "Evaluated by the background service during scan"
          );
          return (
            <div className="space-y-3">
              <KV k="Category" v={detail.cat} />
              <KV k="Risk classification" v={<RiskBadge risk={detail?.risk} />} />
              <KV k="Verification state" v={curState} />
              {(detail.targetValue || meta?.targetValue) && <KV k="Desired optimal value" v={detail.targetValue || meta?.targetValue} mono />}
              <KV k="Applicability" v={appNote} />
              <KV k="Restart requirement" v={detail.cat === "SYSTEM" ? "Firmware reboot" : "No"} />
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
      <Modal
        open={uefiConfirm}
        onClose={() => setUefiConfirm(false)}
        title="Confirm UEFI Firmware Reboot"
        sub="Windows will restart immediately into your motherboard UEFI setup screen."
        footer={
          <>
            <Btn variant="ghost" onClick={() => setUefiConfirm(false)}>
              Cancel
            </Btn>
            <Btn
              variant="danger"
              onClick={async () => {
                setUefiConfirm(false);
                try {
                  await sendHostRequest("bios:reboot-uefi", { confirmed: true }, 15000);
                  toast.success("UEFI firmware reboot scheduled");
                } catch (err) {
                  toast.error("UEFI reboot request failed", { description: err instanceof Error ? err.message : String(err) });
                }
              }}
            >
              Confirm & Reboot to UEFI
            </Btn>
          </>
        }
      >
        <p className="text-xs text-muted-foreground">
          Save any open documents before continuing. Only confirm if you intend to restart directly into UEFI/BIOS settings now.
        </p>
      </Modal>
      <RunModal
        open={run}
        onClose={() => {
          setRun(false);
          scan.refresh();
        }}
        profile={t.id}
        count={sel.size}
        items={eligibleItems
          .filter((i): i is TierItem => Boolean(i && sel.has(i.id)))
          .map((i) => ({ id: i.id, title: i.name, risk: i?.risk || "LOW" }))}
        onComplete={() => {
          scan.refresh();
        }}
      />
    </div>
  );
}

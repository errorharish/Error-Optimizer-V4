// OneClickOptimizationView — dedicated full-page workflow.
import { useMemo, useState } from "react";
import { FilterChip, PremiumSelect } from "../dropdown";
import { useNavigate } from "@tanstack/react-router";
import { usePageScan } from "../usePageScan";
import { hasNativeBackend, isDesktopApp, useIsBackendConnected } from "@/lib/eo/nativeBridge";
import {
  ShieldCheck,
  ChevronDown,
  ScanSearch,
  CheckCircle2,
  SlidersHorizontal,
} from "lucide-react";
import {
  BackLink,
  PageHeader,
  Badge,
  Btn,
  SelectionBar,
  Card,
  Check,
  KV,
  Label,
  Modal,
  RiskBadge,
  SearchBox,
  EmptyState,
  Stepper,
  SelectMark,
} from "../kit";
import { RunModal } from "../RunModal";
import { ReviewSelectionModal } from "../ReviewSelectionModal";
import { useOptimizeWorkflow } from "../useOptimizeWorkflow";
import { ONE_CLICK_ACTIONS, ONE_CLICK_GROUPS } from "@/lib/eo/data";
import { filterRecommendations } from "@/lib/eo/recommendations";
import { cn } from "@/lib/utils";

const FILTERS = {
  STATUS: [
    "All Optimizations",
    "Recommended Only",
    "Optional",
    "Already Optimized",
    "Protected Items",
  ],
  CATEGORY: ["System Performance", "Background Workload", "Memory", "Power / Performance"],
  "ACTION REQUIREMENTS": ["Can Optimize Now", "Background-Safe", "Requires Restart"],
};
const FILTER_OPTIONS = Object.entries(FILTERS).flatMap(([group, opts]) =>
  opts.map((value) => ({ value, group: group === "ACTION REQUIREMENTS" ? "Action requirements" : group[0] + group.slice(1).toLowerCase() })),
);
const PROTECTED = [
  "Windows core binaries",
  "Display & audio drivers",
  "Active foreground application",
  "Anti-cheat & game launchers",
  "Security software",
  "User files & logins",
];
const STEPS = ["Analyze", "Review", "Confirm", "Result"];

export function OneClickPage() {
  const wf = useOptimizeWorkflow(1400);
  const { scan, sel, setSel, summary, selectedItems, lastRun, toggle } = wf;
  const navigate = useNavigate();
  const [query, setQuery] = useState("");
  const [area, setArea] = useState<string | null>(null);
  const [filter, setFilter] = useState("All Optimizations");
  const [review, setReview] = useState(false);
  const [detail, setDetail] = useState<string | null>(null);
  const [run, setRun] = useState(false);
  const [protOpen, setProtOpen] = useState(false);

  const visible = useMemo(() => filterRecommendations(area, filter, query), [area, filter, query]);
  const d = ONE_CLICK_ACTIONS.find((a) => a.id === detail);

  // Analyze is this page's scan when the background service is connected.
  const pageScan = usePageScan(
    "one-click",
    () =>
      new Promise<void>((resolve) => {
        setArea(null);
        wf.analyze(resolve);
      }),
  );
  const connected = useIsBackendConnected();

  const analyzed = wf.analyzed || pageScan.ready;
  const step = lastRun ? 4 : run ? 2 : review ? 2 : analyzed ? 1 : 0;
  const resetFilters = () => {
    setFilter("All Optimizations");
    setQuery("");
    setArea(null);
  };

  return (
    <div className="space-y-4 pb-20">
      <PageHeader
        back={<BackLink onClick={() => navigate({ to: "/" })} />}
        title="1-Click Optimization"
        badges={<Badge tone="cyan">GUIDED WORKFLOW</Badge>}
        sub="Analyze system state, review recommendations, and apply selected optimizations."
        scan={pageScan}
      />

      <Stepper steps={STEPS} current={step} label="1-Click workflow" />

      {/* Status strip: cols=Auto,12,*,Auto */}
      <Card className="flex items-center gap-4 py-3.5">
        <span
          className={cn(
            "flex h-9 w-9 items-center justify-center rounded-full",
            analyzed ? "bg-success/15 text-success" : "bg-cyan/15 text-cyan",
          )}
        >
          {analyzed ? (
            <ShieldCheck aria-hidden="true" className="h-5 w-5" />
          ) : (
            <ScanSearch aria-hidden="true" className="h-5 w-5" />
          )}
        </span>
        <div className="flex-1">
          <div className="text-sm font-bold">
            {scan.running
              ? "ANALYZING SYSTEM STATE…"
              : analyzed
                ? "RECOMMENDATIONS READY"
                : "NOT ANALYZED YET"}
          </div>
          <div className="text-[11.5px] text-muted-foreground">
            {analyzed
              ? `${ONE_CLICK_ACTIONS.length} recommended improvements across ${ONE_CLICK_GROUPS.length} areas · ${sel.size} selected`
              : scan.running
                ? "Scanning hardware and Windows configuration…"
                : "Connect the ERROR OPTIMIZER background service and press Refresh to scan for recommendations."}
          </div>
        </div>
        <Badge tone={analyzed ? "success" : "muted"}>{analyzed ? "ANALYZED" : "NOT SCANNED"}</Badge>
      </Card>

      {/* Optimization areas */}
      <Card>
        <div className="mb-3 flex items-start justify-between gap-4">
          <div>
            <Label as="h2" className="text-foreground">
              OPTIMIZATION AREAS
            </Label>
            <p className="mt-1 text-xs text-muted-foreground">
              Review recommendations by area. Select an area to focus the list.
            </p>
          </div>
          <Btn
            size="sm"
            variant="ghost"
            disabled={!area}
            onClick={() => setArea(null)}
            title="Show all 1-Click optimization checks across all workloads"
          >
            Show all checks
          </Btn>
        </div>
        <div className="grid grid-cols-4 gap-2.5">
          {ONE_CLICK_GROUPS.map((g) => {
            const inGroup = ONE_CLICK_ACTIONS.filter((a) => a.group === g.key);
            const picked = inGroup.filter((a) => sel.has(a.id)).length;
            return (
              <button
                type="button"
                key={g.key}
                aria-pressed={area === g.key}
                onClick={() => setArea(area === g.key ? null : g.key)}
                className={cn(
                  "rounded-xl border p-4 text-left transition-all",
                  area === g.key
                    ? "border-cyan/60 bg-cyan/10 eo-glow"
                    : "border-border bg-window hover:-translate-y-0.5 hover:border-border-strong",
                )}
              >
                <div className="eo-label text-foreground">{g.key}</div>
                <p className="mt-1 text-[11.5px] text-muted-foreground">{g.desc}</p>
                <div className="mt-3 flex items-center justify-between gap-2">
                  <Badge tone={analyzed ? "cyan" : "muted"}>
                    {analyzed ? `${inGroup.length} RECOMMENDED` : "SCAN REQUIRED"}
                  </Badge>
                  {analyzed && (
                    <span className="text-[10px] font-bold text-muted-foreground">
                      {picked}/{inGroup.length} SELECTED
                    </span>
                  )}
                </div>
              </button>
            );
          })}
        </div>
      </Card>

      {/* Recommended list */}
      <Card>
        <div className="mb-3 flex flex-wrap items-start justify-between gap-3">
          <div>
            <Label as="h2" className="text-foreground">
              RECOMMENDED OPTIMIZATIONS
            </Label>
            <p className="mt-1 text-xs text-muted-foreground">
              Choose the optimizations to include; risk and restart requirements stay visible.
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {filter !== "All Optimizations" && (
              <FilterChip onRemove={() => setFilter("All Optimizations")}>{filter}</FilterChip>
            )}
            <PremiumSelect
              size="sm"
              className="eo-dd-filter"
              label="Filter recommendations"
              icon={<SlidersHorizontal aria-hidden="true" className="eo-dd-icon" />}
              display={filter === "All Optimizations" ? "Filter" : filter}
              value={filter}
              onChange={setFilter}
              options={FILTER_OPTIONS}
            />
            {analyzed && (
              <SelectionBar
                count={sel.size}
                total={wf.moded.eligibleCount}
                onAll={wf.selectRecommended}
                onClear={() => setSel(new Set())}
                autoLabel="RECOMMENDED"
                moded={wf.moded}
              />
            )}
          </div>
        </div>
        <div className="mb-4">
          <SearchBox placeholder="Search recommendations…" value={query} onChange={setQuery} />
        </div>
        <div className="space-y-4" aria-live="polite">
          {!scan.running && !analyzed && (
            <EmptyState
              title="Analysis unavailable"
              sub="Connect the ERROR OPTIMIZER background service to analyze this PC and populate personalized recommendations."
            />
          )}
          {!scan.running && analyzed && !visible.length && (
            <EmptyState
              title="No matching recommendations"
              sub="No recommendations match this search or filter."
              action={<Btn onClick={resetFilters}>Reset filters</Btn>}
            />
          )}
          {!scan.running &&
            analyzed &&
            ONE_CLICK_GROUPS.filter((g) => visible.some((a) => a.group === g.key)).map((g) => (
              <section key={g.key} aria-label={g.key}>
                <h3 className="mb-2 eo-card-heading text-cyan">{g.key}</h3>
                <ul className="space-y-2">
                  {visible
                    .filter((a) => a.group === g.key)
                    .map((a) => {
                      const status = wf.getStatus(a.id, a.title);
                      const isDone = status === "AlreadyCompleted" || status === "Verified";
                      const needsAdmin = status === "RequiresAdmin";
                      const isSelected = sel.has(a.id);
                      const badgeTone = isDone ? "success" : needsAdmin ? "warning" : isSelected ? "cyan" : "muted";
                      const badgeLabel = isDone
                        ? "ALREADY OPTIMAL"
                        : needsAdmin
                          ? "REQUIRES ADMIN"
                          : isSelected
                            ? "SELECTED — READY"
                            : "UNSELECTED";
                      return (
                        <li
                          key={a.id}
                          onClick={() => toggle(a.id)}
                          className={cn(
                            "flex cursor-pointer items-center gap-4 rounded-xl border p-3.5 transition-colors",
                            isSelected
                              ? "border-cyan/40 bg-cyan/5"
                              : "border-border bg-window hover:border-border-strong",
                          )}
                        >
                          <SelectMark state={isSelected ? "on" : "off"} />
                          <div className="min-w-0 flex-1">
                            <div className="flex flex-wrap items-center gap-2 text-[13px] font-bold">
                              {a.title}
                              <RiskBadge risk={a?.risk || "LOW"} />
                              {a.restart && <Badge tone="warning">RESTART</Badge>}
                              <Badge tone={badgeTone} className="text-[9.5px] font-bold">
                                {badgeLabel}
                              </Badge>
                            </div>
                            <p className="text-[12px] text-muted-foreground">{a.desc}</p>
                            <p className="mt-0.5 text-[10.5px] text-success">
                              {wf.getStateLabel(a.id, a.title)} · {a.safety}
                            </p>
                          </div>
                          <span onClick={(e) => e.stopPropagation()}>
                            <Btn
                              size="sm"
                              variant="ghost"
                              onClick={() => setDetail(a.id)}
                              label={`Details for ${a.title}`}
                            >
                              Details
                            </Btn>
                          </span>
                        </li>
                      );
                    })}
                </ul>
              </section>
            ))}
        </div>
      </Card>

      {/* Protection banner (expandable) */}
      <Card className="border-success/30">
        <button
          type="button"
          aria-expanded={protOpen}
          aria-controls="eo-protected-list"
          onClick={() => setProtOpen(!protOpen)}
          className="flex w-full items-center gap-3 text-left"
        >
          <ShieldCheck aria-hidden="true" className="h-5 w-5 text-success" />
          <div className="flex-1">
            <div className="eo-label text-success">YOUR APPS & SYSTEM ARE PROTECTED</div>
            <div className="text-xs text-muted-foreground">
              Critical Windows components, active applications, drivers, and games are never treated
              as optimization targets — and never selected automatically.
            </div>
          </div>
          <ChevronDown
            aria-hidden="true"
            className={cn(
              "h-4 w-4 text-muted-foreground transition-transform",
              protOpen && "rotate-180",
            )}
          />
        </button>
        {protOpen && (
          <ul
            id="eo-protected-list"
            className="mt-3 grid grid-cols-2 gap-1.5 pl-8 text-xs text-muted-foreground eo-enter"
          >
            {PROTECTED.map((x) => (
              <li key={x} className="flex items-center gap-1.5">
                <CheckCircle2 aria-hidden="true" className="h-3.5 w-3.5 text-success" />
                {x}
              </li>
            ))}
          </ul>
        )}
      </Card>

      {/* Completion report */}
      {lastRun && (
        <Card className="eo-result">
          <div className="mb-3 flex flex-wrap items-start justify-between gap-3">
            <div>
              <Label as="h2" className="text-success">
                LAST RUN · {lastRun.at}
              </Label>
              <p className="mt-1 text-xs text-muted-foreground">
                Summary of the executed optimization pass.
              </p>
            </div>
            <Btn size="sm" variant="ghost" onClick={wf.dismissRun}>
              Dismiss
            </Btn>
          </div>
          <div className="grid grid-cols-4 gap-2">
            {(
              [
                ["SELECTED", lastRun.selected],
                ["OPERATIONS", lastRun.simulatedSteps],
                ["RESTART NOTED", lastRun.restartNoted],
                ["CHANGES APPLIED", lastRun.realChanges],
              ] as const
            ).map(([k, v]) => (
              <div key={k} className="rounded-lg border border-border bg-window p-3">
                <div className="text-xl font-bold tabular-nums">{v}</div>
                <div className="text-[9.5px] font-bold text-muted-foreground">{k}</div>
              </div>
            ))}
          </div>
          <p className="mt-3 text-[11.5px] text-muted-foreground">
            Included: {lastRun.titles.join(" · ")}
          </p>
        </Card>
      )}

      {/* Sticky action bar: cols=*,Auto */}
      <div className="eo-sticky sticky bottom-0 flex items-center justify-between border-t border-border">
        <span className="text-xs text-muted-foreground" aria-live="polite">
          <b className="text-foreground">{analyzed ? sel.size : 0}</b> selected
          {summary.restart ? ` · ${summary.restart} needs a restart` : ""} · Review risks and
          restart requirements before running optimizations.
        </span>
        <div className="flex gap-2">
          <Btn variant="ghost" disabled={!analyzed || !sel.size} onClick={() => setSel(new Set())}>
            Clear
          </Btn>
          <Btn disabled={!analyzed || !sel.size} onClick={() => setReview(true)}>
            Review
          </Btn>
          <Btn
            variant="primary"
            disabled={!analyzed || !sel.size || scan.running}
            onClick={() => setReview(true)}
          >
            Optimize selected
          </Btn>
        </div>
      </div>

      <ReviewSelectionModal
        open={review}
        onClose={() => setReview(false)}
        onConfirm={() => {
          setReview(false);
          setRun(true);
        }}
        items={selectedItems}
        summary={summary}
        onRemove={toggle}
      />

      <Modal
        open={!!d}
        onClose={() => setDetail(null)}
        title={d?.title ?? ""}
        sub="Optimization details"
        footer={
          d && (() => {
            const dStatus = wf.getStatus(d.id, d.title);
            const dDone = dStatus === "AlreadyCompleted" || dStatus === "Verified";
            const dAdmin = dStatus === "RequiresAdmin";
            const dSel = sel.has(d.id);
            const dTone = dDone ? "success" : dAdmin ? "warning" : dSel ? "cyan" : "muted";
            const dLabel = dDone ? "ALREADY OPTIMAL" : dAdmin ? "REQUIRES ADMIN" : dSel ? "SELECTED — READY" : "UNSELECTED";
            return (
              <div className="flex w-full items-center justify-between">
                <Badge tone={dTone} className="text-[10px] font-bold">
                  {dLabel}
                </Badge>
                <Btn variant="primary" onClick={() => setDetail(null)}>
                  Close
                </Btn>
              </div>
            );
          })()
        }
      >
        {d && (
          <div className="space-y-4">
            <p className="text-[12.5px] text-muted-foreground">{d.desc}</p>
            <div>
              <Label as="h3" className="mb-1 text-cyan">
                ANALYSIS & TARGET STATE
              </Label>
              <KV k="Why recommended:" v={d.why} />
              <KV k="Current state:" v={wf.getStateLabel(d.id, d.title)} />
              <KV k="Target configuration:" v="Optimized" />
            </div>
            <div>
              <Label as="h3" className="mb-1 text-cyan">
                SAFETY, VERIFICATION & RESTORE
              </Label>
              <KV k="Safety & Risk:" v={<RiskBadge risk={d?.risk || "LOW"} />} />
              <KV k="Safety notes:" v={d.safety} />
              <KV k="Restart requirement:" v={d.restart ? "Required" : "Not required"} />
              <KV k="Verification method:" v="Verified readback" />
              <KV k="Rollback capability:" v="Transactional rollback per operation" />
            </div>
          </div>
        )}
      </Modal>

      <RunModal
        open={run}
        onClose={() => {
          setRun(false);
          pageScan.refresh();
        }}
        profile="1-Click"
        count={sel.size}
        items={selectedItems}
        onComplete={(sum) => {
          wf.complete(sum);
          pageScan.refresh();
        }}
      />
    </div>
  );
}

// System Telemetry — System Health Check.
import { useMemo, useState, type CSSProperties } from "react";
import { toast } from "sonner";
import { Activity, AlertTriangle, HeartPulse, Info } from "lucide-react";
import { Badge, Btn, Card, Details, EmptyState, SectionTitle, notifyBackendRequired } from "./kit";
import { hasNativeBackend, useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { sendHostRequest } from "@/lib/eo/hostRpc";
import { parseReadings, usedPct } from "@/lib/eo/dashboardScan";
import { SCAN_FRESH_MS, getTelemetryState, updateSharedScanReadings } from "@/lib/eo/telemetryStore";
import { isLiveFresh } from "@/lib/eo/v4Telemetry";
import { OpProgress } from "./OpProgress";
import { AnimatedNumber, ScoreRing } from "./controls";
import {
  ATTENTION_BELOW,
  KIND_LABEL,
  MIN_COVERAGE,
  SAMPLE_HEALTH,
  evaluateHealth,
  metricScore,
  type HealthCategory,
} from "@/lib/eo/health";

const STAGES = [
  "Reading CPU readings…",
  "Reading memory readings…",
  "Reading GPU readings…",
  "Reading storage readings…",
  "Checking responsiveness…",
  "Calculating score…",
];

const EMPTY_HEALTH: HealthCategory[] = SAMPLE_HEALTH.map((c) => ({
  ...c,
  metrics: c.metrics.map((m) => ({
    ...m,
    value: null,
    missingReason: "Awaiting backend telemetry.",
  })),
}));

export function HealthCheck() {
  const connected = useIsBackendConnected();
  const [lastCheck, setLastCheck] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [progress, setProgress] = useState(0);
  const [categoriesSource, setCategoriesSource] = useState<HealthCategory[]>(EMPTY_HEALTH);
  const [findings, setFindings] = useState<Array<{ title: string; detail: string }>>([]);
  const run = useMemo(() => ({ running, progress }), [running, progress]);
  const result = useMemo(() => evaluateHealth(categoriesSource), [categoriesSource]);
  const has = connected && Boolean(lastCheck) && !run.running;
  const stage =
    STAGES[Math.min(STAGES.length - 1, Math.floor(((run.progress ?? 0) / 100) * STAGES.length))];
  const tone =
    result.status === "Excellent" ? "success" : result.status === "Good" ? "cyan" : "warning";

  const handleRun = async () => {
    if (!hasNativeBackend()) {
      notifyBackendRequired("System health check");
      return;
    }
    setRunning(true);
    setProgress(20);
    const timer = window.setInterval(() => setProgress((p) => Math.min(90, p + 12)), 250);
    try {
      const [scanRes, healthRes] = await Promise.allSettled([
        sendHostRequest<{ readings?: unknown }>("system:scan", undefined, 25000),
        sendHostRequest<Record<string, unknown>>("system:health", undefined, 25000),
      ]);

      const scanReply = scanRes.status === "fulfilled" ? scanRes.value : null;
      const healthSnap = healthRes.status === "fulfilled" ? healthRes.value : null;
      const scanErr = scanRes.status === "rejected"
        ? (scanRes.reason instanceof Error ? scanRes.reason.message : String(scanRes.reason))
        : null;
      const healthErr = healthRes.status === "rejected"
        ? (healthRes.reason instanceof Error ? healthRes.reason.message : String(healthRes.reason))
        : null;

      let rd: ReturnType<typeof parseReadings> = null;
      let scanRawSensors: Record<string, Record<string, unknown>> | null = null;
      if (scanReply) {
        const rawReadings = scanReply.readings !== undefined ? scanReply.readings : scanReply;
        rd = parseReadings(rawReadings);
        if (rawReadings && typeof rawReadings === "object") {
          const maybeSensors = (rawReadings as Record<string, unknown>)["sensors"];
          if (maybeSensors && typeof maybeSensors === "object") {
            scanRawSensors = maybeSensors as Record<string, Record<string, unknown>>;
          }
        }
        if (rd) {
          updateSharedScanReadings(
            rawReadings,
            scanRawSensors,
            (scanReply as Record<string, unknown>)["timestampUtc"],
          );
        }
      }

      // Reuse shared telemetry only when still fresh (stale snapshots must never look freshly measured).
      const nowMs = Date.now();
      const shared = getTelemetryState();
      const liveFresh = isLiveFresh(shared.lastAt, nowMs);
      const scanFresh = shared.scanCompletedAtMs !== null && nowMs - shared.scanCompletedAtMs <= SCAN_FRESH_MS;
      if (!rd && (liveFresh || scanFresh)) {
        rd = (liveFresh ? (shared.effectiveScanResult ?? shared.scanResult) : shared.scanResult)?.readings ?? null;
      }

      const freshLive = liveFresh ? shared.live : null;
      const freshExtras = liveFresh ? shared.liveExtras : null;
      const freshThermal = liveFresh ? shared.thermal : null;
      const cpuLoad = rd?.cpu.loadPct ?? freshLive?.cpuPct ?? null;
      const cpuTemp = rd?.cpu.tempC ?? freshExtras?.cpuTempC ?? freshThermal?.cpu ?? null;
      const ramUsed = rd?.ram.usedGb ?? freshLive?.ramUsedGb ?? null;
      const ramTotal = rd?.ram.totalGb ?? freshLive?.ramTotalGb ?? null;
      let ramPct = usedPct(ramUsed, ramTotal);
      const commitPct = rd?.ram.commitPct ?? freshExtras?.ramCommitPct ?? null;
      const gpuLoad = rd?.gpu.loadPct ?? freshLive?.gpuPct ?? null;
      const gpuTemp = rd?.gpu.tempC ?? freshExtras?.gpuTempC ?? freshThermal?.gpu ?? null;
      let diskPct = rd ? usedPct(rd.storage.usedGb, rd.storage.totalGb) : null;
      const ssdTemp = rd?.storage.tempC ?? freshExtras?.ssdTempC ?? freshThermal?.ssd ?? null;
      const diskHealthPct = rd?.storage.healthPct ?? null;
      const dpcUs = rd?.latency.dpcUs ?? freshExtras?.dpcUs ?? null;

      // Extract authoritative measured category values from `system:health` if `rd` lacked them.
      const backendCats = Array.isArray(healthSnap?.["categories"])
        ? (healthSnap["categories"] as Array<Record<string, unknown>>)
        : [];
      for (const c of backendCats) {
        const isMeas = c["isMeasured"] ?? c["IsMeasured"];
        if (isMeas === false) continue;
        const catKey = String(c["category"] ?? c["Category"] ?? c["name"] ?? "").toLowerCase();
        const measStr = String(c["measuredValue"] ?? c["MeasuredValue"] ?? "");
        const evStr = String(c["evidence"] ?? c["Evidence"] ?? "");
        if (ramPct === null && catKey.includes("mem")) {
          const m = measStr.match(/(\d+(?:\.\d+)?)\s*%/);
          if (m?.[1]) ramPct = Number(m[1]);
        }
        if (diskPct === null && catKey.includes("stor")) {
          const mFreePct =
            `${measStr} ${evStr}`.match(/(\d+(?:\.\d+)?)\s*%\s*(?:available|free)/i) ??
            measStr.match(/\((\d+(?:\.\d+)?)\s*%\)/);
          if (mFreePct?.[1]) diskPct = Math.max(0, Math.min(100, Math.round((100 - Number(mFreePct[1])) * 10) / 10));
        }
      }

      const describeMissing = (
        sensorKey: "cpuTemp" | "gpuTemp" | "ssdTemp" | "dpcLatency",
        storeKey: "cpu" | "gpu" | "ssd" | null,
        metricKey: string,
        fallbackMsg: string,
      ): string => {
        const rawMeta = scanRawSensors?.[sensorKey];
        const storeMeta = storeKey ? shared.sensorMeta[storeKey] : undefined;
        const avail = String(rawMeta?.["availability"] ?? storeMeta?.availability ?? "").toLowerCase();
        const reason = String(rawMeta?.["reason"] ?? storeMeta?.reason ?? "").trim();
        const tsIso = String(
          rawMeta?.["timestampUtc"] ??
          storeMeta?.timestampUtc ??
          shared.metricTimestamps[metricKey]?.timestampUtc ??
          "",
        ).trim();
        const statusPrefix =
          avail === "unsupported"
            ? "Unsupported by hardware/driver"
            : avail === "permission_denied"
              ? "Requires administrator privileges"
              : avail === "error" || avail === "unavailable"
                ? "Collection unavailable"
                : "";
        const base = reason || fallbackMsg;
        const label = statusPrefix ? `${statusPrefix}: ${base}` : base;
        return tsIso ? `${label} (checked ${new Date(tsIso).toLocaleTimeString()})` : label;
      };

      const anyMeasured =
        cpuLoad !== null ||
        cpuTemp !== null ||
        ramPct !== null ||
        commitPct !== null ||
        gpuLoad !== null ||
        gpuTemp !== null ||
        diskPct !== null ||
        ssdTemp !== null ||
        diskHealthPct !== null ||
        dpcUs !== null;

      setCategoriesSource(
        EMPTY_HEALTH.map((cat) => ({
          ...cat,
          metrics: cat.metrics.map((m) => {
            if (!anyMeasured) {
              return {
                ...m,
                missingReason: scanErr ?? healthErr ?? "No fresh backend telemetry available.",
              };
            }
            if (m.key === "cpu-load") return { ...m, value: cpuLoad, ...(cpuLoad === null ? { missingReason: "Collection unavailable: CPU utilization counter did not report" } : {}) };
            if (m.key === "cpu-temp") return { ...m, value: cpuTemp, ...(cpuTemp === null ? { missingReason: describeMissing("cpuTemp", "cpu", "cpu-temp", "Hardware ACPI/WMI thermal sensor not exposed on this CPU/board") } : {}) };
            if (m.key === "mem-used") return { ...m, value: ramPct, ...(ramPct === null ? { missingReason: "Collection unavailable: Physical memory counter did not report" } : {}) };
            if (m.key === "mem-commit") {
              return { ...m, value: commitPct, ...(commitPct === null ? { missingReason: "Collection unavailable: Windows commit charge telemetry did not report" } : {}) };
            }
            if (m.key === "gpu-load") return { ...m, value: gpuLoad, ...(gpuLoad === null ? { missingReason: "Collection unavailable: GPU utilization counter not exposed" } : {}) };
            if (m.key === "gpu-temp") return { ...m, value: gpuTemp, ...(gpuTemp === null ? { missingReason: describeMissing("gpuTemp", "gpu", "gpu-temp", "GPU thermal sensor requires supported NVML/WDDM driver") } : {}) };
            if (m.key === "disk-used") return { ...m, value: diskPct, ...(diskPct === null ? { missingReason: "Collection unavailable: System drive capacity query failed" } : {}) };
            if (m.key === "disk-temp") {
              return { ...m, value: ssdTemp, ...(ssdTemp === null ? { missingReason: describeMissing("ssdTemp", "ssd", "disk-temp", "Storage temperature sensor not exposed by controller/drive") } : {}) };
            }
            if (m.key === "disk-health") {
              return {
                ...m,
                value: diskHealthPct,
                ...(diskHealthPct === null ? { missingReason: "Unsupported by controller: SMART / NVMe wear telemetry unavailable" } : {}),
              };
            }
            if (m.key === "dpc") {
              return {
                ...m,
                value: dpcUs,
                ...(dpcUs === null ? { missingReason: describeMissing("dpcLatency", null, "dpc", "Kernel ETW DPC trace requires Administrator privilege to sample") } : {}),
              };
            }
            return m;
          }),
        })),
      );

      const collected: Array<{ title: string; detail: string }> = [];
      if (scanErr) {
        collected.push({
          title: "[PARTIAL RPC FAILURE] System hardware scan (system:scan)",
          detail: `${scanErr}${rd ? " — used synchronized live/recent measurements for available metrics." : ""}`,
        });
      }
      if (healthErr) {
        collected.push({
          title: "[PARTIAL RPC FAILURE] Windows health evaluator (system:health)",
          detail: healthErr,
        });
      }

      if (healthSnap) {
        const checks = Array.isArray(healthSnap["checks"]) ? (healthSnap["checks"] as Array<Record<string, unknown>>) : null;
        if (checks && checks.length > 0) {
          for (const ch of checks) {
            const name = String(ch["name"] ?? ch["id"] ?? "Health Check");
            const st = String(ch["status"] ?? "Unknown").toUpperCase();
            const measured = typeof ch["measuredValue"] === "string" && ch["measuredValue"] ? ch["measuredValue"] : undefined;
            const threshold = typeof ch["referenceThreshold"] === "string" && ch["referenceThreshold"] ? ` (Target: ${ch["referenceThreshold"]})` : "";
            const err = typeof ch["errorDetail"] === "string" && ch["errorDetail"] ? ` Error: ${ch["errorDetail"]}` : "";
            const exp = String(ch["explanation"] ?? "");
            collected.push({
              title: `[${st}] ${name}`,
              detail: [measured ? `Measured: ${measured}${threshold}` : "", exp, err].filter(Boolean).join(" — "),
            });
          }
        }
        if (Array.isArray(healthSnap["categories"])) {
          for (const c of healthSnap["categories"] as Array<Record<string, unknown>>) {
            if (!checks || checks.length === 0) {
              const name = String(c["name"] ?? "Category");
              const st = String(c["status"] ?? c["healthLabel"] ?? "Evaluated").toUpperCase();
              const err = typeof c["errorDetail"] === "string" && c["errorDetail"] ? ` (${c["errorDetail"]})` : "";
              collected.push({
                title: `[${st}] ${name}`,
                detail: `${String(c["evidence"] ?? "")}${err}`,
              });
            }
            if (Array.isArray(c["recommendations"])) {
              for (const r of c["recommendations"] as Array<Record<string, unknown>>) {
                collected.push({
                  title: `Recommendation: ${String(r["title"] ?? "Diagnostic finding")}`,
                  detail: String(r["why"] ?? r["evidence"] ?? ""),
                });
              }
            }
          }
        }
      }

      setFindings(collected);

      if (!anyMeasured && !healthSnap) {
        toast.error("System health scan returned no data from backend", {
          description: scanErr ?? healthErr ?? "No sensors or diagnostics returned.",
        });
        return;
      }

      if (healthErr && anyMeasured) {
        toast.warning("Health evaluator unavailable", {
          description: `Scored telemetry metrics only; Windows health evaluator reported: ${healthErr}`,
        });
      } else if (scanErr && anyMeasured) {
        toast.warning("Direct scan timed out", {
          description: "Evaluated using synchronized backend telemetry and Windows health checks.",
        });
      }

      const checkTs =
        typeof (scanReply as Record<string, unknown> | null)?.["timestampUtc"] === "string"
          ? new Date(String((scanReply as Record<string, unknown>)["timestampUtc"])).toLocaleString()
          : shared.liveExtras.timestampUtc
            ? new Date(shared.liveExtras.timestampUtc).toLocaleString()
            : new Date().toLocaleString();
      setProgress(100);
      setLastCheck(checkTs);
    } finally {
      window.clearInterval(timer);
      setRunning(false);
    }
  };

  return (
    <Card className="eo-health">
      <SectionTitle
        title="SYSTEM HEALTH CHECK"
        guide="telemetry.health"
        badge={<Badge tone="muted">{has ? result.status.toUpperCase() : "NOT SCANNED"}</Badge>}
        sub="Evaluates hardware, thermal, and system responsiveness telemetry"
        right={
          <Btn
            variant="primary"
            size="sm"
            loading={run.running}
            onClick={handleRun}
          >
            <HeartPulse aria-hidden="true" className="h-3.5 w-3.5" />
            {lastCheck ? "Run again" : "Run health check"}
          </Btn>
        }
      />

      <div className="eo-health-grid">
        <div className="eo-health-score">
          <ScoreRing
            size={132}
            stroke={10}
            value={has && result.overall !== null ? result.overall : 0}
            label={
              has && result.overall !== null
                ? `System health ${result.overall} out of 100, ${result.status}.`
                : "System health 0 out of 100, not checked yet"
            }
            valueClassName="text-[34px] leading-none font-black"
            unitClassName="mt-0.5 text-[10px] font-bold text-subtle"
          />
          {has ? (
            <>
              <Badge tone={tone} className="mt-2">
                {result.status}
              </Badge>
              <dl className="eo-health-meta">
                <div>
                  <dt>Last check</dt>
                  <dd>{lastCheck}</dd>
                </div>
                <div>
                  <dt>Data coverage</dt>
                  <dd>{Math.round(result.coverage * 100)}% of weight measured</dd>
                </div>
              </dl>
            </>
          ) : (
            <p className="mt-2 text-center text-[11px] text-muted-foreground">
              {run.running ? "Analysing…" : "Awaiting backend connection to calculate health score."}
            </p>
          )}
        </div>

        <div className="min-w-0">
          {run.running ? (
            <OpProgress
              value={run.progress ?? 0}
              title={stage ?? "Analysing…"}
              detail="Collecting hardware and responsiveness diagnostics"
              active
            />
          ) : (
            <ul className="eo-health-cats" aria-label="Category scores">
              {result.categories.map((c, i) => {
                const cat = categoriesSource.find((x) => x.key === c.key);
                const kinds = [...new Set(cat?.metrics.map((m) => KIND_LABEL[m.kind]))];
                const show = has && c.score !== null;
                return (
                  <li key={c.key} data-missing={has && c.score === null ? "true" : undefined}>
                    <span className="min-w-0">
                      <span className="block truncate text-[12px] font-bold">{c.label}</span>
                      <span className="block truncate text-[10.5px] text-subtle">
                        {kinds.join(" · ")} · weight {Math.round(c.weight * 100)}%
                      </span>
                    </span>
                    <span
                      className="eo-score-bar"
                      style={{ "--v": `${show ? c.score : 0}%`, "--i": i } as CSSProperties}
                    />
                    <span className="text-right text-[12px] font-bold tabular-nums">
                      {!has ? "—" : c.score === null ? "N/A" : <AnimatedNumber value={c.score} duration={850} />}
                    </span>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      </div>

      {has && (
        <div className="mt-3">
          <div className="eo-label mb-2 flex items-center gap-1.5 text-[10px] text-foreground">
            <AlertTriangle aria-hidden="true" className="h-3.5 w-3.5 text-warning" />
            ATTENTION AREAS · {result.attention.length}
          </div>
          {result.attention.length ? (
            <ul className="eo-health-attn">
              {result.attention.map((a) => (
                <li key={a.metric} data-missing={a.score === null ? "true" : undefined}>
                  <span className="flex items-center justify-between gap-2">
                    <span className="truncate text-[12px] font-bold">{a.finding}</span>
                    <Badge tone={a.score === null ? "muted" : "warning"}>
                      {a.score === null ? "NOT MEASURED" : `${a.score} / 100`}
                    </Badge>
                  </span>
                  <span className="mt-0.5 block text-[10.5px] text-subtle">
                    {a.category} · {KIND_LABEL[a.kind]}
                  </span>
                  <span className="mt-1 block text-[11.5px] text-muted-foreground">{a.advice}</span>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-[12px] text-muted-foreground">No areas need attention.</p>
          )}
          <p className="mt-2 flex items-center gap-1.5 text-[10.5px] text-subtle">
            <Info aria-hidden="true" className="h-3 w-3" />
            Recommendations are information only — nothing runs automatically. High load is resource
            use, not a hardware fault.
          </p>
        </div>
      )}

      <Details inline title="How the score is calculated" hint="Inputs, weights and missing data">
        <ol className="eo-rp-steps mb-3">
          <li>
            Each reading gets 0–100: load ≤ 60% = 100, then −1.75 per %; temperatures score 100 up
            to their warning point and fall to 40 at the critical point; drive use ≤ 75% = 100;
            drive health is used as reported.
          </li>
          <li>A category takes its worst reading, so one hot drive is not hidden by good ones.</li>
          <li>
            The overall score is the weighted average of measured categories. Missing readings are
            left out and shown as coverage — never counted as perfect.
          </li>
          <li>
            Below {Math.round(MIN_COVERAGE * 100)}% coverage there is no score. Readings under{" "}
            {ATTENTION_BELOW} are listed as attention areas.
          </li>
          <li>85+ Excellent · 70–84 Good · below 70 Needs Attention.</li>
        </ol>
        <table className="eo-health-table">
          <thead>
            <tr>
              <th>Reading</th>
              <th>Type</th>
              <th>Value</th>
              <th>Score</th>
            </tr>
          </thead>
          <tbody>
            {categoriesSource.flatMap((c) =>
              c.metrics.map((m) => {
                const s = metricScore(m);
                return (
                  <tr key={m.key}>
                    <td>
                      {c.label} · {m.label}
                    </td>
                    <td>{KIND_LABEL[m.kind]}</td>
                    <td>{m.value === null ? "Unavailable" : `${m.value}${m.unit}`}</td>
                    <td className="tabular-nums">{s === null ? "—" : s}</td>
                  </tr>
                );
              }),
            )}
          </tbody>
        </table>
      </Details>
      <Details inline title="Diagnostic findings" hint={has ? `${findings.length} findings` : "Requires system scan"}>
        {has && findings.length ? (
          <ul className="space-y-1.5 text-xs">
            {findings.map((f, idx) => (
              <li key={idx} className="rounded-lg border border-border bg-window p-2.5">
                <div className="font-semibold text-foreground">{f.title}</div>
                {f.detail && <div className="mt-0.5 text-muted-foreground">{f.detail}</div>}
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState
            title={has ? "No issues detected" : "No diagnostic findings yet"}
            sub={
              has
                ? "System health diagnostics reported no active warnings."
                : "Connect the ERROR OPTIMIZER background service to run hardware and security integrity checks."
            }
          />
        )}
      </Details>
    </Card>
  );
}

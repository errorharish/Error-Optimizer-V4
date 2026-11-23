// System Performance Telemetry console.
// Displays live hardware telemetry when connected to the Windows service, and clean
// unavailable / waiting states when the backend is disconnected.
import { memo, useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent } from "react";
import { Cpu, MemoryStick, MonitorCog, Pause, Play, RefreshCw, type LucideIcon } from "lucide-react";
import { Badge, Bar, Btn, Card, EmptyState, Label, SectionTitle } from "./kit";
import { AnimatedNumber, Segmented } from "./controls";
import { useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { useSharedTelemetryState } from "@/lib/eo/telemetryStore";
import { cn } from "@/lib/utils";

export type Sample = { cpu: number | null; ram: number | null; gpu: number | null };
type Key = keyof Sample;

const SERIES: { key: Key; label: string; color: string; text: string; hw: string; icon: LucideIcon }[] = [
  { key: "cpu", label: "CPU", color: "var(--chart-1)", text: "text-chart-1", hw: "Processor telemetry", icon: Cpu },
  { key: "ram", label: "RAM", color: "var(--chart-2)", text: "text-chart-2", hw: "System memory telemetry", icon: MemoryStick },
  { key: "gpu", label: "GPU", color: "var(--chart-3)", text: "text-chart-3", hw: "Graphics telemetry", icon: MonitorCog },
];
const WINDOWS = ["30 SEC", "1 MIN", "5 MIN"];
const WINDOW_SECONDS: Record<string, number> = { "30 SEC": 30, "1 MIN": 60, "5 MIN": 300 };

/** Subscribes to shared live Windows host telemetry when connected; emits empty array otherwise. */
export function useSampleSeries(n = 30, paused = false): Sample[] {
  const backendConnected = useIsBackendConnected();
  const snap = useSharedTelemetryState({ subscribe: !paused });
  const frozenRef = useRef<Sample[]>([]);

  const slice = useMemo(() => {
    if (!backendConnected) return [];
    return snap.sampleSeries.slice(-Math.max(1, n));
  }, [backendConnected, snap.sampleSeries, n]);

  useEffect(() => {
    if (!paused) {
      frozenRef.current = slice;
    }
  }, [paused, slice]);

  if (!backendConnected) return [];
  return paused ? frozenRef.current : slice;
}

const windowed = (s: Sample[], win: string) =>
  win === "30 SEC"
    ? s
    : win === "1 MIN"
      ? s.filter((_, i) => i % 2 === 0)
      : s.filter((_, i) => i % 3 === 0);

/** Smooth curve: horizontal-tangent cubic segments (no overshoot above 100% / below 0%). */
function curve(rawVals: (number | null)[], w: number, h: number) {
  const vals = rawVals.map((v) => v ?? 0);
  if (!vals.length) return `M0,${h} L${w},${h}`;
  const step = w / Math.max(1, vals.length - 1);
  const pt = (v: number, i: number): [number, number] => [
    i * step,
    h - (Math.min(100, Math.max(0, v)) / 100) * h,
  ];
  return vals
    .map((v, i) => {
      const [x, y] = pt(v, i);
      if (!i) return `M${x.toFixed(1)},${y.toFixed(1)}`;
      const [px, py] = pt(vals[i - 1] ?? v, i - 1);
      const mx = (px + x) / 2;
      return `C${mx.toFixed(1)},${py.toFixed(1)} ${mx.toFixed(1)},${y.toFixed(1)} ${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");
}
const areaPath = (vals: (number | null)[], w: number, h: number) =>
  `${curve(vals, w, h)} L${w},${h} L0,${h} Z`;
/** d as a CSS property lets the browser interpolate between ticks (same command count). */
const morph = (d: string) => ({ d: `path("${d}")` }) as CSSProperties;

const stats = (rawVals: (number | null)[]) => {
  const vals = rawVals.filter((v): v is number => v !== null && Number.isFinite(v));
  if (!vals.length) return { avg: null, peak: null, low: null };
  const avg = vals.reduce((a, b) => a + b, 0) / vals.length;
  return { avg, peak: Math.max(...vals), low: Math.min(...vals) };
};

export function TelemetryConsole() {
  const backendConnected = useIsBackendConnected();
  const [paused, setPaused] = useState(false);
  const [win, setWin] = useState("30 SEC");
  const [shown, setShown] = useState<Set<Key>>(() => new Set<Key>(["cpu", "ram", "gpu"]));
  const [redraw, setRedraw] = useState(0);
  const raw = useSampleSeries(30, paused);
  const series = useMemo(() => windowed(raw, win), [raw, win]);
  const last = raw.at(-1) ?? null;
  const connected = backendConnected && raw.length > 0;

  const toggleSeries = (k: Key) =>
    setShown((s) => {
      const n = new Set(s);
      if (n.has(k)) n.delete(k);
      else n.add(k);
      return n;
    });

  return (
    <section aria-labelledby="eo-telemetry-title" className="space-y-3">
      <Card className="eo-hero">
        <SectionTitle
          id="eo-telemetry-title"
          title="SYSTEM PERFORMANCE TELEMETRY"
          badge={<Badge tone={connected ? "success" : "warning"}>{connected ? "LIVE FEED" : "NO TELEMETRY"}</Badge>}
          sub="CPU, memory and graphics utilization reported by the background service"
          right={
            <>
              <Btn
                size="sm"
                variant="ghost"
                title={paused ? "Resume telemetry updates" : "Pause telemetry updates"}
                pressed={paused}
                onClick={() => setPaused(!paused)}
              >
                {paused ? (
                  <Play aria-hidden="true" className="h-3 w-3" />
                ) : (
                  <Pause aria-hidden="true" className="h-3 w-3" />
                )}
                {paused ? "Resume" : "Pause"}
              </Btn>
              <Btn
                size="sm"
                variant="ghost"
                title="Refresh telemetry view"
                onClick={() => setRedraw((k) => k + 1)}
              >
                <RefreshCw aria-hidden="true" className="h-3 w-3" />
                Refresh
              </Btn>
              <Segmented label="Telemetry window" options={WINDOWS} value={win} onChange={setWin} />
            </>
          }
        />
        <div className="mb-3 flex flex-wrap items-center gap-2">
          {SERIES.map((s) => (
            <button
              key={s.key}
              type="button"
              aria-pressed={shown.has(s.key)}
              onClick={() => toggleSeries(s.key)}
              className="eo-legend"
              style={{ "--series": s.color } as CSSProperties}
              title={`${shown.has(s.key) ? "Hide" : "Show"} ${s.label} series`}
            >
              <span aria-hidden="true" className="eo-legend-dot" />
              {s.label}
              <b className="tabular-nums">
                {last && last[s.key] !== null ? (
                  <AnimatedNumber value={last[s.key]!} decimals={0} suffix="%" duration={650} />
                ) : (
                  "—"
                )}
              </b>
            </button>
          ))}
          <span className="ml-auto flex items-center gap-1.5 text-[10px] font-bold text-subtle">
            <span
              aria-hidden="true"
              className={cn(
                "h-1.5 w-1.5 rounded-full",
                connected && !paused ? "eo-pulse-dot bg-success" : "bg-warning",
              )}
            />
            {!connected ? "WAITING FOR SYSTEM DATA" : paused ? "FEED PAUSED" : "LIVE FEED"}
          </span>
        </div>
        {!connected ? (
          <EmptyState
            title="Telemetry data unavailable"
            sub="Waiting for system data from the ERROR OPTIMIZER background service."
          />
        ) : !shown.size ? (
          <EmptyState
            title="No series selected"
            sub="Choose CPU, RAM or GPU above to plot telemetry data."
            action={
              <Btn size="sm" onClick={() => setShown(new Set<Key>(["cpu", "ram", "gpu"]))}>
                Show all series
              </Btn>
            }
          />
        ) : (
          <TelemetryChart key={redraw} series={series} shown={shown} win={win} />
        )}
        <div className="mt-3 flex flex-wrap justify-between gap-2 text-[9.5px] font-bold text-subtle">
          <span>{connected ? "HARDWARE PERFORMANCE TELEMETRY" : "BACKEND CONNECTION REQUIRED FOR LIVE TELEMETRY"}</span>
          <span>
            WINDOW: {win === "30 SEC" ? "30 SECONDS" : win} · {series.length} SAMPLES
          </span>
        </div>
      </Card>

      <div className="grid grid-cols-3 gap-3">
        {SERIES.map((s) => (
          <MetricCard
            key={`${s.key}-${redraw}`}
            label={`${s.label} UTILIZATION`}
            hw={s.hw}
            color={s.color}
            text={s.text}
            vals={series.map((v) => v[s.key])}
            ready={connected}
            icon={s.icon}
          />
        ))}
      </div>
    </section>
  );
}

const TelemetryChart = memo(function TelemetryChart({
  series,
  shown,
  win,
}: {
  series: Sample[];
  shown: Set<Key>;
  win: string;
}) {
  const [hover, setHover] = useState<number | null>(null);
  const n = series.length;
  const W = 600,
    H = 176;
  const idx = hover === null ? null : Math.min(n - 1, hover);
  const point = idx === null ? null : series[idx];
  const secs = WINDOW_SECONDS[win] ?? 30;
  const ago = idx === null ? 0 : Math.round(((n - 1 - idx) / Math.max(1, n - 1)) * secs);
  const visible = SERIES.filter((s) => shown.has(s.key));

  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === "ArrowLeft" || e.key === "ArrowRight") {
      e.preventDefault();
      const d = e.key === "ArrowLeft" ? -1 : 1;
      setHover((h) => Math.min(n - 1, Math.max(0, (h ?? n - 1) + d)));
    }
    if (e.key === "Escape") setHover(null);
  };

  return (
    <div className="grid" style={{ gridTemplateColumns: "34px minmax(0,1fr)" }}>
      <div className="flex h-52 flex-col justify-between pb-0.5 text-[9.5px] text-subtle">
        {["100%", "75%", "50%", "25%", "0%"].map((x) => (
          <span key={x}>{x}</span>
        ))}
      </div>
      <div>
        <div
          role="img"
          tabIndex={0}
          aria-label={`Utilization chart. Latest: ${visible
            .map((s) => `${s.label} ${(series.at(-1)?.[s.key] ?? 0).toFixed(0)}%`)
            .join(", ")}. Use left and right arrows to inspect readings.`}
          className="eo-tele-plot relative h-52"
          onPointerMove={(e) => {
            const box = e.currentTarget.getBoundingClientRect();
            const x = (e.clientX - box.left) / box.width;
            setHover(Math.round(Math.min(1, Math.max(0, x)) * (n - 1)));
          }}
          onPointerLeave={() => setHover(null)}
          onBlur={() => setHover(null)}
          onKeyDown={onKey}
        >
          <svg
            aria-hidden="true"
            viewBox={`0 0 ${W} ${H}`}
            className="eo-chart absolute inset-0 h-full w-full overflow-visible"
            preserveAspectRatio="none"
          >
            <defs>
              {visible.map((s) => (
                <linearGradient key={s.key} id={`eo-tele-${s.key}`} x1="0" x2="0" y1="0" y2="1">
                  <stop offset="0%" stopColor={s.color} stopOpacity="0.9" />
                  <stop offset="100%" stopColor={s.color} stopOpacity="0" />
                </linearGradient>
              ))}
            </defs>
            {[0, 44, 88, 132, 176].map((y) => (
              <line
                key={y}
                x1="0"
                x2={W}
                y1={y}
                y2={y}
                stroke="var(--border)"
                strokeDasharray="3 4"
                vectorEffect="non-scaling-stroke"
              />
            ))}
            {visible.map((s) => {
              const d = areaPath(
                series.map((v) => v[s.key]),
                W,
                H,
              );
              return (
                <path
                  key={`a-${s.key}`}
                  className="eo-chart-area eo-morph"
                  d={d}
                  style={morph(d)}
                  fill={`url(#eo-tele-${s.key})`}
                />
              );
            })}
            {visible.map((s) => {
              const d = curve(
                series.map((v) => v[s.key]),
                W,
                H,
              );
              return (
                <path
                  key={`l-${s.key}`}
                  className="eo-morph"
                  d={d}
                  style={morph(d)}
                  fill="none"
                  stroke={s.color}
                  strokeWidth="2"
                  vectorEffect="non-scaling-stroke"
                />
              );
            })}
          </svg>
          {point && idx !== null && (
            <>
              <span
                aria-hidden="true"
                className="eo-tele-cross"
                style={{ left: `${(idx / Math.max(1, n - 1)) * 100}%` }}
              />
              {visible.map((s) =>
                point[s.key] === null ? null : (
                  <span
                    key={s.key}
                    aria-hidden="true"
                    className="eo-tele-dot"
                    style={
                      {
                        left: `${(idx / Math.max(1, n - 1)) * 100}%`,
                        top: `${100 - point[s.key]!}%`,
                        "--series": s.color,
                      } as CSSProperties
                    }
                  />
                ),
              )}
              <div
                role="tooltip"
                className="eo-tele-tip"
                data-side={idx > n * 0.62 ? "left" : "right"}
                style={{ left: `${(idx / Math.max(1, n - 1)) * 100}%` }}
              >
                <div className="mb-1 flex items-center justify-between gap-4 text-[9.5px] font-bold text-subtle">
                  <span>{ago ? `−${ago}s` : "NOW"}</span>
                  <span>READING</span>
                </div>
                {visible.map((s) => (
                  <div
                    key={s.key}
                    className="flex items-center justify-between gap-5 text-[11.5px]"
                  >
                    <span className="flex items-center gap-1.5">
                      <span
                        aria-hidden="true"
                        className="eo-legend-dot"
                        style={{ "--series": s.color } as CSSProperties}
                      />
                      {s.label}
                    </span>
                    <b className="tabular-nums">{point[s.key] === null ? "—" : `${point[s.key]!.toFixed(1)}%`}</b>
                  </div>
                ))}
              </div>
            </>
          )}
        </div>
        <div className="mt-1 flex justify-between text-[9.5px] text-subtle">
          {[
            win === "30 SEC" ? "−30s" : win === "1 MIN" ? "−60s" : "−5m",
            "",
            "",
            "",
            "",
            "",
            "NOW",
          ].map((x, i) => (
            <span key={i}>{x}</span>
          ))}
        </div>
      </div>
    </div>
  );
});

function MetricCard({
  label,
  hw,
  color,
  text,
  vals,
  ready,
  icon: Icon,
}: {
  label: string;
  hw: string;
  color: string;
  text: string;
  vals: (number | null)[];
  ready: boolean;
  icon?: LucideIcon;
}) {
  const validVals = vals.filter((v): v is number => v !== null && Number.isFinite(v));
  const cur = vals.at(-1) ?? null;
  const s = stats(vals);
  const W = 200,
    H = 52;
  const line = curve(vals, W, H);
  const fill = areaPath(vals, W, H);
  const gid = `eo-spark-${label.split(" ")[0]?.toLowerCase()}`;
  return (
    <Card className="p-3.5 eo-stat transition-all duration-200 hover:border-primary/50">
      <div className="flex items-center justify-between gap-2">
        <span className="flex items-center gap-2">
          {Icon && <Icon className="h-4 w-4 text-cyan" />}
          <Label as="h3" className="text-foreground">
            {label}
          </Label>
        </span>
      </div>
      <div className="mt-0.5 truncate text-[11px] text-subtle">{ready ? hw : "Waiting for system data"}</div>
      {!ready || cur === null || validVals.length === 0 ? (
        <div className="mt-2 text-[26px] font-bold leading-none tabular-nums text-subtle">—</div>
      ) : (
        <>
          <div className={cn("mt-2 text-[26px] font-bold leading-none tabular-nums", text)}>
            <AnimatedNumber value={cur} decimals={0} suffix="%" duration={650} />
          </div>
          <svg
            aria-hidden="true"
            viewBox={`0 0 ${W} ${H}`}
            preserveAspectRatio="none"
            className="eo-chart mt-2 h-12 w-full"
          >
            <defs>
              <linearGradient id={gid} x1="0" x2="0" y1="0" y2="1">
                <stop offset="0%" stopColor={color} stopOpacity="0.8" />
                <stop offset="100%" stopColor={color} stopOpacity="0" />
              </linearGradient>
            </defs>
            <path
              className="eo-chart-area eo-morph"
              d={fill}
              style={morph(fill)}
              fill={`url(#${gid})`}
            />
            <path
              className="eo-morph"
              d={line}
              style={morph(line)}
              fill="none"
              stroke={color}
              strokeWidth="1.75"
              vectorEffect="non-scaling-stroke"
            />
          </svg>
        </>
      )}
      <div className="mt-2 grid grid-cols-3 gap-1.5 text-center">
        {(
          [
            ["AVG", s.avg],
            ["PEAK", s.peak],
            ["LOW", s.low],
          ] as const
        ).map(([k, v]) => (
          <div key={k} className="rounded-md border border-border bg-window py-1">
            <div className="text-[12px] font-bold tabular-nums">
              {v === null ? "—" : <AnimatedNumber value={v} decimals={0} suffix="%" duration={650} />}
            </div>
            <div className="text-[8.5px] font-bold text-subtle">{k}</div>
          </div>
        ))}
      </div>
      <Bar value={cur ?? 0} className="mt-2.5" />
    </Card>
  );
}

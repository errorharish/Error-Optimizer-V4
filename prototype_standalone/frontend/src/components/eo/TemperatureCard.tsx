// V3 Dashboard Temperature Monitoring card — third card of the first Dashboard row, beside
// Overall Optimization Score and Auto Optimize. Values come only from useThermals (Windows host
// sensors, else the native scan, else SIMULATED DATA in the browser preview); rules live in
// src/lib/eo/thermals.ts. The graph is the System Performance Telemetry chart system (same curve,
// gradient areas, dashed grid, axes, morphing paths, crosshair and tooltip classes); series
// colours are the FIXED temperature palette in styles.css, so every theme shows the same series.
import {
  memo,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
} from "react";
import { Pause, Play, RefreshCw, Thermometer } from "lucide-react";
import { Badge, Btn, Card, DemoBadge } from "./kit";
import { AnimatedNumber, Segmented } from "./controls";
import { StatusIndicator, type StatusTone } from "./StatusIndicator";
import { usePageVisible } from "./usePageVisible";
import { useThermals, type ThermalSource } from "./useThermals";
import type { ScanResult } from "@/lib/eo/dashboardScan";
import {
  THERMAL_SENSORS,
  THERMAL_WINDOWS,
  graphRange,
  graphTicks,
  graphY,
  overallThermal,
  sensorsAvailable,
  thermalAreaPath,
  thermalPath,
  thermalWindow,
  type GraphRange,
  type ThermalLevel,
  type ThermalSample,
  type ThermalSensor,
  type ThermalWindow,
} from "@/lib/eo/thermals";

const LEVEL_TONE: Record<ThermalLevel, StatusTone> = {
  NORMAL: "pos",
  WARM: "warn",
  CRITICAL: "neg",
};
/** Draw order: CPU last so it sits on top. */
const DRAW_ORDER: ThermalSensor[] = ["ssd", "gpuHotspot", "gpu", "cpu"];
const ALL = new Set<ThermalSensor>(THERMAL_SENSORS.map((s) => s.key));
const LABEL: Record<ThermalSensor, string> = Object.fromEntries(
  THERMAL_SENSORS.map((s) => [s.key, s.label]),
) as Record<ThermalSensor, string>;

/** Footer source status — real sensor readings and preview values are never worded alike. */
const SOURCE_STATUS: Record<ThermalSource, string> = {
  pending: "",
  simulated: "NO SENSOR DATA",
  live: "LIVE · WINDOWS SENSORS",
  scan: "LAST SCAN · NOT LIVE",
  unavailable: "NO SENSOR DATA",
};

/** Chart box, same proportions as the System Performance Telemetry chart. */
const W = 600;
const H = 176;
const GRID_Y = [0, 44, 88, 132, 176];
/** `d` as a CSS property lets the browser interpolate between updates (same command count). */
const morph = (d: string) => ({ d: `path("${d}")` }) as CSSProperties;

function SourceBadge({ source }: { source: ThermalSource }) {
  switch (source) {
    case "live":
      return <Badge tone="success">LIVE SENSORS</Badge>;
    case "scan":
      return <Badge tone="cyan">LAST SCAN</Badge>;
    case "simulated":
    case "unavailable":
      return <Badge>NO SENSOR DATA</Badge>;
    default:
      return null;
  }
}

/** False while the card is scrolled out of view, so the graph stops redrawing off-screen. */
function useInView<T extends Element>() {
  const ref = useRef<T>(null);
  const [inView, setInView] = useState(true);
  useEffect(() => {
    const el = ref.current;
    if (!el || typeof IntersectionObserver === "undefined") return;
    const io = new IntersectionObserver(([e]) => setInView(e?.isIntersecting ?? true));
    io.observe(el);
    return () => io.disconnect();
  }, []);
  return [ref, inView] as const;
}

type Feed = { history: ThermalSample[]; total: number };

export const TemperatureCard = memo(function TemperatureCard({
  scan,
}: {
  scan: ScanResult | null;
}) {
  const t = useThermals(scan);
  const pending = t.source === "pending";
  const level = overallThermal(t.current);
  const { available, total } = sensorsAvailable(t.current);

  const [paused, setPaused] = useState<Feed | null>(null);
  const [win, setWin] = useState<ThermalWindow>("1 MIN");
  const [shown, setShown] = useState<Set<ThermalSensor>>(ALL);
  const [redraw, setRedraw] = useState(0);
  const [bodyRef, inView] = useInView<HTMLDivElement>();
  const pageVisible = usePageVisible();

  // Graph input: live feed, or a held snapshot while paused / off-screen / window hidden.
  // Collection never stops, so Resume continues from the latest reading.
  const held = useRef<Feed>({ history: t.history, total: t.total });
  const liveFeed = !paused && inView && pageVisible;
  if (liveFeed) held.current = { history: t.history, total: t.total };
  const feed = paused ?? held.current;

  const spec = THERMAL_WINDOWS.find((w) => w.label === win) ?? THERMAL_WINDOWS[1];
  const plotted = useMemo(
    () => thermalWindow(feed.history, feed.total, spec.seconds, t.intervalMs),
    [feed.history, feed.total, spec.seconds, t.intervalMs],
  );
  const range = useMemo(() => graphRange(plotted.values), [plotted.values]);

  const toggle = (k: ThermalSensor) =>
    setShown((s) => {
      const n = new Set(s);
      if (n.has(k)) n.delete(k);
      else n.add(k);
      return n;
    });

  const emptyLabel = pending
    ? null
    : t.history.length === 0
      ? t.source === "scan"
        ? "WAITING FOR LIVE SENSORS"
        : "SENSOR UNAVAILABLE"
      : shown.size === 0
        ? "NO SERIES SELECTED"
        : null;

  return (
    <Card className="eo-hero eo-temp-card flex flex-col">
      {/* Same header as Auto Optimize: thermal status + sensor availability under the title;
          the data source is the badge in the bottom row, so there is no separate status row. */}
      <div className="eo-card-head">
        <span className="eo-auto-mark" aria-hidden="true">
          <Thermometer className="h-4 w-4" />
        </span>
        <h2 className="eo-heading truncate" title="CPU, GPU and SSD sensor monitoring">
          Temperature Monitoring
        </h2>
        <div className="eo-temp-status eo-card-head-status">
          <StatusIndicator
            tone={pending ? "warn" : level ? LEVEL_TONE[level] : "neg"}
            size="sm"
            live
            reserve="THERMAL CRITICAL"
          >
            {pending ? "CHECKING SENSORS" : level ? `THERMAL ${level}` : "SENSOR UNAVAILABLE"}
          </StatusIndicator>
          <StatusIndicator
            tone={pending ? "warn" : available === total ? "pos" : available > 0 ? "warn" : "neg"}
            size="sm"
            label="SENSORS"
            reserve="0/0"
            title={pending ? undefined : `${available} of ${total} temperature sensors reporting`}
          >
            {pending ? "--" : `${available}/${total}`}
          </StatusIndicator>
        </div>
      </div>

      <div ref={bodyRef} className="eo-temp-body">
        {/* Reading tiles double as the legend: same dot + label + live value, click to show/hide. */}
        <ul className="eo-temp-readings" aria-label="Current temperatures">
          {THERMAL_SENSORS.map((s) => {
            const v = t.current?.[s.key] ?? null;
            const meta = t.sensorMeta[s.key];
            const on = shown.has(s.key);
            const metaDetail = meta
              ? ` · Provider: ${meta.provider} · Device: ${meta.device || s.label}${meta.reason ? ` (${meta.reason})` : ""}`
              : "";
            return (
              <li key={s.key}>
                <button
                  type="button"
                  className="eo-temp-tile"
                  data-series={s.key}
                  aria-pressed={on}
                  disabled={v === null}
                  onClick={() => toggle(s.key)}
                  title={
                    v === null
                      ? `${s.label}: ${meta?.availability ?? "unavailable"}${metaDetail}`
                      : `${on ? "Hide" : "Show"} ${s.label} on the graph${metaDetail}`
                  }
                >
                  <span className="eo-temp-tile-head">
                    <span className="eo-legend-dot" aria-hidden="true" />
                    <span className="truncate">{s.label}</span>
                  </span>
                  {v !== null ? (
                    <span className="eo-temp-value">
                      <AnimatedNumber value={v} decimals={0} duration={650} />
                      <small>°C</small>
                    </span>
                  ) : pending ? (
                    <span className="eo-temp-value text-subtle">--</span>
                  ) : (
                    <span className="eo-temp-na">SENSOR UNAVAILABLE</span>
                  )}
                </button>
              </li>
            );
          })}
        </ul>

        <div className="eo-temp-chartcol">
          <div className="eo-temp-toolbar">
            <Btn
              size="sm"
              pressed={paused !== null}
              label={paused ? "Resume temperature graph" : "Pause temperature graph"}
              title={paused ? "Resume temperature graph" : "Pause temperature graph"}
              onClick={() => setPaused(paused ? null : { history: t.history, total: t.total })}
            >
              {paused ? (
                <Play aria-hidden="true" className="h-3 w-3" />
              ) : (
                <Pause aria-hidden="true" className="h-3 w-3" />
              )}
              <span className="eo-temp-ctl-label">{paused ? "Resume" : "Pause"}</span>
            </Btn>
            <Btn
              size="sm"
              label="Redraw temperature graph"
              title="Redraw temperature graph"
              onClick={() => setRedraw((k) => k + 1)}
            >
              <RefreshCw aria-hidden="true" className="h-3 w-3" />
              <span className="eo-temp-ctl-label">Redraw</span>
            </Btn>
            <Segmented
              label="Temperature graph window"
              options={THERMAL_WINDOWS.map((w) => w.label)}
              value={win}
              onChange={(v) => setWin(v as ThermalWindow)}
              className="eo-temp-seg"
            />
          </div>
          <ThermalChart
            key={redraw}
            values={plotted.values}
            slots={plotted.slots}
            stepMs={plotted.stepMs}
            range={range}
            shownKey={DRAW_ORDER.filter((k) => shown.has(k)).join(",")}
            axisStart={spec.axis}
            windowText={spec.text}
            emptyLabel={emptyLabel}
            simulated={t.source === "simulated"}
          />
        </div>
      </div>

      <div className="eo-temp-foot">
        <span className="truncate">
          {pending
            ? ""
            : `${spec.text} · every ${t.intervalMs / 1000} s${paused ? " · PAUSED" : ""}`}
        </span>
        <span className="eo-temp-source" data-source={t.source} title={SOURCE_STATUS[t.source]}>
          <SourceBadge source={t.source} />
        </span>
      </div>
    </Card>
  );
});

/** Memoised graph: while paused or off-screen its props stay identical, so it never redraws. */
const ThermalChart = memo(function ThermalChart({
  values,
  slots,
  stepMs,
  range,
  shownKey,
  axisStart,
  windowText,
  emptyLabel,
  simulated,
}: {
  values: ThermalSample[];
  slots: number;
  stepMs: number;
  range: GraphRange;
  shownKey: string;
  axisStart: string;
  windowText: string;
  emptyLabel: string | null;
  simulated: boolean;
}) {
  const uid = useId().replace(/:/g, "");
  const [hover, setHover] = useState<number | null>(null);
  const series = (shownKey ? shownKey.split(",") : []) as ThermalSensor[];
  const n = values.length;
  const offset = Math.max(0, slots - n);
  const xPct = (i: number) => ((offset + i) / Math.max(1, slots - 1)) * 100;
  const idx = hover === null || n === 0 ? null : Math.min(n - 1, Math.max(0, hover));
  const point = idx === null ? null : values[idx];
  const ago = idx === null ? 0 : Math.round(((n - 1 - idx) * stepMs) / 1000);
  const latest = values[n - 1];
  const ticks = graphTicks(range);

  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (!n) return;
    if (e.key === "ArrowLeft" || e.key === "ArrowRight") {
      e.preventDefault();
      const d = e.key === "ArrowLeft" ? -1 : 1;
      setHover((h) => Math.min(n - 1, Math.max(0, (h ?? n - 1) + d)));
    }
    if (e.key === "Escape") setHover(null);
  };

  const summary = series
    .map((k) => {
      const v = latest?.[k] ?? null;
      return `${LABEL[k]} ${v === null ? "unavailable" : `${Math.round(v)} °C`}`;
    })
    .join(", ");

  return (
    <div className="eo-temp-chart">
      <div className="eo-temp-yaxis" aria-hidden="true">
        {ticks.map((c, i) => (
          <span key={i}>{c}°</span>
        ))}
      </div>
      <div className="eo-temp-plotcol">
        <div
          role="img"
          tabIndex={0}
          aria-label={`Temperature graph, ${windowText.toLowerCase()}.${
            summary ? ` Latest: ${summary}.` : ""
          } Use left and right arrows to inspect samples.`}
          className="eo-tele-plot eo-temp-plot"
          onPointerMove={(e) => {
            if (!n) return;
            const box = e.currentTarget.getBoundingClientRect();
            const x = Math.min(1, Math.max(0, (e.clientX - box.left) / box.width));
            setHover(Math.round(x * Math.max(1, slots - 1)) - offset);
          }}
          onPointerLeave={() => setHover(null)}
          onBlur={() => setHover(null)}
          onKeyDown={onKey}
        >
          <svg
            aria-hidden="true"
            viewBox={`0 0 ${W} ${H}`}
            preserveAspectRatio="none"
            className="eo-chart absolute inset-0 h-full w-full overflow-visible"
          >
            <defs>
              {series.map((k) => (
                <linearGradient
                  key={k}
                  id={`${uid}-${k}`}
                  data-series={k}
                  x1="0"
                  x2="0"
                  y1="0"
                  y2="1"
                >
                  <stop offset="0%" stopColor="var(--series)" stopOpacity="0.9" />
                  <stop offset="100%" stopColor="var(--series)" stopOpacity="0" />
                </linearGradient>
              ))}
            </defs>
            {GRID_Y.map((y) => (
              <line
                key={y}
                x1="0"
                x2={W}
                y1={y}
                y2={y}
                className="eo-temp-grid"
                vectorEffect="non-scaling-stroke"
              />
            ))}
            {series.map((k) => {
              const d = thermalAreaPath(
                values.map((v) => v[k]),
                W,
                H,
                slots,
                range,
              );
              return d ? (
                <path
                  key={`a-${k}`}
                  className="eo-chart-area eo-morph"
                  d={d}
                  style={morph(d)}
                  fill={`url(#${uid}-${k})`}
                />
              ) : null;
            })}
            {series.map((k) => {
              const d = thermalPath(
                values.map((v) => v[k]),
                W,
                H,
                slots,
                range,
              );
              return d ? (
                <path
                  key={`l-${k}`}
                  className="eo-temp-line eo-morph"
                  data-series={k}
                  d={d}
                  style={morph(d)}
                  vectorEffect="non-scaling-stroke"
                />
              ) : null;
            })}
          </svg>
          {emptyLabel && (
            <span className="eo-temp-empty" aria-hidden="true">
              {emptyLabel}
            </span>
          )}
          {point && idx !== null && series.length > 0 && (
            <>
              <span
                aria-hidden="true"
                className="eo-tele-cross"
                style={{ left: `${xPct(idx)}%` }}
              />
              {series.map((k) => {
                const v = point[k];
                return v === null ? null : (
                  <span
                    key={k}
                    aria-hidden="true"
                    className="eo-tele-dot"
                    data-series={k}
                    style={{ left: `${xPct(idx)}%`, top: `${graphY(v, range) * 100}%` }}
                  />
                );
              })}
              <div
                role="tooltip"
                className="eo-tele-tip eo-temp-tip"
                data-side={xPct(idx) > 55 ? "left" : "right"}
                style={{ left: `${xPct(idx)}%` }}
              >
                <div className="mb-1 flex items-center justify-between gap-4 text-[9.5px] font-bold text-subtle">
                  <span>{ago ? `−${ago}s` : "NOW"}</span>
                  <span>READING</span>
                </div>
                {series.map((k) => (
                  <div key={k} className="flex items-center justify-between gap-4 text-[11.5px]">
                    <span className="flex items-center gap-1.5" data-series={k}>
                      <span aria-hidden="true" className="eo-legend-dot" />
                      {LABEL[k]}
                    </span>
                    <b className="tabular-nums">
                      {point[k] === null ? "N/A" : `${point[k]!.toFixed(1)} °C`}
                    </b>
                  </div>
                ))}
              </div>
            </>
          )}
        </div>
        <div className="eo-temp-xaxis" aria-hidden="true">
          <span>{axisStart}</span>
          <span>NOW</span>
        </div>
      </div>
    </div>
  );
});

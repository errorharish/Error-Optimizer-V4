// Premium controls shared by the Dashboard, System Telemetry, AI Optimization and V4 pages.
// Visual rules live in src/styles.css (.eo-seg, .eo-range, .eo-score, .eo-number).
import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
} from "react";
import { cn } from "@/lib/utils";
import { clampStep, fraction } from "@/lib/eo/slider";
import { clampScore, scoreAt, scoreTweenDuration } from "@/lib/eo/scoreTween";

/* ── Segmented control: one sliding indicator behind aria-pressed options ──
 * WPF: RadioButton row (ToggleButton style) with a single Border animated via TranslateTransform. */
export function Segmented({
  options,
  value,
  onChange,
  label,
  className,
  fill,
}: {
  options: string[];
  value: string;
  onChange: (v: string) => void;
  label: string;
  className?: string | undefined;
  /** Stretch options to fill the available width. */
  fill?: boolean | undefined;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [box, setBox] = useState<{ x: number; y: number; w: number; h: number } | null>(null);
  const measure = useCallback(() => {
    const el = ref.current?.querySelector<HTMLElement>('[aria-pressed="true"]');
    setBox(
      el ? { x: el.offsetLeft, y: el.offsetTop, w: el.offsetWidth, h: el.offsetHeight } : null,
    );
  }, []);
  useLayoutEffect(measure, [value, options.length, measure]);
  useEffect(() => {
    const node = ref.current;
    if (!node || typeof ResizeObserver === "undefined") return;
    const ro = new ResizeObserver(measure);
    ro.observe(node);
    return () => ro.disconnect();
  }, [measure]);
  return (
    <div
      ref={ref}
      role="group"
      aria-label={label}
      data-ready={box ? "true" : undefined}
      className={cn("eo-seg", fill ? "flex w-full" : "inline-flex", className)}
    >
      {box && (
        <span
          aria-hidden="true"
          className="eo-seg-indicator"
          style={{
            transform: `translate(${box.x}px, ${box.y}px)`,
            width: box.w,
            height: box.h,
          }}
        />
      )}
      {options.map((o) => (
        <button
          key={o}
          type="button"
          aria-pressed={value === o}
          onClick={() => onChange(o)}
          className={cn("eo-seg-item", fill && "flex-1")}
        >
          {o}
        </button>
      ))}
    </div>
  );
}

/* ── Range slider: native input (keyboard, touch, screen readers) with a custom track ──
 * Fill and marks use the same thumb-travel maths, so the fill always ends under the thumb. */
export interface SliderMark {
  value: number;
  label: string;
  tone?: "cyan" | "success" | undefined;
}
export function RangeSlider({
  value,
  onChange,
  min,
  max,
  step,
  label,
  valueText,
  marks,
  id,
  className,
}: {
  value: number;
  onChange: (v: number) => void;
  min: number;
  max: number;
  step: number;
  label: string;
  valueText?: string | undefined;
  marks?: SliderMark[] | undefined;
  id?: string | undefined;
  className?: string | undefined;
}) {
  const p = fraction(value, min, max);
  return (
    <div
      className={cn("eo-range-wrap", marks?.length && "eo-range-wrap-marks", className)}
      style={{ "--p": p.toFixed(4) } as CSSProperties}
    >
      <input
        id={id}
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        aria-label={label}
        aria-valuetext={valueText}
        onChange={(e) => onChange(clampStep(+e.target.value, min, max, step))}
        className="eo-range"
      />
      {marks && (
        <div aria-hidden="true" className="eo-range-marks">
          {marks.map((m) => {
            const f = fraction(m.value, min, max);
            return (
              <span
                key={m.label}
                data-tone={m.tone}
                data-align={f < 0.06 ? "start" : f > 0.94 ? "end" : "center"}
                data-active={value === m.value ? "true" : undefined}
                style={{ "--m": f.toFixed(4) } as CSSProperties}
              >
                <i />
                {m.label}
              </span>
            );
          })}
        </div>
      )}
    </div>
  );
}

/* ── Numeric field synchronised with a slider ──
 * Valid in-range typing updates the value live; blur / Enter clamps and snaps; Esc reverts;
 * ArrowUp / ArrowDown step by `step`. */
export function NumberField({
  value,
  onChange,
  min,
  max,
  step,
  unit,
  label,
  decimals = 2,
  id,
}: {
  value: number;
  onChange: (v: number) => void;
  min: number;
  max: number;
  step: number;
  unit: string;
  label: string;
  decimals?: number | undefined;
  id?: string | undefined;
}) {
  const fallbackId = useId();
  const inputId = id ?? fallbackId;
  const fmt = useCallback((v: number) => v.toFixed(decimals), [decimals]);
  // While focused the field shows the typed draft; otherwise it always renders the live value,
  // so it can never lag behind the slider.
  const [draft, setDraft] = useState<string | null>(null);
  const cancelled = useRef(false);
  const atFocus = useRef(value);
  const commit = (text: string | null) => {
    if (text === null) return;
    const n = parseFloat(text.replace(",", "."));
    onChange(Number.isFinite(n) ? clampStep(n, min, max, step) : value);
    setDraft(null);
  };
  return (
    <div className="eo-number">
      <input
        id={inputId}
        inputMode="decimal"
        aria-label={label}
        value={draft ?? fmt(value)}
        onFocus={(e) => {
          atFocus.current = value;
          setDraft(fmt(value));
          e.currentTarget.select();
        }}
        onBlur={() => {
          if (cancelled.current) cancelled.current = false;
          else commit(draft);
          setDraft(null);
        }}
        onChange={(e) => {
          setDraft(e.target.value);
          const n = parseFloat(e.target.value.replace(",", "."));
          if (Number.isFinite(n) && n >= min && n <= max) onChange(clampStep(n, min, max, step));
        }}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            commit(draft);
            setDraft(null);
          }
          if (e.key === "Escape") {
            cancelled.current = true;
            onChange(atFocus.current);
            setDraft(null);
            e.currentTarget.blur();
          }
          if (e.key === "ArrowUp" || e.key === "ArrowDown") {
            e.preventDefault();
            const v = clampStep(value + (e.key === "ArrowUp" ? step : -step), min, max, step);
            onChange(v);
            setDraft(fmt(v));
          }
        }}
      />
      <span aria-hidden="true" className="eo-number-unit">
        {unit}
      </span>
    </div>
  );
}

/* ── Radial score gauge ──
 * One requestAnimationFrame tween (src/lib/eo/scoreTween.ts) drives the number, the gradient arc
 * and the lit ticks together (no endpoint marker), written straight to the DOM so the card does
 * not re-render per frame. First mount counts up from 0; later value changes (a finished re-scan)
 * move from the score currently shown. Parent re-renders with the same value never restart it. */
export function ScoreRing({
  value,
  label,
  size = 152,
  stroke = 11,
  valueClassName = "text-[40px] leading-none font-black",
  unit = "/ 100",
  unitClassName = "mt-0.5 text-[10.5px] font-bold text-subtle",
  children,
}: {
  value: number;
  label: string;
  size?: number | undefined;
  stroke?: number | undefined;
  valueClassName?: string | undefined;
  unit?: string | undefined;
  unitClassName?: string | undefined;
  children?: ReactNode | undefined;
}) {
  const gid = useId().replace(/[^a-zA-Z0-9]/g, "");
  const c = size / 2;
  const r = c - stroke / 2 - 9;
  const len = 2 * Math.PI * r;
  const ticks = 48;
  const target = clampScore(value);

  const rootRef = useRef<HTMLDivElement>(null);
  const arcRef = useRef<SVGCircleElement>(null);
  const tickRef = useRef<SVGGElement>(null);
  const numRef = useRef<HTMLSpanElement>(null);
  const shown = useRef(0);
  const raf = useRef<number | null>(null);

  const paint = useCallback(
    (v: number) => {
      const arc = arcRef.current;
      if (arc) {
        arc.style.strokeDashoffset = (len * (1 - v / 100)).toFixed(2);
        arc.style.opacity = v > 0.25 ? "1" : "0";
        // A full ring with round caps overlaps its own start and draws a bright seam; close it flat.
        arc.style.strokeLinecap = v >= 99.5 ? "butt" : "round";
      }
      const g = tickRef.current;
      if (g) {
        const lit = Math.ceil((v / 100) * ticks - 1e-6);
        for (let i = 0; i < g.children.length; i++) {
          const el = g.children[i] as SVGElement;
          const on = i < lit;
          if ((el.getAttribute("data-lit") === "true") !== on) {
            if (on) el.setAttribute("data-lit", "true");
            else el.removeAttribute("data-lit");
          }
        }
      }
      const num = numRef.current;
      if (num) {
        const t = String(Math.round(v));
        if (num.textContent !== t) num.textContent = t;
      }
    },
    [len],
  );

  useEffect(() => {
    if (raf.current !== null) cancelAnimationFrame(raf.current);
    raf.current = null;
    const from = shown.current;
    const to = target;
    const root = rootRef.current;
    const reduced =
      document.documentElement.dataset["motion"] === "reduced" ||
      window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    const duration = scoreTweenDuration(from, to);
    if (reduced || duration === 0) {
      shown.current = to;
      paint(to);
      return;
    }
    let t0: number | null = null;
    root?.setAttribute("data-moving", "true");
    const step = (ts: number) => {
      t0 ??= ts;
      const t = (ts - t0) / duration;
      const v = scoreAt(from, to, t);
      shown.current = v;
      paint(v);
      if (t < 1) {
        raf.current = requestAnimationFrame(step);
      } else {
        raf.current = null;
        root?.removeAttribute("data-moving");
      }
    };
    raf.current = requestAnimationFrame(step);
    return () => {
      if (raf.current !== null) cancelAnimationFrame(raf.current);
      raf.current = null;
      root?.removeAttribute("data-moving");
    };
  }, [target, paint]);

  return (
    <div
      ref={rootRef}
      role="img"
      aria-label={label}
      className="eo-score"
      style={{ width: size, height: size }}
    >
      <svg viewBox={`0 0 ${size} ${size}`} aria-hidden="true" className="absolute inset-0">
        <defs>
          <linearGradient id={`${gid}-arc`} x1="0" y1="0" x2="1" y2="1">
            {/* Styles (not presentation attributes) so theme variables always resolve: a stop that
                fails to resolve falls back to a default colour and shows as a spot on the ring. */}
            <stop offset="0%" style={{ stopColor: "var(--cyan)" }} />
            <stop
              offset="55%"
              style={{ stopColor: "color-mix(in oklab, var(--cyan) 45%, var(--primary))" }}
            />
            <stop offset="100%" style={{ stopColor: "var(--primary)" }} />
          </linearGradient>
        </defs>
        <g ref={tickRef}>
          {Array.from({ length: ticks }, (_, i) => {
            const a = (i / ticks) * Math.PI * 2 - Math.PI / 2;
            const r1 = c - 3;
            const r2 = c - (i % 4 === 0 ? 7 : 5);
            return (
              <line
                key={i}
                x1={(c + Math.cos(a) * r1).toFixed(2)}
                y1={(c + Math.sin(a) * r1).toFixed(2)}
                x2={(c + Math.cos(a) * r2).toFixed(2)}
                y2={(c + Math.sin(a) * r2).toFixed(2)}
                className="eo-score-tick"
              />
            );
          })}
        </g>
        <circle cx={c} cy={c} r={r} fill="none" strokeWidth={stroke} className="eo-score-track" />
        <circle
          ref={arcRef}
          cx={c}
          cy={c}
          r={r}
          fill="none"
          strokeWidth={stroke}
          stroke={`url(#${gid}-arc)`}
          strokeLinecap="round"
          strokeDasharray={len.toFixed(2)}
          strokeDashoffset={len.toFixed(2)}
          transform={`rotate(-90 ${c} ${c})`}
          className="eo-score-arc"
          style={{ opacity: 0 }}
        />
      </svg>
      <div className="eo-score-center">
        <span
          ref={numRef}
          aria-hidden="true"
          className={cn("eo-score-value tabular-nums", valueClassName)}
        >
          0
        </span>
        {unit && (
          <span aria-hidden="true" className={unitClassName}>
            {unit}
          </span>
        )}
        {children}
      </div>
    </div>
  );
}

export interface AnimatedNumberProps {
  value: number;
  duration?: number;
  decimals?: number;
  prefix?: string;
  suffix?: string;
  format?: (val: number) => string;
  className?: string | undefined;
  initialAnimate?: boolean;
}

/**
 * Global number animation: one requestAnimationFrame tween (ease-out quart) that writes the
 * formatted text straight into its own text node, so a moving number never re-renders React
 * (telemetry tiles update every second on low-end PCs). The text only changes when the visible
 * digits change. Reduced motion (OS or app setting) and hidden windows jump straight to the value.
 */
export function AnimatedNumber({
  value,
  duration = 750,
  decimals = 0,
  prefix = "",
  suffix = "",
  format,
  className,
  initialAnimate = true,
}: AnimatedNumberProps) {
  const fmt = (n: number) => (format ? format(n) : `${prefix}${n.toFixed(decimals)}${suffix}`);
  const fmtRef = useRef(fmt);
  fmtRef.current = fmt;
  const elRef = useRef<HTMLSpanElement>(null);
  const shownRef = useRef<number>(initialAnimate ? 0 : value);
  // Rendered once; afterwards the tween owns the text node, so React never rewrites it.
  const [initialText] = useState(() => fmt(initialAnimate ? 0 : value));

  const write = useCallback((n: number) => {
    const node = elRef.current?.firstChild;
    if (!node) return;
    const text = fmtRef.current(n);
    if (node.nodeValue !== text) node.nodeValue = text;
  }, []);

  useEffect(() => {
    const start = shownRef.current;
    const end = value;
    const root = document.documentElement;
    const still =
      start === end ||
      document.hidden ||
      root.dataset["motion"] === "reduced" ||
      window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (still) {
      shownRef.current = end;
      write(end);
      return;
    }
    let t0: number | null = null;
    let raf = 0;
    const step = (ts: number) => {
      t0 ??= ts;
      const p = Math.min(1, (ts - t0) / Math.max(1, duration));
      const cur = p < 1 ? start + (end - start) * (1 - Math.pow(1 - p, 4)) : end;
      shownRef.current = cur;
      write(cur);
      if (p < 1) raf = requestAnimationFrame(step);
    };
    raf = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf);
  }, [value, duration, write]);

  // Formatting changes (unit, decimals) re-format the number currently shown.
  useEffect(() => write(shownRef.current), [decimals, prefix, suffix, format, write]);

  return (
    <span
      ref={elRef}
      aria-hidden="true"
      className={cn("tabular-nums inline-block transition-colors", className)}
    >
      {initialText}
    </span>
  );
}

/** Integer score count-up using smooth RAF animation (duration=900ms, ease-emphasized). */
export function CountUp({
  value,
  className,
  duration = 900,
}: {
  value: number;
  className?: string | undefined;
  duration?: number | undefined;
}) {
  return (
    <AnimatedNumber
      value={value}
      duration={duration}
      decimals={0}
      className={className}
      initialAnimate={true}
    />
  );
}

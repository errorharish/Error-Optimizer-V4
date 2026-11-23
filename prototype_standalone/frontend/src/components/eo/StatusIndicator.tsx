// The one status display for V3 + V4: online/offline, connected/disconnected, running/stopped,
// connecting/detecting, error. It looks like the premium primary button (gradient, border, glow)
// coloured by its state, but it is strictly informational: a <span> with status semantics, no click
// handler, no navigation, normal cursor, no press feedback. Looks live in the "Shared status
// indicators" block of styles.css (`.eo-st`, `[data-st]`). It only displays the state it is given —
// callers pass real backend results, or label simulated data with `demo`.
import type React from "react";
import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { cn } from "@/lib/utils";
import { placeFloating } from "@/lib/eo/floatingPlacement";

/**
 * Details tooltip for a status display, rendered in the overlay layer (a <body> portal at
 * `--z-tooltip`) so cards with overflow, transforms, filters or their own stacking context can
 * never clip or cover it. Opens on hover / keyboard focus, closes on leave, blur or Escape;
 * positions itself with `placeFloating` (flips above / clamps to the window) and follows scroll
 * and resize only while open. Position is written straight to the DOM — no re-render per move.
 */
function useStatusTip(enabled: boolean, align: "start" | "end") {
  const trigger = useRef<HTMLDivElement | null>(null);
  const tip = useRef<HTMLDivElement | null>(null);
  const [open, setOpen] = useState(false);
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(enabled), [enabled]);

  const place = useCallback(() => {
    const t = trigger.current;
    const p = tip.current;
    if (!t || !p) return;
    const r = t.getBoundingClientRect();
    const pos = placeFloating(
      r,
      { width: p.offsetWidth, height: p.offsetHeight },
      { width: window.innerWidth, height: window.innerHeight },
      align,
    );
    p.style.top = `${pos.top}px`;
    p.style.left = `${pos.left}px`;
    p.dataset["side"] = pos.side;
  }, [align]);

  useLayoutEffect(() => {
    if (!open) return;
    place();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
      window.removeEventListener("keydown", onKey);
    };
  }, [open, place]);

  const show = useCallback(() => setOpen(true), []);
  const hide = useCallback(() => setOpen(false), []);
  const handlers = enabled
    ? { onPointerEnter: show, onPointerLeave: hide, onFocus: show, onBlur: hide }
    : {};
  return { trigger, tip, open, mounted, handlers };
}

/**
 * Exactly three colour families are drawn:
 * pos  = connected / running / online / active              (green → emerald)
 * warn = connecting / detecting / initializing / pending     (yellow → amber)
 * neg  = disconnected / closed / not running / offline       (red → rose)
 * `err` (an actual error, shown with its error text), `muted` (off / not run) and `info`
 * (temporarily skipped, e.g. in focus) are accepted for meaning but render in a family:
 * err + muted → red, info → yellow.
 */
export type StatusTone = "pos" | "warn" | "neg" | "err" | "info" | "muted";
export type StatusFamily = "pos" | "warn" | "neg";

/** The colour family a tone is drawn in (the only three status colours). */
export function statusFamily(t: StatusTone): StatusFamily {
  return t === "pos" ? "pos" : t === "warn" || t === "info" ? "warn" : "neg";
}

/** Maps the app's older tone names onto the shared status tones. */
export function statusTone(t: string): StatusTone {
  switch (t) {
    case "success":
    case "ok":
    case "online":
      return "pos";
    case "warning":
    case "busy":
    case "connecting":
      return "warn";
    case "danger":
    case "down":
    case "offline":
      return "neg";
    case "error":
    case "failed":
      return "err";
    case "cyan":
      return "info";
    default:
      return "muted";
  }
}

export function StatusIndicator({
  tone,
  children,
  size = "md",
  className,
  live,
  icon,
  label,
  demo,
  reserve,
  tip,
  tipAlign = "end",
  title,
}: {
  tone: StatusTone;
  /** Status text. */
  children: ReactNode;
  size?: "sm" | "md" | "block" | undefined;
  className?: string | undefined;
  /** Announce text changes to screen readers (role="status"). */
  live?: boolean | undefined;
  /** Optional leading icon. */
  icon?: ReactNode | undefined;
  /** Optional muted key in front of the status text (e.g. "MSI APP PLAYER"). */
  label?: ReactNode | undefined;
  /** Shows the DEMO tag for simulated sources. */
  demo?: boolean | undefined;
  /** Longest text this status can show; reserves its width so state changes never shift layout. */
  reserve?: string | undefined;
  /** Hover / keyboard-focus details. Makes the display focusable only so the details are reachable. */
  tip?: ReactNode | undefined;
  tipAlign?: "start" | "end" | undefined;
  title?: string | undefined;
}) {
  const tipId = useId();
  const family = statusFamily(tone);
  const t = useStatusTip(Boolean(tip), tipAlign);
  // A status with details is a focusable block container; everything else stays an inline span.
  const Tag = tip ? "div" : "span";
  return (
    <Tag
      ref={tip ? (t.trigger as React.Ref<never>) : undefined}
      {...t.handlers}
      className={cn("eo-st", className)}
      data-st={statusFamily(tone)}
      data-st-tone={tone !== statusFamily(tone) ? tone : undefined}
      data-size={size}
      data-tip-align={tip ? tipAlign : undefined}
      title={title}
      tabIndex={tip ? 0 : undefined}
      aria-describedby={tip ? tipId : undefined}
    >
      <span aria-hidden="true" className="eo-st-dot" />
      {icon && (
        <span aria-hidden="true" className="eo-st-icon">
          {icon}
        </span>
      )}
      {label && <span className="eo-st-key">{label}</span>}
      <span
        className="eo-st-text"
        data-reserve={reserve}
        role={live ? "status" : undefined}
        aria-live={live ? "polite" : undefined}
      >
        {children}
      </span>
      {tip &&
        t.mounted &&
        createPortal(
          <div
            ref={t.tip}
            id={tipId}
            role="tooltip"
            className="eo-st-tip"
            data-st={family}
            data-open={t.open ? "true" : undefined}
          >
            {tip}
          </div>,
          document.body,
        )}
    </Tag>
  );
}

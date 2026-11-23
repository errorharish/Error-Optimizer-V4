/* ── Button system (Theme/Buttons.xaml) ──
 * One button system for the whole app. Every look lives in the `.eo-btn` block of
 * styles.css, keyed by data-variant / data-size and built on the --switch-* tokens of the
 * header V3/V4 switch.
 *
 * Every ACTION button wears the premium primary look (the APPLY PRO OPTIMIZATION style).
 * "secondary", "ghost", "outline" and "success" are legacy names kept so old call sites
 * compile; they all render as "primary". Only three looks differ on purpose:
 *   danger  — destructive actions (Delete, Terminate, destructive Reset)
 *   toggle  — on/off chips (aria-pressed); the "on" state wears the premium gradient
 *   quiet   — non-action chrome: window controls, the dock collapse toggle and
 *             row disclosure chevrons
 * Sizes: sm / md / lg, plus icon / icon-sm for icon-only buttons (give them a label). */
export type BtnVariant =
  | "primary"
  | "danger"
  | "toggle"
  | "quiet"
  /** @deprecated renders as primary */
  | "secondary"
  /** @deprecated renders as primary */
  | "ghost"
  /** @deprecated renders as primary */
  | "outline"
  /** @deprecated renders as primary */
  | "success";
export type BtnSize = "sm" | "md" | "lg" | "icon" | "icon-sm";

/** The look actually rendered: legacy action variants collapse into "primary". */
export function variantAttr(v: BtnVariant): "primary" | "danger" | "toggle" | "quiet" {
  return v === "danger" || v === "toggle" || v === "quiet" ? v : "primary";
}

/** Props that give any <button> or <Link> the shared button look. */
export function btnProps(variant: BtnVariant = "primary", size: BtnSize = "md") {
  return { className: "eo-btn", "data-variant": variantAttr(variant), "data-size": size } as const;
}

// Placement for floating details (status tooltips) rendered in the overlay layer (<body> portal).
// Pure geometry so it can be tested: prefer below the trigger, flip above when there is not enough
// room below, never cover the trigger when either side fits, and always stay inside the window.

export interface Rect {
  top: number;
  left: number;
  right: number;
  bottom: number;
  width: number;
  height: number;
}

export interface Placement {
  top: number;
  left: number;
  side: "bottom" | "top";
}

/** Gap between trigger and floating panel. */
export const FLOAT_GAP = 8;
/** Minimum distance from the window edges. */
export const FLOAT_EDGE = 8;

export function placeFloating(
  trigger: Rect,
  size: { width: number; height: number },
  viewport: { width: number; height: number },
  align: "start" | "end" = "end",
): Placement {
  const below = viewport.height - trigger.bottom - FLOAT_GAP - FLOAT_EDGE;
  const above = trigger.top - FLOAT_GAP - FLOAT_EDGE;
  const side: Placement["side"] = size.height <= below || below >= above ? "bottom" : "top";

  let top = side === "bottom" ? trigger.bottom + FLOAT_GAP : trigger.top - FLOAT_GAP - size.height;
  // Only when neither side fits does it clamp (and may then overlap the trigger) — it never leaves the window.
  top = Math.min(
    Math.max(FLOAT_EDGE, top),
    Math.max(FLOAT_EDGE, viewport.height - size.height - FLOAT_EDGE),
  );

  let left = align === "end" ? trigger.right - size.width : trigger.left;
  left = Math.min(
    Math.max(FLOAT_EDGE, left),
    Math.max(FLOAT_EDGE, viewport.width - size.width - FLOAT_EDGE),
  );

  return { top: Math.round(top), left: Math.round(left), side };
}

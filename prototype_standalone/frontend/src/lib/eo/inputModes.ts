// Input Optimization modes — pure data and rules.
// Normal and Advanced are recommendation-driven (their selection is computed, never edited);
// Custom is the only mode where the user picks items, and it is saved separately.
import type { Risk } from "./data";
import { eligibleSelection, isDefaultEligible, type SelectionCandidate } from "./selection";

export type InputMode = "NORMAL" | "ADVANCED" | "CUSTOM";
export const INPUT_MODES: InputMode[] = ["NORMAL", "ADVANCED", "CUSTOM"];

export interface InputItem extends SelectionCandidate<string> {
  id: string;
  title: string;
  desc: string;
  group: "normal" | "advanced";
  risk: Risk;
  recommended: boolean;
  /** Compatibility note on the sample system. */
  compat: string;
  restart?: boolean;
}

export const INPUT_CANONICAL_TO_FRONTEND: Record<string, string> = {
  "mouse.speed": "mouse-accel",
  "mouse.threshold1": "mouse-accel",
  "mouse.threshold2": "mouse-accel",
  "mouse.pointer_curves": "mouse-accel",
  "keyboard.speed": "kb-repeat-rate",
  "keyboard.delay": "kb-repeat-delay",
  "mouse.hover_time": "mouse-hover",
  "queue.mouclass_size": "mouse-queue",
  "queue.kbdclass_size": "kb-queue",
  "scheduling.system_responsiveness": "timer-request",
  "usb.selective_suspend": "usb-suspend",
  "scheduling.priority_separation": "csrss-priority",
  "scheduling.mouclass_priority": "csrss-priority",
  "scheduling.kbdclass_priority": "csrss-priority",
  "input.mouse.cursor_shadow_overhead_disable": "mouse-cursor-shadow",
  "mouse.sensitivity": "mouse-sensitivity-cal",
  "mouse.snap_to_default": "mouse-snap-default",
  "mouse.active_window_tracking": "mouse-active-tracking",
  "mouse.trails": "mouse-trails-disable",
  "input.keyboard.debounce_latency_reduction": "kb-debounce-reduce",
  "keyboard.filter_keys": "kb-filterkeys-disable",
  "keyboard.sticky_keys": "kb-stickykeys-disable",
  "queue.mouhid_length": "queue-mouhid-length",
  "queue.kbdhid_length": "queue-kbdhid-length",
  "input.hid.polling_interval_calibrate": "hid-polling-calibrate",
  "input.usb.selective_suspend_per_device": "usb-perdevice-suspend",
  "input.dwm.input_separation_quantum": "dwm-input-separation",
  "input.gaming.controller_deadzone_linearity": "gamepad-deadzone-linearity",
};

export const FRONTEND_TO_INPUT_CANONICAL: Record<string, string[]> = {
  "mouse-accel": ["mouse.speed", "mouse.threshold1", "mouse.threshold2", "mouse.pointer_curves"],
  "kb-repeat-rate": ["keyboard.speed"],
  "kb-repeat-delay": ["keyboard.delay"],
  "mouse-hover": ["mouse.hover_time"],
  "mouse-queue": ["queue.mouclass_size"],
  "kb-queue": ["queue.kbdclass_size"],
  "timer-request": ["scheduling.system_responsiveness"],
  "usb-suspend": ["usb.selective_suspend"],
  "csrss-priority": ["scheduling.priority_separation", "scheduling.mouclass_priority", "scheduling.kbdclass_priority"],
  "ps2-queue": [],
  "mouse-cursor-shadow": ["input.mouse.cursor_shadow_overhead_disable"],
  "mouse-sensitivity-cal": ["mouse.sensitivity"],
  "mouse-snap-default": ["mouse.snap_to_default"],
  "mouse-active-tracking": ["mouse.active_window_tracking"],
  "mouse-trails-disable": ["mouse.trails"],
  "kb-debounce-reduce": ["input.keyboard.debounce_latency_reduction"],
  "kb-filterkeys-disable": ["keyboard.filter_keys"],
  "kb-stickykeys-disable": ["keyboard.sticky_keys"],
  "queue-mouhid-length": ["queue.mouhid_length"],
  "queue-kbdhid-length": ["queue.kbdhid_length"],
  "hid-polling-calibrate": ["input.hid.polling_interval_calibrate"],
  "usb-perdevice-suspend": ["input.usb.selective_suspend_per_device"],
  "dwm-input-separation": ["input.dwm.input_separation_quantum"],
  "gamepad-deadzone-linearity": ["input.gaming.controller_deadzone_linearity"],
};

export const INPUT_ITEMS: InputItem[] = [
  // ── Standard Pointer & Keyboard Presets (Normal) ──
  {
    id: "mouse-accel",
    title: "Disable mouse acceleration",
    desc: "1:1 raw pointer movement (Enhance pointer precision off)",
    group: "normal",
    risk: "LOW",
    recommended: true,
    compat: "Already applied on the sample system",
    done: true,
  },
  {
    id: "kb-repeat-rate",
    title: "Maximize keyboard repeat rate",
    desc: "Fastest standard repeat rate (31 repetitions/sec)",
    group: "normal",
    risk: "LOW",
    recommended: true,
    compat: "Any keyboard",
  },
  {
    id: "kb-repeat-delay",
    title: "Minimum keyboard repeat delay",
    desc: "Shortest standard delay before a key repeats (250ms)",
    group: "normal",
    risk: "LOW",
    recommended: true,
    compat: "Any keyboard",
  },
  {
    id: "mouse-hover",
    title: "Reduce mouse hover time",
    desc: "Tooltips and hover menus open sooner (10ms)",
    group: "normal",
    risk: "SAFE",
    recommended: true,
    compat: "Any pointer",
  },
  {
    id: "mouse-cursor-shadow",
    title: "Disable cursor drop-shadow composition",
    desc: "Removes legacy GDI cursor drop-shadow overhead for zero-lag hardware cursor plane presentation",
    group: "normal",
    risk: "SAFE",
    recommended: true,
    compat: "Any display adapter",
  },
  {
    id: "mouse-sensitivity-cal",
    title: "1:1 pointer sensitivity calibration",
    desc: "Locks desktop pointer sensitivity to 10 (nominal unscaled 1:1 hardware multiplier)",
    group: "normal",
    risk: "SAFE",
    recommended: true,
    compat: "Standard mouse / touchpad",
  },
  {
    id: "mouse-snap-default",
    title: "Disable snap-to-default button jumping",
    desc: "Prevents automatic cursor teleportation to default dialog buttons for consistent muscular memory",
    group: "normal",
    risk: "SAFE",
    recommended: true,
    compat: "Any pointer",
  },
  {
    id: "mouse-active-tracking",
    title: "Disable active window mouse tracking",
    desc: "Prevents window focus tracking latency overhead during rapid cursor movement",
    group: "normal",
    risk: "SAFE",
    recommended: true,
    compat: "Desktop Windows shell",
  },
  {
    id: "mouse-trails-disable",
    title: "Disable mouse pointer trails",
    desc: "Eliminates multi-cursor trail replication latency and GDI pointer redraw stalls",
    group: "normal",
    risk: "SAFE",
    recommended: true,
    compat: "Any display adapter",
  },
  {
    id: "kb-filterkeys-disable",
    title: "Disable accessibility FilterKeys latency",
    desc: "Prevents accidental brief keystroke filtering delays in competitive gaming",
    group: "normal",
    risk: "SAFE",
    recommended: true,
    compat: "Any keyboard",
  },
  {
    id: "kb-stickykeys-disable",
    title: "Disable accessibility StickyKeys interception",
    desc: "Removes Shift-key repeat trap interception and notification dialog latency",
    group: "normal",
    risk: "SAFE",
    recommended: true,
    compat: "Any keyboard",
  },

  // ── Advanced Queue, Timer, USB & Driver Options ──
  {
    id: "mouse-queue",
    title: "Mouse data queue size",
    desc: "MouseDataQueueSize for the mouclass driver",
    group: "advanced",
    risk: "LOW",
    recommended: true,
    compat: "Already applied on the sample system",
    done: true,
    restart: true,
  },
  {
    id: "kb-queue",
    title: "Keyboard data queue size",
    desc: "KeyboardDataQueueSize for the kbdclass driver",
    group: "advanced",
    risk: "LOW",
    recommended: true,
    compat: "Standard HID keyboard",
    restart: true,
  },
  {
    id: "timer-request",
    title: "High-precision timer request",
    desc: "Lets apps keep their requested timer resolution (GlobalTimerResolutionRequests)",
    group: "advanced",
    risk: "MEDIUM",
    recommended: true,
    compat: "Windows 11 · higher idle power use",
    restart: true,
  },
  {
    id: "usb-suspend",
    title: "USB selective suspend (input devices)",
    desc: "Keeps USB input devices from sleeping",
    group: "advanced",
    risk: "LOW",
    recommended: true,
    compat: "Desktop on AC power",
  },
  {
    id: "csrss-priority",
    title: "Raise input thread priority",
    desc: "CSRSS / input thread priority override",
    group: "advanced",
    risk: "HIGH",
    recommended: true,
    compat: "High-priority thread tuning for competitive input response",
    restart: true,
  },
  {
    id: "kb-debounce-reduce",
    title: "Keyboard debounce latency reduction",
    desc: "Zeroes accessibility keyboard response flags to eliminate keystroke debounce latency",
    group: "advanced",
    risk: "SAFE",
    recommended: true,
    compat: "Mechanical & membrane keyboards",
  },
  {
    id: "queue-mouhid-length",
    title: "Mouhid USB driver packet buffer",
    desc: "Optimizes USB HID mouse class miniport packet queue buffer length",
    group: "advanced",
    risk: "LOW",
    recommended: true,
    compat: "USB HID mouse",
    restart: true,
  },
  {
    id: "queue-kbdhid-length",
    title: "Kbdhid USB driver packet buffer",
    desc: "Optimizes USB HID keyboard class miniport packet queue buffer length",
    group: "advanced",
    risk: "LOW",
    recommended: true,
    compat: "USB HID keyboard",
    restart: true,
  },
  {
    id: "hid-polling-calibrate",
    title: "HID polling interval calibration",
    desc: "Calibrates HID device polling interval queue parameter for consistent input frame pacing",
    group: "advanced",
    risk: "SAFE",
    recommended: true,
    compat: "HID input devices",
  },
  {
    id: "usb-perdevice-suspend",
    title: "Per-device USB selective suspend exemption",
    desc: "Prevents root hub runtime power shifts on active HID device nodes",
    group: "advanced",
    risk: "SAFE",
    recommended: true,
    compat: "USB controllers",
  },
  {
    id: "dwm-input-separation",
    title: "DWM independent input separation quantum",
    desc: "Ensures DWM compositing thread maintains independent rendering quantum for zero-lag hardware cursor response",
    group: "advanced",
    risk: "SAFE",
    recommended: true,
    compat: "Windows DWM composition",
  },
  {
    id: "gamepad-deadzone-linearity",
    title: "DirectInput & XInput controller linearity",
    desc: "Linearizes joystick axis deadzone parameters for precision emulator and PC gamepad response",
    group: "advanced",
    risk: "SAFE",
    recommended: true,
    compat: "DirectInput / XInput controllers",
  },
  {
    id: "ps2-queue",
    title: "PS/2 keyboard buffer",
    desc: "i8042prt buffer size",
    group: "advanced",
    risk: "LOW",
    recommended: true,
    compat: "Not applicable — no PS/2 keyboard on the sample system",
    unsupported: true,
  },
];

/** Items each mode shows. Advanced includes the Normal set plus the advanced options. */
export function itemsForMode(mode: InputMode, items: readonly InputItem[] = INPUT_ITEMS) {
  return mode === "NORMAL" ? items.filter((i) => i.group === "normal") : [...items];
}

/** The computed (read-only) selection of a recommendation-driven mode. */
export function modeSelection(
  mode: Exclude<InputMode, "CUSTOM">,
  items: readonly InputItem[] = INPUT_ITEMS,
): Set<string> {
  return eligibleSelection(itemsForMode(mode, items));
}

/** Why an item is not part of a recommendation-driven mode, or null when it is included. */
export function exclusionReason(i: InputItem): string | null {
  if (isDefaultEligible(i)) return null;
  if (i.unsupported) return "Not applicable";
  if (i.risk === "PROTECTED") return "Protected";
  if (i.done) return "Already applied";
  if (!i.recommended) return "Not recommended";
  return "Excluded";
}

/** Normalizes a stored mode value. */
export const parseInputMode = (raw: unknown): InputMode =>
  raw === "ADVANCED" || raw === "CUSTOM" ? raw : "NORMAL";

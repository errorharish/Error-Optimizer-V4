// Title-bar status: Auto Optimize state + countdown, visible on every page while enabled.
// Informational only — it never navigates or triggers anything.
import { CalendarClock } from "lucide-react";
import { useIsBackendConnected } from "@/lib/eo/nativeBridge";
import { useAutoOptimize, useNow } from "./autoOptimizeStore";
import { STATUS_TONE, autoOnline, engineStatus } from "./AutoOptimizePanel";
import { StatusIndicator } from "./StatusIndicator";
import { formatCountdown } from "@/lib/eo/autoOptimize";

export function AutoChip() {
  const s = useAutoOptimize();
  const now = useNow();
  const status = engineStatus(s);
  const connected = useIsBackendConnected();
  // Same two-state source as the Auto Optimize badges: hidden whenever Auto Optimize is OFFLINE.
  if (!autoOnline(s) && !s.run) return null;
  const text = !connected
    ? "STANDBY"
    : status === "Running"
      ? "RUNNING"
      : status === "Paused"
        ? "PAUSED"
        : status === "Suspended"
          ? "SUSPENDED"
          : s.nextAt && now
            ? formatCountdown(s.nextAt - now)
            : "—";
  return (
    <StatusIndicator
      tone={connected ? STATUS_TONE[status] : "warn"}
      className="eo-st-head"
      icon={<CalendarClock className="h-3.5 w-3.5" />}
      label="AUTO"
      reserve="SUSPENDED"
      title={
        connected
          ? `Auto Optimize ${status}${status === "Enabled" ? `, next run in ${text}` : ""}`
          : "Auto Optimize armed · waiting for backend connection"
      }
    >
      <span className="font-mono tabular-nums">{text}</span>
    </StatusIndicator>
  );
}

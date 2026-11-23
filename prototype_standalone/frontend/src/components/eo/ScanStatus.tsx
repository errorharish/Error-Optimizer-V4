// Shared dashboard scan indicators (V3 Dashboard + V4 Performance Center): one scanning line
// and one data-source badge, so both dashboards show scans the same way.
import { AlertTriangle, Loader2 } from "lucide-react";
import { Badge, DemoBadge } from "./kit";
import type { SystemScan } from "./useSystemScan";

export function ScanStatusLine({ scan }: { scan: SystemScan }) {
  if (scan.phase === "scanning") {
    return (
      <span
        role="status"
        aria-live="polite"
        className="flex items-center gap-1.5 text-[10.5px] font-semibold text-cyan"
      >
        <Loader2 aria-hidden="true" className="h-3 w-3 animate-spin" />
        {scan.stage}
        <span className="tabular-nums text-subtle">{Math.round(scan.progress)}%</span>
      </span>
    );
  }
  if (scan.phase === "error") {
    return (
      <span role="status" className="flex items-center gap-1.5 text-[10.5px] font-semibold text-warning">
        <AlertTriangle aria-hidden="true" className="h-3 w-3" />
        {scan.error}
      </span>
    );
  }
  return <span className="text-[10.5px] font-semibold text-subtle">Not scanned yet</span>;
}

export function ScanSourceBadge({ scan }: { scan: SystemScan }) {
  if (scan.phase === "scanning") return <Badge tone="cyan">SCANNING</Badge>;
  if (scan.phase === "error") return <Badge tone="warning">NO SCAN RESULT</Badge>;
  if (scan.result?.source === "native") return <Badge tone="success">MEASURED</Badge>;
  return <Badge tone="muted">NOT SCANNED</Badge>;
}

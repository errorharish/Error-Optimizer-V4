// Compact building blocks shared by every Settings & Control Center section: setting rows
// (title, one-line description, optional tip/status, control on the right) and section cards.
// Looks live in the `.eo-set-*` block of styles.css.
import type { ReactNode } from "react";
import { Info } from "lucide-react";
import { Card, SectionTitle } from "./kit";
import { StatusIndicator, statusTone } from "./StatusIndicator";
import { cn } from "@/lib/utils";

export function SettingRow({
  id,
  title,
  desc,
  tip,
  status,
  children,
  disabled,
}: {
  /** Matches SETTINGS_INDEX ids so search can open and highlight the row. */
  id?: string | undefined;
  title: string;
  desc: string;
  tip?: string | undefined;
  status?: ReactNode | undefined;
  children: ReactNode;
  disabled?: boolean | undefined;
}) {
  return (
    <div className={cn("eo-set-row", disabled && "eo-set-row-off")} data-setting-id={id}>
      <div className="min-w-0">
        <div className="flex min-w-0 items-center gap-1.5">
          <span className="truncate text-[13px] font-semibold text-foreground">{title}</span>
          {tip && (
            <span className="eo-set-tip" title={tip} aria-label={tip} role="img">
              <Info aria-hidden="true" className="h-3 w-3" />
            </span>
          )}
          {status}
        </div>
        <div className="text-[11.5px] leading-snug text-muted-foreground">{desc}</div>
      </div>
      <div className="flex min-w-0 flex-wrap items-center justify-end gap-2">{children}</div>
    </div>
  );
}

export function SettingsCard({
  id,
  title,
  sub,
  right,
  children,
  className,
}: {
  id?: string | undefined;
  title: string;
  sub?: string | undefined;
  right?: ReactNode | undefined;
  children: ReactNode;
  className?: string | undefined;
}) {
  return (
    <div data-setting-id={id} className={cn("eo-set-card min-w-0", className)}>
      <Card className="h-full">
        <SectionTitle title={title} sub={sub} right={right} />
        {children}
      </Card>
    </div>
  );
}

export function StatusDot({ tone, children }: { tone: "success" | "warning" | "danger" | "muted" | "cyan"; children: ReactNode }) {
  return (
    <StatusIndicator tone={statusTone(tone)} size="sm">
      {children}
    </StatusIndicator>
  );
}

// Shared canonical dropdown filter for all ERROR OPTIMIZER V4 optimization catalogs.
// Standardizes filtering to:
//   1. Eligible / Actionable
//   2. Optimized
//   3. Ineligible
//   4. All
// Replaces separate quick-filter buttons (ACTIONABLE, ELIGIBLE, OPTIMIZED, INELIGIBLE, ALL).
import { useMemo } from "react";
import { SlidersHorizontal } from "lucide-react";
import { PremiumSelect, type DropdownOption } from "./dropdown";

export type CatalogFilterScope = "ACTIONABLE" | "OPTIMIZED" | "INELIGIBLE" | "ALL";

export function normalizeFilterScope(scope: string): CatalogFilterScope {
  const upper = (scope || "").toUpperCase().trim();
  if (upper === "OPTIMIZED") return "OPTIMIZED";
  if (upper === "INELIGIBLE") return "INELIGIBLE";
  if (upper === "ALL") return "ALL";
  return "ACTIONABLE"; // Default covers "ACTIONABLE", "ELIGIBLE", "ELIGIBLE_ACTIONABLE", "ELIGIBLE / ACTIONABLE", etc.
}

export interface CatalogFilterDropdownProps {
  value: string;
  onChange: (value: CatalogFilterScope) => void;
  counts?: {
    actionable?: number;
    optimized?: number;
    ineligible?: number;
    all?: number;
  };
  className?: string;
  disabled?: boolean;
  size?: "sm" | "md";
  label?: string;
}

export function CatalogFilterDropdown({
  value,
  onChange,
  counts,
  className,
  disabled,
  size = "md",
  label = "Catalog filter",
}: CatalogFilterDropdownProps) {
  const normVal = normalizeFilterScope(value);

  const options: DropdownOption<CatalogFilterScope>[] = useMemo(
    () => [
      {
        value: "ACTIONABLE",
        label: (
          <span className="flex items-center justify-between w-full gap-2 pr-1">
            <span className="font-medium text-foreground">Eligible / Actionable</span>
            {counts?.actionable !== undefined && (
              <span className="eo-dd-count ml-auto">{counts.actionable}</span>
            )}
          </span>
        ),
      },
      {
        value: "OPTIMIZED",
        label: (
          <span className="flex items-center justify-between w-full gap-2 pr-1">
            <span className="font-medium text-foreground">Optimized</span>
            {counts?.optimized !== undefined && (
              <span className="eo-dd-count ml-auto">{counts.optimized}</span>
            )}
          </span>
        ),
      },
      {
        value: "INELIGIBLE",
        label: (
          <span className="flex items-center justify-between w-full gap-2 pr-1">
            <span className="font-medium text-foreground">Ineligible</span>
            {counts?.ineligible !== undefined && (
              <span className="eo-dd-count ml-auto">{counts.ineligible}</span>
            )}
          </span>
        ),
      },
      {
        value: "ALL",
        label: (
          <span className="flex items-center justify-between w-full gap-2 pr-1">
            <span className="font-medium text-foreground">All</span>
            {counts?.all !== undefined && (
              <span className="eo-dd-count ml-auto">{counts.all}</span>
            )}
          </span>
        ),
      },
    ],
    [counts?.actionable, counts?.optimized, counts?.ineligible, counts?.all],
  );

  const currentLabel =
    normVal === "ACTIONABLE"
      ? "Eligible / Actionable"
      : normVal === "OPTIMIZED"
        ? "Optimized"
        : normVal === "INELIGIBLE"
          ? "Ineligible"
          : "All";

  const currentCount =
    normVal === "ACTIONABLE"
      ? counts?.actionable
      : normVal === "OPTIMIZED"
        ? counts?.optimized
        : normVal === "INELIGIBLE"
          ? counts?.ineligible
          : counts?.all;

  const display = (
    <span className="flex items-center justify-between w-full gap-2">
      <span className="truncate font-medium text-foreground">{currentLabel}</span>
      {currentCount !== undefined && (
        <span className="eo-dd-count shrink-0">{currentCount}</span>
      )}
    </span>
  );

  return (
    <PremiumSelect<CatalogFilterScope>
      value={normVal}
      onChange={onChange}
      options={options}
      label={label}
      icon={<SlidersHorizontal aria-hidden="true" className="eo-dd-icon" />}
      display={display}
      className={className}
      disabled={disabled}
      size={size}
    />
  );
}

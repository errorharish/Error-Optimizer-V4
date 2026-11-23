// Page slug → page component. All page modules are statically imported so that
// page navigation is synchronous and instant, avoiding any asynchronous chunks or blank frames.
import type { ReactNode } from "react";
import { DashboardPage } from "./pages/Dashboard";
import { OneClickPage } from "./pages/OneClick";
import { TierPage } from "./pages/Tier";
import {
  InputOptimizerPage,
  SystemInfoPage,
  RegistryTweakPage,
  RegistryValuesPage,
  RegistryValuesRamPage,
  GpuRegistryValuesPage,
} from "./pages/SystemPages";
import { AutoOptimizePage } from "./pages/AutoOptimize";
import {
  NetworkPage,
  ProcessReductionPage,
  ServiceManagerPage,
  StartupManagerPage,
  StoragePage,
  ToolsPage,
} from "./pages/ManagerPages";
import {
  AboutPage,
  AiOptimizationPage,
  BackupRestorePage,
  PerformanceTuningPage,
  SettingsPage,
} from "./pages/MiscPages";
import { CoreOptimizationPage } from "./pages/V4Pages";

export const PAGE_COMPONENTS: Record<string, () => ReactNode> = {
  "": () => <DashboardPage />,
  "one-click": () => <OneClickPage />,
  "input-optimizer": () => <InputOptimizerPage />,
  "system-info": () => <SystemInfoPage />,
  "registry-tweak": () => <RegistryTweakPage />,
  "registry-values": () => <RegistryValuesPage />,
  "registry-values-ram": () => <RegistryValuesRamPage />,
  "gpu-registry-values": () => <GpuRegistryValuesPage />,
  "startup-manager": () => <StartupManagerPage />,
  "service-manager": () => <ServiceManagerPage />,
  network: () => <NetworkPage />,
  storage: () => <StoragePage />,
  tools: () => <ToolsPage />,
  "process-reduction": () => <ProcessReductionPage />,
  "auto-optimize": () => <AutoOptimizePage />,
  normal: () => <TierPage slug="normal" />,
  pro: () => <TierPage slug="pro" />,
  ultimate: () => <TierPage slug="ultimate" />,
  debloat: () => <TierPage slug="debloat" />,
  "bios-safe": () => <TierPage slug="bios-safe" />,
  "core-optimization": () => <CoreOptimizationPage />,
  "ai-optimization": () => <AiOptimizationPage />,
  "backup-restore": () => <BackupRestorePage />,
  settings: () => <SettingsPage />,
  about: () => <AboutPage />,
  "performance-tuning": () => <PerformanceTuningPage />,
  "max-performance": () => <TierPage slug="max-performance" />,
  "v4-max-performance": () => <CoreOptimizationPage />,
  "v4-dashboard": () => <DashboardPage />,
};

/** All page modules are statically imported in the desktop bundle; navigation is synchronous and instant. */
export function warmPageModules(): void {
  // Direct static imports - no async warming needed.
}

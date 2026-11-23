// Page registry for ERROR OPTIMIZER — VERSION V4 unified application.
export type Suite = "V4";

export interface PageDef {
  slug: string; // URL slug in this prototype
  routeKey: string; // original NavigateCommand CommandParameter
  title: string;
  tooltip: string; // original dock ToolTip
  view: string; // original XAML view
  viewModel: string;
  suite: Suite;
  inDock: boolean; // reachable from GlassSidebarDock
  dockGroup?: number; // separator groups in dock order
  icon: string; // lucide icon name
  description: string;
}

export const PAGES: PageDef[] = [
  // ── Dock group 1
  {
    slug: "",
    routeKey: "Dashboard",
    title: "Dashboard",
    tooltip: "Dashboard",
    view: "DashboardView.xaml",
    viewModel: "DashboardViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 1,
    icon: "LayoutDashboard",
    description: "System overview, optimization score and automatic maintenance.",
  },
  {
    slug: "input-optimizer",
    routeKey: "InputOptimizer",
    title: "Input Optimizer",
    tooltip: "Input Optimization",
    view: "InputOptimizerView.xaml",
    viewModel: "InputOptimizerViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 1,
    icon: "MousePointer2",
    description: "Pointer precision, repeat rates, timer resolution and jitter measurement.",
  },
  {
    slug: "system-info",
    routeKey: "SystemInfo",
    title: "System Telemetry",
    tooltip: "System Telemetry",
    view: "SystemInfoView.xaml",
    viewModel: "SystemInfoViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 1,
    icon: "Cpu",
    description: "Real-time hardware, Windows architecture and system health diagnostics.",
  },
  // ── Dock group 2
  {
    slug: "registry-tweak",
    routeKey: "RegistryTweak",
    title: "Registry Intelligence",
    tooltip: "Registry Optimization",
    view: "RegistryTweakView.xaml",
    viewModel: "RegistryTweakViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 2,
    icon: "Binary",
    description: "Registry optimization categorized by CPU scheduling, gaming, memory and more.",
  },
  {
    slug: "registry-values",
    routeKey: "RegistryValues",
    title: "Registry Values",
    tooltip: "Hardware Registry Values",
    view: "RegistryValuesView.xaml",
    viewModel: "RegistryValuesViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 2,
    icon: "SlidersHorizontal",
    description: "Hardware-aware RAM and GPU registry value configuration.",
  },
  // ── Dock group 3
  {
    slug: "startup-manager",
    routeKey: "StartupManager",
    title: "Startup Manager",
    tooltip: "Startup Manager",
    view: "StartupManagerView.xaml",
    viewModel: "StartupManagerViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 3,
    icon: "Rocket",
    description: "Manage startup applications and background tasks.",
  },
  {
    slug: "network",
    routeKey: "Network",
    title: "Network Boost",
    tooltip: "Network Optimization",
    view: "NetworkView.xaml",
    viewModel: "NetworkViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 3,
    icon: "Wifi",
    description: "Optimize the Windows network stack for lower latency.",
  },
  {
    slug: "storage",
    routeKey: "Storage",
    title: "Storage Optimizer",
    tooltip: "Storage Optimization",
    view: "StorageView.xaml",
    viewModel: "StorageViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 3,
    icon: "HardDrive",
    description: "Analyze storage, find unnecessary files, and safely manage disk space.",
  },
  {
    slug: "tools",
    routeKey: "Tools",
    title: "System Tools",
    tooltip: "System Tools",
    view: "ToolsView.xaml",
    viewModel: "ToolsViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 3,
    icon: "Wrench",
    description: "Unified toolbox — 16 diagnostics, repair and cleanup tools.",
  },
  // ── Dock group 4
  {
    slug: "normal",
    routeKey: "Normal",
    title: "Normal Optimization",
    tooltip: "Normal Profile",
    view: "TierView.xaml",
    viewModel: 'TierViewModel("Normal")',
    suite: "V4",
    inDock: true,
    dockGroup: 4,
    icon: "ShieldCheck",
    description: "Safe optimizations for everyday use.",
  },
  {
    slug: "pro",
    routeKey: "Pro",
    title: "Pro Optimization",
    tooltip: "Pro Profile",
    view: "TierView.xaml",
    viewModel: 'TierViewModel("Pro")',
    suite: "V4",
    inDock: true,
    dockGroup: 4,
    icon: "Gauge",
    description: "Advanced optimizations for power users.",
  },
  {
    slug: "ultimate",
    routeKey: "Ultimate",
    title: "Ultimate Optimization",
    tooltip: "Ultimate Profile",
    view: "TierView.xaml",
    viewModel: 'TierViewModel("Ultimate")',
    suite: "V4",
    inDock: true,
    dockGroup: 4,
    icon: "Flame",
    description: "Maximum system overhaul. High risk.",
  },
  {
    slug: "max-performance",
    routeKey: "MaxPerformance",
    title: "Maximum Performance",
    tooltip: "Maximum Performance Profile",
    view: "TierView.xaml",
    viewModel: 'TierViewModel("MaximumPerformance")',
    suite: "V4",
    inDock: true,
    dockGroup: 4,
    icon: "Zap",
    description: "Complete machine-specific performance optimization. Combines Ultimate + CPU + GPU + power policy + memory + storage.",
  },
  {
    slug: "debloat",
    routeKey: "Debloat",
    title: "Debloat",
    tooltip: "Debloat Engine",
    view: "TierView.xaml",
    viewModel: 'TierViewModel("Debloat")',
    suite: "V4",
    inDock: true,
    dockGroup: 4,
    icon: "PackageX",
    description: "Removes telemetry, diagnostic tracking, and background Windows bloatware.",
  },
  {
    slug: "bios-safe",
    routeKey: "BiosSafe",
    title: "BIOS Safe",
    tooltip: "BIOS Safe Engine",
    view: "TierView.xaml",
    viewModel: 'TierViewModel("BiosSafe")',
    suite: "V4",
    inDock: true,
    dockGroup: 4,
    icon: "CircuitBoard",
    description: "Safe firmware-aware analysis and UEFI hardware optimization.",
  },
  {
    slug: "core-optimization",
    routeKey: "CoreOptimization",
    title: "Core Optimization",
    tooltip: "Core Optimization — Host latency reduction and core footprint convergence",
    view: "CoreOptimizationView.xaml",
    viewModel: "CoreOptimizationViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 4,
    icon: "Microchip",
    description: "Multi-pass latency reduction, resource management and core convergence.",
  },
  {
    slug: "ai-optimization",
    routeKey: "AiOptimization",
    title: "AI Optimization",
    tooltip: "AI Optimization",
    view: "AiOptimizationView.xaml",
    viewModel: "AiOptimizationViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 4,
    icon: "BrainCircuit",
    description: "Workload optimization, process reduction and AI power plans.",
  },
  // ── Dock group 5
  {
    slug: "backup-restore",
    routeKey: "BackupRestore",
    title: "Backup & Restore",
    tooltip: "Backup & Restore",
    view: "BackupRestoreView.xaml",
    viewModel: "BackupRestoreViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 5,
    icon: "History",
    description: "Universal transaction engine, profile containers and restore points.",
  },
  {
    slug: "settings",
    routeKey: "Settings",
    title: "Settings",
    tooltip: "Settings & Preferences",
    view: "SettingsView.xaml",
    viewModel: "SettingsViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 5,
    icon: "Settings",
    description: "Startup, appearance, performance and safety guardrails.",
  },
  {
    slug: "about",
    routeKey: "About",
    title: "About",
    tooltip: "About & Diagnostics",
    view: "AboutView.xaml",
    viewModel: "AboutViewModel",
    suite: "V4",
    inDock: true,
    dockGroup: 5,
    icon: "Info",
    description: "What Error Optimizer changes, profiles and the optimization catalog.",
  },

  // ── Non-dock routes (reachable via in-page links and quick actions)
  {
    slug: "one-click",
    routeKey: "OneClick",
    title: "1-Click Optimization",
    tooltip: "Dedicated 1-Click workflow",
    view: "OneClickOptimizationView.xaml",
    viewModel: "OneClickOptimizationViewModel",
    suite: "V4",
    inDock: false,
    icon: "MousePointerClick",
    description: "Find safe performance improvements and apply them with verification.",
  },
  {
    slug: "auto-optimize",
    routeKey: "AutoOptimize",
    title: "Auto Optimize",
    tooltip: "Gaming Mode / Auto Optimize",
    view: "AutoOptimizeView.xaml",
    viewModel: "AutoOptimizeViewModel",
    suite: "V4",
    inDock: false,
    icon: "CalendarClock",
    description: "Schedule safe recurring maintenance for selected cleanup actions.",
  },
  {
    slug: "process-reduction",
    routeKey: "ProcessReduction",
    title: "Process Intelligence",
    tooltip: "RAM Optimizer",
    view: "ProcessReductionView.xaml",
    viewModel: "ProcessReductionViewModel",
    suite: "V4",
    inDock: false,
    icon: "Activity",
    description: "Safe background process management.",
  },
  {
    slug: "service-manager",
    routeKey: "ServiceManager",
    title: "Service Manager",
    tooltip: "Services Manager",
    view: "ServiceManagerView.xaml",
    viewModel: "ServiceManagerViewModel",
    suite: "V4",
    inDock: false,
    icon: "Server",
    description: "Inspect, monitor and optimize Windows background services.",
  },
  {
    slug: "registry-values-ram",
    routeKey: "RegistryValuesRam",
    title: "RAM Registry Values",
    tooltip: "RAM Registry Values",
    view: "RegistryValuesRamView.xaml",
    viewModel: "RegistryValuesRamViewModel",
    suite: "V4",
    inDock: false,
    icon: "MemoryStick",
    description: "Svchost split threshold configuration.",
  },
  {
    slug: "gpu-registry-values",
    routeKey: "GpuRegistryValues",
    title: "GPU Registry Values",
    tooltip: "GPU Tuning",
    view: "GpuRegistryValuesView.xaml",
    viewModel: "GpuRegistryValuesViewModel",
    suite: "V4",
    inDock: false,
    icon: "MonitorCog",
    description: "Hardware-aware GPU registry configuration.",
  },
  {
    slug: "performance-tuning",
    routeKey: "PerformanceTuning",
    title: "Performance Tuning",
    tooltip: "CPU / GPU Performance Tuning",
    view: "PerformanceTuningView.xaml",
    viewModel: "PerformanceTuningViewModel",
    suite: "V4",
    inDock: false,
    icon: "Thermometer",
    description: "Optional CPU and GPU tuning for supported hardware.",
  },
];

export const pageBySlug = (slug: string) => PAGES.find((p) => p.slug === slug);
export const pageByKey = (key: string) => PAGES.find((p) => p.routeKey === key);
export const hrefFor = (slug: string) => (slug ? `/${slug}` : "/");

export const V4_TABS = ["core-optimization"] as const;

/** References that could not be fully determined from the uploads. */
export const MISSING_REFERENCES: { area: string; detail: string }[] = [
  {
    area: "Auto Optimize task list",
    detail:
      "AutoOptimizeView.xaml only defines Clean Temp Files and Free Standby Memory. The other tasks (crash dumps, browser caches per browser, Recycle Bin, DirectX shader cache, DNS flush) are operations that already exist on the Storage and Tools pages. Per-browser cache cleanup and the 1-minute–24-hour schedule follow the user's brief; their native engine support must be confirmed in the C# source.",
  },
  {
    area: "Overall Optimization Score",
    detail:
      "Overall Optimization Score is computed from genuine hardware discovery, system telemetry, and verified Core Optimization engine states.",
  },
  {
    area: "Dock icon glyphs",
    detail:
      "GlassSidebarDock passes Tag to controls:NavigationIcon (NavigationIcon.cs). The glyph geometry mapping was not inspected in source form here; icons in this prototype are stand-ins.",
  },
  {
    area: "ItemsControl item templates",
    detail:
      "Many lists (CuratedGroups, TopRecommendations, VisibleItems, FilteredTools…) bind to ViewModel collections. Row layouts are reproduced from the in-file DataTemplates; row contents are sample data from the manifest, not live values.",
  },
  {
    area: "Dashboard layout",
    detail:
      "DashboardView.xaml contains 5 telemetry tiles, a telemetry chart + Optimization Status, Hardware Information table, Quick Actions and Recent Activity; the build report describes a HealthGauge + 1-Click hero card. By explicit user direction the prototype now leads with an Overall Optimization Score + the Auto Optimize maintenance panel, keeps compact tiles plus a one-row System Information strip, removes Optimization Status and Recent Activity, and moves the telemetry chart to System Telemetry. The hardware table's content remains in the tiles and on System Telemetry.",
  },
  {
    area: "DebloatView / BiosSafeView",
    detail:
      "Standalone XAML exists, but MainViewModel routes Debloat and BiosSafe to TierViewModel → TierView. The standalone views are documented in the blueprint but not routed.",
  },
  {
    area: "Unrouted pages in dock",
    detail:
      "OneClick, AutoOptimize, ProcessReduction, ServiceManager, and PerformanceTuning have no dock entry. Their entry points are in-page links only (Dashboard quick actions, Registry Values tabs).",
  },
  {
    area: "Original reference files",
    detail:
      "ERROR_OPTIMIZER_UI_REFERENCE.zip, the source ZIP, the backend manifest and the build report are not attached to this project copy. The registry and layouts were kept from the original audit; they could not be re-checked against the XAML here.",
  },
  {
    area: "Stepper and loading skeletons",
    detail:
      "The workflow stepper (One-Click, run dialog) and skeleton loading rows are prototype additions with no XAML source. The blueprint suggests WPF equivalents but they are not original controls.",
  },
  {
    area: "3D depth HIGH (DEEP)",
    detail:
      "Settings offers LOW / MEDIUM / HIGH depth. Only LOW (no tilt) differs visually in the prototype; the original HIGH behaviour is not documented in the supplied material.",
  },
  {
    area: "Animated backgrounds",
    detail:
      "MetallicBackground / AuroraBackground / parallax starfield are C# render controls; only their static appearance is approximated.",
  },
];

/** Canonical dock navigation definitions for ERROR OPTIMIZER V4. */
export const V4_NAV: { slug: string; label: string; group: string }[] = PAGES.filter((p) => p.inDock).map((p) => ({
  slug: p.slug,
  label: p.title,
  group: String(p.dockGroup ?? 0),
}));

/** Pages shared throughout the suite. */
export const SHARED_SLUGS = ["settings", "backup-restore", "about"] as const;
export const isSharedSlug = (slug: string) => (SHARED_SLUGS as readonly string[]).includes(slug);

export const V4_NAV_BOTTOM: { slug: (typeof SHARED_SLUGS)[number]; label: string }[] = [
  { slug: "settings", label: "SETTINGS" },
  { slug: "backup-restore", label: "BACKUP" },
  { slug: "about", label: "ABOUT" },
];

/** Former V3/V4 destinations folded into the unified structure; old links redirect here. */
export const V4_REDIRECTS: Record<string, string> = {
  "v4-dashboard": "",
  "v4-max-performance": "core-optimization",
  "v4-core-optimizer": "core-optimization",
  "emulator-optimization": "",
  "emulator-debloater": "",
  "emulator-advanced": "",
  "emulator-modules": "",
  "emulator-deployer": "",
  "low-end-steam": "",
};

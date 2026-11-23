// Settings & Control Center rules: categories, saved preferences (startup, AI policies,
// performance, safety, logs, motion), saved-value cleanup and the settings search index.
// Pure (no React, no storage). Themes live in theme.ts.

export const SETTINGS_CATEGORIES = [
  { id: "overview", label: "Overview", hint: "Current configuration at a glance" },
  { id: "appearance", label: "Appearance & Themes", hint: "Theme presets and custom colours" },
  { id: "effects", label: "Animations & Motion", hint: "UI animations, page transitions and reduced motion" },
  { id: "performance", label: "Performance & Telemetry", hint: "Resource budget and update rate" },
  { id: "startup", label: "Startup & Automation", hint: "Windows startup and AI engine policies" },
  { id: "safety", label: "Safety & Recovery", hint: "Confirmations, backups and restore" },
  { id: "logs", label: "Logs & Diagnostics", hint: "Execution logs and retention" },
  { id: "system", label: "System Information", hint: "Version, build and service status" },
] as const;
export type SettingsCategory = (typeof SETTINGS_CATEGORIES)[number]["id"];

export const PERFORMANCE_MODES = [
  { id: "auto", label: "Auto", hint: "Adjusts the monitoring budget to your PC automatically" },
  { id: "low", label: "Low Resource (Smooth)", hint: "Lightest monitoring for the smoothest feel" },
  { id: "balanced", label: "Balanced", hint: "Good detail with modest resource use" },
  { id: "high", label: "High Telemetry", hint: "Most detail, uses a little more CPU" },
] as const;
export type PerformanceMode = (typeof PERFORMANCE_MODES)[number]["id"];

export const MONITOR_INTERVALS = [1, 2, 3, 5, 10] as const;
export type MonitorInterval = (typeof MONITOR_INTERVALS)[number];

export const LOG_RETENTION_DAYS = [3, 7, 14, 30] as const;
export type LogRetention = (typeof LOG_RETENTION_DAYS)[number];

export const DEPTH_OPTIONS = ["LOW (2D)", "MEDIUM (3D)", "HIGH (DEEP)"] as const;
export type DepthOption = (typeof DEPTH_OPTIONS)[number];

export const AI_POLICIES = [
  { id: "powerPlan", label: "AI Power Plan Auto-Enable", hint: "Turn on the optimized power plan when you sign in to Windows" },
  { id: "workload", label: "AI Workload Optimization", hint: "Start safe, watch-only workload monitoring at boot" },
  { id: "autoOptimize", label: "Smart Auto Optimize", hint: "Let the maintenance scheduler resume after restart" },
] as const;
export type AiPolicy = (typeof AI_POLICIES)[number]["id"];

export const SAFETY_KEYS = ["confirmMedium", "confirmHigh", "autoBackup"] as const;
export type SafetyKey = (typeof SAFETY_KEYS)[number];

export type Suite = "V3" | "V4";

export interface AppSettings {
  startWithWindows: boolean;
  startMinimized: boolean;
  policies: Record<AiPolicy, boolean>;
  aiEnabled: boolean;
  performanceMode: PerformanceMode;
  monitorIntervalSec: MonitorInterval;
  confirmMedium: boolean;
  confirmHigh: boolean;
  autoBackup: boolean;
  detailedLogs: boolean;
  logRetentionDays: LogRetention;
  uiAnimations: boolean;
  pageTransitions: boolean;
  reducedMotion: boolean;
  /** Lightweight rendering for low-end PCs: no glass blur or decorative entrance motion. */
  lightweight: boolean;
  depth: DepthOption;
  /** Last V3 / V4 suite the user worked in (shown in the Overview; never reset). */
  lastSuite: Suite;

  /* ── Unified Auto Optimize & Global Settings ── */
  autoOptimizationEnabled: boolean;
  aiOptimizationEnabled: boolean;
  runInSystemTray: boolean;
  backgroundServiceEnabled: boolean;
  adaptiveHardwareDetectionEnabled: boolean;
  automaticScanEnabled: boolean;
  automaticExecutionEnabled: boolean;
  monitoringInterval: number;
  selectedOptimizationProfile: string;
  approvedOptimizationCategories: string[];
  userConsentVersion: string;
  lastSuccessfulScan: string;
  lastExecutionStatus: string;
  configVersion: number;
}

export const DEFAULT_SETTINGS: AppSettings = {
  startWithWindows: false,
  startMinimized: false,
  policies: { powerPlan: false, workload: false, autoOptimize: false },
  aiEnabled: true,
  performanceMode: "auto",
  monitorIntervalSec: 3,
  confirmMedium: true,
  confirmHigh: true,
  autoBackup: true,
  detailedLogs: false,
  logRetentionDays: 7,
  uiAnimations: true,
  pageTransitions: true,
  reducedMotion: false,
  lightweight: false,
  depth: "MEDIUM (3D)",
  lastSuite: "V3",

  autoOptimizationEnabled: true,
  aiOptimizationEnabled: true,
  runInSystemTray: true,
  backgroundServiceEnabled: true,
  adaptiveHardwareDetectionEnabled: true,
  automaticScanEnabled: true,
  automaticExecutionEnabled: false,
  monitoringInterval: 60,
  selectedOptimizationProfile: "Balanced",
  approvedOptimizationCategories: ["TEMP", "MEMORY", "RECYCLE", "BROWSER"],
  userConsentVersion: "1.0",
  lastSuccessfulScan: "",
  lastExecutionStatus: "Ready",
  configVersion: 1,
};

/** Factory defaults; the last used suite is kept because it is not a preference. */
export function restoreDefaults(s: AppSettings): AppSettings {
  return { ...DEFAULT_SETTINGS, lastSuite: s.lastSuite };
}

const bool = (v: unknown, d: boolean) => (typeof v === "boolean" ? v : d);
const oneOf = <T>(list: readonly T[], v: unknown, d: T): T =>
  (list as readonly unknown[]).includes(v) ? (v as T) : d;

export function sanitizeSettings(raw: unknown): AppSettings {
  const r = (raw && typeof raw === "object" ? raw : {}) as Record<string, unknown>;
  const p = (r["policies"] && typeof r["policies"] === "object" ? r["policies"] : {}) as Record<string, unknown>;
  const d = DEFAULT_SETTINGS;
  const policies = { ...d.policies };
  for (const a of AI_POLICIES) policies[a.id] = bool(p[a.id], d.policies[a.id]);
  return {
    startWithWindows: bool(r["startWithWindows"], d.startWithWindows),
    startMinimized: bool(r["startMinimized"], d.startMinimized),
    policies,
    aiEnabled: bool(r["aiEnabled"], d.aiEnabled),
    performanceMode: oneOf(PERFORMANCE_MODES.map((m) => m.id), r["performanceMode"], d.performanceMode),
    monitorIntervalSec: oneOf(MONITOR_INTERVALS, r["monitorIntervalSec"], d.monitorIntervalSec),
    confirmMedium: bool(r["confirmMedium"], d.confirmMedium),
    confirmHigh: bool(r["confirmHigh"], d.confirmHigh),
    autoBackup: bool(r["autoBackup"], d.autoBackup),
    detailedLogs: bool(r["detailedLogs"], d.detailedLogs),
    logRetentionDays: oneOf(LOG_RETENTION_DAYS, r["logRetentionDays"], d.logRetentionDays),
    uiAnimations: bool(r["uiAnimations"], d.uiAnimations),
    pageTransitions: bool(r["pageTransitions"], d.pageTransitions),
    reducedMotion: bool(r["reducedMotion"], d.reducedMotion),
    lightweight: bool(r["lightweight"], d.lightweight),
    depth: oneOf(DEPTH_OPTIONS, r["depth"], d.depth),
    lastSuite: oneOf(["V3", "V4"] as const, r["lastSuite"], d.lastSuite),

    autoOptimizationEnabled: bool(r["autoOptimizationEnabled"], d.autoOptimizationEnabled),
    aiOptimizationEnabled: bool(r["aiOptimizationEnabled"], bool(r["aiEnabled"], d.aiOptimizationEnabled)),
    runInSystemTray: bool(r["runInSystemTray"], d.runInSystemTray),
    backgroundServiceEnabled: bool(r["backgroundServiceEnabled"], d.backgroundServiceEnabled),
    adaptiveHardwareDetectionEnabled: bool(r["adaptiveHardwareDetectionEnabled"], d.adaptiveHardwareDetectionEnabled),
    automaticScanEnabled: bool(r["automaticScanEnabled"], d.automaticScanEnabled),
    automaticExecutionEnabled: bool(r["automaticExecutionEnabled"], d.automaticExecutionEnabled),
    monitoringInterval: typeof r["monitoringInterval"] === "number" ? (r["monitoringInterval"] as number) : d.monitoringInterval,
    selectedOptimizationProfile: typeof r["selectedOptimizationProfile"] === "string" ? (r["selectedOptimizationProfile"] as string) : d.selectedOptimizationProfile,
    approvedOptimizationCategories: Array.isArray(r["approvedOptimizationCategories"]) ? (r["approvedOptimizationCategories"] as string[]) : d.approvedOptimizationCategories,
    userConsentVersion: typeof r["userConsentVersion"] === "string" ? (r["userConsentVersion"] as string) : d.userConsentVersion,
    lastSuccessfulScan: typeof r["lastSuccessfulScan"] === "string" ? (r["lastSuccessfulScan"] as string) : d.lastSuccessfulScan,
    lastExecutionStatus: typeof r["lastExecutionStatus"] === "string" ? (r["lastExecutionStatus"] as string) : d.lastExecutionStatus,
    configVersion: typeof r["configVersion"] === "number" ? (r["configVersion"] as number) : d.configVersion,
  };
}

/** "protected" only while every safeguard is on. */
export function safetyStatus(s: AppSettings): "protected" | "reduced" {
  return SAFETY_KEYS.every((k) => s[k]) ? "protected" : "reduced";
}

/** Turning a safeguard OFF asks for confirmation first; turning it on never does. */
export const needsSafetyConfirm = (key: SafetyKey, next: boolean) =>
  SAFETY_KEYS.includes(key) && next === false;

export const performanceLabel = (m: PerformanceMode) =>
  PERFORMANCE_MODES.find((x) => x.id === m)?.label ?? "Auto";

export const intervalLabel = (n: MonitorInterval) =>
  n === 1 ? "1 second (Fast)" : n === 3 ? "3 seconds (Default)" : n === 10 ? "10 seconds (Eco)" : `${n} seconds`;

export const retentionLabel = (n: LogRetention) => (n === 7 ? "7 Days (Default)" : `${n} Days`);

/* ── Settings search ── */
export interface SettingEntry {
  /** Anchor id of the setting row (data-setting-id). */
  id: string;
  label: string;
  category: SettingsCategory;
  keywords?: string;
}

export const SETTINGS_INDEX: readonly SettingEntry[] = [
  { id: "overview-status", label: "Configuration overview", category: "overview", keywords: "summary status" },
  { id: "restore-defaults", label: "Restore Defaults", category: "overview", keywords: "reset factory" },
  { id: "theme-active", label: "Active theme", category: "appearance", keywords: "apply restore previous reset default live preview" },
  { id: "theme-classic", label: "Theme Preset", category: "appearance", keywords: "classic custom error default midnight blue cyber cyan crimson red emerald green purple neon royal violet arctic ice amber gold obsidian graphite ocean teal rose pink color colour" },
  { id: "theme-colors", label: "Manual Color Customization", category: "appearance", keywords: "custom colors colours accent background surface text border success warning danger hex save custom theme" },
  { id: "display-depth", label: "3D Spatial Depth", category: "appearance", keywords: "advanced display elevation tilt" },
  { id: "fx-ui", label: "UI Animations", category: "effects", keywords: "motion hover press" },
  { id: "fx-page", label: "Page Transitions", category: "effects", keywords: "fade motion" },
  { id: "fx-reduced", label: "Reduce Motion", category: "effects", keywords: "accessibility reduced motion" },
  { id: "fx-lite", label: "Lightweight Rendering", category: "effects", keywords: "low end pc performance blur glass fps smooth lite" },
  { id: "perf-mode", label: "System Performance Mode", category: "performance", keywords: "auto low resource smooth balanced high telemetry" },
  { id: "perf-interval", label: "Monitor Update Interval", category: "performance", keywords: "polling refresh seconds" },
  { id: "startup-windows", label: "Start with Windows", category: "startup", keywords: "boot logon" },
  { id: "startup-minimized", label: "Start Minimized", category: "startup", keywords: "background tray" },
  { id: "startup-status", label: "Startup Status", category: "startup", keywords: "refresh status registration" },
  { id: "policy-powerPlan", label: "AI Power Plan Auto-Enable", category: "startup", keywords: "power plan" },
  { id: "policy-workload", label: "AI Workload Optimization", category: "startup", keywords: "workload" },
  { id: "policy-autoOptimize", label: "Smart Auto Optimize", category: "startup", keywords: "scheduler maintenance" },
  { id: "safety-confirmMedium", label: "Confirm Medium Risk", category: "safety", keywords: "prompt dialog" },
  { id: "safety-confirmHigh", label: "Confirm High Risk", category: "safety", keywords: "prompt dialog" },
  { id: "safety-autoBackup", label: "Automatic Transactional Backup", category: "safety", keywords: "snapshot registry services" },
  { id: "safety-restore", label: "Restore & Recovery", category: "safety", keywords: "restore point backup recovery undo" },
  { id: "logs-detailed", label: "Detailed Execution Logs", category: "logs", keywords: "verbose trace" },
  { id: "logs-retention", label: "Log Retention Period", category: "logs", keywords: "days cleanup" },
  { id: "system-version", label: "Application Version", category: "system", keywords: "build identifier architecture" },
  { id: "system-service", label: "Backend / Service Status", category: "system", keywords: "service online offline" },
];

export const categoryLabel = (c: SettingsCategory) =>
  SETTINGS_CATEGORIES.find((x) => x.id === c)?.label ?? c;

/** Matches setting names, category names and keywords; every word must match. */
export function searchSettings(query: string, limit = 8): SettingEntry[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return [];
  const hay = (e: SettingEntry) => `${e.label} ${categoryLabel(e.category)} ${e.keywords ?? ""}`.toLowerCase();
  const scored = SETTINGS_INDEX.map((e) => {
    const h = hay(e);
    if (!words.every((w) => h.includes(w))) return null;
    const name = e.label.toLowerCase();
    const score = name.startsWith(words[0]!) ? 0 : name.includes(words[0]!) ? 1 : 2;
    return { e, score };
  }).filter((x): x is { e: SettingEntry; score: number } => !!x);
  scored.sort((a, b) => a.score - b.score);
  return scored.slice(0, limit).map((x) => x.e);
}

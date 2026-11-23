// Global theme rules: normal colour presets, saved custom themes, hex validation, live preview /
// applied / previous state and the CSS variables the theme engine writes on <html>. Pure (no React,
// no storage). Every other colour in the app is derived from the --theme-* variables in
// src/styles.css. Themes only change colours; they carry no decorative effects.

export const THEME_KEYS = [
  "primary",
  "secondary",
  "accent",
  "background",
  "surface",
  "elevated",
  "text",
  "muted",
  "border",
  "success",
  "warning",
  "danger",
] as const;
export type ThemeKey = (typeof THEME_KEYS)[number];
export type ThemeColors = Record<ThemeKey, string>;

export const THEME_KEY_LABELS: Record<ThemeKey, { label: string; hint: string }> = {
  primary: { label: "Primary Accent", hint: "Buttons, active switch, main highlights" },
  secondary: { label: "Secondary Accent", hint: "Hover states and second gradient stop" },
  accent: { label: "Highlight Accent", hint: "Indicators, headings, focus outlines, charts" },
  background: { label: "Background", hint: "Window canvas behind every page" },
  surface: { label: "Surface", hint: "Cards, dialogs, dropdowns, inputs" },
  elevated: { label: "Elevated Surface", hint: "Raised panels, chips and hover rows" },
  text: { label: "Primary Text", hint: "Headings and body text" },
  muted: { label: "Secondary Text", hint: "Descriptions, hints and labels" },
  border: { label: "Border", hint: "Borders and dividers" },
  success: { label: "Success", hint: "Healthy, connected and safe states" },
  warning: { label: "Warning", hint: "Caution and in-progress states" },
  danger: { label: "Danger", hint: "Errors, high risk and destructive actions" },
};

export type ThemeCategory = "classic" | "custom";

export const THEME_CATEGORIES: readonly { id: ThemeCategory | "all"; label: string }[] = [
  { id: "all", label: "All Themes" },
  { id: "classic", label: "Classic Presets" },
  { id: "custom", label: "Custom Themes" },
];

export const CATEGORY_LABEL: Record<ThemeCategory, string> = {
  classic: "Classic",
  custom: "Custom",
};

export interface ThemePreset {
  id: string;
  name: string;
  category: ThemeCategory;
  colors: ThemeColors;
}

export const CUSTOM_ID = "custom";
export const USER_PREFIX = "user:";
export const DEFAULT_PRESET_ID = "error-default";

const HEX_RE = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i;

type C = ThemeColors;
const c = (
  primary: string,
  secondary: string,
  accent: string,
  background: string,
  surface: string,
  text: string,
  border: string,
  success: string,
  warning: string,
  danger: string,
): C => ({
  primary,
  secondary,
  accent,
  background,
  surface,
  elevated: deriveElevated(surface, border),
  text,
  muted: deriveMuted(text, border),
  border,
  success,
  warning,
  danger,
});

/** Elevated surface derived from a theme's surface and border (presets and older saved themes). */
export const deriveElevated = (surface: string, border: string) => mixHex(surface, border, 0.4);
/** Secondary text derived from a theme's text and border colours. */
export const deriveMuted = (text: string, border: string) => mixHex(text, border, 0.38);

const classic = (id: string, name: string, colors: C): ThemePreset => ({
  id,
  name,
  category: "classic",
  colors,
});

export const THEME_PRESETS: readonly ThemePreset[] = [
  classic("error-default", "ERROR DEFAULT", c("#2563EB", "#3B82F6", "#38BDF8", "#050811", "#0D1426", "#F8FAFC", "#1A2544", "#10B981", "#F59E0B", "#EF4444")),
  classic("midnight-blue", "Midnight Blue", c("#3B5BDB", "#4C6EF5", "#748FFC", "#04060F", "#0B1022", "#E9ECF8", "#1B2140", "#12B886", "#FAB005", "#FA5252")),
  classic("cyber-cyan", "Cyber Cyan", c("#0891B2", "#06B6D4", "#22D3EE", "#020A0E", "#08161D", "#ECFEFF", "#12303A", "#10B981", "#F59E0B", "#EF4444")),
  classic("crimson-red", "Crimson Red", c("#DC2626", "#EF4444", "#F87171", "#0A0506", "#170B0D", "#FAF5F5", "#2E1519", "#22C55E", "#F59E0B", "#F43F5E")),
  classic("emerald-green", "Emerald Green", c("#059669", "#10B981", "#34D399", "#030A07", "#0A1712", "#F0FDF4", "#14302A", "#22C55E", "#EAB308", "#EF4444")),
  classic("purple-neon", "Purple Neon", c("#A21CAF", "#C026D3", "#E879F9", "#08040C", "#140A1C", "#FAF5FF", "#2A1638", "#10B981", "#F59E0B", "#F43F5E")),
  classic("royal-violet", "Royal Violet", c("#6D28D9", "#7C3AED", "#A78BFA", "#06040E", "#110C22", "#F5F3FF", "#231A44", "#10B981", "#F59E0B", "#EF4444")),
  classic("arctic-ice", "Arctic Ice", c("#0284C7", "#0EA5E9", "#0891B2", "#E8EFF6", "#FFFFFF", "#0F172A", "#C3D1DF", "#047857", "#B45309", "#DC2626")),
  classic("amber-gold", "Amber Gold", c("#D97706", "#F59E0B", "#FBBF24", "#0A0703", "#18110A", "#FFFBEB", "#33240F", "#10B981", "#F97316", "#EF4444")),
  classic("obsidian", "Obsidian", c("#52525B", "#71717A", "#D4D4D8", "#050505", "#111113", "#FAFAFA", "#27272A", "#22C55E", "#EAB308", "#EF4444")),
  classic("graphite", "Graphite", c("#64748B", "#94A3B8", "#7DD3FC", "#0A0B0D", "#15171B", "#F1F5F9", "#2A2E35", "#22C55E", "#EAB308", "#EF4444")),
  classic("ocean-teal", "Ocean Teal", c("#0D9488", "#14B8A6", "#2DD4BF", "#020A0B", "#081618", "#F0FDFA", "#12302F", "#22C55E", "#F59E0B", "#EF4444")),
  classic("rose-pink", "Rose Pink", c("#DB2777", "#EC4899", "#F472B6", "#0A0408", "#180A12", "#FDF2F8", "#331527", "#10B981", "#F59E0B", "#EF4444")),

];

export const DEFAULT_COLORS: ThemeColors = THEME_PRESETS[0]!.colors;


/** True for #RGB or #RRGGBB (the leading # is optional). */
export function isValidHex(v: string): boolean {
  return HEX_RE.test(v.trim());
}

/** "#abc" / "abc" / "#aabbcc" → "#AABBCC"; null when invalid. */
export function normalizeHex(v: string): string | null {
  const m = HEX_RE.exec(v.trim());
  if (!m) return null;
  let h = m[1]!;
  if (h.length === 3) h = h.replace(/./g, (ch) => ch + ch);
  return `#${h.toUpperCase()}`;
}

function luminance(hex: string): number {
  const h = normalizeHex(hex) ?? "#000000";
  const ch = [1, 3, 5].map((i) => {
    const v = parseInt(h.slice(i, i + 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * ch[0]! + 0.7152 * ch[1]! + 0.0722 * ch[2]!;
}

export function contrastRatio(a: string, b: string): number {
  const la = luminance(a);
  const lb = luminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

/** Readable text colour on top of `hex`: white while it keeps 3:1 contrast (UI text), else the stronger of white / near-black. */
export function readableOn(hex: string): string {
  const l = luminance(hex);
  const onWhite = 1.05 / (l + 0.05);
  const onDark = (l + 0.05) / (luminance("#0B1020") + 0.05);
  return onWhite >= 3 || onWhite >= onDark ? "#FFFFFF" : "#0B1020";
}

/** Average of two hex colours (used for text on the accent → primary gradient). */
export function mixHex(a: string, b: string, t = 0.5): string {
  const x = normalizeHex(a) ?? "#000000";
  const y = normalizeHex(b) ?? "#000000";
  const ch = [1, 3, 5].map((i) =>
    Math.round(parseInt(x.slice(i, i + 2), 16) * (1 - t) + parseInt(y.slice(i, i + 2), 16) * t)
      .toString(16)
      .padStart(2, "0"),
  );
  return `#${ch.join("").toUpperCase()}`;
}

export const isLightColor = (hex: string) => luminance(hex) > 0.4;

/**
 * Primary used as text (links, values): the primary itself when it reads on the background
 * (3:1), otherwise blended toward the accent / text until it does (e.g. a very dark custom primary).
 */
export function primaryInk(colors: ThemeColors): string {
  if (contrastRatio(colors.primary, colors.background) >= 3) return colors.primary;
  for (const t of [0.35, 0.55, 0.75, 1]) {
    const v = mixHex(colors.primary, colors.accent, t);
    if (contrastRatio(v, colors.background) >= 3) return v;
  }
  return colors.text;
}

export function presetById(id: string): ThemePreset | undefined {
  return THEME_PRESETS.find((p) => p.id === id);
}

/** A custom theme the user saved under a name (reusable from "Custom Themes"). */
export interface UserTheme {
  id: string;
  name: string;
  colors: ThemeColors;
}

/** One theme choice: preset id, "custom" (the working palette) or "user:<id>". */
export interface ThemeChoice {
  preset: string;
  custom: ThemeColors;
}

export interface ThemeState {
  /** What the app shows right now (live preview). */
  live: ThemeChoice;
  /** What is applied and loads after a restart. */
  saved: ThemeChoice;
  /** Previously applied themes, newest last (for Restore Previous Theme). */
  history: ThemeChoice[];
  userThemes: UserTheme[];
}

const MAX_HISTORY = 10;
export const MAX_USER_THEMES = 12;

export const DEFAULT_CHOICE: ThemeChoice = {
  preset: DEFAULT_PRESET_ID,
  custom: DEFAULT_COLORS,
};
export const DEFAULT_THEME_STATE: ThemeState = {
  live: DEFAULT_CHOICE,
  saved: DEFAULT_CHOICE,
  history: [],
  userThemes: [],
};

export interface ResolvedTheme {
  id: string;
  name: string;
  category: ThemeCategory;
  colors: ThemeColors;
}

/** Everything the UI needs for a choice: colours, name and category. */
export function resolveTheme(c: ThemeChoice, userThemes: readonly UserTheme[] = []): ResolvedTheme {
  if (c.preset === CUSTOM_ID)
    return { id: CUSTOM_ID, name: "Custom Theme", category: "custom", colors: c.custom };
  if (c.preset.startsWith(USER_PREFIX)) {
    const u = userThemes.find((t) => USER_PREFIX + t.id === c.preset);
    if (u) return { id: c.preset, name: u.name, category: "custom", colors: u.colors };
  }
  const p = presetById(c.preset) ?? THEME_PRESETS[0]!;
  return { id: p.id, name: p.name, category: p.category, colors: p.colors };
}

export const colorsOf = (c: ThemeChoice, userThemes: readonly UserTheme[] = []) =>
  resolveTheme(c, userThemes).colors;

/** Theme picker list: presets then saved custom themes, filtered by category and search text. */
export function filterThemes(
  s: ThemeState,
  category: ThemeCategory | "all",
  query: string,
): ResolvedTheme[] {
  const all: ResolvedTheme[] = [
    ...THEME_PRESETS.map((p) => resolveTheme({ ...DEFAULT_CHOICE, preset: p.id })),
    resolveTheme({ ...s.live, preset: CUSTOM_ID }),
    ...s.userThemes.map((u) => resolveTheme({ ...DEFAULT_CHOICE, preset: USER_PREFIX + u.id }, s.userThemes)),
  ];
  const q = query.trim().toLowerCase();
  return all.filter(
    (t) =>
      (category === "all" || t.category === category) &&
      (!q ||
        t.name.toLowerCase().includes(q) ||
        CATEGORY_LABEL[t.category].toLowerCase().includes(q)),
  );
}

const sameColors = (a: ThemeColors, b: ThemeColors) => THEME_KEYS.every((k) => a[k] === b[k]);
export const sameChoice = (a: ThemeChoice, b: ThemeChoice) =>
  a.preset === b.preset && sameColors(a.custom, b.custom);

/** True while the app shows a preview that has not been applied. */
export const isDirty = (s: ThemeState) => !sameChoice(s.live, s.saved);

function commitSaved(s: ThemeState, next: ThemeChoice): ThemeState {
  if (sameChoice(next, s.saved)) return { ...s, live: next };
  return { ...s, live: next, saved: next, history: [...s.history, s.saved].slice(-MAX_HISTORY) };
}

const knownId = (s: ThemeState, id: string) =>
  id === CUSTOM_ID ||
  !!presetById(id) ||
  (id.startsWith(USER_PREFIX) && s.userThemes.some((u) => USER_PREFIX + u.id === id));

/** Selecting a theme previews it instantly across the app; Apply Theme makes it stick. */
export function previewTheme(s: ThemeState, id: string): ThemeState {
  if (!knownId(s, id)) return s;
  return { ...s, live: { ...s.live, preset: id } };
}

/** Applies (saves) the live preview. */
export function applyTheme(s: ThemeState): ThemeState {
  return commitSaved(s, s.live);
}

/**
 * Editing one colour previews it live. Editing while a preset is active starts a Custom Theme from
 * that theme's colours. Invalid hex values are ignored.
 */
export function editColor(s: ThemeState, key: ThemeKey, value: string): ThemeState {
  const hex = normalizeHex(value);
  if (!hex) return s;
  const base = resolveTheme(s.live, s.userThemes);
  return {
    ...s,
    live: { preset: CUSTOM_ID, custom: { ...base.colors, [key]: hex } },
  };
}

/** Puts one colour back to its ERROR DEFAULT value (live preview). */
export function resetColor(s: ThemeState, key: ThemeKey): ThemeState {
  return editColor(s, key, DEFAULT_COLORS[key]);
}

/** Saves the live colours as a named, reusable custom theme and applies it. */
export function saveCustomTheme(s: ThemeState, name: string): ThemeState {
  const base = resolveTheme(s.live, s.userThemes);
  const n = s.userThemes.reduce((m, u) => Math.max(m, Number(u.id) || 0), 0) + 1;
  const label = name.trim().slice(0, 32) || `My Theme ${n}`;
  const theme: UserTheme = { id: String(n), name: label, colors: base.colors };
  const userThemes = [...s.userThemes, theme].slice(-MAX_USER_THEMES);
  return commitSaved(
    { ...s, userThemes },
    { preset: USER_PREFIX + theme.id, custom: base.colors },
  );
}

/** Removes a saved custom theme; if it is in use, its colours stay as the working Custom Theme. */
export function deleteUserTheme(s: ThemeState, id: string): ThemeState {
  const u = s.userThemes.find((t) => t.id === id);
  if (!u) return s;
  const ref = USER_PREFIX + id;
  const swap = (c: ThemeChoice): ThemeChoice =>
    c.preset === ref ? { preset: CUSTOM_ID, custom: u.colors } : c;
  return {
    live: swap(s.live),
    saved: swap(s.saved),
    history: s.history.map(swap),
    userThemes: s.userThemes.filter((t) => t.id !== id),
  };
}

/** ERROR DEFAULT, applied. The working Custom Theme and saved custom themes are kept. */
export function resetToDefault(s: ThemeState): ThemeState {
  return commitSaved(s, { ...s.live, preset: DEFAULT_PRESET_ID });
}

/** An unapplied preview is discarded first; otherwise the previously applied theme comes back. */
export function restorePrevious(s: ThemeState): ThemeState {
  if (isDirty(s)) return { ...s, live: s.saved };
  const prev = s.history[s.history.length - 1];
  if (!prev) return s;
  return { ...s, live: prev, saved: prev, history: s.history.slice(0, -1) };
}

export const canRestore = (s: ThemeState) => isDirty(s) || s.history.length > 0;

/** Minimum text-on-background contrast a draft must keep before it can be saved. */
export const MIN_TEXT_CONTRAST = 3;

/**
 * Checks the draft before Save: every colour must be a valid hex value and body text must stay
 * readable on the background. Nothing is saved when this fails.
 */
export function validateThemeDraft(s: ThemeState): { ok: true } | { ok: false; reason: string } {
  const t = resolveTheme(s.live, s.userThemes);
  for (const k of THEME_KEYS) {
    if (!isValidHex(t.colors[k])) return { ok: false, reason: `${THEME_KEY_LABELS[k].label} is not a valid colour.` };
  }
  if (contrastRatio(t.colors.text, t.colors.background) < MIN_TEXT_CONTRAST) {
    return { ok: false, reason: "Primary text is too hard to read on the background. Pick a lighter or darker text colour." };
  }
  return { ok: true };
}

function sanitizeColors(raw: unknown): ThemeColors {
  const r = (raw && typeof raw === "object" ? raw : {}) as Record<string, unknown>;
  const out = { ...DEFAULT_COLORS };
  const seen = new Set<ThemeKey>();
  for (const k of THEME_KEYS) {
    const v = r[k];
    const hex = typeof v === "string" ? normalizeHex(v) : null;
    if (hex) {
      out[k] = hex;
      seen.add(k);
    }
  }
  // Themes saved before Elevated Surface / Secondary Text existed derive them from their own colours.
  if (!seen.has("elevated")) out.elevated = deriveElevated(out.surface, out.border);
  if (!seen.has("muted")) out.muted = deriveMuted(out.text, out.border);
  return out;
}

function sanitizeChoice(raw: unknown, users: readonly UserTheme[]): ThemeChoice {
  const r = (raw && typeof raw === "object" ? raw : {}) as Record<string, unknown>;
  const p = r["preset"];
  const ok =
    typeof p === "string" &&
    (p === CUSTOM_ID || !!presetById(p) || users.some((u) => USER_PREFIX + u.id === p));
  return {
    preset: ok ? (p as string) : DEFAULT_PRESET_ID,
    custom: sanitizeColors(r["custom"]),
  };
}

/**
 * Cleans a saved value. Live always starts as the applied theme. A theme id that no longer exists
 * (for example a removed character theme) falls back to ERROR DEFAULT; old effect fields are dropped.
 */
export function sanitizeThemeState(raw: unknown): ThemeState {
  const r = (raw && typeof raw === "object" ? raw : {}) as Record<string, unknown>;
  const userThemes: UserTheme[] = Array.isArray(r["userThemes"])
    ? r["userThemes"]
        .filter((u): u is Record<string, unknown> => !!u && typeof u === "object")
        .filter((u) => typeof u["id"] === "string" && typeof u["name"] === "string")
        .slice(-MAX_USER_THEMES)
        .map((u) => ({
          id: u["id"] as string,
          name: (u["name"] as string).slice(0, 32),
          colors: sanitizeColors(u["colors"]),
        }))
    : [];
  const saved = sanitizeChoice(r["saved"], userThemes);
  // History entries for themes that no longer exist are dropped instead of restoring ERROR DEFAULT.
  const history = Array.isArray(r["history"])
    ? r["history"]
        .slice(-MAX_HISTORY)
        .filter((h) => {
          const id = h && typeof h === "object" ? (h as Record<string, unknown>)["preset"] : null;
          return typeof id === "string" && knownId({ ...DEFAULT_THEME_STATE, userThemes }, id);
        })
        .map((h) => sanitizeChoice(h, userThemes))
    : [];
  return { live: saved, saved, history, userThemes };
}

/** CSS custom properties written on <html>. src/styles.css derives every token from these. */
export function themeVars(colors: ThemeColors): Record<string, string> {
  return {
    "--theme-primary": colors.primary,
    "--theme-secondary": colors.secondary,
    "--theme-accent": colors.accent,
    "--theme-background": colors.background,
    "--theme-surface": colors.surface,
    "--theme-elevated": colors.elevated,
    "--theme-text": colors.text,
    "--theme-text-muted": colors.muted,
    "--theme-border": colors.border,
    "--theme-success": colors.success,
    "--theme-warning": colors.warning,
    "--theme-danger": colors.danger,
    "--theme-primary-ink": primaryInk(colors),
    // Primary buttons and switches sit on the accent → primary gradient.
    "--theme-on-primary": readableOn(mixHex(colors.primary, colors.accent)),
    "--theme-on-accent": readableOn(colors.accent),
    "--theme-on-danger": readableOn(colors.danger),
    "--theme-shadow-strength": isLightColor(colors.background) ? "0.35" : "1",
  };
}

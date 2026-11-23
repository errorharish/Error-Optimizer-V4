// Global Smart Search rules (pure: no React, no storage). Searches the real page registry
// (PAGES) plus a small keyword / category table, so every result opens an existing route.
import { PAGES, isSharedSlug, type PageDef } from "./registry";

export type SearchGroup = "V4" | "Shared";

/**
 * Category + related keywords per page slug. Keywords are lower-case aliases users type; `modules`
 * are named features hosted inside the page (e.g. RAM Limiter in AI Optimization) and rank like a title.
 */
export const SEARCH_META: Record<
  string,
  { category: string; keywords: readonly string[]; modules?: readonly string[] }
> = {
  "": { category: "Overview", keywords: ["home", "dashboard", "overview", "score"] },
  "input-optimizer": {
    category: "Input & Latency",
    keywords: ["input", "mouse", "keyboard", "pointer", "input lag", "latency", "timer resolution", "jitter", "polling"],
  },
  "system-info": {
    category: "System",
    keywords: ["telemetry", "hardware", "cpu", "processor", "gpu", "ram", "memory", "specs", "diagnostics", "system info"],
  },
  "registry-tweak": {
    category: "Registry",
    keywords: ["registry", "regedit", "tweaks", "cpu scheduling", "processor scheduling", "gaming", "memory"],
  },
  "registry-values": {
    category: "Registry",
    keywords: ["registry", "registry values", "ram", "memory", "gpu", "hardware values"],
  },
  "startup-manager": {
    category: "Startup & Services",
    keywords: ["startup", "boot", "autorun", "startup apps", "background tasks"],
  },
  network: {
    category: "Network",
    keywords: ["network", "network optimization", "internet", "ping", "latency", "wifi", "tcp", "dns", "lag"],
  },
  storage: {
    category: "Storage",
    keywords: ["storage", "disk", "ssd", "hdd", "drive", "cleanup", "temp files", "free space", "junk"],
  },
  tools: {
    category: "Maintenance",
    keywords: ["tools", "toolbox", "repair", "diagnostics", "cleanup", "sfc", "dism", "dns flush"],
  },
  normal: { category: "Optimization Profiles", keywords: ["profile", "tier", "safe", "everyday", "normal"] },
  pro: { category: "Optimization Profiles", keywords: ["profile", "tier", "advanced", "power user", "fps", "pro"] },
  ultimate: { category: "Optimization Profiles", keywords: ["profile", "tier", "ultimate", "overhaul", "fps", "gaming"] },
  debloat: {
    category: "Optimization Profiles",
    keywords: ["debloat", "bloatware", "telemetry", "privacy", "remove apps", "windows apps"],
  },
  "bios-safe": { category: "Optimization Profiles", keywords: ["bios", "uefi", "firmware", "hardware"] },
  "max-performance": {
    category: "Optimization Profiles",
    keywords: ["max performance", "maximum", "fps", "gaming", "rendering", "cpu", "gpu", "power"],
  },
  "ai-optimization": {
    category: "AI & Memory",
    modules: ["ram limiter", "workload optimization", "power plan optimization"],
    keywords: [
      "ai",
      "ram limiter",
      "ram",
      "memory",
      "workload",
      "workload optimization",
      "power plan",
      "power plan optimization",
      "power",
      "cpu",
      "cpu affinity",
      "processor",
      "priority",
    ],
  },
  "backup-restore": {
    category: "Settings & Safety",
    keywords: ["backup", "restore", "restore point", "rollback", "undo", "profiles"],
  },
  settings: {
    category: "Settings & Safety",
    keywords: ["settings", "preferences", "options", "theme", "appearance", "animations", "safety"],
  },
  about: { category: "Settings & Safety", keywords: ["about", "version", "info", "diagnostics", "catalog"] },
  "one-click": {
    category: "Quick Optimization",
    keywords: ["one click", "1-click", "1 click", "quick", "auto", "verify"],
  },
  "auto-optimize": {
    category: "Maintenance",
    keywords: ["auto optimize", "schedule", "maintenance", "automatic", "gaming mode", "recurring"],
  },
  "process-reduction": {
    category: "AI & Memory",
    keywords: ["process", "processes", "ram optimizer", "ram", "memory", "background", "cpu", "task manager"],
  },
  "service-manager": {
    category: "Startup & Services",
    keywords: ["services", "service manager", "windows services", "background", "svchost"],
  },
  "registry-values-ram": {
    category: "Registry",
    keywords: ["ram", "memory", "svchost", "split threshold", "registry"],
  },
  "gpu-registry-values": {
    category: "Registry",
    keywords: ["gpu", "graphics", "nvidia", "amd", "intel", "registry"],
  },
  "performance-tuning": {
    category: "Performance Tuning",
    modules: ["cpu tuning", "processor tuning", "gpu tuning"],
    keywords: ["cpu", "processor", "gpu", "tuning", "overclock", "thermal", "power", "boost"],
  },
  "core-optimization": {
    category: "Performance",
    keywords: ["core optimization", "latency", "cpu", "processor", "priority", "resource", "power", "workload", "multi pass", "convergence"],
  },
};

export interface SearchEntry {
  slug: string;
  title: string;
  description: string;
  icon: string;
  group: SearchGroup;
  category: string;
  keywords: readonly string[];
  modules: readonly string[];
}

export const groupOf = (p: PageDef): SearchGroup => (isSharedSlug(p.slug) ? "Shared" : p.suite);

/** Every searchable page: the real registry, nothing invented. */
export const SEARCH_ENTRIES: readonly SearchEntry[] = PAGES.map((p) => ({
  slug: p.slug,
  title: p.title,
  description: p.description,
  icon: p.icon,
  group: groupOf(p),
  category: SEARCH_META[p.slug]?.category ?? "Page",
  keywords: SEARCH_META[p.slug]?.keywords ?? [],
  modules: SEARCH_META[p.slug]?.modules ?? [],
}));

export interface SearchResult {
  entry: SearchEntry;
  score: number;
  /** The related keyword that matched when the title itself did not (shown as a hint). */
  matchedKeyword?: string | undefined;
}

const norm = (s: string) => s.toLowerCase().replace(/\s+/g, " ").trim();
export const tokensOf = (q: string) => norm(q).split(" ").filter(Boolean);

const wordStarts = (text: string, t: string) => text.split(/[^a-z0-9]+/).some((w) => w.startsWith(t));

/** Score one token against an entry; 0 = no match. */
function tokenScore(e: SearchEntry, t: string): { score: number; keyword?: string | undefined } {
  const title = e.title.toLowerCase();
  let best = 0;
  let keyword: string | undefined;
  if (title.startsWith(t)) best = 100;
  else if (wordStarts(title, t)) best = 85;
  else if (title.includes(t)) best = 60;
  for (const m of e.modules) {
    const s = m.startsWith(t) ? 95 : wordStarts(m, t) ? 80 : 0;
    if (s > best) {
      best = s;
      keyword = m;
    }
  }
  for (const k of e.keywords) {
    const s = k === t ? 75 : k.startsWith(t) ? 62 : wordStarts(k, t) ? 55 : k.includes(t) ? 35 : 0;
    if (s > best) {
      best = s;
      keyword = k;
    }
  }
  const cat = e.category.toLowerCase();
  if (best < 30 && wordStarts(cat, t)) {
    best = 30;
    keyword = undefined;
  }
  if (best < 18 && e.description.toLowerCase().includes(t)) {
    best = 18;
    keyword = undefined;
  }
  return { score: best, keyword: best > 0 && keyword && !title.includes(t) ? keyword : undefined };
}

/**
 * Ranked results for a query. Every token must match (title, keyword, category or description);
 * optimization pages rank above shared settings pages; ties keep registry order.
 */
export function searchPages(query: string, limit = 24): SearchResult[] {
  const tokens = tokensOf(query);
  if (!tokens.length) return [];
  const out: (SearchResult & { order: number })[] = [];
  SEARCH_ENTRIES.forEach((e, order) => {
    let total = 0;
    let kw: string | undefined;
    for (const t of tokens) {
      const r = tokenScore(e, t);
      if (!r.score) return;
      total += r.score;
      kw ??= r.keyword;
    }
    const whole = norm(query);
    if (tokens.length > 1 && e.title.toLowerCase().includes(whole)) total += 40;
    if (e.group === "Shared") total -= 25;
    else if (e.category === "Overview") total -= 10;
    out.push({ entry: e, score: total, matchedKeyword: kw, order });
  });
  out.sort((a, b) => b.score - a.score || a.order - b.order);
  return out.slice(0, limit).map(({ order: _o, ...r }) => r);
}

export const GROUP_LABEL: Record<SearchGroup, string> = {
  V4: "Optimization Features",
  Shared: "Settings & System",
};

/**
 * Groups ranked results by suite. The group holding the best match comes first, so the top result
 * is always first; shared pages are always last.
 */
export function groupResults(results: readonly SearchResult[]): { group: SearchGroup; items: SearchResult[] }[] {
  const order: SearchGroup[] = [];
  for (const r of results) if (r.entry.group !== "Shared" && !order.includes(r.entry.group)) order.push(r.entry.group);
  if (results.some((r) => r.entry.group === "Shared")) order.push("Shared");
  return order.map((g) => ({ group: g, items: results.filter((r) => r.entry.group === g) }));
}

/** Popular optimization destinations for the empty state (all real routes). */
export const POPULAR_SLUGS = [
  "core-optimization",
  "input-optimizer",
  "ai-optimization",
  "network",
  "storage",
  "registry-tweak",
] as const;

export const MAX_RECENT = 5;

/** Adds a visited page to the front of the recent list (deduped, capped, real pages only). */
export function pushRecent(list: readonly string[], slug: string): string[] {
  if (!SEARCH_ENTRIES.some((e) => e.slug === slug)) return [...list];
  return [slug, ...list.filter((s) => s !== slug)].slice(0, MAX_RECENT);
}

export const entryBySlug = (slug: string) => SEARCH_ENTRIES.find((e) => e.slug === slug);

/** Splits text into plain / matched segments for subtle highlighting. */
export function highlightSegments(text: string, query: string): { text: string; hit: boolean }[] {
  const tokens = tokensOf(query).sort((a, b) => b.length - a.length);
  if (!tokens.length) return [{ text, hit: false }];
  const esc = tokens.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  const re = new RegExp(`(${esc.join("|")})`, "gi");
  return text
    .split(re)
    .filter((s) => s !== "")
    .map((s) => ({ text: s, hit: tokens.includes(s.toLowerCase()) }));
}

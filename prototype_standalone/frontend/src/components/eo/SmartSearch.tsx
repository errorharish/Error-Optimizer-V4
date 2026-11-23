// The one global Smart Search (header centre, every V3 + V4 route). Matches the real page registry
// from the first typed character (rules in src/lib/eo/smartSearch.ts) and opens the existing route;
// the Shell derives V3 / V4 from the opened page, so suite switching happens automatically.
import { useLocation, useNavigate } from "@tanstack/react-router";
import { Circle, Search } from "lucide-react";
import { Fragment, useCallback, useEffect, useId, useMemo, useRef, useState } from "react";
import { hrefFor } from "@/lib/eo/registry";
import {
  GROUP_LABEL,
  POPULAR_SLUGS,
  entryBySlug,
  groupResults,
  highlightSegments,
  pushRecent,
  searchPages,
  type SearchEntry,
} from "@/lib/eo/smartSearch";
import { ICONS } from "./icons";

const RECENT_KEY = "eo-search-recent";

function Hl({ text, q }: { text: string; q: string }) {
  return (
    <>
      {highlightSegments(text, q).map((s, i) =>
        s.hit ? (
          <mark key={i} className="eo-ss-hit">
            {s.text}
          </mark>
        ) : (
          <Fragment key={i}>{s.text}</Fragment>
        ),
      )}
    </>
  );
}

type Section = { label: string; items: { entry: SearchEntry; keyword?: string | undefined }[] };

export function SmartSearch() {
  const navigate = useNavigate();
  const loc = useLocation();
  const slug = loc.pathname.replace(/^\//, "");
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [recent, setRecent] = useState<string[]>([]);
  const rootRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const id = useId();
  const listId = `${id}-list`;

  // Recently visited pages (read after hydration; updated on every route change).
  useEffect(() => {
    try {
      const raw = JSON.parse(window.localStorage.getItem(RECENT_KEY) ?? "[]");
      if (Array.isArray(raw)) setRecent(raw.filter((s): s is string => typeof s === "string"));
    } catch {
      /* ignore corrupt storage */
    }
  }, []);
  useEffect(() => {
    if (slug === "blueprint") return;
    setRecent((prev) => {
      const next = pushRecent(prev, slug);
      try {
        window.localStorage.setItem(RECENT_KEY, JSON.stringify(next));
      } catch {
        /* storage unavailable */
      }
      return next;
    });
  }, [slug]);

  const sections: Section[] = useMemo(() => {
    if (query.trim()) {
      return groupResults(searchPages(query)).map((g) => ({
        label: GROUP_LABEL[g.group],
        items: g.items.map((r) => ({ entry: r.entry, keyword: r.matchedKeyword })),
      }));
    }
    const rec = recent.filter((s) => s !== slug).slice(0, 4).flatMap((s) => entryBySlug(s) ?? []);
    const pop = POPULAR_SLUGS.filter((s) => s !== slug && !rec.some((r) => r.slug === s)).flatMap(
      (s) => entryBySlug(s) ?? [],
    );
    return [
      ...(rec.length ? [{ label: "Recently visited", items: rec.map((entry) => ({ entry })) }] : []),
      { label: "Popular optimizations", items: pop.map((entry) => ({ entry })) },
    ];
  }, [query, recent, slug]);
  const flat = useMemo(() => sections.flatMap((s) => s.items), [sections]);

  useEffect(() => setActive(0), [query]);
  useEffect(() => {
    if (!open) return;
    listRef.current
      ?.querySelector<HTMLElement>(`[data-idx="${active}"]`)
      ?.scrollIntoView({ block: "nearest" });
  }, [active, open]);

  const close = useCallback(() => setOpen(false), []);
  const go = useCallback(
    (entry: SearchEntry) => {
      setOpen(false);
      setQuery("");
      inputRef.current?.blur();
      if (entry.slug !== slug) navigate({ to: hrefFor(entry.slug) as never });
    },
    [navigate, slug],
  );

  // Ctrl + K (⌘ + K on macOS) focuses Smart Search from anywhere.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === "k") {
        e.preventDefault();
        inputRef.current?.focus();
        inputRef.current?.select();
        setOpen(true);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  // Clicking outside closes the dropdown.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: PointerEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) close();
    };
    document.addEventListener("pointerdown", onDown);
    return () => document.removeEventListener("pointerdown", onDown);
  }, [open, close]);

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Escape") {
      e.preventDefault();
      if (open) close();
      else {
        setQuery("");
        inputRef.current?.blur();
      }
    } else if (e.key === "ArrowDown") {
      e.preventDefault();
      if (!open) setOpen(true);
      else if (flat.length) setActive((a) => (a + 1) % flat.length);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      if (!open) setOpen(true);
      else if (flat.length) setActive((a) => (a - 1 + flat.length) % flat.length);
    } else if (e.key === "Enter") {
      const item = flat[active];
      if (open && item) {
        e.preventDefault();
        go(item.entry);
      }
    }
  };

  const showList = open;
  const activeId = showList && flat[active] ? `${id}-opt-${active}` : undefined;
  let idx = -1;

  return (
    <div ref={rootRef} className="eo-ss" data-open={showList ? "true" : undefined}>
      <div className="eo-ss-field">
        <Search aria-hidden="true" className="eo-ss-icon" />
        <input
          ref={inputRef}
          type="text"
          role="combobox"
          aria-label="Smart Search: optimizations, modules or pages"
          aria-expanded={showList}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={activeId}
          autoComplete="off"
          spellCheck={false}
          placeholder="Search optimizations, modules, or pages..."
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={onKeyDown}
          className="eo-ss-input"
        />
        <kbd className="eo-ss-kbd" aria-hidden="true">
          Ctrl + K
        </kbd>
      </div>

      <div
        ref={listRef}
        id={listId}
        role="listbox"
        aria-label={query.trim() ? "Search results" : "Suggested pages"}
        className="eo-ss-pop"
        data-open={showList ? "true" : undefined}
      >
        {flat.length === 0 ? (
          <div className="eo-ss-empty" role="presentation">
            <Search aria-hidden="true" className="h-4 w-4" />
            <div>
              <p className="eo-ss-empty-title">No matching optimization pages found</p>
              <p className="eo-ss-empty-sub">Try a module name such as “ram”, “network” or “emulator”.</p>
            </div>
          </div>
        ) : (
          sections.map((sec) => (
            <div key={sec.label} role="group" aria-label={sec.label} className="eo-ss-group">
              <div className="eo-ss-group-label" aria-hidden="true">
                {sec.label}
              </div>
              {sec.items.map(({ entry, keyword }) => {
                idx += 1;
                const i = idx;
                const C = ICONS[entry.icon] ?? Circle;
                return (
                  <div
                    key={`${sec.label}-${entry.slug}`}
                    id={`${id}-opt-${i}`}
                    role="option"
                    aria-selected={i === active}
                    data-idx={i}
                    className="eo-ss-opt"
                    onPointerMove={() => i !== active && setActive(i)}
                    onPointerDown={(e) => e.preventDefault()}
                    onClick={() => go(entry)}
                  >
                    <span className="eo-ss-opt-icon" aria-hidden="true">
                      <C className="h-3.5 w-3.5" />
                    </span>
                    <span className="eo-ss-opt-body">
                      <span className="eo-ss-opt-title">
                        <Hl text={entry.title} q={query} />
                      </span>
                      <span className="eo-ss-opt-desc">
                        <Hl text={entry.description} q={query} />
                      </span>
                    </span>
                    <span className="eo-ss-opt-meta">
                      <span className="eo-ss-suite" data-suite={entry.group}>
                        {entry.group === "Shared" ? "V3 · V4" : entry.group}
                      </span>
                      {keyword ? (
                        <span className="eo-ss-kw">
                          <Hl text={keyword} q={query} />
                        </span>
                      ) : (
                        <span className="eo-ss-cat">{entry.category}</span>
                      )}
                    </span>
                  </div>
                );
              })}
            </div>
          ))
        )}
        <div className="eo-ss-foot" aria-hidden="true">
          <span><kbd>↑</kbd><kbd>↓</kbd> move</span>
          <span><kbd>Enter</kbd> open</span>
          <span><kbd>Esc</kbd> close</span>
        </div>
      </div>
    </div>
  );
}

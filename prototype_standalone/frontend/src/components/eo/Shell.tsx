import { isDesktopApp, minimizeWindow, closeWindow, dragWindow } from "@/lib/eo/nativeBridge";
import { Link, useLocation, useNavigate, useRouter } from "@tanstack/react-router";
import { Circle, PanelLeftClose, PanelLeftOpen, Zap } from "lucide-react";
import { ICONS } from "./icons";
import { Fragment, useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { cn } from "@/lib/utils";
import { Btn, btnProps } from "./kit";
import { AutoChip } from "./AutoChip";
import { GlobalServiceStatus } from "./ServiceStatus";
import { SmartSearch } from "./SmartSearch";
import { useClickRipple } from "./useClickRipple";
import { initTheme } from "./themeStore";
import { warmPageModules } from "./pageMap";
import { useAutoOptimizeBoot } from "./autoOptimizeStore";
import { PAGES, hrefFor, pageBySlug } from "@/lib/eo/registry";

function Icon({ name, className }: { name: string; className?: string }) {
  const C = ICONS[name] ?? Circle;
  return <C aria-hidden="true" className={className} />;
}

const DOCK_LABEL: Record<string, string> = {
  Dashboard: "HOME",
  InputOptimizer: "INPUT",
  SystemInfo: "SYSTEM",
  RegistryTweak: "REGISTRY",
  RegistryValues: "VALUES",
  StartupManager: "STARTUP",
  Network: "NETWORK",
  Storage: "STORAGE",
  Tools: "TOOLS",
  Normal: "NORMAL",
  Pro: "PRO",
  Ultimate: "ULTIMATE",
  Debloat: "DEBLOAT",
  BiosSafe: "BIOS",
  CoreOptimization: "CORE",
  MaxPerformance: "MAXIMUM PERFORMANCE",
  AiOptimization: "AI",
  BackupRestore: "BACKUP",
  Settings: "SETTINGS",
  About: "ABOUT",
};

const DOCK_KEY = "eo-dock-collapsed";

type Box = { x: number; y: number; w: number; h: number } | null;

/** Measures the item marked data-active="true" inside the nav so one shared indicator
 *  slides between items instead of each item painting its own. A ResizeObserver keeps it
 *  glued to the item while the dock collapses/expands or the window resizes. */
function useActiveBox(deps: unknown[]) {
  const ref = useRef<HTMLElement>(null);
  const [box, setBox] = useState<Box>(null);
  const measure = useCallback(() => {
    const el = ref.current?.querySelector<HTMLElement>('[data-active="true"]');
    setBox((prev) => {
      if (!el) return null;
      const next = { x: el.offsetLeft, y: el.offsetTop, w: el.offsetWidth, h: el.offsetHeight };
      return prev &&
        prev.x === next.x &&
        prev.y === next.y &&
        prev.w === next.w &&
        prev.h === next.h
        ? prev
        : next;
    });
  }, []);
  useEffect(() => {
    measure();
    const nav = ref.current;
    if (!nav) return;
    const active = nav.querySelector<HTMLElement>('[data-active="true"]');
    if (active && nav.scrollHeight > nav.clientHeight) {
      const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
      active.scrollIntoView({ block: "nearest", behavior: reduce ? "auto" : "smooth" });
    }
    const ro = new ResizeObserver(measure);
    ro.observe(nav);
    nav.querySelectorAll(".eo-nav-item").forEach((n) => ro.observe(n));
    return () => ro.disconnect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  return { ref, box };
}

export function Shell({ children }: { children: ReactNode }) {
  const loc = useLocation();
  const navigate = useNavigate();
  useAutoOptimizeBoot();
  const slug = loc.pathname.replace(/^\//, "");
  const page = pageBySlug(slug);
  const isBlueprint = slug === "blueprint";
  const dock = PAGES.filter((p) => p.inDock).map((p) => ({
    slug: p.slug,
    key: p.routeKey,
    title: p.title,
    tooltip: p.tooltip,
    icon: p.icon,
    label: DOCK_LABEL[p.routeKey] ?? p.title,
    group: String(p.dockGroup ?? 0),
  }));
  const [collapsed, setCollapsed] = useState(false);
  useEffect(() => {
    setCollapsed(window.localStorage.getItem(DOCK_KEY) === "1");
  }, []);
  // While the dock width animates, the indicator tracks the item frame by frame.
  const [resizing, setResizing] = useState(false);
  const resizeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const toggleDock = () => {
    setResizing(true);
    clearTimeout(resizeTimer.current);
    resizeTimer.current = setTimeout(() => setResizing(false), 360);
    setCollapsed((c) => {
      window.localStorage.setItem(DOCK_KEY, c ? "0" : "1");
      return !c;
    });
  };
  useEffect(() => () => clearTimeout(resizeTimer.current), []);
  // The indicator starts sliding the moment an item is clicked, not when the page finishes
  // loading; it falls back to the real route once navigation resolves (or is cancelled).
  const router = useRouter();
  const [target, setTarget] = useState<string | null>(null);
  useEffect(() => setTarget(null), [slug]);
  useEffect(() => router.subscribe("onResolved", () => setTarget(null)), [router]);
  const visualSlug = target ?? slug;
  const dockBox = useActiveBox([visualSlug, collapsed]);
  useClickRipple();
  useEffect(() => initTheme(), []);
  // The shell mounts: warm background page modules for instant route transitions.
  useEffect(() => {
    warmPageModules();
  }, []);

  return (
    <div className="relative isolate flex h-screen flex-col overflow-hidden">
      <div aria-hidden="true" className="eo-ambient" />
      <a
        href="#main-content"
        className="eo-skip"
        onClick={(e) => {
          e.preventDefault();
          const el = document.getElementById("main-content");
          el?.scrollIntoView({ behavior: "smooth" });
          el?.focus();
        }}
      >
        Skip to page content
      </a>
      {/* ── Title bar (48px) ── */}
      <header
        className="eo-shell-header relative z-(--z-float) flex h-12 shrink-0 items-center justify-between px-4 select-none"
        style={{ WebkitAppRegion: "drag" } as React.CSSProperties}
        onMouseDown={(e) => {
          if (
            e.button === 0 &&
            !(e.target as HTMLElement).closest("button, a, input, select, [data-no-drag]")
          ) {
            dragWindow();
          }
        }}
      >
        <div
          className="flex shrink-0 items-center gap-3.5"
          data-no-drag="true"
          style={{ WebkitAppRegion: "no-drag" } as React.CSSProperties}
        >
          <Link to="/" className="eo-logo" aria-label="ERROR OPTIMIZER — Dashboard">
            <span className="eo-logo-mark" aria-hidden="true">
              <Zap className="h-3.5 w-3.5" />
            </span>
            <span className="eo-logo-text">
              <span className="eo-shell-brand">ERROR OPTIMIZER</span>
              <span className="eo-logo-edition">V4</span>
            </span>
          </Link>
        </div>
        {/* Global Smart Search: one shared search across all pages. */}
        <div
          className="eo-ss-slot"
          data-no-drag="true"
          style={{ WebkitAppRegion: "no-drag" } as React.CSSProperties}
        >
          <SmartSearch />
        </div>
        <div
          className="flex shrink-0 items-center gap-2"
          data-no-drag="true"
          style={{ WebkitAppRegion: "no-drag" } as React.CSSProperties}
        >
          <AutoChip />
          <GlobalServiceStatus />
          {isDesktopApp() && (
            <div className="flex items-center gap-1 pl-2 border-l border-border/40">
              <button
                type="button"
                onClick={minimizeWindow}
                {...btnProps("quiet", "icon-sm")}
                aria-label="Minimize"
                title="Minimize"
              >
                ─
              </button>
              <button
                type="button"
                onClick={closeWindow}
                {...btnProps("quiet", "icon-sm")}
                data-window="close"
                aria-label="Close"
                title="Close"
              >
                ✕
              </button>
            </div>
          )}
        </div>
      </header>

      <div className="flex min-h-0 flex-1">
        {/* ── Glass sidebar dock (84px, 60px collapsed); V3 and V4 each list only their own pages ── */}
        {
          <div
            className="eo-dock relative z-10 flex shrink-0 flex-col border-r border-border"
            data-collapsed={collapsed}
            data-resizing={resizing ? "true" : undefined}
            style={{ width: collapsed ? 60 : 84 }}
          >
            <nav
              ref={dockBox.ref}
              aria-label="Navigation pages"
              data-suite="V4"
              className="eo-scroll-fade relative flex min-h-0 flex-1 flex-col items-center gap-0.5 overflow-y-auto py-2"
            >
              {dockBox.box && (
                <span
                  aria-hidden="true"
                  className="eo-dock-indicator"
                  style={{
                    transform: `translate(${dockBox.box.x}px, ${dockBox.box.y}px)`,
                    width: dockBox.box.w,
                    height: dockBox.box.h,
                  }}
                />
              )}
              {dock.map((p, i) => {
                const active = slug === p.slug;
                const isMaxPerf = p.key === "MaxPerformance" || p.slug === "max-performance";
                const sep = i > 0 && dock[i - 1]?.group !== p.group;
                // V4 bottom section (Settings, Backup & Restore, About) is anchored to the dock's foot.
                const bottom = p.group === "bottom" && sep;
                return (
                  <Fragment key={p.key}>
                    {sep && (
                      <div
                        className="eo-dock-sep my-1.5 h-px w-10 shrink-0 bg-border"
                        data-dock-bottom={bottom ? "true" : undefined}
                      />
                    )}
                    <Link
                      to={hrefFor(p.slug) as never}
                      title={isMaxPerf ? "Maximum Performance" : p.tooltip}
                      aria-label={p.title}
                      aria-current={active ? "page" : undefined}
                      data-active={visualSlug === p.slug ? "true" : undefined}
                      onClick={(e) => {
                        if (
                          e.button === 0 &&
                          !e.metaKey &&
                          !e.ctrlKey &&
                          !e.shiftKey &&
                          !e.altKey
                        ) {
                          setTarget(p.slug);
                        }
                      }}
                      style={{ "--i": i } as React.CSSProperties}
                      className="eo-nav-item eo-dock-enter relative flex w-[68px] shrink-0 flex-col items-center gap-1 rounded-lg py-2"
                    >
                      <Icon name={p.icon} className="h-4 w-4" />
                      {isMaxPerf ? (
                        <span
                          className="eo-nav-label flex flex-col items-center justify-center text-center font-semibold uppercase select-none pointer-events-none"
                          style={{
                            maxHeight: collapsed ? 0 : "22px",
                            height: collapsed ? 0 : "auto",
                            lineHeight: 1.05,
                            whiteSpace: "normal",
                            overflow: collapsed ? "hidden" : "visible",
                            opacity: collapsed ? 0 : 1,
                            marginTop: collapsed ? -4 : 0,
                            letterSpacing: "0.02em",
                          }}
                        >
                          <span className="block leading-[9px] text-[7.5px] tracking-[0.02em]">MAXIMUM</span>
                          <span className="block leading-[9px] text-[6.8px] tracking-[0.01em]">PERFORMANCE</span>
                        </span>
                      ) : (
                        <span className="eo-nav-label text-[9.2px] font-semibold tracking-wide">
                          {p.label}
                        </span>
                      )}
                    </Link>
                  </Fragment>
                );
              })}
            </nav>
            <div className="eo-dock-toggle flex shrink-0 justify-center border-t border-border py-1.5">
              <Btn
                variant="quiet"
                pressed={collapsed}
                onClick={toggleDock}
                title={collapsed ? "Show dock labels" : "Collapse dock to icons"}
                label={collapsed ? "Show dock labels" : "Collapse dock to icons"}
                size="icon-sm"
                className="w-10"
              >
                {collapsed ? (
                  <PanelLeftOpen aria-hidden="true" className="h-4 w-4" />
                ) : (
                  <PanelLeftClose aria-hidden="true" className="h-4 w-4" />
                )}
              </Btn>
            </div>
          </div>
        }

        <main
          id="main-content"
          tabIndex={-1}
          className="min-w-0 flex-1 overflow-y-auto outline-none"
        >
          <div key={loc.pathname} className="eo-page-content eo-enter mx-auto max-w-[1440px]">
            {children}
          </div>
        </main>
      </div>

      {/* ── Footer status bar (32px) ── */}
      <footer className="eo-shell-footer relative z-10 flex h-8 shrink-0 items-center justify-between border-t border-border px-4 text-[10px] font-semibold text-subtle">
        <span>
          <span className="text-cyan">⚡</span> ERROR OPTIMIZER V4 ·{" "}
          {(page?.routeKey ?? (isBlueprint ? "BLUEPRINT" : "")).toUpperCase()}
        </span>
        <span>UNIFIED SYSTEM OPTIMIZATION SUITE</span>
      </footer>
    </div>
  );
}

<!-- LOVABLE:BEGIN -->
> [!IMPORTANT]
> This project is connected to [Lovable](https://lovable.dev). Avoid rewriting
> published git history — force pushing, or rebasing/amending/squashing commits
> that are already pushed — as it rewrites history on Lovable's side and the
> user will likely lose their project history.
>
> Commits you push to the connected branch sync back to Lovable and show up in
> the editor, so keep the branch in a working state.

## What Lovable manages

Rules outside this block are the project's own. Change the pieces below in
Lovable, not by hand. With the [Lovable MCP](https://mcp.lovable.dev)
connected, `send_message` asks Lovable to do it; otherwise ask the user to
use the editor.

- **Database.** Do not add or edit `supabase/migrations/` or `drizzle/`.
  Ask Lovable to change the schema.
- **Generated files.** Do not edit a file whose header says it is automatically
  generated.
- **AI gateway.** Keep model calls on Lovable's gateway, and keep
  `LOVABLE_API_KEY` out of browser code.
- **Connectors.** Lovable provides native connectors. `list_connectors` shows
  which are enabled; `add_connector` only returns the dashboard link. When a
  connector is enabled, calls go through the connector gateway and need
  `LOVABLE_API_KEY`. When Lovable does not provide one, users can bring their
  own key and store it in Secrets.
- **Secrets.** Lovable manages them. Do not put secret values in the repo.
<!-- LOVABLE:END -->
- The router uses normal browser history (not hash history): hash history breaks server rendering and left the preview blank.
- The V3 and V4 sidebar is the single dock in `Shell.tsx`, styled by one CSS block that shares the `--switch-*` tokens with the header V3/V4 switch; its one sliding indicator follows `data-active` (set on click) while `aria-current` follows the real route. Why: every sidebar item gets the same interaction quality without per-page copies.
- Every action button is `.eo-btn` with `data-variant` / `data-size` (via `<Btn>` or `btnProps()` from `src/components/eo/buttonSystem.ts`) and renders the premium primary look; only `danger` (destructive), `toggle` (on/off chips) and `quiet` (window controls, dock toggle, disclosure chevrons) differ, and legacy secondary/ghost/outline/success collapse to primary. All looks live in one styles.css block on the `--switch-*` tokens; pages never restyle buttons. Why: one consistent premium button across V3 and V4.
- Optimization pages get the ALL ELIGIBLE / CUSTOM switch only from the shared SelectionBar; its CUSTOM segment is the single Customize control, so pages must not add their own Customize button (avoids duplicates).
- Every individual item selection uses `SelectMark` (indicator) or `PremiumCircularCheckbox` (standalone control, legacy alias `Check`) from `kit.tsx`, styled only by the `.eo-sel` block in styles.css; pages never draw their own tick boxes. Segmented selectors, switches, dropdown options and radio cards keep their own controls. Why: one consistent circular selection across V3 and V4.
- All colours come from the twelve `--theme-*` colour variables written on `<html>` by `src/components/eo/themeStore.ts` (rules in `src/lib/eo/theme.ts`); styles.css derives every token from them and pages never hardcode hues — the one exception is the status-indicator palette (see StatusIndicator rule). Why: one global theme recolours V3 and V4 together.
- Character VFX are driven only by `data-vfx-motif` + `data-vfx-<effect>` levels on `<html>` (set by themeStore / VfxLayer); per-character scenes, button gradients and glows live in the one Character VFX block in styles.css. Why: one engine, every page gets the same effects.
- Settings is one Control Center (`SettingsCenter.tsx`): category nav + search over `SETTINGS_INDEX` in `src/lib/eo/settings.ts`, sections built from `settingsUi.tsx` rows/cards; every theme control lives only in Appearance & Themes and every effect control only in Animations & Effects (`ThemeSettings.tsx`); other preferences persist in `settingsStore.ts`. Why: one place per setting, searchable, no duplicates.
- The fresh-launch startup skeleton is static markup (`StartupSkeleton.tsx`) rendered once by RootShell, styled by inline critical CSS and driven by `<html data-eo-boot>` (loading → ready → done) from `src/components/eo/startup.ts`; only `markAppReady()` (Shell mount, root error/404) retires it and it never waits for backend services. Why: the document shell never remounts, so the skeleton can only appear on a fresh launch.
- Score gauges are only `ScoreRing` (controls.tsx): one rAF tween from `src/lib/eo/scoreTween.ts` writes the number, arc and ticks directly to the DOM (no endpoint marker on the arc); callers pass the real score and never render their own count-up. Why: number and gauge stay in sync and land exactly on the calculated score.
- Theme editing is draft-based with global Live Preview: `themeStore.ts` writes the SAVED theme on `<html>` unless in-memory Live Preview mode is on, then the draft (`state.live`) recolours every page; only the single Settings LIVE PREVIEW button turns it on (preset picks, colour edits and custom-theme saves never change it), and the one root-mounted `LivePreviewTray` saves (validated) or turns off (discards), and only the saved theme is ever persisted. Why: users inspect drafts anywhere without unsaved edits ever sticking.
- Every status display (service, Auto Optimize, emulator MSI/ADB/guest, engine run state, workload, RAM Limiter app states, process and connection states) is `StatusIndicator.tsx`: a non-interactive pill drawn in exactly three families via `statusFamily()` (pos green, warn yellow, neg red; err/muted render red, info renders yellow), with one shared breathing-dot animation, in the "Shared status indicators" block of styles.css. That block uses its own FIXED `--status-*` palette, font, sizes and timing (declared on `:root, .eo-st`) and never reads `--theme-*` or theme-derived tokens (`status-theme-lock.test.ts`). Why: presets, custom colours and Live Preview never change how a status looks, and it can never be mistaken for an action.
- On wide desktop windows the Settings page (`.eo-set-page`) is sized to the window and only its content column (`.eo-set-main`) scrolls; Settings, Backup & Restore and About layouts live in one "Settings, Backup & Restore and About layouts" block of styles.css. Why: no dead space below Settings and one place to tune these pages.
- Settings, Backup & Restore and About are the shared pages (`SHARED_SLUGS` in registry.ts): one page each, listed at the anchored bottom of the V4 dock (`V4_NAV_BOTTOM`) and in the V3 dock; when open, Shell keeps the suite from the remembered `lastSuite` and never changes it. Why: no duplicate pages and the user stays in the suite they came from.
- Global search is the one `SmartSearch.tsx` in the Shell header centre (every V3 + V4 route, Ctrl+K); it only searches `SEARCH_ENTRIES` built from the page registry in `src/lib/eo/smartSearch.ts` (keywords/modules per slug) and opens existing routes, letting Shell derive the suite. Why: no per-page search copies and no invented destinations.
- Optimization pages pass `scan={usePageScan(key, task?)}` to `PageHeader` (never `actions`): GUIDE + REFRESH only; scan rules in `src/lib/eo/pageScan.ts`. Why: no duplicate Scan buttons.
- LIVE PREVIEW button + tray use `eo-theme-scope eo-brand-lock` (fixed palette), never pressed styling. Why: preview never restyles its own controls.

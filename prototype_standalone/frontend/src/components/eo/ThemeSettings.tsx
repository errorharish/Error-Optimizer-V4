// Settings → Appearance & Themes (the one place every normal and custom theme is managed) and
// Animations & Motion. Theme controls edit only the draft, which global Live Preview mode shows on
// every page (tray in LivePreviewTray); Save validates and persists it. Turn off / Discard restores
// the saved theme. Motion controls write to the saved motion preferences.
import { useEffect, useState } from "react";
import { toast } from "sonner";
import { Eye, RotateCcw, Trash2 } from "lucide-react";
import { Badge, Btn, Details, Toggle } from "./kit";
import { Segmented } from "./controls";
import { PremiumSelect } from "./dropdown";
import { useAppearance } from "./Appearance";
import { SettingRow, SettingsCard, StatusDot } from "./settingsUi";
import {
  previewThemePreset,
  removeCustomTheme,
  resetThemeColor,
  resolveLive,
  saveCurrentAsCustomTheme,
  saveThemeDraft,
  isLivePreviewActive,
  livePreviewTheme,
  setThemeColor,
  useLivePreview,
  useThemeState,
} from "./themeStore";
import {
  CUSTOM_ID,
  DEFAULT_COLORS,
  DEFAULT_PRESET_ID,
  THEME_KEYS,
  THEME_KEY_LABELS,
  USER_PREFIX,
  filterThemes,
  isDirty,
  isValidHex,
  normalizeHex,
  validateThemeDraft,
  type ResolvedTheme,
  type ThemeColors,
  type ThemeKey,
  type ThemeState,
} from "@/lib/eo/theme";
import { DEPTH_OPTIONS, type DepthOption } from "@/lib/eo/settings";

const PREVIEW_KEYS: ThemeKey[] = ["primary", "secondary", "accent", "background", "surface", "success", "danger"];

function SwatchStrip({ colors }: { colors: ThemeColors }) {
  return (
    <span aria-hidden="true" className="eo-theme-strip">
      {PREVIEW_KEYS.map((k) => (
        <span key={k} style={{ background: colors[k] }} />
      ))}
    </span>
  );
}

function Orb({ t, size = 34 }: { t: ResolvedTheme; size?: number }) {
  return (
    <span
      aria-hidden="true"
      className="eo-theme-opt-orb"
      style={
        {
          width: size,
          height: size,
          "--opt-a": t.colors.accent,
          "--opt-p": t.colors.primary,
          "--opt-bg": t.colors.background,
        } as React.CSSProperties
      }
    />
  );
}

function OptionLabel({ t }: { t: ResolvedTheme }) {
  return (
    <span className="flex min-w-0 items-center gap-2">
      <SwatchStrip colors={t.colors} />
      <span className="truncate">{t.name}</span>
    </span>
  );
}

/** Selected-state badge for a theme in a picker. */
function ThemeStateBadge({ s, id }: { s: ThemeState; id: string }) {
  const dirty = isDirty(s);
  if (dirty && s.live.preset === id) return <Badge tone="warning">IN LIVE PREVIEW</Badge>;
  if (s.saved.preset === id) return <Badge tone="success">SAVED</Badge>;
  return null;
}

/** Validates and saves the draft, then reports the result. */
export function saveDraftWithToast() {
  const r = saveThemeDraft();
  if (r.ok) toast.success(`${r.name} saved`, { description: "Applied to V3 Classic and V4 Professional." });
  else toast.error("Theme not saved", { description: r.reason });
  return r.ok;
}

/** The single LIVE PREVIEW button. Turns Live Preview on with this theme; the floating tray is the
 *  only place to save or turn it off. ON and OFF look identical (brand-locked premium primary, no
 *  pressed styling, both labels share one reserved width); only the label changes. */
function LivePreviewButton({ id }: { id: string }) {
  const active = useLivePreview();
  return (
    <Btn size="sm" className="eo-theme-scope eo-brand-lock" onClick={() => livePreviewTheme(id)}>
      <Eye aria-hidden="true" className="h-3.5 w-3.5" />
      <span className="eo-lp-btn-label" data-on={active}>
        <span>Live Preview off</span>
        <span>Live Preview on</span>
      </span>
    </Btn>
  );
}

function ColorField({ k, value }: { k: ThemeKey; value: string }) {
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  const valid = isValidHex(text);
  const meta = THEME_KEY_LABELS[k];
  const id = `theme-${k}`;
  return (
    <div className="eo-theme-field" data-invalid={!valid || undefined}>
      <label className="eo-theme-swatch" title={`Pick ${meta.label.toLowerCase()}`}>
        <span className="sr-only">{meta.label} picker</span>
        <input type="color" value={value.toLowerCase()} onChange={(e) => setThemeColor(k, e.target.value)} />
        <span aria-hidden="true" style={{ background: value }} />
      </label>
      <div className="min-w-0 flex-1">
        <label htmlFor={id} className="block truncate text-[12px] font-semibold text-foreground">
          {meta.label}
        </label>
        <div className="truncate text-[10.5px] text-muted-foreground">
          {valid ? meta.hint : "Enter a hex value like #2563EB"}
        </div>
      </div>
      <input
        id={id}
        className="eo-theme-hex"
        value={text}
        spellCheck={false}
        maxLength={7}
        aria-invalid={!valid}
        onChange={(e) => {
          const v = e.target.value;
          setText(v);
          const hex = normalizeHex(v);
          if (hex && v.replace("#", "").length === 6) setThemeColor(k, hex);
        }}
        onBlur={() => {
          const hex = normalizeHex(text);
          if (hex) setThemeColor(k, hex);
          else setText(value);
        }}
        onKeyDown={(e) => {
          if (e.key === "Enter") (e.target as HTMLInputElement).blur();
        }}
      />
      <Btn
        variant="quiet"
        size="sm"
        label={`Reset ${meta.label.toLowerCase()} to default`}
        title="Reset to ERROR DEFAULT"
        disabled={value === DEFAULT_COLORS[k]}
        onClick={() => resetThemeColor(k)}
      >
        <RotateCcw aria-hidden="true" className="h-3.5 w-3.5" />
      </Btn>
    </div>
  );
}

/* ───────── Appearance & Themes ───────── */

function ClassicThemesCard({ s }: { s: ThemeState }) {
  const classic = filterThemes(s, "classic", "");
  const custom = filterThemes(s, "custom", "");
  const all = [...classic, ...custom];
  const liveId = s.live.preset;
  const [pick, setPick] = useState(() => (all.some((t) => t.id === liveId) ? liveId : DEFAULT_PRESET_ID));
  useEffect(() => {
    if (all.some((t) => t.id === liveId)) setPick(liveId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [liveId]);
  const t = all.find((x) => x.id === pick) ?? classic[0]!;
  const userId = t.id.startsWith(USER_PREFIX) ? t.id.slice(USER_PREFIX.length) : null;
  return (
    <SettingsCard id="theme-classic" title="CLASSIC & CUSTOM THEMES" sub="Clean colour presets and your own saved themes.">
      <div className="space-y-2.5">
        <PremiumSelect
          label="Theme preset"
          value={t.id}
          onChange={(id) => {
            setPick(id);
            if (isLivePreviewActive()) previewThemePreset(id);
          }}
          options={all.map((x) => ({
            value: x.id,
            label: <OptionLabel t={x} />,
            hint: x.category === "custom" ? (x.id === CUSTOM_ID ? "Working palette" : "Saved theme") : undefined,
          }))}
        />
        <div className="eo-set-pick">
          <Orb t={t} />
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-1.5">
              <span className="truncate text-[13px] font-bold text-foreground">{t.name}</span>
              <ThemeStateBadge s={s} id={t.id} />
            </div>
            <div className="mt-1">
              <SwatchStrip colors={t.colors} />
            </div>
          </div>
          {userId && (
            <Btn variant="danger" size="sm" label={`Delete ${t.name}`} onClick={() => removeCustomTheme(userId)}>
              <Trash2 aria-hidden="true" className="h-3.5 w-3.5" />
            </Btn>
          )}
        </div>
        <LivePreviewButton id={t.id} />
      </div>
    </SettingsCard>
  );
}

function ColorsCard({ s }: { s: ThemeState }) {
  const live = resolveLive(s);
  const [name, setName] = useState("");
  return (
    <SettingsCard
      id="theme-colors"
      title="MANUAL COLOR CUSTOMIZATION"
      sub="Change any colour; with Live Preview on it shows across the app right away. It becomes your Custom Theme."
      right={
        <div className="flex gap-1.5">
          <input
            aria-label="Custom theme name"
            placeholder="Theme name"
            value={name}
            maxLength={32}
            onChange={(e) => setName(e.target.value)}
            className="eo-theme-name w-36"
          />
          <Btn
            size="sm"
            onClick={() => {
              const check = validateThemeDraft(s);
              if (!check.ok) {
                toast.error("Theme not saved", { description: check.reason });
                return;
              }
              saveCurrentAsCustomTheme(name);
              setName("");
              toast.success("Custom theme saved", { description: "Added to your themes. Use SAVE THEME in the Live Preview tray to apply it." });
            }}
          >
            Save custom theme
          </Btn>
        </div>
      }
    >
      <Details
        inline
        title="CUSTOMIZE COLORS"
        hint="Accents, surfaces, text, border and status colours"
        defaultOpen={live.category === "custom"}
      >
        <div className="eo-set-colors">
          {THEME_KEYS.map((k) => (
            <ColorField key={k} k={k} value={live.colors[k]} />
          ))}
        </div>
        <p className="mt-2 text-[11px] text-muted-foreground">
          Any valid hex colour works (#RGB or #RRGGBB). Save changes or Save custom theme applies your
          colours everywhere and keeps them after a restart.
        </p>
      </Details>
    </SettingsCard>
  );
}

export function AppearanceSection() {
  const s = useThemeState();
  const appearance = useAppearance();
  return (
    <div className="space-y-3">
      <ClassicThemesCard s={s} />
      <ColorsCard s={s} />
      <div className="eo-card px-4" data-setting-id="display-depth">
        <Details inline title="ADVANCED DISPLAY" hint="Surface depth">
          <SettingRow
            title="3D Spatial Depth"
            desc="How much cards lift and tilt under your pointer."
            tip="Low keeps every surface flat. High adds deeper elevation."
          >
            <Segmented
              label="3D spatial depth"
              options={[...DEPTH_OPTIONS]}
              value={appearance.depth}
              onChange={(v) => appearance.setDepth(v as DepthOption)}
            />
          </SettingRow>
        </Details>
      </div>
    </div>
  );
}

/* ───────── Animations & Motion ───────── */

export function MotionSection() {
  const appearance = useAppearance();
  const motionOff = appearance.reduced || appearance.systemReduced;
  return (
    <div className="space-y-3">
      <SettingsCard
        title="ANIMATIONS & MOTION"
        sub="Functional motion only: smooth hover, press and page changes. Themes never add decorative effects."
      >
        <SettingRow
          id="fx-ui"
          title="UI Animations"
          desc="Smooth motion on buttons, cards and controls."
          status={<StatusDot tone={appearance.animations ? "success" : "muted"}>{appearance.animations ? "ON" : "OFF"}</StatusDot>}
        >
          <Toggle label="UI animations" on={appearance.animations} onChange={appearance.setAnimations} />
        </SettingRow>
        <SettingRow id="fx-page" title="Page Transitions" desc="Gentle fade when you switch pages.">
          <Toggle label="Page transitions" on={appearance.transitions} onChange={appearance.setTransitions} />
        </SettingRow>
        <SettingRow
          id="fx-reduced"
          title="Reduce Motion"
          desc="Stops non-essential motion. Your system accessibility setting is respected too."
          status={appearance.systemReduced ? <StatusDot tone="warning">SYSTEM ON</StatusDot> : undefined}
        >
          <Toggle label="Reduce motion" on={appearance.reduced} onChange={appearance.setReduced} />
        </SettingRow>
        <SettingRow
          id="fx-lite"
          title="Lightweight Rendering"
          desc="For low-end PCs: removes glass blur and entrance animations. Themes, status indicators and score updates stay the same."
        >
          <Toggle label="Lightweight rendering" on={appearance.lightweight} onChange={appearance.setLightweight} />
        </SettingRow>
      </SettingsCard>
      {motionOff && (
        <p className="text-[11px] text-warning">
          Reduce Motion is on, so animations and page transitions are paused. Theme colours are not affected.
        </p>
      )}
    </div>
  );
}

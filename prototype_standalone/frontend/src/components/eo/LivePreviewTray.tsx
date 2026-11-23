// The one global Live Preview tray. Mounted once at the root, so it survives every route change and
// V3/V4 switch. Visible only while Live Preview mode is on: APPEARANCE opens Settings → Appearance &
// Themes (draft and Live Preview untouched), SAVE THEME commits the draft, TURN OFF discards it.
// The tray is brand-locked (`eo-brand-lock`), so the previewed theme never restyles its controls.
import { useEffect, useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { Palette } from "lucide-react";
import { Btn } from "./kit";
import { saveDraftWithToast } from "./ThemeSettings";
import { stopLivePreview, useLivePreview, useThemeState, resolveLive } from "./themeStore";
import { requestSettingsCategory } from "./settingsStore";

export function LivePreviewTray() {
  const active = useLivePreview();
  const s = useThemeState();
  const navigate = useNavigate();
  // Keep it mounted briefly after turning off so the exit transition can play.
  const [shown, setShown] = useState(active);
  useEffect(() => {
    if (active) return setShown(true);
    const t = window.setTimeout(() => setShown(false), 220);
    return () => window.clearTimeout(t);
  }, [active]);
  if (!shown) return null;
  const openAppearance = () => {
    requestSettingsCategory("appearance");
    void navigate({ to: "/$page", params: { page: "settings" } });
  };
  return (
    <div
      className="eo-lp-tray eo-theme-scope eo-brand-lock"
      data-state={active ? "open" : "closed"}
      role="region"
      aria-label="Live Preview"
    >
      <span aria-hidden="true" className="eo-lp-dot" />
      <span className="eo-lp-label">
        Live Preview
        <span className="eo-lp-name">{resolveLive(s).name}</span>
      </span>
      <Btn
        size="sm"
        disabled={!active}
        onClick={openAppearance}
        title="Open Settings → Appearance & Themes (Live Preview stays on)"
      >
        <Palette aria-hidden="true" className="h-3.5 w-3.5" />
        Appearance
      </Btn>
      <Btn size="sm" disabled={!active} onClick={() => saveDraftWithToast()}>
        Save theme
      </Btn>
      <Btn size="sm" disabled={!active} onClick={stopLivePreview}>
        Turn off
      </Btn>
    </div>
  );
}

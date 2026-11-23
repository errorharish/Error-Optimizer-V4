// Motion & display preferences (UI animations, page transitions, reduced motion, depth). Values
// are saved in settingsStore so they survive reloads and are shared by V3 and V4; this provider
// writes the matching data-* attributes on <html>.
import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { setSettings, useSettings } from "./settingsStore";
import type { DepthOption } from "@/lib/eo/settings";

const AppearanceContext = createContext({
  reduced: false,
  systemReduced: false,
  animations: true,
  transitions: true,
  depth: "MEDIUM (3D)" as string,
  lightweight: false,
  setLightweight: (_: boolean) => {},
  setReduced: (_: boolean) => {},
  setAnimations: (_: boolean) => {},
  setTransitions: (_: boolean) => {},
  setDepth: (_: string) => {},
});

export function AppearanceProvider({ children }: { children: ReactNode }) {
  const s = useSettings();
  const [systemReduced, setSystemReduced] = useState(false);
  useEffect(() => {
    const query = window.matchMedia("(prefers-reduced-motion: reduce)");
    const update = () => setSystemReduced(query.matches);
    update();
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);
  useEffect(() => {
    const root = document.documentElement;
    root.dataset["motion"] = s.reducedMotion || systemReduced || !s.uiAnimations ? "reduced" : "full";
    root.dataset["transitions"] = s.pageTransitions ? "on" : "off";
    root.dataset["depth"] = s.depth.startsWith("LOW") ? "flat" : s.depth.startsWith("HIGH") ? "deep" : "subtle";
    root.dataset["eoLite"] = s.lightweight ? "on" : "off";
  }, [s.reducedMotion, systemReduced, s.uiAnimations, s.pageTransitions, s.depth, s.lightweight]);
  return (
    <AppearanceContext.Provider
      value={{
        reduced: s.reducedMotion,
        systemReduced,
        animations: s.uiAnimations,
        transitions: s.pageTransitions,
        depth: s.depth,
        lightweight: s.lightweight,
        setLightweight: (v) => setSettings({ lightweight: v }),
        setReduced: (v) => setSettings({ reducedMotion: v }),
        setAnimations: (v) => setSettings({ uiAnimations: v }),
        setTransitions: (v) => setSettings({ pageTransitions: v }),
        setDepth: (v) => setSettings({ depth: v as DepthOption }),
      }}
    >
      {children}
    </AppearanceContext.Provider>
  );
}

export const useAppearance = () => useContext(AppearanceContext);

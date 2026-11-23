// True while the app window/tab is visible. Decorative timers (demo charts, simulated meters)
// pause when the window is hidden or minimised so they cost nothing in the background.
import { useSyncExternalStore } from "react";

function subscribe(l: () => void) {
  document.addEventListener("visibilitychange", l);
  return () => document.removeEventListener("visibilitychange", l);
}

export function usePageVisible(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => document.visibilityState !== "hidden",
    () => true,
  );
}

// Soft click ripple on interactive buttons (never on disabled controls). Off when motion is reduced.
import { useEffect } from "react";

export function useClickRipple() {
  useEffect(() => {
    const onDown = (e: PointerEvent) => {
      if (document.documentElement.dataset["motion"] === "reduced") return;
      const el = (e.target as Element | null)?.closest<HTMLElement>(".eo-btn, [data-ripple]");
      if (!el || el.matches(":disabled, [aria-disabled='true']")) return;
      const r = el.getBoundingClientRect();
      const size = Math.max(r.width, r.height) * 1.6;
      const dot = document.createElement("span");
      dot.className = "eo-ripple";
      dot.setAttribute("aria-hidden", "true");
      dot.style.cssText = `width:${size}px;height:${size}px;left:${e.clientX - r.left - size / 2}px;top:${e.clientY - r.top - size / 2}px`;
      el.appendChild(dot);
      const done = () => dot.remove();
      dot.addEventListener("animationend", done, { once: true });
      window.setTimeout(done, 900);
    };
    document.addEventListener("pointerdown", onDown, { capture: true, passive: true });
    return () => document.removeEventListener("pointerdown", onDown, { capture: true });
  }, []);
}

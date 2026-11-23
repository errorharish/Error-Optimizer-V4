// Fresh-launch startup skeleton (#eo-boot, rendered once by RootShell in __root.tsx).
// Lifecycle lives on <html data-eo-boot>: "loading" (set by the inline BOOT_SCRIPT before first
// paint) → "ready" (fade out) → "done" (display:none). The skeleton is part of the document shell,
// which never remounts, so client navigation, V3/V4 switches, Settings, scans and service
// reconnects can never bring it back. Readiness means "the interface has rendered" — it never
// waits for the Windows service, scans or network requests.

import { PAGES, SHARED_SLUGS, V4_REDIRECTS } from "@/lib/eo/registry";
import { SETTINGS_KEY } from "./settingsStore";

const BOOT_ATTR = "data-eo-boot";

/** Which skeleton layout the first route needs: V3 Dashboard, V4 Dashboard, Settings or a standard page. */
export type BootView = "dash" | "v4dash" | "settings" | "page";
export type BootSuite = "V3" | "V4";

const V4_SLUGS = [
  ...PAGES.filter((p) => p.suite === "V4").map((p) => p.slug),
  ...Object.keys(V4_REDIRECTS),
];

/** Plain ES5 route classifier embedded in BOOT_SCRIPT (and evaluated by the tests), mirroring how
 *  Shell picks the suite: V4 pages are V4, shared pages keep the remembered suite, the rest is V3. */
export const BOOT_ROUTE_FN = `function(path,last){var v4=${JSON.stringify(V4_SLUGS)},sh=${JSON.stringify(SHARED_SLUGS)};var s=String(path||"").split("?")[0].split("#")[0].replace(/^[/]+|[/]+$/g,"").split("/")[0];var view=s===""||s==="v4-dashboard"?"dash":s==="settings"?"settings":"page";return{view:view,suite:"V4"};}`;
/** Show a "taking longer than usual" fallback (with Reload) if the UI has not rendered by then. */
export const BOOT_SLOW_MS = 15000;
/** Never hold the skeleton longer than this waiting for web fonts. */
export const BOOT_FONT_WAIT_MS = 450;
export const BOOT_FADE_MS = 320;

/** Inline <head> script: runs before first paint. Plain ES5, no imports. */
export const BOOT_SCRIPT = `(function(){try{var d=document.documentElement;if(d.getAttribute("${BOOT_ATTR}"))return;d.setAttribute("${BOOT_ATTR}","loading");try{if(localStorage.getItem("eo-dock-collapsed")==="1")d.setAttribute("data-eo-boot-dock","collapsed");}catch(e){}var last=null;try{last=JSON.parse(localStorage.getItem("${SETTINGS_KEY}")||"{}").lastSuite;}catch(e){}var p=location.hash&&location.hash.length>1?location.hash.slice(1):(location.pathname==="/index.html"?"/":location.pathname);var r=(${BOOT_ROUTE_FN})(p,last);d.setAttribute("data-eo-boot-view",r.view);d.setAttribute("data-eo-boot-suite",r.suite);window.__eoBootTimer=setTimeout(function(){if(d.getAttribute("${BOOT_ATTR}")==="loading")d.setAttribute("${BOOT_ATTR}","slow");},${BOOT_SLOW_MS});document.addEventListener("click",function(e){var t=e.target;if(t&&t.closest&&t.closest("[data-eo-boot-reload]"))location.reload();});}catch(e){}})();`;

/** Critical CSS for the skeleton, inlined in <head> so it never depends on the app stylesheet.
 *  Colours come from the saved --theme-* variables (written by the theme pre-paint script). */
export const BOOT_STYLE = `
html{background:var(--theme-background,#050811);color-scheme:dark}
#eo-boot{--b-bg:var(--theme-background,#050811);--b-sf:var(--theme-surface,#0D1426);--b-el:var(--theme-elevated,#121B32);--b-bd:var(--theme-border,#1A2544);--b-tx:var(--theme-text,#F8FAFC);--b-mt:var(--theme-text-muted,#94A3B8);--b-pr:var(--theme-primary,#2563EB);--b-ac:var(--theme-accent,#38BDF8);--b-chrome:color-mix(in oklab,color-mix(in oklab,var(--b-sf) 25%,var(--b-bg)) 72%,transparent);--b-dur:1.9s;
position:fixed;inset:0;z-index:2147483000;display:flex;flex-direction:column;overflow:hidden;color:var(--b-tx);font-family:Arial,Helvetica,sans-serif;
background:radial-gradient(1100px 560px at 12% -12%,color-mix(in oklab,var(--b-pr) 15%,transparent),transparent 62%),radial-gradient(820px 480px at 108% 4%,color-mix(in oklab,var(--b-ac) 7%,transparent),transparent 60%),var(--b-bg);
opacity:1;transition:opacity ${BOOT_FADE_MS}ms cubic-bezier(.2,.8,.2,1);cursor:progress;user-select:none}
html[${BOOT_ATTR}="ready"] #eo-boot{opacity:0;pointer-events:none}
html[${BOOT_ATTR}="ready"] #eo-boot *,html[${BOOT_ATTR}="ready"] #eo-boot *::after{animation-play-state:paused!important}
html[${BOOT_ATTR}="done"] #eo-boot{display:none}
#eo-boot *{box-sizing:border-box}
.eob-sk{position:relative;overflow:hidden;display:block;flex-shrink:0;border-radius:6px;background:linear-gradient(180deg,color-mix(in oklab,var(--b-tx) 8%,transparent),color-mix(in oklab,var(--b-tx) 5%,transparent));box-shadow:inset 0 1px 0 color-mix(in oklab,var(--b-tx) 4%,transparent)}
.eob-sk::after{content:"";position:absolute;inset:0;transform:translateX(-100%);background:linear-gradient(100deg,transparent 20%,color-mix(in oklab,var(--b-tx) 6%,transparent) 42%,color-mix(in oklab,var(--b-ac) 11%,transparent) 50%,color-mix(in oklab,var(--b-tx) 6%,transparent) 58%,transparent 80%);animation:eob-shimmer var(--b-dur) cubic-bezier(.45,.05,.25,1) infinite;animation-delay:var(--b-d,0s);will-change:transform}
@keyframes eob-shimmer{0%{transform:translateX(-100%)}62%,100%{transform:translateX(100%)}}
.eob-in{animation:eob-in .42s cubic-bezier(.2,.8,.2,1) both;animation-delay:calc(var(--b-d,0s) * .35)}
@keyframes eob-in{from{opacity:0;transform:translateY(4px)}to{opacity:1;transform:none}}
.eob-header{height:3rem;flex-shrink:0;display:flex;align-items:center;gap:14px;padding:0 16px;box-shadow:inset 0 -1px 0 var(--b-bd);background:var(--b-chrome)}
.eob-brand{display:flex;flex-shrink:0;align-items:center;gap:9px}
.eob-mark{position:relative;display:flex;width:30px;height:30px;flex-shrink:0;align-items:center;justify-content:center;border-radius:9px;color:#fff;background:linear-gradient(135deg,var(--b-pr),var(--b-ac));box-shadow:0 6px 18px -10px color-mix(in oklab,var(--b-pr) 60%,transparent),inset 0 1px 0 color-mix(in oklab,#fff 35%,transparent)}
.eob-mark svg{width:14px;height:14px}
.eob-name{display:block;font-size:13.5px;font-weight:900;letter-spacing:.06em;line-height:1.1}
.eob-ed{margin-top:4px;width:62px;height:7px}
.eob-suite{display:none!important}
.eob-suite>span{border-radius:6px}
.eob-suite>span:first-child,html[data-eo-boot-suite="V4"] .eob-suite>span:last-child{background:linear-gradient(135deg,color-mix(in oklab,var(--b-pr) 55%,transparent),color-mix(in oklab,var(--b-ac) 35%,transparent));box-shadow:inset 0 1px 0 color-mix(in oklab,#fff 18%,transparent)}
html[data-eo-boot-suite="V4"] .eob-suite>span:first-child{background:none;box-shadow:none}
.eob-search{flex:1 1 auto;min-width:0;display:flex;justify-content:center;padding:0 16px}
.eob-sfield{display:flex;align-items:center;gap:10px;width:100%;max-width:440px;height:32px;padding:0 10px;border-radius:9px;border:1px solid color-mix(in oklab,var(--b-ac) 22%,var(--b-bd));background:color-mix(in oklab,var(--b-sf) 55%,transparent)}
.eob-hend{display:flex;flex-shrink:0;align-items:center;gap:8px}
.eob-grow{flex:1}
.eob-center{margin:0 auto}
.eob-end{margin-left:auto}
.eob-pill{height:24px;border-radius:999px}
.eob-body{display:flex;flex:1;min-height:0}
.eob-dock{width:84px;flex-shrink:0;display:flex;flex-direction:column;border-right:1px solid var(--b-bd);background:var(--b-chrome);overflow:hidden}
.eob-dnav{display:flex;flex:1;min-height:0;flex-direction:column;align-items:center;gap:.125rem;padding:.5rem 0;overflow:hidden}
.eob-dnav[data-suite="V4"],html[data-eo-boot-suite="V4"] .eob-dnav[data-suite="V3"]{display:none}
html[data-eo-boot-suite="V4"] .eob-dnav[data-suite="V4"]{display:flex}
.eob-dtoggle{display:flex;flex-shrink:0;justify-content:center;padding:6px 0;border-top:1px solid var(--b-bd)}
html[data-eo-boot-dock="collapsed"] .eob-dock{width:60px}
html[data-eo-boot-dock="collapsed"] .eob-lbl{display:none}
.eob-nav{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:.4rem;width:68px;height:44px;border-radius:8px;flex-shrink:0}
html[data-eo-boot-dock="collapsed"] .eob-nav{width:44px}
.eob-nav[data-on]{background:linear-gradient(135deg,color-mix(in oklab,var(--b-pr) 34%,transparent),color-mix(in oklab,var(--b-ac) 14%,transparent));box-shadow:inset 0 0 0 1px color-mix(in oklab,var(--b-ac) 26%,transparent)}
html:is([data-eo-boot-view="page"],[data-eo-boot-view="settings"]) .eob-nav[data-on]{background:none;box-shadow:none}
.eob-ico{width:1rem!important;height:1rem!important;border-radius:5px}
.eob-lbl{width:40px;height:6px;border-radius:3px}
.eob-sep{width:40px;height:1px;margin:.375rem 0;background:var(--b-bd);flex-shrink:0}
.eob-sep[data-bottom]{margin-top:auto}
.eob-main{position:relative;flex:1;min-width:0;overflow:hidden}
.eob-v{display:none;max-width:1440px;margin:0 auto;padding:18px 24px;flex-direction:column;gap:.875rem}
html:not([data-eo-boot-view]) .eob-v[data-v="dash"],html[data-eo-boot-view="dash"] .eob-v[data-v="dash"],html[data-eo-boot-view="v4dash"] .eob-v[data-v="v4dash"],html[data-eo-boot-view="settings"] .eob-v[data-v="settings"],html[data-eo-boot-view="page"] .eob-v[data-v="page"]{display:flex}
.eob-row{display:flex;align-items:center;gap:10px}
.eob-col{display:flex;flex-direction:column;gap:8px}
.eob-card{border-radius:10px;border:1px solid var(--b-bd);background:linear-gradient(160deg,color-mix(in oklab,var(--b-tx) 3.5%,transparent),transparent 42%),color-mix(in oklab,var(--b-sf) 90%,transparent);box-shadow:inset 0 1px 0 color-mix(in oklab,var(--b-tx) 6%,transparent),0 10px 28px -18px rgba(0,0,0,.85)}
.eob-hero{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}
.eob-hero>.eob-card:nth-child(2),.eob-2>.eob-card:nth-child(2),.eob-3>.eob-card:nth-child(2),.eob-metrics>.eob-card:nth-child(2){--b-d:.14s}
.eob-hero>.eob-card:nth-child(3),.eob-3>.eob-card:nth-child(3),.eob-metrics>.eob-card:nth-child(3){--b-d:.28s}
.eob-metrics>.eob-card:nth-child(4){--b-d:.42s}
.eob-metrics>.eob-card:nth-child(5){--b-d:.56s}
.eob-score,.eob-panel{min-width:0;min-height:376px;padding:16px;display:flex;flex-direction:column;align-items:stretch;border-color:color-mix(in oklab,var(--b-ac) 18%,var(--b-bd))}
.eob-score{gap:12px}
.eob-panel{gap:11px}
.eob-ring{position:relative;flex-shrink:0;width:128px;height:128px;margin:2px 0 0;border-radius:50%;background:conic-gradient(from 0deg,color-mix(in oklab,var(--b-ac) 45%,transparent),color-mix(in oklab,var(--b-pr) 25%,transparent) 22%,color-mix(in oklab,var(--b-tx) 5%,transparent) 34%,color-mix(in oklab,var(--b-tx) 5%,transparent));-webkit-mask:radial-gradient(farthest-side,transparent calc(100% - 20px),#000 calc(100% - 19px),#000 calc(100% - 9px),transparent calc(100% - 8px));mask:radial-gradient(farthest-side,transparent calc(100% - 20px),#000 calc(100% - 19px),#000 calc(100% - 9px),transparent calc(100% - 8px));animation:eob-spin 2.6s cubic-bezier(.5,.15,.5,.85) infinite;will-change:transform}
.eob-ring[data-lg]{width:148px;height:148px}
@keyframes eob-spin{to{transform:rotate(1turn)}}
.eob-cats{margin:auto 0}
.eob-bar{display:grid;grid-template-columns:90px 1fr 22px;gap:10px;align-items:center}
.eob-head{display:grid;grid-template-columns:auto minmax(0,1fr) auto;align-items:start;gap:12px}
.eob-cd{display:grid;grid-template-columns:repeat(4,auto);justify-content:start;gap:18px}
.eob-grid2{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:6px}
.eob-foot{margin-top:auto;padding-top:12px;border-top:1px solid var(--b-bd)}
.eob-tbody{display:flex;flex:1;flex-direction:column;gap:8px;min-height:0}
.eob-tchart{flex:1;min-height:0;min-width:0;gap:6px}
.eob-graph{position:relative;flex:1;min-height:92px;padding:0;border-bottom:1px dashed var(--b-bd);background-image:repeating-linear-gradient(180deg,transparent 0 calc(25% - 1px),color-mix(in oklab,var(--b-bd) 70%,transparent) calc(25% - 1px) 25%);margin:0 0 15px 26px;overflow:hidden}
.eob-graph svg{position:absolute;inset:0;width:100%;height:100%;overflow:visible}
.eob-graph path{fill:none;stroke:color-mix(in oklab,var(--b-tx) 10%,transparent);stroke-width:1.75;vector-effect:non-scaling-stroke}
.eob-graph path.eob-flow{stroke:color-mix(in oklab,var(--b-ac) 40%,transparent);stroke-dasharray:18 82;stroke-dashoffset:100;animation:eob-flow 2.8s linear infinite}
.eob-graph path.eob-flow+path.eob-flow{stroke:color-mix(in oklab,var(--b-pr) 45%,transparent);animation-delay:-1.4s}
@keyframes eob-flow{to{stroke-dashoffset:0}}
.eob-box{border-radius:8px;border:1px solid var(--b-bd);background:color-mix(in oklab,var(--b-bg) 45%,transparent);padding:14px}
.eob-metrics{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:10px}
.eob-metric{padding:12px;display:flex;flex-direction:column;gap:10px;height:125px}
.eob-2{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px}
.eob-3{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:14px}
.eob-v4card{min-height:260px;padding:18px;display:flex;flex-direction:column;gap:12px}
.eob-mod{padding:16px;display:flex;flex-direction:column;gap:10px;min-height:170px}
.eob-selbar{display:flex;align-items:center;gap:12px;padding:10px 12px}
.eob-seg{display:grid;grid-template-columns:110px 90px;gap:3px;padding:3px;border-radius:9px;border:1px solid var(--b-bd)}
.eob-seg>span:first-child{background:linear-gradient(135deg,color-mix(in oklab,var(--b-pr) 50%,transparent),color-mix(in oklab,var(--b-ac) 30%,transparent))}
.eob-list{padding:6px 14px}
.eob-item{display:grid;grid-template-columns:18px 30px minmax(0,1fr) auto;align-items:center;gap:12px;padding:12px 0;border-bottom:1px solid color-mix(in oklab,var(--b-bd) 70%,transparent)}
.eob-item:last-child{border-bottom:0}
.eob-actbar{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:12px 14px}
.eob-set{display:grid;grid-template-columns:240px minmax(0,1fr);gap:14px;align-items:start}
.eob-setnav{padding:10px;display:flex;flex-direction:column;gap:6px}
.eob-setnav>.eob-row{height:34px;padding:0 8px;border-radius:8px}
.eob-setnav>.eob-row[data-on]{background:color-mix(in oklab,var(--b-pr) 22%,transparent);box-shadow:inset 0 0 0 1px color-mix(in oklab,var(--b-ac) 22%,transparent)}
.eob-setrow{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:12px 0;border-bottom:1px solid color-mix(in oklab,var(--b-bd) 70%,transparent)}
.eob-setrow:last-child{border-bottom:0}
@media (max-width:1131px){.eob-hero{grid-template-columns:repeat(2,minmax(0,1fr))}.eob-temp{grid-column:1/-1;min-height:270px}.eob-tbody{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1.5fr);gap:10px}.eob-metrics{grid-template-columns:repeat(3,minmax(0,1fr))}.eob-3{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media (max-width:1023px){.eob-2{grid-template-columns:minmax(0,1fr)}.eob-set{grid-template-columns:minmax(0,1fr)}.eob-setnav{display:none}}
@media (max-width:892px){.eob-hero{grid-template-columns:minmax(0,1fr)}.eob-score{min-height:310px}.eob-panel{min-height:330px}.eob-temp{min-height:350px}.eob-3{grid-template-columns:minmax(0,1fr)}}
.eob-footer{height:2rem;flex-shrink:0;display:flex;align-items:center;justify-content:space-between;padding:0 16px;border-top:1px solid var(--b-bd);background:var(--b-chrome)}
.eob-status{position:absolute;left:50%;bottom:22px;transform:translateX(-50%);display:none;align-items:center;gap:12px;padding:10px 16px 10px 12px;border-radius:12px;border:1px solid color-mix(in oklab,var(--b-ac) 22%,var(--b-bd));background:color-mix(in oklab,var(--b-el) 88%,transparent);box-shadow:0 18px 40px -20px rgba(0,0,0,.8),inset 0 1px 0 color-mix(in oklab,var(--b-tx) 7%,transparent);white-space:nowrap}
html[${BOOT_ATTR}="slow"] .eob-status{display:flex}
.eob-status .eob-mark{width:26px;height:26px;border-radius:8px}
.eob-slow{font-size:11px;color:var(--b-mt);white-space:normal;max-width:280px}
.eob-slow button{margin-top:8px;height:26px;padding:0 12px;border-radius:7px;border:0;font:inherit;font-size:11px;font-weight:700;letter-spacing:.04em;color:#fff;background:linear-gradient(135deg,var(--b-pr),var(--b-ac));cursor:pointer}
html[${BOOT_ATTR}="slow"] #eo-boot{cursor:default}
html[${BOOT_ATTR}="slow"] #eo-boot *,html[${BOOT_ATTR}="slow"] #eo-boot *::after{animation-play-state:paused}
@media (max-width:760px){.eob-temp{min-height:348px}.eob-tbody{display:flex}.eob-dock{width:60px}.eob-lbl{display:none}.eob-nav{width:44px}.eob-hero{grid-template-columns:minmax(0,1fr)}.eob-metrics{grid-template-columns:repeat(2,minmax(0,1fr))}.eob-v{padding:14px}.eob-hide-sm{display:none}.eob-search{padding:0 8px}}
@media (max-width:480px){.eob-metrics{grid-template-columns:minmax(0,1fr)}.eob-suite{display:none}}
@media (max-height:860px){.eob-nav{height:43px;gap:.3rem}.eob-sep{margin:4px 0}}
html[data-eo-lite] #eo-boot .eob-sk::after{animation-duration:2.6s}
@media (prefers-reduced-motion:reduce){#eo-boot,#eo-boot *,#eo-boot *::after{animation:none!important;transition:none!important}}
html[data-motion="reduced"] #eo-boot,html[data-motion="reduced"] #eo-boot *,html[data-motion="reduced"] #eo-boot *::after{animation:none!important;transition:none!important}
`;

declare global {
  interface Window {
    __eoBootTimer?: ReturnType<typeof setTimeout>;
    __eoBootReady?: boolean;
  }
}

/** Returns true while the application is in its initial boot lifecycle (#eo-boot overlay active).
 *  Decommissioned: Landing/startup full-screen overlay removed in favor of single page-level Lovable skeleton. */
export function isAppBooting(): boolean {
  return false;
}

/** Called once the real interface has committed.
 *  Decommissioned: No full-screen overlay to fade out. */
export function markAppReady(): void {
  // No-op: Full-screen boot overlay removed.
}

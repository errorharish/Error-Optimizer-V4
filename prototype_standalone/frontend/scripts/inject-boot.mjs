import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import React from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createServer } from "vite";
import tsconfigPaths from "vite-tsconfig-paths";

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const distHtmlPath = path.join(rootDir, "dist", "index.html");

const server = await createServer({
  root: rootDir,
  configFile: false,
  plugins: [tsconfigPaths()],
  server: { middlewareMode: true },
  appType: "custom",
  logLevel: "error",
});

try {
  const [{ THEME_PREPAINT_SCRIPT }, { BOOT_SCRIPT, BOOT_STYLE }, { StartupSkeleton }] =
    await Promise.all([
      server.ssrLoadModule("/src/components/eo/themeStore.ts"),
      server.ssrLoadModule("/src/components/eo/startup.ts"),
      server.ssrLoadModule("/src/components/eo/StartupSkeleton.tsx"),
    ]);

  const skeletonHtml = renderToStaticMarkup(React.createElement(StartupSkeleton));
  const headInjection = [
    `<script>${THEME_PREPAINT_SCRIPT}</script>`,
    `<style>${BOOT_STYLE}</style>`,
    `<script>${BOOT_SCRIPT}</script>`,
  ].join("\n    ");

  let html = fs.readFileSync(distHtmlPath, "utf8");
  if (!html.includes('name="eo-boot-head"') || !html.includes('id="eo-boot-slot"')) {
    throw new Error("Missing boot injection markers in dist/index.html");
  }

  html = html.replace(/<meta\s+name="eo-boot-head"\s*\/?>/, () => headInjection);
  html = html.replace(/<div\s+id="eo-boot-slot"\s*><\/div>/, () => skeletonHtml);
  fs.writeFileSync(distHtmlPath, html, "utf8");
  console.log("Injected StartupSkeleton & pre-paint boot assets into dist/index.html");
} finally {
  await server.close();
}

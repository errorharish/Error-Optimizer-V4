import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  Outlet,
  Link,
  createRootRouteWithContext,
  useRouter,
  type ErrorComponentProps,
} from "@tanstack/react-router";
import { useEffect, type ReactNode } from "react";

import { LivePreviewTray } from "@/components/eo/LivePreviewTray";
import { ExecutionResultDialog } from "@/components/eo/ExecutionResult";
import { reportLovableError } from "../lib/lovable-error-reporting";
import { btnProps } from "@/components/eo/buttonSystem";
import { AppearanceProvider } from "@/components/eo/Appearance";
import { Shell } from "@/components/eo/Shell";
import { Toaster } from "@/components/ui/sonner";

function NotFoundComponent() {
  return (
    <div className="flex min-h-screen items-center justify-center bg-background px-4">
      <div className="max-w-md text-center">
        <h1 className="text-7xl font-bold text-foreground">404</h1>
        <h2 className="mt-4 text-xl font-semibold text-foreground">Page not found</h2>
        <p className="mt-2 text-sm text-muted-foreground">
          The page you're looking for doesn't exist or has been moved.
        </p>
        <div className="mt-6">
          <Link
            to="/"
            {...btnProps("primary", "lg")}
          >
            Go home
          </Link>
        </div>
      </div>
    </div>
  );
}

function ErrorComponent({ error, reset }: ErrorComponentProps) {
  console.error(error);
  const router = useRouter();
  useEffect(() => {
    reportLovableError(error, { boundary: "tanstack_root_error_component" });
  }, [error]);

  const err = error instanceof Error ? error : (error && typeof error === "object" && "message" in error ? error as { message?: string; stack?: string } : null);
  const errorText = err?.message || (typeof error === "string" ? error : "An unexpected error occurred during application startup.");
  const errStack = err?.stack;

  return (
    <div className="flex min-h-screen items-center justify-center bg-background px-4">
      <div className="max-w-lg text-center">
        <h1 className="text-xl font-semibold tracking-tight text-foreground">
          This page didn't load
        </h1>
        <p className="mt-2 text-sm text-muted-foreground">
          Something went wrong on our end. You can try refreshing or head back home.
        </p>
        <div className="mt-4 rounded-lg border border-border bg-window/70 p-3 text-left font-mono text-xs text-danger select-text">
          <div className="font-semibold break-words">{errorText}</div>
          {errStack && (
            <details className="mt-2 text-muted-foreground">
              <summary className="cursor-pointer text-[11px] text-cyan hover:underline">
                View diagnostic stack trace
              </summary>
              <pre className="mt-1.5 whitespace-pre-wrap text-[10px] max-h-40 overflow-y-auto">
                {errStack}
              </pre>
            </details>
          )}
        </div>
        <div className="mt-6 flex flex-wrap justify-center gap-2">
          <button
            type="button"
            onClick={() => {
              router.invalidate();
              reset();
            }}
            {...btnProps("primary", "lg")}
          >
            Try again
          </button>
          <button
            type="button"
            onClick={() => {
              window.history.back();
              reset();
            }}
            {...btnProps("secondary", "lg")}
          >
            Go back
          </button>
          <Link
            to="/"
            onClick={() => reset()}
            {...btnProps("secondary", "lg")}
          >
            Go home
          </Link>
        </div>
      </div>
    </div>
  );
}

export const Route = createRootRouteWithContext<{ queryClient: QueryClient }>()({
  head: () => ({
    meta: [
      { charSet: "utf-8" },
      { name: "viewport", content: "width=device-width, initial-scale=1" },
      { title: "ERROR OPTIMIZER" },
      {
        name: "description",
        content:
          "ERROR OPTIMIZER V3 and V4 desktop system optimization, telemetry, and tuning suite.",
      },
      { property: "og:type", content: "website" },
      { name: "twitter:card", content: "summary_large_image" },
    ],
    links: [
      { rel: "icon", href: "./favicon.png", type: "image/png" },
    ],
  }),
  shellComponent: RootShell,
  component: RootComponent,
  notFoundComponent: NotFoundComponent,
  errorComponent: ErrorComponent,
});

function RootShell({ children }: { children: ReactNode }) {
  // In standalone SPA mode, index.html already owns <html>, <head>, <body>, and #eo-boot.
  return <>{children}</>;
}

function RootComponent() {
  const { queryClient } = Route.useRouteContext();

  useEffect(() => {
    // Notify native host that the initial application shell is mounted and painted
    const frameId = requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        try {
          if (typeof window !== "undefined" && window.chrome?.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ action: "app:ready" }));
          }
        } catch {
          // Ignore if outside desktop host
        }
      });
    });
    return () => cancelAnimationFrame(frameId);
  }, []);

  return (
    <QueryClientProvider client={queryClient}>
      <AppearanceProvider>
        <Shell>
          {/* Required: nested routes render here. Removing <Outlet /> breaks all child routes. */}
          <Outlet />
        </Shell>
      </AppearanceProvider>
      <LivePreviewTray />
      <ExecutionResultDialog />
      <Toaster theme="dark" position="top-right" />
    </QueryClientProvider>
  );
}

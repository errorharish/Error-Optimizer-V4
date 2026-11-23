import { createFileRoute, notFound, redirect, useRouter, type ErrorComponentProps } from "@tanstack/react-router";
import { V4_REDIRECTS, pageBySlug } from "@/lib/eo/registry";
import { PAGE_COMPONENTS } from "@/components/eo/pageMap";
import { PageGuideContext } from "@/components/eo/Guide";
import { pageGuide } from "@/lib/eo/guides";
import { PageErrorBoundary } from "@/components/eo/PageErrorBoundary";
import { Card, Btn, Badge } from "@/components/eo/kit";
import { Link } from "@tanstack/react-router";
import { AlertCircle, RefreshCw, ArrowLeft, Home } from "lucide-react";

function PageRouteErrorComponent({ error, reset }: ErrorComponentProps) {
  const router = useRouter();
  const err = error instanceof Error ? error : (error && typeof error === "object" && "message" in error ? error as { message?: string; stack?: string } : null);
  const errMsg = err?.message || (typeof error === "string" ? error : "An unexpected error occurred while loading this route.");
  const errStack = err?.stack;

  return (
    <div className="space-y-4 p-2">
      <Card className="border-danger/40 bg-window/80 p-6">
        <div className="flex items-start gap-3">
          <div className="rounded-md bg-danger/10 p-2 text-danger">
            <AlertCircle className="h-6 w-6" />
          </div>
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2">
              <h2 className="text-base font-bold text-foreground">
                Page Loading Exception
              </h2>
              <Badge tone="danger">ROUTE BOUNDARY</Badge>
            </div>
            <p className="mt-1 text-xs text-muted-foreground">
              This specific page could not complete initialization. All other sections of Error Optimizer remain operational.
            </p>

            <div className="mt-4 rounded-lg border border-border bg-window/60 p-3 font-mono text-[11px] text-danger/90 overflow-x-auto select-text">
              <div className="font-semibold">{errMsg}</div>
              {errStack && (
                <details className="mt-2 text-muted-foreground">
                  <summary className="cursor-pointer text-[10px] text-cyan hover:underline">
                    View stack trace
                  </summary>
                  <pre className="mt-1.5 whitespace-pre-wrap text-[10px] max-h-48 overflow-y-auto">
                    {errStack}
                  </pre>
                </details>
              )}
            </div>

            <div className="mt-5 flex flex-wrap items-center gap-2.5">
              <Btn
                variant="primary"
                size="sm"
                onClick={() => {
                  router.invalidate();
                  reset();
                }}
              >
                <RefreshCw className="mr-1.5 h-3.5 w-3.5" />
                Retry loading
              </Btn>
              <Btn variant="secondary" size="sm" onClick={() => window.history.back()}>
                <ArrowLeft className="mr-1.5 h-3.5 w-3.5" />
                Go back
              </Btn>
              <Link to="/">
                <Btn variant="ghost" size="sm">
                  <Home className="mr-1.5 h-3.5 w-3.5" />
                  Return to Dashboard
                </Btn>
              </Link>
            </div>
          </div>
        </div>
      </Card>
    </div>
  );
}

export const Route = createFileRoute("/$page")({
  loader: ({ params }) => {
    const moved = V4_REDIRECTS[params.page];
    if (moved !== undefined) {
      if (moved === "") {
        throw redirect({ to: "/", replace: true });
      }
      throw redirect({ to: "/$page", params: { page: moved }, replace: true });
    }
    const p = pageBySlug(params.page);
    if (!p || !PAGE_COMPONENTS[params.page]) throw notFound();
    return { title: p.title, description: p.description, suite: "V4" };
  },
  head: ({ loaderData }) => {
    const title = loaderData
      ? `${loaderData.title} — ERROR OPTIMIZER V4`
      : "Unavailable — ERROR OPTIMIZER";
    const description = loaderData?.description ?? "Page not found.";
    return {
      meta: [
        { title },
        { name: "description", content: description },
        { property: "og:title", content: title },
        { property: "og:description", content: description },
        { property: "og:type", content: "website" },
        { name: "twitter:card", content: "summary" },
        ...(loaderData ? [] : [{ name: "robots", content: "noindex" }]),
      ],
    };
  },
  component: PageRoute,
  errorComponent: PageRouteErrorComponent,
});

function PageRoute() {
  const { page } = Route.useParams();
  const C = PAGE_COMPONENTS[page];
  // Keyed by slug: each page (e.g. Normal vs Pro tier) starts with its own fresh state.
  return (
    <PageGuideContext.Provider value={pageGuide(page)}>
      <PageErrorBoundary page={page}>
        {C ? C() : null}
      </PageErrorBoundary>
    </PageGuideContext.Provider>
  );
}

import React, { Component, type ErrorInfo, type ReactNode } from "react";
import { Link } from "@tanstack/react-router";
import { Card, Btn, Badge } from "@/components/eo/kit";
import { AlertCircle, RefreshCw, ArrowLeft, Home } from "lucide-react";
import { abandonPageScan } from "@/lib/eo/pageScan";

interface Props {
  page?: string;
  children: ReactNode;
  fallback?: ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
  errorInfo: ErrorInfo | null;
}

export class PageErrorBoundary extends Component<Props, State> {
  public override state: State = {
    hasError: false,
    error: null,
    errorInfo: null,
  };

  public static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error, errorInfo: null };
  }

  public override componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    this.setState({ errorInfo });
    console.error(`[PageErrorBoundary] Error caught on page '${this.props.page ?? "unknown"}':`, error, errorInfo);
  }

  private handleReset = () => {
    if (this.props.page) {
      abandonPageScan(`tier:${this.props.page}`, 0);
      abandonPageScan(this.props.page, 0);
    }
    this.setState({ hasError: false, error: null, errorInfo: null });
  };

  public override render() {
    if (this.state.hasError) {
      if (this.props.fallback) {
        return this.props.fallback;
      }

      const pageName = this.props.page ? this.props.page.toUpperCase() : "PAGE";
      const errorMsg = this.state.error?.message || "An unexpected error occurred while loading this view.";

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
                    Failed to load {pageName}
                  </h2>
                  <Badge tone="danger">RECOVERABLE ERROR</Badge>
                </div>
                <p className="mt-1 text-xs text-muted-foreground">
                  The application encountered a component exception. Your navigation and system state remain intact.
                </p>

                <div className="mt-4 rounded-lg border border-border bg-window/60 p-3 font-mono text-[11px] text-danger/90 overflow-x-auto select-text">
                  <div className="font-semibold">{errorMsg}</div>
                  {this.state.error?.stack && (
                    <details className="mt-2 text-muted-foreground">
                      <summary className="cursor-pointer text-[10px] text-cyan hover:underline">
                        View diagnostic stack trace
                      </summary>
                      <pre className="mt-1.5 whitespace-pre-wrap text-[10px] max-h-48 overflow-y-auto">
                        {this.state.error.stack}
                      </pre>
                    </details>
                  )}
                </div>

                <div className="mt-5 flex flex-wrap items-center gap-2.5">
                  <Btn variant="primary" size="sm" onClick={this.handleReset}>
                    <RefreshCw className="mr-1.5 h-3.5 w-3.5" />
                    Retry this page
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

    return this.props.children;
  }
}

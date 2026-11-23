import { createFileRoute } from "@tanstack/react-router";
import { DashboardPage } from "@/components/eo/pages/Dashboard";
import { PageErrorBoundary } from "@/components/eo/PageErrorBoundary";

function DashboardRoute() {
  return (
    <PageErrorBoundary page="dashboard">
      <DashboardPage />
    </PageErrorBoundary>
  );
}

export const Route = createFileRoute("/")({
  head: () => ({
    meta: [
      { title: "Dashboard — ERROR OPTIMIZER V4" },
      {
        name: "description",
        content:
          "ERROR OPTIMIZER V4 Dashboard: system telemetry, optimization readiness, and maintenance controls.",
      },
      { property: "og:title", content: "Dashboard — ERROR OPTIMIZER V4" },
      {
        property: "og:description",
        content: "ERROR OPTIMIZER V4 system optimization and hardware telemetry suite.",
      },
      { property: "og:type", content: "website" },
      { name: "twitter:card", content: "summary" },
    ],
  }),
  component: DashboardRoute,
});

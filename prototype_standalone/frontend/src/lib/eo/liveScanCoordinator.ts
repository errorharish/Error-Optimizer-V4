// Centralized Authoritative Live Scan Coordinator for ERROR OPTIMIZER V3 + V4.
// Enforces single authoritative scan per module, coalesces concurrent requests,
// provides event-driven post-execution invalidation, and rejects stale responses.
import {
  normalizeOptimizationCatalog,
  type CatalogNormalizationResult,
  type CanonicalOptimizationItem,
} from "./catalogContract";

export interface ScanSnapshot {
  scanId: string;
  moduleId: string;
  catalogVersion: string;
  startedAt: string;
  completedAt: string;
  systemFingerprint: string;
  hardwareSummary: string;
  items: CanonicalOptimizationItem[];
  needsOptimization: CanonicalOptimizationItem[];
  alreadyOptimized: CanonicalOptimizationItem[];
  deferred: CanonicalOptimizationItem[];
  ineligible: CanonicalOptimizationItem[];
  totalCount: number;
  isFresh: boolean;
  fetchedAt: number;
}

type ScanListener = (snapshot: ScanSnapshot | null) => void;
type GlobalListener = (moduleId: string) => void;

class LiveScanCoordinator {
  private snapshots = new Map<string, ScanSnapshot>();
  private inflight = new Map<string, { gen: number; promise: Promise<ScanSnapshot> }>();
  private moduleListeners = new Map<string, Set<ScanListener>>();
  private globalListeners = new Set<GlobalListener>();
  private genCounter = 0;
  private activeGenerations = new Map<string, number>();

  /** 30 seconds freshness window for read-only snapshots before background revalidation. */
  private freshnessTtlMs = 30000;

  public subscribe(moduleId: string, listener: ScanListener): () => void {
    const key = moduleId.toLowerCase();
    if (!this.moduleListeners.has(key)) {
      this.moduleListeners.set(key, new Set());
    }
    this.moduleListeners.get(key)!.add(listener);

    // Provide existing snapshot if present
    const existing = this.snapshots.get(key);
    if (existing) {
      try {
        listener(existing);
      } catch {
        /* listener error ignored */
      }
    }

    return () => {
      const set = this.moduleListeners.get(key);
      if (set) {
        set.delete(listener);
        if (set.size === 0) this.moduleListeners.delete(key);
      }
    };
  }

  public subscribeAny(listener: GlobalListener): () => void {
    this.globalListeners.add(listener);
    return () => {
      this.globalListeners.delete(listener);
    };
  }

  public getSnapshot(moduleId: string): ScanSnapshot | null {
    return this.snapshots.get(moduleId.toLowerCase()) ?? null;
  }

  public invalidate(moduleId?: string): void {
    if (moduleId) {
      const key = moduleId.toLowerCase();
      this.snapshots.delete(key);
      this.activeGenerations.delete(key);
      this.emitModule(key, null);
      this.emitGlobal(key);
    } else {
      this.snapshots.clear();
      this.activeGenerations.clear();
      for (const key of this.moduleListeners.keys()) {
        this.emitModule(key, null);
        this.emitGlobal(key);
      }
    }
  }

  /**
   * Called whenever any optimization execution completes.
   * Immediately invalidates the module snapshot and notifies all listeners to rescan!
   */
  public notifyOptimizationCompleted(moduleId?: string): void {
    this.invalidate(moduleId);
  }

  /**
   * Coalesces concurrent scans for the same module and enforces generation validation.
   */
  public async executeScan(
    moduleId: string,
    task: () => Promise<unknown>,
    force = false,
  ): Promise<ScanSnapshot> {
    const key = moduleId.toLowerCase();
    const existing = this.snapshots.get(key);
    const now = Date.now();

    if (!force && existing && now - existing.fetchedAt < this.freshnessTtlMs) {
      return existing;
    }

    const running = this.inflight.get(key);
    if (running && !force) {
      return running.promise;
    }

    const gen = ++this.genCounter;
    this.activeGenerations.set(key, gen);

    const promise = (async () => {
      try {
        const raw = await task();

        // Reject stale / superseded responses
        if (this.activeGenerations.get(key) !== gen) {
          throw new Error(`[LiveScanCoordinator] Response superseded for module ${moduleId}`);
        }

        const rawObj = raw && typeof raw === "object" ? (raw as Record<string, unknown>) : {};
        const itemsList = Array.isArray(rawObj["items"]) ? (rawObj["items"] as unknown[]) : [];
        const excludedList = Array.isArray(rawObj["excludedItems"]) ? (rawObj["excludedItems"] as unknown[]) : [];
        const combined = itemsList.length > 0 || excludedList.length > 0 ? [...itemsList, ...excludedList] : (Array.isArray(raw) ? raw : []);

        const norm: CatalogNormalizationResult = normalizeOptimizationCatalog(combined, moduleId);

        const snapshot: ScanSnapshot = {
          scanId: String(rawObj["scanId"] ?? `scan-${gen}`),
          moduleId,
          catalogVersion: String(rawObj["catalogVersion"] ?? "4.0.0"),
          startedAt: String(rawObj["startedAt"] ?? new Date().toISOString()),
          completedAt: String(rawObj["completedAt"] ?? new Date().toISOString()),
          systemFingerprint: String(rawObj["systemFingerprint"] ?? ""),
          hardwareSummary: String(rawObj["hardwareSummary"] ?? ""),
          items: norm.items,
          needsOptimization: norm.needsOptimizationItems,
          alreadyOptimized: norm.alreadyOptimizedItems,
          deferred: norm.deferredItems,
          ineligible: norm.ineligibleItems,
          totalCount: norm.totalCatalogCount,
          isFresh: true,
          fetchedAt: Date.now(),
        };

        this.snapshots.set(key, snapshot);
        this.emitModule(key, snapshot);
        this.emitGlobal(key);
        return snapshot;
      } finally {
        if (this.inflight.get(key)?.gen === gen) {
          this.inflight.delete(key);
        }
      }
    })();

    this.inflight.set(key, { gen, promise });
    return promise;
  }

  private emitModule(key: string, snapshot: ScanSnapshot | null): void {
    const listeners = this.moduleListeners.get(key);
    if (listeners) {
      for (const listener of listeners) {
        try {
          listener(snapshot);
        } catch {
          /* listener errors ignored */
        }
      }
    }
  }

  private emitGlobal(moduleId: string): void {
    for (const listener of this.globalListeners) {
      try {
        listener(moduleId);
      } catch {
        /* listener errors ignored */
      }
    }
  }
}

export const liveScanCoordinator = new LiveScanCoordinator();

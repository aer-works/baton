/**
 * Pure, testable core of tools/fleet-glass/worker.js's paging and heartbeat-merge logic (#1656,
 * F2 -- 2026-09-02 review). worker.js `import`s these functions rather than redefining them --
 * Cloudflare Workers support ES modules, and the deployed entry point stays worker.js (wrangler.toml's
 * `main`). Split out solely so tools/fleet-glass/worker.selftest.mjs can exercise the actual code
 * path with plain `node`, no live Cloudflare Worker or KV namespace needed.
 */

// #1656: deliverables_list's opaque cursor (full contract in spec/baton.md §6). `atob`/`btoa` are
// standard Workers runtime globals; Node also provides both as globals (18.16+ / current LTS).
export function encodeDeliverablesCursor(item) {
  return btoa(JSON.stringify({ pushedAt: item.pushed_at || "", id: item.id }));
}
export function decodeDeliverablesCursor(cursor) {
  try {
    const parsed = JSON.parse(atob(cursor));
    if (parsed && typeof parsed.id === "string" && parsed.id) {
      return { pushedAt: typeof parsed.pushedAt === "string" ? parsed.pushedAt : "", id: parsed.id };
    }
  } catch {
    // Malformed or foreign cursor -- degrade to the start, never throw.
  }
  return null;
}

// deliverables_list's page computation, pulled out of handleMcp's tools/call branch verbatim (see
// worker.js) so it can be exercised without a live KV-backed inbox index. `filtered` is the index
// AFTER any `room` filter has already been applied by the caller -- this function knows nothing
// about rooms, only paging. Returns the exact shape the tool response carries: `items`, `count`
// (filtered.length, the total after the room filter -- NOT the page size), and `next_cursor`
// (null once exhausted).
export function computeDeliverablesPage(filtered, rawLimit, cursor) {
  const limit = typeof rawLimit === "number" && rawLimit > 0 ? Math.min(Math.floor(rawLimit), 200) : 50;
  let startIndex = 0;
  if (typeof cursor === "string" && cursor) {
    const decoded = decodeDeliverablesCursor(cursor);
    if (decoded) {
      // #1656 F2 fix (2026-09-02 review, found while writing worker.selftest.mjs): the encoded
      // cursor names the FIRST item of the NEXT page (`nextItem = filtered[startIndex + limit]`
      // below), not the last item of the page just shown -- resuming at `foundAt + 1` skipped that
      // item on every single "load more" click. Resume AT the found index instead.
      const foundAt = filtered.findIndex((m) => m && m.id === decoded.id && (m.pushed_at || "") === decoded.pushedAt);
      startIndex = foundAt >= 0 ? foundAt : 0;
    }
  }
  const items = filtered.slice(startIndex, startIndex + limit);
  const nextItem = filtered[startIndex + limit];
  const nextCursor = nextItem ? encodeDeliverablesCursor(nextItem) : null;
  return { items, count: filtered.length, next_cursor: nextCursor };
}

// fleet_status's page computation over the FULL terminal archive, pulled out the same way. `page`
// is expected already validated by the caller (see isValidFleetStatusPage below) -- this function
// only computes the slice, page/limit clamping, and next_page.
export function computeFleetStatusPage(archive, rawPage, rawLimit) {
  const limit = typeof rawLimit === "number" && rawLimit > 0 ? Math.min(Math.floor(rawLimit), 200) : 50;
  const page = Math.floor(rawPage);
  const start = page * limit;
  const rooms = archive.slice(start, start + limit);
  return {
    rooms,
    page,
    limit,
    terminal_total: archive.length,
    next_page: start + limit < archive.length ? page + 1 : null,
  };
}

// Bad/missing `page` (non-number, negative, NaN, Infinity) degrades to the plain unpaged
// fleet_status response rather than crashing -- this is the gate worker.js's handleMcp checks
// before calling computeFleetStatusPage at all.
export function isValidFleetStatusPage(page) {
  return typeof page === "number" && Number.isFinite(page) && page >= 0;
}

// Both isoStrings this ever compares come from the same producer's datetime.isoformat() call
// (pusher.py), so a plain string comparison over two well-formed ISO-8601 UTC instants sorts the
// same as comparing the instants themselves -- no Date parsing, and no timezone-offset pitfall to
// get wrong. Either argument being absent/non-string degrades to "the other one, or null".
export function maxIsoOrNull(a, b) {
  const aOk = typeof a === "string" && a.length > 0;
  const bOk = typeof b === "string" && b.length > 0;
  if (aOk && bOk) return a > b ? a : b;
  if (aOk) return a;
  if (bOk) return b;
  return null;
}

// #1690 item 2: the pure core of handleDeliver's batching -- given the existing inbox index and the
// items in one /deliver POST, returns the updated index (each stored item stamped with the batch id
// it lives in), the single content blob to write under `inbox:batch:<batchId>`, and any INBOX_CAP
// eviction overflow. worker.js does only the actual `env.FLEET.put`/`delete` calls around this, so
// worker.selftest.mjs can exercise the real batching logic with plain node -- no live KV needed.
// This is the fold that turns a K-item POST from K+1 KV writes (one inbox:item:<id> put per item,
// pre-#1690) into 2 (one inbox:batch:<id> put for the whole batch, plus the index put).
// F8 (2026-09-02 review): the pure decision for deliverable_read's outcome, pulled out of
// handleMcp's tools/call branch (see worker.js) so it can be exercised without live KV -- same
// "worker.js does only the actual env.FLEET calls around this" split every other function in this
// file follows. `content` is whatever the batch-blob/legacy-key reads resolved to (null if neither
// hit); `batchKey` non-null means the INDEX itself claims this id exists (deliverableBatchKeyFor
// found a batch_id for it), which is what lets this tell "genuinely no such id" apart from "known,
// but KV's eventual consistency across colos hasn't propagated its blob yet" -- the index and the
// blob are two separate reads that can observably disagree for a short window.
export function deliverableReadOutcome(content, batchKey) {
  if (content !== null) return { found: true, content };
  if (batchKey) return { found: false, pending: true };
  return { found: false, pending: false };
}

// F5 (2026-09-02 review): refcount `inbox:batch:<id>` blobs so worker.js can reclaim ones no index
// entry references any more, instead of leaving them orphaned forever (unbounded KV storage growth
// -- storage has its own free-tier ceiling even though it isn't write-budgeted). A batch id can lose
// its last reference two ways: (1) eviction, when the index exceeds `inboxCap`, and (2) re-delivery
// of the SAME id under a new batch id, which re-stamps its index entry and would otherwise abandon
// the old batch it used to point at. `staleBatchIds` tracks both; `orphanedBatchIds` (returned
// alongside `evicted`) is the subset with no remaining reference anywhere in the FINAL index, for
// worker.js to `env.FLEET.delete`. Batches evict roughly contiguously, so the amortised cost is ~1
// delete per batch (DELIVER_BATCH_KV_WRITE_COST in pusher.py budgets a flat +1 for this).
export function computeDeliverBatch(existingIndex, items, batchId, inboxCap) {
  let index = existingIndex.slice();
  const batchContent = {};
  const staleBatchIds = new Set();
  for (const item of items) {
    if (!item || typeof item.id !== "string" || !item.id) continue;
    if (typeof item.room !== "string" || !item.room) continue;
    batchContent[item.id] = String(item.content ?? "");
    const { content: _content, ...meta } = item;
    const prior = index.find((m) => m.id === item.id);
    if (prior && typeof prior.batch_id === "string" && prior.batch_id && prior.batch_id !== batchId) {
      staleBatchIds.add(prior.batch_id);
    }
    index = index.filter((m) => m.id !== item.id);
    index.unshift({ ...meta, pushed_at: item.pushed_at || new Date().toISOString(), batch_id: batchId });
  }
  // F12 (2026-09-02 review): count DISTINCT ids actually stored -- two items sharing an id within
  // one POST filter-then-unshift down to a single index entry above, so counting `items.length`
  // (or incrementing per loop iteration) double-counted the duplicate; `stored` is what the
  // /deliver response reports back to the caller.
  const stored = Object.keys(batchContent).length;
  let evicted = [];
  if (index.length > inboxCap) {
    evicted = index.slice(inboxCap);
    index = index.slice(0, inboxCap);
  }
  for (const m of evicted) {
    if (typeof m.batch_id === "string" && m.batch_id) staleBatchIds.add(m.batch_id);
  }
  const referenced = new Set(index.filter((m) => typeof m.batch_id === "string").map((m) => m.batch_id));
  const orphanedBatchIds = [...staleBatchIds].filter((id) => !referenced.has(id) && id !== batchId);
  return { index, batchContent, stored, evicted, orphanedBatchIds };
}

// #1690 item 2, read side: which `inbox:batch:<id>` key (if any) currently holds `itemId`'s content,
// per the index's own `batch_id` stamp -- null means "not found, or delivered before this change",
// which worker.js's deliverable_read treats as "fall back to the legacy inbox:item:<id> key".
export function deliverableBatchKeyFor(index, itemId) {
  const meta = index.find((m) => m && m.id === itemId);
  return meta && typeof meta.batch_id === "string" && meta.batch_id ? `inbox:batch:${meta.batch_id}` : null;
}

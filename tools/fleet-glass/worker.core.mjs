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

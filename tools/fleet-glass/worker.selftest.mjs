// Executable tests for tools/fleet-glass/worker.core.mjs -- the pure functions worker.js's
// paging (deliverables_list, fleet_status) and heartbeat-merge logic are built from (#1656, F2 --
// 2026-09-02 review). No JS test runner exists in this repo; this is a standalone `node` script,
// same `check`/failures-list pattern pusher.py's own `_selftest` already uses.
//
// Run: `node tools/fleet-glass/worker.selftest.mjs` (pixi task: fleet-glass-worker-selftest).

import {
  encodeDeliverablesCursor,
  decodeDeliverablesCursor,
  computeDeliverablesPage,
  computeFleetStatusPage,
  isValidFleetStatusPage,
  maxIsoOrNull,
} from "./worker.core.mjs";

const failures = [];
function check(name, cond) {
  if (!cond) failures.push(name);
}

// -- cursor round-trip --
{
  const item = { id: "abc123", pushed_at: "2026-09-02T07:00:00Z" };
  const cursor = encodeDeliverablesCursor(item);
  const decoded = decodeDeliverablesCursor(cursor);
  check("cursor round-trips id", decoded && decoded.id === "abc123");
  check("cursor round-trips pushedAt", decoded && decoded.pushedAt === "2026-09-02T07:00:00Z");
}
{
  // An item with no pushed_at still encodes/decodes -- worker.js's handleDeliver always stamps one,
  // but computeDeliverablesPage/encodeDeliverablesCursor make no such assumption themselves.
  const item = { id: "no-pushed-at" };
  const decoded = decodeDeliverablesCursor(encodeDeliverablesCursor(item));
  check("cursor round-trips an item with no pushed_at (encodes as empty string)",
        decoded && decoded.id === "no-pushed-at" && decoded.pushedAt === "");
}

// -- malformed cursor degrades to the start, never throws --
check("garbage (non-base64) cursor decodes to null", decodeDeliverablesCursor("!!!not-base64!!!") === null);
check("valid base64 of non-JSON decodes to null", decodeDeliverablesCursor(btoa("not json")) === null);
check("valid JSON missing 'id' decodes to null", decodeDeliverablesCursor(btoa(JSON.stringify({ pushedAt: "x" }))) === null);
{
  const index = [
    { id: "a", pushed_at: "2026-09-02T07:03:00Z" },
    { id: "b", pushed_at: "2026-09-02T07:02:00Z" },
    { id: "c", pushed_at: "2026-09-02T07:01:00Z" },
  ];
  const withMalformedCursor = computeDeliverablesPage(index, 2, "garbage-cursor");
  check("a malformed cursor restarts the page from the beginning, not a crash",
        withMalformedCursor.items.length === 2 && withMalformedCursor.items[0].id === "a");
}

// -- limit respected, count = filtered total, next_cursor null at the end --
{
  const index = Array.from({ length: 7 }, (_, i) => ({ id: `id-${i}`, pushed_at: `2026-09-02T07:0${i}:00Z` }));
  const page1 = computeDeliverablesPage(index, 3, undefined);
  check("limit is respected: page1 has exactly 3 items", page1.items.length === 3);
  check("count is the filtered total, not the page size", page1.count === 7);
  check("next_cursor is set when more items remain", page1.next_cursor !== null);

  const page2 = computeDeliverablesPage(index, 3, page1.next_cursor);
  check("page2 continues where page1 left off", page2.items[0].id === "id-3");
  check("page2 count still reflects the filtered total", page2.count === 7);

  const page3 = computeDeliverablesPage(index, 3, page2.next_cursor);
  check("the final page holds the remainder (1 item, not 3)", page3.items.length === 1 && page3.items[0].id === "id-6");
  check("next_cursor is null once the page is exhausted", page3.next_cursor === null);

  const noCursorNeeded = computeDeliverablesPage(index, 200, undefined);
  check("a limit above the total still returns next_cursor null (nothing left)",
        noCursorNeeded.items.length === 7 && noCursorNeeded.next_cursor === null);
  check("limit is clamped at 200 even when a caller asks for more",
        computeDeliverablesPage(index, 9999, undefined).items.length <= 200);
  check("a non-positive/absent limit defaults to 50",
        computeDeliverablesPage(index, 0, undefined).items.length === 7 // 7 < default 50, whole list
        && computeDeliverablesPage(index, -5, undefined).items.length === 7);
}

// -- 1,000 synthetic entries: served page stays under the limit and under 64 KB --
{
  const bigIndex = Array.from({ length: 1000 }, (_, i) => ({
    id: `synthetic-${i}`,
    room: "/r/synthetic",
    room_name: "synthetic-room",
    artifact: "report.md",
    title: `Synthetic deliverable number ${i}`,
    pushed_at: new Date(Date.UTC(2026, 8, 2, 0, 0, i)).toISOString(),
    content_hash: "0".repeat(64),
    withheld: false,
  }));
  const page = computeDeliverablesPage(bigIndex, 50, undefined);
  check("1,000-entry index: served page stays at/under the requested limit", page.items.length <= 50);
  check("1,000-entry index: count reports the full 1,000, not the page size", page.count === 1000);
  const bytes = Buffer.byteLength(JSON.stringify(page), "utf8");
  check(`1,000-entry index: served page body stays under 64 KB (was ${bytes} bytes)`, bytes < 64 * 1024);

  // Paging all the way through 1,000 entries at the default limit (50) never exceeds the limit per
  // page and terminates (next_cursor eventually null) -- the same code path glass.html's "load
  // more" drives.
  let cursor;
  let pages = 0;
  let seen = 0;
  do {
    const p = computeDeliverablesPage(bigIndex, undefined, cursor);
    check(`page ${pages}: never exceeds the default limit of 50`, p.items.length <= 50);
    seen += p.items.length;
    cursor = p.next_cursor;
    pages += 1;
  } while (cursor != null && pages < 100);
  check("paging through all 1,000 entries visits every one exactly once", seen === 1000);
  check("paging through 1,000 entries at limit 50 takes exactly 20 pages", pages === 20);
}

// -- fleet_status page computation: limit respected, terminal_total, next_page null at the end --
{
  const archive = Array.from({ length: 95 }, (_, i) => ({ path: `/r/term-${i}` }));
  const page0 = computeFleetStatusPage(archive, 0, 40);
  check("fleet_status page 0 respects the limit", page0.rooms.length === 40);
  check("fleet_status terminal_total is the full archive size", page0.terminal_total === 95);
  check("fleet_status next_page advances when more remain", page0.next_page === 1);

  const page2 = computeFleetStatusPage(archive, 2, 40);
  check("fleet_status page 2 holds the remainder (15 items, not 40)", page2.rooms.length === 15);
  check("fleet_status next_page is null once exhausted", page2.next_page === null);

  check("fleet_status limit is clamped at 200", computeFleetStatusPage(archive, 0, 9999).limit === 200);
  check("fleet_status limit defaults to 50 on a non-positive/absent limit",
        computeFleetStatusPage(archive, 0, 0).limit === 50 && computeFleetStatusPage(archive, 0, undefined).limit === 50);
}

// -- fleet_status page/limit bad-input degrades to default (isValidFleetStatusPage gate) --
check("isValidFleetStatusPage rejects a missing page", isValidFleetStatusPage(undefined) === false);
check("isValidFleetStatusPage rejects a non-number page", isValidFleetStatusPage("0") === false);
check("isValidFleetStatusPage rejects a negative page", isValidFleetStatusPage(-1) === false);
check("isValidFleetStatusPage rejects NaN", isValidFleetStatusPage(NaN) === false);
check("isValidFleetStatusPage rejects Infinity", isValidFleetStatusPage(Infinity) === false);
check("isValidFleetStatusPage accepts a valid page", isValidFleetStatusPage(0) === true);

// -- heartbeat merge picks the fresher of heartbeat/pushed_at, both directions --
check("a fresher pushed_at pulls the merged heartbeat forward",
      maxIsoOrNull("2026-09-02T07:11:28Z", "2026-09-02T07:34:00Z") === "2026-09-02T07:34:00Z");
check("(control, other direction) a fresher heartbeat wins when pushed_at is older",
      maxIsoOrNull("2026-09-02T07:34:00Z", "2026-09-02T07:11:28Z") === "2026-09-02T07:34:00Z");
check("an older pushed_at never regresses the merged heartbeat",
      maxIsoOrNull("2026-09-02T07:11:28Z", "2026-09-02T06:00:00Z") === "2026-09-02T07:11:28Z");
check("a quiet fleet (no push at all) still shows the raw heartbeat",
      maxIsoOrNull("2026-09-02T07:11:28Z", null) === "2026-09-02T07:11:28Z");
check("no heartbeat recorded yet, but a push has landed: the push time is still an honest signal",
      maxIsoOrNull(null, "2026-09-02T07:34:00Z") === "2026-09-02T07:34:00Z");
check("neither heartbeat nor push recorded yet: stays absent, never fabricated",
      maxIsoOrNull(null, null) === null);

if (failures.length) {
  console.error(`worker.selftest.mjs: FAIL -- ${failures.length} check(s):`);
  for (const f of failures) console.error(`  !! ${f}`);
  process.exit(1);
}
console.log("worker.selftest.mjs: pass");

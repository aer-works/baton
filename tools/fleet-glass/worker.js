/**
 * Fleet Glass mailbox (aer-works/baton#1392 follow-on; moved into the repo by #1413).
 *
 * Cloudflare Worker, three faces:
 *  - POST /push/<PUSH_TOKEN>    : the operator's machine pushes the latest fleet snapshot (JSON,
 *    from the fleet_status derivation). Outbound-only from the machine; this Worker never connects
 *    back to it.
 *  - POST /heartbeat/<PUSH_TOKEN> : the operator's machine pings on TWO independent cadences that
 *    share this one route (#1486, extended by #1613 item 2): an hourly liveness beat, and a more
 *    frequent derived-freshness ping whenever a snapshot push hasn't already delivered a fresh
 *    `derived_at` recently -- see pusher.py's cadence comments for the write-budget arithmetic. The
 *    stored `at` is this Worker's own receipt time (no dependency on the pusher host's clock);
 *    `derived_at`, if the body carries one, is the pusher's own claim about when ITS OWN snapshot
 *    derivation last completed -- a fact only the pusher knows, so unlike `at` it is NOT
 *    re-stamped here. Lets a reader tell "fleet is quiet" (snapshot old, heartbeat fresh) apart
 *    from "pusher is dead" (both old) apart from "pusher alive but derivation stuck" (heartbeat
 *    fresh, derived_at old) -- see fleet_status below. `pending_push_age_s`, if the body carries
 *    one (a 2026-09-01 review finding), is likewise the pusher's own claim -- seconds since its
 *    last SUCCESSFUL snapshot push, present only while it has content waiting to go out. Lets a
 *    reader tell "derivation stuck" apart from a FOURTH state this route alone can distinguish:
 *    "derivation is healthy but every push keeps failing" (a 413 from the push cap, a 5xx, a
 *    network blip) -- derived_at stays fresh in that case, since it only reflects derivation, not
 *    delivery.
 *  - POST /deliver/<PUSH_TOKEN> : the operator's machine pushes deliverable(s) -- a terminal room's
 *    declared output artifact(s) plus its verdict summary -- for the inbox surface (#1413). Body is
 *    `{"items": [...]}`; see `handleDeliver` for the item shape.
 *  - POST /mcp/<READ_SEGMENT>   : a minimal stateless MCP server (Streamable HTTP, JSON-RPC 2.0)
 *    exposing three read-only tools: `fleet_status` (the last pushed snapshot, with `heartbeat_at`
 *    and `derived_at` merged in from the separate key below), `deliverables_list` (inbox index,
 *    newest-first, optionally filtered by room), and `deliverable_read` (one item's full content).
 *    Read auth is the unguessable URL segment -- same posture as the operator's private ntfy topics.
 *
 * Storage, all in one KV namespace:
 *  - "snapshot"          : the fleet snapshot, verbatim JSON, carrying pushed_at so consumers can
 *                          render honest staleness; absent data renders as absent, never fabricated.
 *                          Also carries `derived_at` (#1613 item 2) whenever the pusher included one
 *                          in the push body -- NOT part of pusher.py's own snapshot_hash, so its
 *                          presence never gates the #1457 change-gate.
 *  - "heartbeat_at"      : JSON `{"at": ISO-8601, "derived_at"?: ISO-8601, "pending_push_age_s"?:
 *                          number}` (#1613 item 2 widened this from a bare ISO-8601 string;
 *                          `pending_push_age_s` was added by a 2026-09-01 review finding; a bare
 *                          string still reads back as a legacy `at` value, self-healing the moment
 *                          the next heartbeat lands). Deliberately NOT part of the "snapshot" value
 *                          or its hash -- none of `at`/`derived_at`/`pending_push_age_s` may ever
 *                          count as a snapshot content change and trigger the change-gate (#1457)
 *                          to push early.
 *  - "inbox:index"       : JSON array of deliverable METADATA (no content), newest-first, capped at
 *                          INBOX_CAP entries -- what deliverables_list returns.
 *  - "inbox:item:<id>"   : one deliverable's full content (or a withheld stub), keyed by the id the
 *                          pusher assigned it -- what deliverable_read returns.
 *  - "terminal_archive"  : JSON array of EVERY terminal room (#1656), split out of the /push body's
 *                          `terminal_archive` field so it never inflates the plain `snapshot` value
 *                          -- fleet_status's `rooms` only ever carries non-terminal rooms plus the
 *                          newest N terminal ones (pusher.py's HOT_TERMINAL_CAP); the rest is read
 *                          back a page at a time via fleet_status's own `page`/`limit` arguments.
 */

const INBOX_CAP = 500;

const TOOLS = [
  {
    name: "fleet_status",
    description:
      "Read-only snapshot of room statuses across the operator's baton fleet, as last pushed by the fleet machine. Includes pushed_at for snapshot staleness, heartbeat_at for pusher liveness, derived_at for snapshot-derivation health, and pending_push_age_s for push-delivery health -- these are independent (#1486, #1613 item 2, 2026-09-01 review): a quiet fleet lets pushed_at go stale on purpose (heartbeat_at tells that apart from a dead pusher), a fleet whose derivation keeps failing lets derived_at go stale even while heartbeat_at stays fresh, and a fleet whose PUSHES keep failing (derivation healthy) grows pending_push_age_s even while derived_at stays fresh. With no arguments, `rooms` carries every non-terminal room plus only the newest N terminal ones (terminal_total names the full terminal count) -- pass `page` (0-based) and optionally `limit` (default 50, max 200) to page through the REST of the terminal archive instead; a paged call's response carries rooms/page/limit/terminal_total/next_page (null once exhausted) and omits every other top-level field.",
    inputSchema: {
      type: "object",
      properties: {
        page: { type: "number" },
        limit: { type: "number" },
      },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true },
  },
  {
    name: "deliverables_list",
    description:
      "Newest-first index of lane deliverables pushed across rooms (title, room, artifact, pushed_at, content_hash, withheld). Optionally filtered to one room. Never carries content -- call deliverable_read for that. Paged: limit (default 50, max 200) and an opaque cursor from a prior call's next_cursor; response carries items, count (the total after any room filter), and next_cursor (null once exhausted).",
    inputSchema: {
      type: "object",
      properties: {
        room: { type: "string" },
        limit: { type: "number" },
        cursor: { type: "string" },
      },
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true },
  },
  {
    name: "deliverable_read",
    description:
      "Full content of one deliverable by id (from deliverables_list), rendered markdown or a withheld-secret stub.",
    inputSchema: {
      type: "object",
      properties: { id: { type: "string" } },
      required: ["id"],
      additionalProperties: false,
    },
    annotations: { readOnlyHint: true },
  },
];

function rpcResult(id, result) {
  return { jsonrpc: "2.0", id, result };
}
function rpcError(id, code, message) {
  return { jsonrpc: "2.0", id, error: { code, message } };
}
function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}
function toolText(text) {
  return { content: [{ type: "text", text }] };
}
function toolError(text) {
  return { content: [{ type: "text", text }], isError: true };
}

// #1613 item 2: "heartbeat_at" widened from a bare ISO-8601 string to a small JSON object so one
// key can carry both `at` (this Worker's own receipt time, unconditionally re-stamped on every
// /heartbeat POST) and `derived_at` (the pusher's own claim, taken from the POST body verbatim
// when present, never re-stamped -- see this file's header). `pending_push_age_s` (2026-09-01
// review finding) rides the same object, same "taken from the body verbatim, never re-stamped"
// rule -- it too is a fact only the pusher itself knows. Reads a pre-#1613 bare-string value back
// as a legacy `at` with no `derived_at`/`pending_push_age_s`, so an old stored value degrades
// gracefully instead of throwing; the next heartbeat overwrites it with the new shape either way.
function readStoredHeartbeat(raw) {
  if (!raw) return { at: null, derivedAt: null, pendingPushAgeS: null };
  try {
    const parsed = JSON.parse(raw);
    if (parsed && typeof parsed === "object") {
      return {
        at: parsed.at ?? null,
        derivedAt: parsed.derived_at ?? null,
        pendingPushAgeS: typeof parsed.pending_push_age_s === "number" ? parsed.pending_push_age_s : null,
      };
    }
  } catch {
    // Falls through to the legacy bare-string reading below.
  }
  return { at: raw, derivedAt: null, pendingPushAgeS: null };
}

async function readHeartbeat(env) {
  const raw = await env.FLEET.get("heartbeat_at");
  const { at, derivedAt, pendingPushAgeS } = readStoredHeartbeat(raw);
  return { heartbeatAt: at, derivedAt, pendingPushAgeS };
}

// Both isoStrings this ever compares come from the same producer's datetime.isoformat() call
// (pusher.py), so a plain string comparison over two well-formed ISO-8601 UTC instants sorts the
// same as comparing the instants themselves -- no Date parsing, and no timezone-offset pitfall to
// get wrong. Either argument being absent/non-string degrades to "the other one, or null".
function maxIsoOrNull(a, b) {
  const aOk = typeof a === "string" && a.length > 0;
  const bOk = typeof b === "string" && b.length > 0;
  if (aOk && bOk) return a > b ? a : b;
  if (aOk) return a;
  if (bOk) return b;
  return null;
}

async function readInboxIndex(env) {
  const raw = await env.FLEET.get("inbox:index");
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    // A corrupt index must not wedge every future push -- start fresh rather than fail closed on
    // metadata (the SECRET gate below is what fails closed; this is a resilience nicety for it).
    return [];
  }
}

// #1656: deliverables_list's opaque cursor (full contract in spec/baton.md §6). `atob`/`btoa` are
// standard Workers runtime globals.
function encodeDeliverablesCursor(item) {
  return btoa(JSON.stringify({ pushedAt: item.pushed_at || "", id: item.id }));
}
function decodeDeliverablesCursor(cursor) {
  try {
    const parsed = JSON.parse(atob(cursor));
    if (parsed && typeof parsed.id === "string" && parsed.id) {
      return { pushedAt: typeof parsed.pushedAt === "string" ? parsed.pushedAt : "", id: parsed.id };
    }
  } catch {
    // Malformed or foreign cursor -- degrade to page 0, never throw.
  }
  return null;
}

async function handleDeliver(request, env) {
  if (request.method !== "POST") return new Response(null, { status: 405 });
  const body = await request.text();
  if (body.length > 5_000_000) return new Response("too large", { status: 413 });
  let parsed;
  try {
    parsed = JSON.parse(body);
  } catch {
    return new Response("not json", { status: 400 });
  }
  const items = Array.isArray(parsed?.items) ? parsed.items : null;
  if (!items) return new Response("expected {\"items\": [...]}", { status: 400 });

  let index = await readInboxIndex(env);
  let stored = 0;
  for (const item of items) {
    if (!item || typeof item.id !== "string" || !item.id) continue;
    if (typeof item.room !== "string" || !item.room) continue;
    await env.FLEET.put(`inbox:item:${item.id}`, String(item.content ?? ""));
    const { content: _content, ...meta } = item;
    index = index.filter((m) => m.id !== item.id);
    index.unshift({ ...meta, pushed_at: item.pushed_at || new Date().toISOString() });
    stored += 1;
  }
  if (index.length > INBOX_CAP) {
    const evicted = index.slice(INBOX_CAP);
    index = index.slice(0, INBOX_CAP);
    for (const m of evicted) {
      await env.FLEET.delete(`inbox:item:${m.id}`);
    }
  }
  await env.FLEET.put("inbox:index", JSON.stringify(index));
  return json({ ok: true, stored, index_size: index.length });
}

async function handleMcp(request, env) {
  if (request.method === "GET") {
    // Streamable HTTP allows a server that does not offer a GET/SSE stream.
    return new Response(null, { status: 405 });
  }
  let msg;
  try {
    msg = await request.json();
  } catch {
    return json(rpcError(null, -32700, "parse error"), 400);
  }
  // Batch requests are not supported by this minimal server.
  if (Array.isArray(msg)) {
    return json(rpcError(null, -32600, "batch not supported"), 400);
  }
  const { id, method, params } = msg;
  if (method === "initialize") {
    return json(
      rpcResult(id, {
        protocolVersion: params?.protocolVersion ?? "2025-03-26",
        capabilities: { tools: {} },
        serverInfo: { name: "baton-fleet", version: "0.2.0" },
      }),
    );
  }
  if (method === "notifications/initialized") {
    return new Response(null, { status: 202 });
  }
  if (method === "tools/list") {
    return json(rpcResult(id, { tools: TOOLS }));
  }
  if (method === "tools/call") {
    const name = params?.name;
    if (name === "fleet_status") {
      const args = params?.arguments || {};
      // #1656: `page` pages over the FULL terminal-room archive -- a plain 0-based page index
      // rather than an opaque cursor (unlike deliverables_list below), since the archive is a
      // single append-mostly array with no independent per-item identity worth round-tripping. On
      // the SAME tool rather than a new one so worker.js's TOOLS array stays exactly the three
      // read-only names FleetGlassReadOnlyTests pins.
      if (typeof args.page === "number" && Number.isFinite(args.page) && args.page >= 0) {
        const limit = typeof args.limit === "number" && args.limit > 0 ? Math.min(Math.floor(args.limit), 200) : 50;
        const page = Math.floor(args.page);
        const raw = await env.FLEET.get("terminal_archive");
        let archive = [];
        if (raw) {
          try {
            const parsed = JSON.parse(raw);
            archive = Array.isArray(parsed) ? parsed : [];
          } catch {
            archive = [];
          }
        }
        const start = page * limit;
        const rooms = archive.slice(start, start + limit);
        return json(rpcResult(id, toolText(JSON.stringify({
          rooms,
          page,
          limit,
          terminal_total: archive.length,
          next_page: start + limit < archive.length ? page + 1 : null,
        }))));
      }
      const stored = await env.FLEET.get("snapshot");
      const { heartbeatAt, derivedAt: derivedAtFromHeartbeat, pendingPushAgeS } = await readHeartbeat(env);
      const storedSnapshot = stored === null ? null : JSON.parse(stored);
      // derived_at (#1613 item 2, spec/baton.md §6) can reach this Worker by two independent
      // routes: a snapshot push's own body, or a dedicated /heartbeat ping (see readHeartbeat).
      // Both stamp the SAME isoformat() shape from the SAME producer (pusher.py), so a plain
      // lexicographic string max is a sound "most recent" comparison -- no Date parsing needed.
      const derivedAt = maxIsoOrNull(storedSnapshot?.derived_at, derivedAtFromHeartbeat);
      // pending_push_age_s (2026-09-01 review finding) has only ONE route -- the heartbeat ping,
      // never the snapshot body itself (a snapshot that successfully pushed has nothing pending by
      // definition) -- so there is no second value to max against here.
      // #1656: fold pushed_at into the DISPLAYED heartbeat_at (same maxIsoOrNull merge derived_at
      // already uses) -- rationale and the bug this fixes: spec/baton.md §6.
      const heartbeatDisplayAt = maxIsoOrNull(heartbeatAt, storedSnapshot?.pushed_at);
      if (stored === null) {
        return json(rpcResult(id, toolText(JSON.stringify({ pushed_at: null, rooms: null, heartbeat_at: heartbeatAt, derived_at: derivedAt, pending_push_age_s: pendingPushAgeS, note: "no snapshot pushed yet" }))));
      }
      // heartbeat_at/derived_at/pending_push_age_s are merged in at read time, never written into
      // the "snapshot" value itself -- that keeps them out of pusher.py's change-gate hash (see
      // this file's header).
      const snapshot = { ...storedSnapshot, heartbeat_at: heartbeatDisplayAt, derived_at: derivedAt, pending_push_age_s: pendingPushAgeS };
      return json(rpcResult(id, toolText(JSON.stringify(snapshot))));
    }
    if (name === "deliverables_list") {
      const index = await readInboxIndex(env);
      const room = params?.arguments?.room;
      const filtered = room ? index.filter((m) => m.room === room) : index;
      // #1656: paged the same way as fleet_status's terminal archive, but with an OPAQUE cursor
      // (base64 of {pushedAt, id}) rather than a page index -- the inbox index is mutated by every
      // /deliver POST (dedupe-by-id unshift, INBOX_CAP eviction), so a page-index cursor could skip
      // or repeat items across two calls; a cursor anchored to a specific item's own identity
      // degrades gracefully (falls back to the start) instead of returning a silently wrong slice.
      const limit = typeof params?.arguments?.limit === "number" && params.arguments.limit > 0
        ? Math.min(Math.floor(params.arguments.limit), 200) : 50;
      const cursor = params?.arguments?.cursor;
      let startIndex = 0;
      if (typeof cursor === "string" && cursor) {
        const decoded = decodeDeliverablesCursor(cursor);
        if (decoded) {
          const foundAt = filtered.findIndex((m) => m && m.id === decoded.id && (m.pushed_at || "") === decoded.pushedAt);
          startIndex = foundAt >= 0 ? foundAt + 1 : 0;
        }
      }
      const items = filtered.slice(startIndex, startIndex + limit);
      const nextItem = filtered[startIndex + limit];
      const nextCursor = nextItem ? encodeDeliverablesCursor(nextItem) : null;
      return json(rpcResult(id, toolText(JSON.stringify({ items, count: filtered.length, next_cursor: nextCursor }))));
    }
    if (name === "deliverable_read") {
      const itemId = params?.arguments?.id;
      if (!itemId) return json(rpcResult(id, toolError("id is required")));
      const content = await env.FLEET.get(`inbox:item:${itemId}`);
      if (content === null) return json(rpcResult(id, toolError(`no deliverable with id ${itemId}`)));
      return json(rpcResult(id, toolText(content)));
    }
    return json(rpcResult(id, toolError(`unknown tool: ${name}`)));
  }
  if (typeof method === "string" && method.startsWith("notifications/")) {
    return new Response(null, { status: 202 });
  }
  return json(rpcError(id ?? null, -32601, `method not found: ${method}`));
}

// Constant-time token compare: a plain !== leaks match-prefix length through timing. Network
// jitter makes that impractical to exploit against a Worker, but the fix costs nothing.
function tokenMatches(candidate, secret) {
  if (typeof candidate !== "string" || typeof secret !== "string") return false;
  const enc = new TextEncoder();
  const a = enc.encode(candidate);
  const b = enc.encode(secret);
  let diff = a.length ^ b.length;
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    diff |= (a[i] ?? 0) ^ (b[i] ?? 0);
  }
  return diff === 0;
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const parts = url.pathname.split("/").filter(Boolean);

    if (parts[0] === "push") {
      if (!tokenMatches(parts[1], env.PUSH_TOKEN)) return new Response(null, { status: 404 });
      if (request.method !== "POST") return new Response(null, { status: 405 });
      const body = await request.text();
      if (body.length > 1_000_000) return new Response("too large", { status: 413 });
      let parsed;
      try {
        parsed = JSON.parse(body);
      } catch {
        return new Response("not json", { status: 400 });
      }
      // Legacy body is a bare rooms array; newer pushers send {rooms, underhood, ...} --
      // spread object bodies so extra sections ride along, keep wrapping bare arrays.
      const payload = Array.isArray(parsed) ? { rooms: parsed } : parsed;
      // #1656: `terminal_archive` (every terminal room; pusher.py's own hot-set cap keeps `rooms`
      // itself to non-terminal + the newest N terminal only, see pusher.py's HOT_TERMINAL_CAP)
      // rides the SAME push body but is stored under its OWN key, never folded into "snapshot" --
      // the whole point of the split is that a plain (no `page`) fleet_status call stays small
      // regardless of how many terminal rooms the fleet has ever accumulated. Read back a page at a
      // time only when `page` is passed (see handleMcp's fleet_status branch above).
      const { terminal_archive: terminalArchive, ...hotPayload } = payload;
      if (Array.isArray(terminalArchive)) {
        await env.FLEET.put("terminal_archive", JSON.stringify(terminalArchive));
      }
      const snapshot = JSON.stringify({ pushed_at: new Date().toISOString(), ...hotPayload });
      await env.FLEET.put("snapshot", snapshot);
      return new Response("ok", { status: 200 });
    }

    if (parts[0] === "heartbeat") {
      if (!tokenMatches(parts[1], env.PUSH_TOKEN)) return new Response(null, { status: 404 });
      if (request.method !== "POST") return new Response(null, { status: 405 });
      // `at` is always THIS Worker's own receipt time, never read from the request (#1486) -- a
      // heartbeat's liveness claim must not depend on the pusher host's clock. `derived_at`
      // (#1613 item 2) and `pending_push_age_s` (2026-09-01 review finding), when the body carries
      // them, ARE read from the request: both name a fact only the pusher itself knows (when ITS
      // OWN derivation last completed; how long ITS OWN content has been waiting to push), which
      // this Worker has no other way to learn. A missing/unparseable body (including the
      // pre-#1613 literal "{}") degrades to neither field on this ping -- still a valid heartbeat.
      let derivedAt = null;
      let pendingPushAgeS = null;
      try {
        const body = await request.text();
        if (body) {
          const parsed = JSON.parse(body);
          if (parsed && typeof parsed.derived_at === "string" && parsed.derived_at) {
            derivedAt = parsed.derived_at;
          }
          if (parsed && typeof parsed.pending_push_age_s === "number" && isFinite(parsed.pending_push_age_s)) {
            pendingPushAgeS = parsed.pending_push_age_s;
          }
        }
      } catch {
        // Malformed body -- treat exactly like an absent one; still a valid liveness ping.
      }
      const stored = { at: new Date().toISOString() };
      if (derivedAt) stored.derived_at = derivedAt;
      if (pendingPushAgeS !== null) stored.pending_push_age_s = pendingPushAgeS;
      await env.FLEET.put("heartbeat_at", JSON.stringify(stored));
      return new Response("ok", { status: 200 });
    }

    if (parts[0] === "deliver") {
      if (!tokenMatches(parts[1], env.PUSH_TOKEN)) return new Response(null, { status: 404 });
      return handleDeliver(request, env);
    }

    if (parts[0] === "mcp") {
      if (!tokenMatches(parts[1], env.READ_SEGMENT)) return new Response(null, { status: 404 });
      return handleMcp(request, env);
    }

    return new Response(null, { status: 404 });
  },
};

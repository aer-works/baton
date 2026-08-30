/**
 * Fleet Glass mailbox (aer-works/baton#1392 follow-on; moved into the repo by #1413).
 *
 * Cloudflare Worker, three faces:
 *  - POST /push/<PUSH_TOKEN>    : the operator's machine pushes the latest fleet snapshot (JSON,
 *    from the fleet_status derivation). Outbound-only from the machine; this Worker never connects
 *    back to it.
 *  - POST /deliver/<PUSH_TOKEN> : the operator's machine pushes deliverable(s) -- a terminal room's
 *    declared output artifact(s) plus its verdict summary -- for the inbox surface (#1413). Body is
 *    `{"items": [...]}`; see `handleDeliver` for the item shape.
 *  - POST /mcp/<READ_SEGMENT>   : a minimal stateless MCP server (Streamable HTTP, JSON-RPC 2.0)
 *    exposing three read-only tools: `fleet_status` (the last pushed snapshot), `deliverables_list`
 *    (inbox index, newest-first, optionally filtered by room), and `deliverable_read` (one item's
 *    full content). Read auth is the unguessable URL segment -- same posture as the operator's
 *    private ntfy topics.
 *
 * Storage, all in one KV namespace:
 *  - "snapshot"          : the fleet snapshot, verbatim JSON, carrying pushed_at so consumers can
 *                          render honest staleness; absent data renders as absent, never fabricated.
 *  - "inbox:index"       : JSON array of deliverable METADATA (no content), newest-first, capped at
 *                          INBOX_CAP entries -- what deliverables_list returns.
 *  - "inbox:item:<id>"   : one deliverable's full content (or a withheld stub), keyed by the id the
 *                          pusher assigned it -- what deliverable_read returns.
 */

const INBOX_CAP = 500;

const TOOLS = [
  {
    name: "fleet_status",
    description:
      "Read-only snapshot of room statuses across the operator's baton fleet, as last pushed by the fleet machine. Includes pushed_at for staleness.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
    annotations: { readOnlyHint: true },
  },
  {
    name: "deliverables_list",
    description:
      "Newest-first index of lane deliverables pushed across rooms (title, room, artifact, pushed_at, content_hash, withheld). Optionally filtered to one room. Never carries content -- call deliverable_read for that.",
    inputSchema: {
      type: "object",
      properties: { room: { type: "string" } },
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
      const stored = await env.FLEET.get("snapshot");
      if (stored === null) {
        return json(rpcResult(id, toolText(JSON.stringify({ pushed_at: null, rooms: null, note: "no snapshot pushed yet" }))));
      }
      return json(rpcResult(id, toolText(stored)));
    }
    if (name === "deliverables_list") {
      const index = await readInboxIndex(env);
      const room = params?.arguments?.room;
      const items = room ? index.filter((m) => m.room === room) : index;
      return json(rpcResult(id, toolText(JSON.stringify({ items, count: items.length }))));
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
      const snapshot = JSON.stringify({ pushed_at: new Date().toISOString(), ...payload });
      await env.FLEET.put("snapshot", snapshot);
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

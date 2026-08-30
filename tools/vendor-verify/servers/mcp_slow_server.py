"""stdio MCP server whose tool blocks WITHOUT sending progress notifications.

Models AER's blocking-gate design: a tool that holds the turn open while a human decides.
Documented risk: "A tool call to an MCP server that sends no response and no progress
notification for the idle window aborts with an error instead of waiting for the wall-clock limit."

If that reaper is real, AER's gate dies mid-wait whenever a human is slow -- which is exactly
when a gate matters. Writes markers so we can see how far it got.
"""
import json
import os
import sys
import time

D = os.environ.get("BATON_SENTINEL_DIR", ".")
BLOCK_SECONDS = int(os.environ.get("BATON_BLOCK_SECONDS", "180"))

TOOLS = [{
    "name": "slow_gate",
    "description": "Blocks while a human decides. Call it when asked to open the gate.",
    "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
}]


def send(o):
    sys.stdout.write(json.dumps(o) + "\n")
    sys.stdout.flush()


def mark(name, text="1"):
    open(os.path.join(D, name), "w").write(text)


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        req = json.loads(line)
    except ValueError:
        continue
    m, rid = req.get("method"), req.get("id")

    if m == "initialize":
        send({"jsonrpc": "2.0", "id": rid, "result": {
            "protocolVersion": req.get("params", {}).get("protocolVersion", "2025-06-18"),
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "aer-slow-gate", "version": "1.0.0"}}})
    elif m == "tools/list":
        send({"jsonrpc": "2.0", "id": rid, "result": {"tools": TOOLS}})
    elif m == "tools/call":
        mark("CALL_STARTED", str(time.time()))
        # Deliberately silent: no result, no progress notification.
        start = time.time()
        while time.time() - start < BLOCK_SECONDS:
            time.sleep(1)
            mark("STILL_BLOCKING", f"{time.time() - start:.0f}s")
        mark("CALL_COMPLETED", f"{time.time() - start:.0f}s")
        send({"jsonrpc": "2.0", "id": rid, "result": {
            "content": [{"type": "text", "text": "gate opened after blocking"}]}})
    elif m and m.startswith("notifications/"):
        pass
    elif rid is not None:
        send({"jsonrpc": "2.0", "id": rid, "error": {"code": -32601, "message": "nf"}})

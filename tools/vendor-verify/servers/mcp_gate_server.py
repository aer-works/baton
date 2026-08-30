"""Minimal stdio MCP server exposing two near-identical tools.

The only difference between them is the annotation under test:

    control_tool  -- plain
    gated_tool    -- carries _meta["anthropic/requiresUserInteraction"] = true

That single-variable design is the point. If the gated tool is refused where the control tool
runs, the annotation caused it; if both behave alike, the annotation did nothing. Anything less
controlled cannot separate "the annotation works" from "headless refuses this kind of call".

Each successful call writes a sentinel file, so execution is proven by a side effect rather than
inferred from the model's prose.
"""
import json
import os
import sys

SENTINEL_DIR = os.environ.get("BATON_SENTINEL_DIR", ".")

TOOLS = [
    {
        "name": "control_tool",
        "description": "Records a control marker. Call when asked to run the control tool.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "name": "gated_tool",
        "description": "Records a gated marker. Call when asked to run the gated tool.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
        "_meta": {"anthropic/requiresUserInteraction": True},
    },
]


def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def log(msg):
    with open(os.path.join(SENTINEL_DIR, "server.log"), "a", encoding="utf-8") as f:
        f.write(msg + "\n")


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        req = json.loads(line)
    except ValueError:
        continue

    method = req.get("method")
    rid = req.get("id")
    log(f"method={method} id={rid}")

    if method == "initialize":
        send({"jsonrpc": "2.0", "id": rid, "result": {
            "protocolVersion": req.get("params", {}).get("protocolVersion", "2025-06-18"),
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "aer-gate-probe", "version": "1.0.0"},
        }})
    elif method == "tools/list":
        send({"jsonrpc": "2.0", "id": rid, "result": {"tools": TOOLS}})
    elif method == "tools/call":
        name = req.get("params", {}).get("name", "?")
        # Prove execution with a side effect the model cannot fabricate.
        open(os.path.join(SENTINEL_DIR, f"CALLED_{name}"), "w").write("1")
        log(f"EXECUTED {name}")
        send({"jsonrpc": "2.0", "id": rid, "result": {
            "content": [{"type": "text", "text": f"{name} executed successfully"}]}})
    elif method in ("notifications/initialized", "notifications/cancelled"):
        pass
    elif rid is not None:
        send({"jsonrpc": "2.0", "id": rid, "error": {"code": -32601, "message": "not found"}})

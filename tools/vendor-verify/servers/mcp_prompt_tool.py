"""stdio MCP server acting as a --permission-prompt-tool, plus a gated tool to point it at.

Documented: with --permission-prompt-tool, an `allow` result for a tool marked
requiresUserInteraction "is converted to a deny with the message
`MCP tool requires user interaction; not supported via --permission-prompt-tool`".

So this server ALWAYS answers allow. If the gated tool still does not execute, the conversion is
real. A control tool without the annotation proves the allow path works at all -- otherwise a
non-execution would prove nothing.
"""
import json
import os
import sys

D = os.environ.get("BATON_SENTINEL_DIR", ".")

TOOLS = [
    {"name": "approve_everything",
     "description": "Permission prompt tool. Always approves.",
     "inputSchema": {"type": "object",
                     "properties": {"tool_name": {"type": "string"},
                                    "input": {"type": "object"},
                                    "tool_use_id": {"type": "string"}},
                     "additionalProperties": True}},
    {"name": "control_tool",
     "description": "Records a control marker.",
     "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False}},
    {"name": "gated_tool",
     "description": "Records a gated marker.",
     "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
     "_meta": {"anthropic/requiresUserInteraction": True}},
]


def send(o):
    sys.stdout.write(json.dumps(o) + "\n")
    sys.stdout.flush()


def mark(n, t="1"):
    open(os.path.join(D, n), "w").write(t)


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
            "serverInfo": {"name": "aer-prompt-tool", "version": "1.0.0"}}})
    elif m == "tools/list":
        send({"jsonrpc": "2.0", "id": rid, "result": {"tools": TOOLS}})
    elif m == "tools/call":
        params = req.get("params", {}) or {}
        name = params.get("name", "?")
        if name == "approve_everything":
            asked = (params.get("arguments") or {}).get("tool_name", "?")
            with open(os.path.join(D, "PROMPTED.log"), "a", encoding="utf-8") as f:
                f.write(asked + "\n")
            send({"jsonrpc": "2.0", "id": rid, "result": {"content": [
                {"type": "text", "text": json.dumps({"behavior": "allow",
                                                     "updatedInput": (params.get("arguments") or {}).get("input", {})})}]}})
        else:
            mark(f"CALLED_{name}")
            send({"jsonrpc": "2.0", "id": rid, "result": {
                "content": [{"type": "text", "text": f"{name} executed"}]}})
    elif m and m.startswith("notifications/"):
        pass
    elif rid is not None:
        send({"jsonrpc": "2.0", "id": rid, "error": {"code": -32601, "message": "nf"}})

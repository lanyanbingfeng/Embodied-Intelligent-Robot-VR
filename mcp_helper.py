# -*- coding: utf-8 -*-
"""Doubao <-> Unity MCP (mcp-for-unity HTTP transport) helper.

Usage:
  python mcp_helper.py list-tools
  python mcp_helper.py list-resources
  python mcp_helper.py call <tool_name> [json_arguments]
  python mcp_helper.py read <resource_uri>
  python mcp_helper.py reset
"""
import sys, os, json, time, urllib.request, urllib.error

BASE = "http://127.0.0.1:8080/mcp"
SESSION_FILE = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "Library", "MCPForUnity", "doubao_session.txt")

_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))  # no proxy, localhost


def _post(payload, session_id=None, timeout=180):
    req = urllib.request.Request(
        BASE, data=json.dumps(payload).encode("utf-8"), method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Accept", "application/json, text/event-stream")
    if session_id:
        req.add_header("Mcp-Session-Id", session_id)
    try:
        resp = _opener.open(req, timeout=timeout)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace"), e.headers.get("Mcp-Session-Id")
    return resp.status, resp.read().decode("utf-8", "replace"), resp.headers.get("Mcp-Session-Id")


def _parse_body(body):
    """Extract JSON objects from either a plain JSON body or an SSE stream."""
    body = body.strip()
    if not body:
        return []
    if body.startswith("event:") or body.startswith("data:"):
        out = []
        for line in body.splitlines():
            if line.startswith("data:"):
                chunk = line[5:].strip()
                if chunk:
                    try:
                        out.append(json.loads(chunk))
                    except json.JSONDecodeError:
                        pass
        return out
    try:
        return [json.loads(body)]
    except json.JSONDecodeError:
        return []


def _initialize():
    status, body, sid = _post({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                   "clientInfo": {"name": "doubao", "version": "1.0"}}})
    if not sid:
        objs = _parse_body(body)
        raise RuntimeError("initialize failed: %s" % (objs or body))
    os.makedirs(os.path.dirname(SESSION_FILE), exist_ok=True)
    with open(SESSION_FILE, "w") as f:
        f.write(sid)
    _post({"jsonrpc": "2.0", "method": "notifications/initialized"},
          session_id=sid, timeout=15)
    return sid


def _get_session(reset=False):
    if not reset and os.path.exists(SESSION_FILE):
        sid = open(SESSION_FILE).read().strip()
        if sid:
            return sid
    return _initialize()


def request(method, params=None, reset=False):
    sid = _get_session(reset=reset)
    payload = {"jsonrpc": "2.0", "id": int(time.time() * 1000) % 1000000000,
               "method": method}
    if params is not None:
        payload["params"] = params
    status, body, _ = _post(payload, session_id=sid)
    if status in (400, 404, 406) and not reset:
        # stale/invalid session -> reinitialize once and retry
        return request(method, params, reset=True)
    objs = _parse_body(body)
    for obj in objs:
        if obj.get("id") == payload["id"]:
            if "error" in obj:
                raise RuntimeError("MCP error: %s" % json.dumps(obj["error"], ensure_ascii=False))
            return obj.get("result")
    if not objs:
        return {"http_status": status, "raw": body[:500]}
    return objs[-1].get("result", objs[-1])


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return
    cmd = args[0]
    if cmd == "reset":
        if os.path.exists(SESSION_FILE):
            os.remove(SESSION_FILE)
        print("session reset")
        return
    if cmd == "list-tools":
        result = request("tools/list")
        tools = result.get("tools", [])
        print("%d tools:" % len(tools))
        for t in tools:
            desc = (t.get("description") or "").strip().splitlines()[0]
            print("  - %s: %s" % (t["name"], desc[:110]))
        return
    if cmd == "list-resources":
        result = request("resources/list")
        resources = result.get("resources", [])
        print("%d resources:" % len(resources))
        for r in resources:
            print("  - %s  (%s)" % (r["uri"], r.get("name", "")))
        return
    if cmd == "call":
        name = args[1]
        arg = json.loads(args[2]) if len(args) > 2 and args[2] else {}
        result = request("tools/call", {"name": name, "arguments": arg})
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return
    if cmd == "create-script":
        # create-script <asset_path> <local_source_file>
        asset_path = args[1]
        with open(args[2], encoding="utf-8") as f:
            content = f.read()
        result = request("tools/call", {
            "name": "create_script",
            "arguments": {"path": asset_path, "contents": content}})
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return
    if cmd == "read":
        uri = args[1]
        result = request("resources/read", {"uri": uri})
        contents = result.get("contents", [])
        for c in contents:
            if "text" in c:
                print(c["text"])
            else:
                print(json.dumps(c, ensure_ascii=False, indent=2))
        return
    print("unknown command:", cmd)
    print(__doc__)


if __name__ == "__main__":
    main()

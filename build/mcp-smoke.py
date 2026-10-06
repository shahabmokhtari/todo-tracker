#!/usr/bin/env python3
"""Smoke test an MCP server binary over stdio: initialize, create a task, check the markdown file, exit cleanly.

Usage: mcp-smoke.py <path-to-tt>
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    data = tempfile.mkdtemp(prefix="tt-smoke-")
    vault = os.path.join(data, "vault")
    os.makedirs(vault)
    proc = subprocess.Popen(
        [sys.argv[1], "mcp", "--data", data, "--vault", vault, "--no-history"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    timer = threading.Timer(60, proc.kill)
    timer.start()
    try:
        def send(message):
            proc.stdin.write(json.dumps(message) + "\n")
            proc.stdin.flush()

        def receive():
            line = proc.stdout.readline()
            if not line:
                raise RuntimeError("server closed stdout: " + proc.stderr.read())
            return json.loads(line)

        send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "ci-smoke", "version": "1"}}})
        init = receive()["result"]
        assert init["serverInfo"]["name"] == "todo-tracker", init
        assert init.get("instructions"), "server instructions missing"
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        send({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "create_task", "arguments": {"title": "Smoke test"}}})
        call = receive()["result"]
        assert not call.get("isError"), call
        assert json.loads(call["content"][0]["text"])["title"] == "Smoke test", call
        assert os.path.isfile(os.path.join(vault, "Work", "Smoke test.md")), "task file not written"
        proc.stdin.close()
        code = proc.wait(timeout=30)
        assert code == 0, f"exit code {code}"
        print(f"OK {init['serverInfo']}")
        return 0
    finally:
        timer.cancel()
        if proc.poll() is None:
            proc.kill()
            proc.wait(timeout=10)
        shutil.rmtree(data, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

# -*- coding: utf-8 -*-
"""
V2 protocol E2E test: this script plays the role of Flow Launcher talking to
main.py (a flogin/Python_v2 plugin) over newline-delimited JSON-RPC on stdin/stdout.

A dedicated reader task continuously consumes stdout, answers any `ChangeQuery`
requests coming FROM the plugin (as Flow does), and records streaming frames.

Usage (requires a real API key, never stored in source):
    set AI_API_KEY=your-key              # cmd.exe
    $env:AI_API_KEY = "your-key"         # PowerShell
    python test_v2_protocol.py
"""

from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PY = ROOT / ".venv" / "Scripts" / "python.exe"

BASE_URL = os.environ.get(
    "AI_BASE_URL", "https://devops.realtek.com/realgpt-api/openai-compatible/v1"
)
API_KEY = os.environ.get("AI_API_KEY")
MODEL = os.environ.get("AI_MODEL", "fast")

SETTINGS = {
    "base_url": BASE_URL,
    "api_key": API_KEY,
    "model": MODEL,
    "max_tokens": "200",
    "timeout": "60",
    "system_prompt": "You are a helpful assistant.",
    "skills": "be concise\nreply in lowercase",
}


class FlowSim:
    """Minimal Flow Launcher V2 side: spawns plugin, issues RPCs, auto-answers
    ChangeQuery requests, exposes the streamed text."""

    def __init__(self):
        self.proc = None
        self._req_id = 0
        self._pending: dict[int, asyncio.Future] = {}
        self.query_title = []
        self.change_query_seen = 0
        self.final_change_query = ""
        self.query_response_seen = False
        self.writer_lock = asyncio.Lock()

    async def start(self):
        self.proc = await asyncio.create_subprocess_exec(
            str(PY), str(ROOT / "main.py"),
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            cwd=str(ROOT),
        )
        asyncio.create_task(self._reader())
        asyncio.create_task(self._stderr_reader())

    async def _stderr_reader(self):
        assert self.proc and self.proc.stderr
        while True:
            raw = await self.proc.stderr.readline()
            if not raw:
                break
            line = raw.decode("utf-8", "replace").rstrip()
            if line:
                print(f"[plugin-stderr] {line}")

    async def _send(self, obj: dict) -> None:
        assert self.proc and self.proc.stdin
        async with self.writer_lock:
            self.proc.stdin.write((json.dumps(obj) + "\n").encode("utf-8"))
            await self.proc.stdin.drain()

    async def request(self, method: str, params: list, timeout: float = 15) -> dict:
        self._req_id += 1
        rid = self._req_id
        fut = asyncio.get_event_loop().create_future()
        self._pending[rid] = fut
        await self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        return await asyncio.wait_for(fut, timeout=timeout)

    async def _reader(self):
        """Continuously consume stdout. Answer ChangeQuery requests as Flow would."""
        assert self.proc and self.proc.stdout
        while True:
            raw = await self.proc.stdout.readline()
            if not raw:
                break
            try:
                msg = json.loads(raw.decode("utf-8"))
            except json.JSONDecodeError:
                continue

            if "method" in msg and "id" in msg:
                # request from plugin -> we must respond (Flow-side)
                if msg["method"] == "ChangeQuery":
                    self.change_query_seen += 1
                    self.final_change_query = msg["params"][0]
                resp = {"jsonrpc": "2.0", "id": msg["id"], "result": None}
                await self._send(resp)

            elif "id" in msg and ("result" in msg or "error" in msg):
                fut = self._pending.pop(msg["id"], None)
                if fut and not fut.done():
                    fut.set_result(msg)

    async def stop(self):
        if self.proc and self.proc.returncode is None:
            self.proc.kill()
            try:
                await asyncio.wait_for(self.proc.wait(), timeout=3)
            except Exception:
                pass


async def main() -> int:
    if not API_KEY:
        print("SKIP: set AI_API_KEY to run the live V2 streaming test.")
        return 0

    sim = FlowSim()
    await sim.start()
    try:
        init_meta = {
            "id": "test-plugin", "name": "AIAsk", "actionKeywords": ["ai"],
            "pluginDirectory": str(ROOT),
            "pluginSettingsDirectoryPath": str(ROOT / "settings"),
            "executeFileName": "main.py",
        }
        r = await sim.request("initialize", [{"currentPluginMetadata": init_meta}], timeout=10)
        print("[init]", json.dumps(r)[:120])

        raw_query = {
            "search": "Reply with exactly: STREAM_OK",
            "rawQuery": "ai Reply with exactly: STREAM_OK",
            "isReQuery": False,
            "actionKeyword": "ai",
        }
        qr = await sim.request("query", [raw_query, SETTINGS], timeout=120)
        res = qr.get("result", {})
        titles = [x.get("title") for x in (res.get("result") or [])]
        print("[query response] titles =", titles)

        # Wait for streaming to settle (ChangeQuery frames arrive async)
        for i in range(12):
            await asyncio.sleep(5)
            print(f"[wait {5*(i+1)}s] ChangeQuery frames so far: {sim.change_query_seen}",
                  flush=True)
            if sim.change_query_seen > 0:
                break

        print("\n=== RESULT ===")
        print("query_response_seen:   ", titles is not None)
        print("ChangeQuery frames:    ", sim.change_query_seen)
        print("final ChangeQuery text:", repr(sim.final_change_query)[:200])

        ok = True
        if not titles:
            print("FAIL: no query response frame")
            ok = False
        if sim.change_query_seen == 0:
            print("FAIL: no streaming ChangeQuery frames")
            ok = False
        elif "stream_ok" not in sim.final_change_query.lower():
            print("FAIL: streamed text did not contain expected reply")
            ok = False
        else:
            print("[ok] streamed text contains expected reply")
        return 0 if ok else 1
    finally:
        await sim.stop()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))

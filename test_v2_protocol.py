# -*- coding: utf-8 -*-
"""
End-to-end Python_v2 protocol test for AI Ask.

Acts as a tiny Flow Launcher V2 client: starts main.py, sends initialize/query
requests over newline-delimited JSON-RPC, answers plugin->Flow UpdateResults
requests, and verifies a real streamed answer arrives as an updated result.

Usage (real endpoint; key is intentionally never stored in source):
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
    # Intentional CJK coverage: verifies non-ASCII skills remain in the prompt
    # yet are not sent as invalid non-ASCII HTTP headers.
    "skills": "be concise\n用中文回答",
}


class FlowSim:
    """Minimal Flow V2 side: sends RPCs and handles plugin->Flow requests."""

    def __init__(self):
        self.proc = None
        self._request_id = 0
        self._pending: dict[int, asyncio.Future] = {}
        self.updated_results: list[dict] = []
        self.clipboard_requests: list[list] = []
        self.writer_lock = asyncio.Lock()

    async def start(self):
        self.proc = await asyncio.create_subprocess_exec(
            str(PY),
            str(ROOT / "main.py"),
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            cwd=str(ROOT),
        )
        asyncio.create_task(self._reader())
        asyncio.create_task(self._stderr_reader())

    async def _stderr_reader(self):
        assert self.proc and self.proc.stderr
        while raw := await self.proc.stderr.readline():
            line = raw.decode("utf-8", "replace").rstrip()
            if line:
                print(f"[plugin-stderr] {line}")

    async def _send(self, obj: dict) -> None:
        assert self.proc and self.proc.stdin
        async with self.writer_lock:
            self.proc.stdin.write((json.dumps(obj) + "\n").encode("utf-8"))
            await self.proc.stdin.drain()

    async def request(self, method: str, params: list, timeout: float = 15) -> dict:
        self._request_id += 1
        request_id = self._request_id
        future = asyncio.get_event_loop().create_future()
        self._pending[request_id] = future
        await self._send(
            {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
        )
        return await asyncio.wait_for(future, timeout=timeout)

    async def _reader(self):
        assert self.proc and self.proc.stdout
        while raw := await self.proc.stdout.readline():
            try:
                message = json.loads(raw.decode("utf-8"))
            except json.JSONDecodeError:
                continue

            if "method" in message and "id" in message:
                # Request FROM the plugin. This is where a real Flow instance
                # performs UpdateResults or ChangeQuery before acknowledging it.
                if message["method"] == "UpdateResults":
                    response = message["params"][1]
                    self.updated_results.append(response)
                elif message["method"] == "CopyToClipboard":
                    self.clipboard_requests.append(message["params"])
                await self._send({"jsonrpc": "2.0", "id": message["id"], "result": None})
            elif "id" in message and ("result" in message or "error" in message):
                future = self._pending.pop(message["id"], None)
                if future and not future.done():
                    future.set_result(message)

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
        metadata = {
            "id": "test-plugin",
            "name": "AIAsk",
            "actionKeywords": ["ai"],
            "pluginDirectory": str(ROOT),
            "pluginSettingsDirectoryPath": str(ROOT / "settings"),
            "executeFileName": "main.py",
        }
        initialize = await sim.request(
            "initialize", [{"currentPluginMetadata": metadata}], timeout=10
        )
        print("[initialize]", json.dumps(initialize)[:120])

        raw_query = {
            "search": "Reply with exactly: STREAM_OK",
            "rawQuery": "ai Reply with exactly: STREAM_OK",
            "isReQuery": False,
            "actionKeyword": "ai",
        }
        query_response = await sim.request("query", [raw_query, SETTINGS], timeout=120)
        initial_results = query_response.get("result", {}).get("result") or []
        print("[query] titles =", [result.get("title") for result in initial_results])

        # Streaming UpdateResults calls arrive asynchronously.
        for _ in range(12):
            await asyncio.sleep(5)
            if sim.updated_results:
                break

        flattened = json.dumps(sim.updated_results, ensure_ascii=False).lower()
        print("UpdateResults frames:", len(sim.updated_results))
        print("Last update:", json.dumps(sim.updated_results[-1], ensure_ascii=False)[:300]
              if sim.updated_results else "<none>")

        assert initial_results and initial_results[0]["title"] == "Generating…"
        assert sim.updated_results, "no streamed UpdateResults frame received"
        assert "stream_ok" in flattened, "streamed answer did not contain expected reply"
        assert "error" not in flattened, "non-ASCII skill caused a request error"

        # Execute the updated answer result exactly as Flow does: the plugin
        # should issue a CopyToClipboard request, not merely hide its window.
        final_result = sim.updated_results[-1]["result"][0]
        action = final_result["jsonRPCAction"]
        action_response = await sim.request(action["method"], action.get("parameters", []))
        assert action_response.get("result", {}).get("hide") is True
        assert sim.clipboard_requests, "answer Enter action did not request clipboard copy"
        assert "stream_ok" in sim.clipboard_requests[-1][0].lower()
        print("[ok] V2 streaming, CJK skills, and Enter-to-copy passed")
        return 0
    finally:
        await sim.stop()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))

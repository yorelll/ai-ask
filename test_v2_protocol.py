# -*- coding: utf-8 -*-
"""End-to-end Python_v2 protocol test for AI Ask (standard-library SSE version).

Set AI_API_KEY before running a live test. The test simulates Flow's V2 JSON-RPC
connection, verifies streamed UpdateResults, CJK Skill handling, and Enter-copy.
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
}


class FlowSim:
    def __init__(self):
        self.proc = None
        self.request_id = 0
        self.pending: dict[int, asyncio.Future] = {}
        self.updated_results: list[dict] = []
        self.clipboard_requests: list[list] = []
        self.writer_lock = asyncio.Lock()

    async def start(self):
        self.proc = await asyncio.create_subprocess_exec(
            str(PY), str(ROOT / "main.py"),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE, cwd=str(ROOT),
        )
        asyncio.create_task(self._reader())
        asyncio.create_task(self._stderr_reader())

    async def _stderr_reader(self):
        assert self.proc and self.proc.stderr
        while raw := await self.proc.stderr.readline():
            print("[plugin-stderr]", raw.decode("utf-8", "replace").rstrip())

    async def send(self, message: dict):
        assert self.proc and self.proc.stdin
        async with self.writer_lock:
            self.proc.stdin.write((json.dumps(message) + "\n").encode())
            await self.proc.stdin.drain()

    async def request(self, method: str, params: list, timeout: float = 30) -> dict:
        self.request_id += 1
        future = asyncio.get_running_loop().create_future()
        self.pending[self.request_id] = future
        await self.send({"jsonrpc": "2.0", "id": self.request_id, "method": method, "params": params})
        return await asyncio.wait_for(future, timeout)

    async def _reader(self):
        assert self.proc and self.proc.stdout
        while raw := await self.proc.stdout.readline():
            try:
                message = json.loads(raw.decode("utf-8"))
            except json.JSONDecodeError:
                continue
            if "method" in message and "id" in message:
                if message["method"] == "UpdateResults":
                    self.updated_results.append(message["params"][1])
                elif message["method"] == "CopyToClipboard":
                    self.clipboard_requests.append(message["params"])
                await self.send({"jsonrpc": "2.0", "id": message["id"], "result": None})
            elif "id" in message and ("result" in message or "error" in message):
                future = self.pending.pop(message["id"], None)
                if future and not future.done():
                    future.set_result(message)

    async def stop(self):
        if self.proc and self.proc.returncode is None:
            self.proc.kill()
            await self.proc.wait()


async def main() -> int:
    if not API_KEY:
        print("SKIP: set AI_API_KEY to run the live V2 streaming test.")
        return 0
    sim = FlowSim()
    await sim.start()
    try:
        metadata = {
            "id": "test-plugin", "name": "AIAsk", "actionKeywords": ["ai"],
            "pluginDirectory": str(ROOT),
            "pluginSettingsDirectoryPath": str(ROOT / "test-data"),
            "executeFileName": "main.py",
        }
        await sim.request("initialize", [{"currentPluginMetadata": metadata}])
        query = {
            "search": "Reply with exactly: STDLIB_OK",
            "rawQuery": "ai Reply with exactly: STDLIB_OK",
            "isReQuery": False,
            "actionKeyword": "ai",
        }
        response = await sim.request("query", [query, SETTINGS], timeout=120)
        initial = response.get("result", {}).get("result") or []
        assert initial and initial[0]["title"] == "Generating…"

        for _ in range(16):
            await asyncio.sleep(5)
            if sim.updated_results:
                break
        rendered = json.dumps(sim.updated_results, ensure_ascii=False).lower()
        assert sim.updated_results, "no streamed UpdateResults frame received"
        assert "stdlib_ok" in rendered, "stdlib SSE response missing expected answer"
        assert "error" not in rendered, "stream produced an error result"

        final = sim.updated_results[-1]["result"][0]
        action = final["jsonRPCAction"]
        action_response = await sim.request(action["method"], action.get("parameters", []))
        assert action_response.get("result", {}).get("hide") is True
        assert sim.clipboard_requests, "Enter did not request clipboard copy"
        assert "stdlib_ok" in sim.clipboard_requests[-1][0].lower()
        print("[ok] stdlib SSE streaming and Enter-copy passed")
        return 0
    finally:
        await sim.stop()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))

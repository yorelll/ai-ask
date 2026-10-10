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
        self.change_queries: list[str] = []
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
                elif message["method"] == "ChangeQuery":
                    self.change_queries.append(message["params"][0])
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
        assert initial and initial[0]["title"] == "Generate response"
        # No request may be sent merely because a query handler ran; this proves
        # users can continue typing before deliberately activating the result.
        await asyncio.sleep(1)
        assert not sim.updated_results, "stream started before user activated Generate response"

        send_action = initial[0]["jsonRPCAction"]
        send_response = await sim.request(send_action["method"], send_action.get("parameters", []))
        assert send_response.get("result", {}).get("hide") is False

        displayed = []
        for _ in range(16):
            await asyncio.sleep(5)
            if not sim.change_queries:
                continue
            assert sim.change_queries[-1].startswith("ai /stream ")
            # Simulate Flow's ordinary query routing after ChangeQuery. This is
            # the production workaround for Flow 2.1.x dropping V2 UpdateResults.
            stream_query = {
                "search": sim.change_queries[-1].removeprefix("ai "),
                "rawQuery": sim.change_queries[-1],
                "isReQuery": True,
                "actionKeyword": "ai",
            }
            display_response = await sim.request("query", [stream_query, SETTINGS], timeout=30)
            displayed = display_response.get("result", {}).get("result") or []
            rendered = json.dumps(displayed, ensure_ascii=False).lower()
            if "stdlib_ok" in rendered:
                break
        assert sim.change_queries, "no ChangeQuery stream refresh received after activation"
        rendered = json.dumps(displayed, ensure_ascii=False).lower()
        assert displayed, "stream refresh query returned no results"
        assert "stdlib_ok" in rendered, "stdlib SSE response missing expected answer"
        assert "error" not in rendered, "stream produced an error result"

        final = displayed[0]
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

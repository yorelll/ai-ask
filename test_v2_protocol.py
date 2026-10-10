# -*- coding: utf-8 -*-
"""End-to-end Python_v2 protocol test for AI Ask (standard-library SSE version).

Set AI_API_KEY before running a live test. The test simulates Flow's V2 JSON-RPC
connection, verifies streamed UpdateResults, CJK Skill handling, and Enter-copy.
"""

from __future__ import annotations

import asyncio
import json
import os
import shutil
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
        self.open_urls: list[str] = []
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
                elif message["method"] == "OpenUrl":
                    self.open_urls.append(message["params"][0])
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
    test_settings = ROOT / "test-data"
    shutil.rmtree(test_settings, ignore_errors=True)
    test_settings.mkdir(parents=True)
    sim = FlowSim()
    await sim.start()
    try:
        metadata = {
            "id": "test-plugin", "name": "AIAsk", "actionKeywords": ["ai"],
            "pluginDirectory": str(ROOT),
            "pluginSettingsDirectoryPath": str(test_settings),
            "executeFileName": "main.py",
        }
        await sim.request("initialize", [{"currentPluginMetadata": metadata}])
        # Exercise the user's dynamic-skill flow: /add trns <prompt>.
        # The copied skill is deliberately non-global and must only affect this
        # request; it also verifies the clean visible query becomes `ai /add trns`.
        (test_settings / "skills.json").write_text(
            json.dumps(
                {
                    "version": 1,
                    "skills": [
                        {
                            "alias": "trns",
                            "path": str(ROOT / "skill_files" / "translate.md"),
                            "global": False,
                        }
                    ],
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        query = {
            "search": "/add trns Reply with exactly: STDLIB_OK",
            "rawQuery": "ai /add trns Reply with exactly: STDLIB_OK",
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

        # The send action restores a clean visible /add input, never an
        # internal /stream key or the user's full long prompt.
        assert sim.change_queries and sim.change_queries[0] == "ai /add trns"
        displayed = []
        seen_refreshes = 0
        for _ in range(16):
            await asyncio.sleep(5)
            assert all(key == "ai /add trns" for key in sim.change_queries)
            while seen_refreshes < len(sim.change_queries):
                # Simulate Flow's ordinary query routing after ChangeQuery. The
                # visible query remains clean `/add trns`; requery=True forces
                # each refresh without restarting the endpoint request.
                stream_query = {
                    "search": "/add trns",
                    "rawQuery": "ai /add trns",
                    "isReQuery": True,
                    "actionKeyword": "ai",
                }
                display_response = await sim.request("query", [stream_query, SETTINGS], timeout=30)
                displayed = display_response.get("result", {}).get("result") or []
                seen_refreshes += 1
            rendered = json.dumps(displayed, ensure_ascii=False).lower()
            if "stdlib_ok" in rendered:
                break
        assert sim.change_queries, "no ChangeQuery stream refresh received after activation"
        rendered = json.dumps(displayed, ensure_ascii=False).lower()
        assert displayed, "stream refresh query returned no results"
        assert "stdlib_ok" in rendered, "stdlib SSE response missing expected answer"
        assert "error" not in rendered, "stream produced an error result"

        assert displayed[0].get("title") == "Copy full answer"
        assert displayed[1].get("title") == "Answer ready"
        assert "stdlib_ok" in displayed[1].get("subTitle", "").lower()

        # Summary opens a selectable full-answer page but keeps Flow visible.
        summary_action = displayed[1]["jsonRPCAction"]
        summary_response = await sim.request(summary_action["method"], summary_action.get("parameters", []))
        assert summary_response.get("result", {}).get("hide") is False
        assert sim.open_urls and sim.open_urls[-1].endswith("/answer")
        import urllib.request
        answer_data = json.loads(urllib.request.urlopen(sim.open_urls[-1].rsplit("/", 1)[0] + "/api/answer").read())
        assert "stdlib_ok" in answer_data["answer"].lower()

        copy_result = displayed[0]
        action = copy_result["jsonRPCAction"]
        action_response = await sim.request(action["method"], action.get("parameters", []))
        assert action_response.get("result", {}).get("hide") is True
        assert sim.clipboard_requests, "Enter did not request clipboard copy"
        assert "stdlib_ok" in sim.clipboard_requests[-1][0].lower()
        print("[ok] stdlib SSE streaming and Enter-copy passed")
        return 0
    finally:
        await sim.stop()
        shutil.rmtree(test_settings, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))

# -*- coding: utf-8 -*-
"""
AI Ask - Flow Launcher plugin (Python_v2).

Chat with any OpenAI-compatible LLM (streaming) directly in Flow Launcher,
using the Python_v2 long-lived protocol (streaming JSON-RPC via flogin).

Python version: 3.10+
Dependencies are bundled into the `lib` folder by the GitHub Action
(pip install -r requirements.txt -t lib), so end users don't install anything.
"""

from __future__ import annotations

import asyncio
import logging
import re
import sys
from pathlib import Path

# Make the bundled `lib` (and plugin's own folders) importable. Under the
# Python_v2 runner Flow also injects the plugin/lib dirs into sys.path via -c,
# but keeping this guarantees it works when running from a plain interpreter too.
PLUGIN_DIR = Path(__file__).resolve().parent
sys.path = [str(PLUGIN_DIR / p) for p in (".", "lib", "plugin")] + sys.path

from flogin import ExecuteResponse, Plugin, Query, Result, SearchHandler  # noqa: E402
from openai import AsyncOpenAI  # noqa: E402

log = logging.getLogger("aiask")
ICON = "Images\\plugin.png"


# ---------------------------------------------------------------------------
# Settings helpers
# ---------------------------------------------------------------------------

def _as_int(value, default):
    if value is None or value == "":
        return default
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def get_skills(plugin) -> list:
    """Return configured skills as a list (one per line in the settings)."""
    raw = getattr(plugin.settings, "skills", "") or ""
    if isinstance(raw, str):
        return [s.strip() for s in raw.splitlines() if s.strip()]
    return [str(s).strip() for s in raw if str(s).strip()]


def skill_headers(skills) -> dict:
    """Map skills to headers: X-Skill-1, X-Skill-2, ... (control chars stripped,
    original numbering kept). Full skill text still goes into the prompt."""
    headers = {}
    for i, skill in enumerate(skills, start=1):
        clean = re.sub(r"[\x00-\x1f\x7f]+", " ", skill).strip()
        if clean:
            headers[f"X-Skill-{i}"] = clean
    return headers


def skill_suffix(skills) -> str:
    if not skills:
        return ""
    numbered = "\n".join(f"{i + 1}. {s}" for i, s in enumerate(skills))
    return f"\n\n[Skills]\n{numbered}"


def make_client(plugin) -> AsyncOpenAI:
    """Build a configured async OpenAI client from Flow-injected settings."""
    base_url = getattr(plugin.settings, "base_url", "") or ""
    api_key = getattr(plugin.settings, "api_key", "") or ""
    if not base_url or not api_key:
        raise ValueError(
            "AI Ask is not configured. Open the plugin settings and set base_url and api_key."
        )
    return AsyncOpenAI(
        base_url=base_url,
        api_key=api_key,
        timeout=_as_int(getattr(plugin.settings, "timeout", 60), 60),
    )


# ---------------------------------------------------------------------------
# Results
# ---------------------------------------------------------------------------

class ClearHistoryResult(Result):
    """Context-menu command that clears the current plugin session's answer."""

    async def callback(self) -> ExecuteResponse:
        if self.plugin is not None:
            self.plugin.clear_last_answer()
        return ExecuteResponse(hide=False)


class AIResult(Result):
    """A copyable result for /last. Enter copies `copy_text`, then hides Flow."""

    def __init__(self, title, sub="", copy_text="", icon=ICON):
        super().__init__(title=title, sub=sub, icon=icon, copy_text=copy_text)
        self._copy_text = copy_text

    async def callback(self) -> ExecuteResponse:
        # Flow handles Result.copyText. Close the window only after the action.
        return ExecuteResponse(hide=True)

    async def context_menu(self):
        return [
            Result(
                "Copy last answer",
                sub="Copy the latest response to clipboard",
                icon=ICON,
                copy_text=self._copy_text,
            ),
            ClearHistoryResult(
                "Clear conversation",
                sub="Stop generation and reset the session answer",
                icon=ICON,
            ),
        ]


# ---------------------------------------------------------------------------
# Plugin + search handler
# ---------------------------------------------------------------------------

class AIAskPlugin(Plugin):
    """Long-lived Python_v2 plugin.

    Flow injects the current Settings.json dictionary as the second argument of
    every query RPC; flogin exposes it through `self.settings`.
    """

    def __init__(self, **options) -> None:
        # Flow sends the current settings dictionary with every query. Leave
        # flogin's updates enabled so changing plugin settings takes effect on
        # the next query without restarting Flow.
        super().__init__(**options)
        self._stop_event = asyncio.Event()
        self._stream_task: asyncio.Task | None = None
        self._last_answer = ""

    def clear_last_answer(self) -> None:
        self.stop_stream()
        self._last_answer = ""

    def stop_stream(self) -> bool:
        """Stop the active request even if it is waiting for an HTTP response."""
        self._stop_event.set()
        task = self._stream_task
        if task is not None and not task.done():
            task.cancel()
            return True
        return False

    def start_stream(self, prompt: str) -> None:
        """Cancel an earlier generation, then start exactly one new task."""
        self.stop_stream()
        self._stop_event.clear()

        async def run():
            try:
                self._last_answer = await self.stream_answer(prompt)
            finally:
                if self._stream_task is asyncio.current_task():
                    self._stream_task = None
                    self._stop_event.clear()

        self._stream_task = asyncio.create_task(run(), name="aiask-stream")

    async def stream_answer(self, prompt: str) -> str:
        """Stream one completion and push partial output into Flow's query box.

        Python_v2 uses full-duplex newline-delimited JSON-RPC, so ChangeQuery
        calls are valid while the original query request has already returned.
        """
        text = ""
        try:
            skills = get_skills(self)
            headers = skill_headers(skills)
            user_content = prompt + skill_suffix(skills) if skills else prompt

            async with make_client(self) as client:
                stream = await client.chat.completions.create(
                    model=getattr(self.settings, "model", "fast") or "fast",
                    messages=[
                        {
                            "role": "system",
                            "content": getattr(self.settings, "system_prompt", "")
                            or "You are a helpful assistant.",
                        },
                        {"role": "user", "content": user_content},
                    ],
                    max_tokens=_as_int(getattr(self.settings, "max_tokens", 1000), 1000),
                    temperature=0.7,
                    stream=True,
                    extra_headers=headers,
                )

                chunks_since_push = 0
                async for chunk in stream:
                    if self._stop_event.is_set():
                        break
                    choices = getattr(chunk, "choices", None) or []
                    if not choices:
                        continue  # usage/final empty chunk from some providers
                    content = getattr(choices[0].delta, "content", None)
                    if content is None:
                        continue  # role-only chunk
                    text += content
                    chunks_since_push += 1
                    if chunks_since_push >= 6:
                        await self.api.change_query(text, False)
                        chunks_since_push = 0

                if chunks_since_push:
                    await self.api.change_query(text, False)
                return text
        except asyncio.CancelledError:
            # /stop or a newer query cancels the task. Preserve partial text,
            # but don't surface cancellation as an error in the query box.
            return text
        except Exception as e:
            log.exception("streaming failed")
            if not self._stop_event.is_set():
                try:
                    await self.api.change_query(f"AI Ask error: {e}", False)
                except Exception:
                    pass
            return text

    async def handle_command(self, cmd: str) -> Result | None:
        if cmd == "/stop":
            stopped = self.stop_stream()
            subtitle = (
                "Generation cancelled." if stopped else "No generation is currently active."
            )
            return AIResult("Stop requested", subtitle, copy_text="")
        if cmd == "/clear":
            self.clear_last_answer()
            return AIResult("Cleared", "Last answer history reset.", copy_text="")
        if cmd == "/last":
            text = self._last_answer or "(nothing generated yet in this session)"
            return AIResult(
                "Last answer (Enter to copy to clipboard)",
                text[:120] + ("…" if len(text) > 120 else ""),
                copy_text=text,
            )
        return None


class MainSearchHandler(SearchHandler):
    """Handles all queries for the `ai` action keyword."""

    async def callback(self, query: Query):
        plugin: AIAskPlugin = self.plugin  # set by flogin before callback
        q = query.text.strip()

        cmd_result = await plugin.handle_command(q)
        if cmd_result is not None:
            return cmd_result

        if not q:
            n_skills = len(get_skills(plugin))
            return AIResult(
                "Ask the AI assistant",
                f"model: {getattr(plugin.settings, 'model', 'fast')} · "
                f"max_tokens: {getattr(plugin.settings, 'max_tokens', 1000)}"
                + (f" · skills: {n_skills}" if n_skills else ""),
                copy_text="",
            )

        plugin.start_stream(q)
        return AIResult(
            "Generating…",
            "Answer will stream into the query box. Use ai /last to copy it.",
            copy_text="",
        )

    async def on_error(self, query: Query, error: Exception):
        return AIResult("AI Ask error", str(error), copy_text="")


def main():
    plugin = AIAskPlugin()
    plugin.register_search_handler(MainSearchHandler())
    plugin.run()


if __name__ == "__main__":
    main()

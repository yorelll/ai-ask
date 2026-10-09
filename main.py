# -*- coding: utf-8 -*-
"""
AI Ask - Flow Launcher plugin (Python_v2).

Chat with an OpenAI-compatible LLM directly in Flow Launcher. This plugin uses
the long-lived Python_v2 protocol and updates its result list as tokens arrive.

Release artifact requirement: 64-bit CPython 3.11.x. Flow Launcher's embedded
CPython 3.11.4 is the supported runtime. Dependencies are bundled into `lib/`
by the GitHub Action, so end users do not install packages manually.
"""

from __future__ import annotations

import asyncio
import logging
import re
import sys
from pathlib import Path

# Flow's Python_v2 runner injects these directories itself, but maintaining the
# paths here also supports direct local execution.
PLUGIN_DIR = Path(__file__).resolve().parent
sys.path = [str(PLUGIN_DIR / p) for p in (".", "lib", "plugin")] + sys.path

from flogin import ErrorResponse, ExecuteResponse, Plugin, Query, Result, SearchHandler  # noqa: E402
from openai import AsyncOpenAI  # noqa: E402

# Flogin logs raw JSON-RPC input at INFO/DEBUG; incoming query settings contain
# api_key, so suppress those logs even if a host configures the root logger.
logging.getLogger("flogin").setLevel(logging.WARNING)
log = logging.getLogger("aiask")
log.addHandler(logging.NullHandler())
log.propagate = False

ICON = "Images\\plugin.png"
GENERATING_ICON = "Images\\generating.png"
ANSWER_ICON = "Images\\answer.png"
STOP_ICON = "Images\\stop.png"
CLEAR_ICON = "Images\\clear.png"
ERROR_ICON = "Images\\error.png"


# ---------------------------------------------------------------------------
# Settings and request helpers
# ---------------------------------------------------------------------------

def _as_int(value, default):
    if value is None or value == "":
        return default
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _setting_text(plugin, name: str) -> str:
    value = getattr(plugin.settings, name, "")
    return value.strip() if isinstance(value, str) else ""


def _is_configured(plugin) -> bool:
    return bool(_setting_text(plugin, "base_url") and _setting_text(plugin, "api_key"))


def get_skills(plugin) -> list:
    """Return configured skills as a list (one per nonblank line)."""
    raw = getattr(plugin.settings, "skills", "") or ""
    if isinstance(raw, str):
        return [skill.strip() for skill in raw.splitlines() if skill.strip()]
    return [str(skill).strip() for skill in raw if str(skill).strip()]


def skill_headers(skills) -> dict:
    """Map ASCII-safe skills to X-Skill-N HTTP headers.

    httpx permits only ASCII HTTP header values. A non-ASCII skill remains fully
    effective in the prompt suffix, but is omitted from headers rather than
    failing the complete request. Numbering follows the original skill index.
    """
    headers = {}
    for i, skill in enumerate(skills, start=1):
        clean = re.sub(r"[\x00-\x1f\x7f]+", " ", skill).strip()
        if clean and clean.isascii():
            headers[f"X-Skill-{i}"] = clean
    return headers


def skill_suffix(skills) -> str:
    if not skills:
        return ""
    numbered = "\n".join(f"{i + 1}. {skill}" for i, skill in enumerate(skills))
    return f"\n\n[Skills]\n{numbered}"


def make_client(plugin) -> AsyncOpenAI:
    """Build an async OpenAI client from Flow-injected settings."""
    base_url = _setting_text(plugin, "base_url")
    api_key = _setting_text(plugin, "api_key")
    if not base_url or not api_key:
        raise ValueError("not configured")
    return AsyncOpenAI(
        base_url=base_url,
        api_key=api_key,
        timeout=_as_int(getattr(plugin.settings, "timeout", 60), 60),
    )


def _friendly_error(error: Exception) -> str:
    """Return a safe message; never render raw URLs, server bodies, or secrets."""
    name = type(error).__name__.lower()
    if isinstance(error, ValueError):
        return "Configure Base URL and API Key in the plugin settings."
    if "authentication" in name or "permission" in name:
        return "Authentication failed. Check the API key in plugin settings."
    if "ratelimit" in name:
        return "Rate limit reached. Please try again later."
    if "timeout" in name:
        return "The AI request timed out. Try again or increase Timeout."
    if "connection" in name or "connect" in name:
        return "Could not connect to the AI endpoint. Check Base URL and network."
    return "The AI request failed. Check endpoint, model, and plugin settings."


# ---------------------------------------------------------------------------
# Result types
# ---------------------------------------------------------------------------

async def _copy_to_clipboard(result: Result, text: str) -> bool:
    """Call Flow's V2 CopyToClipboard RPC from a selectable plugin result."""
    if not text or result.plugin is None:
        return False
    try:
        response = await result.plugin.jsonrpc.request("CopyToClipboard", [text, False, True])
        return not isinstance(response, ErrorResponse)
    except Exception:
        return False


class ClearHistoryResult(Result):
    """Context-menu command that clears the current session's last answer."""

    async def callback(self) -> ExecuteResponse:
        if self.plugin is not None:
            self.plugin.clear_last_answer()
        return ExecuteResponse(hide=False)


class CopyAnswerResult(Result):
    """A context-menu result that actually copies its answer on Enter."""

    def __init__(self, text: str):
        super().__init__(
            title="Copy last answer",
            sub="Copy the latest response to clipboard",
            icon=ANSWER_ICON,
            copy_text=text,
        )
        self._answer = text

    async def callback(self) -> ExecuteResponse:
        copied = await _copy_to_clipboard(self, self._answer)
        return ExecuteResponse(hide=copied)


class AIResult(Result):
    """An answer result that copies its text to the clipboard on Enter."""

    def __init__(self, title, sub="", copy_text=None, icon=ICON):
        super().__init__(title=title, sub=sub, icon=icon, copy_text=copy_text)
        self._answer = copy_text or ""

    async def callback(self) -> ExecuteResponse:
        copied = await _copy_to_clipboard(self, self._answer)
        return ExecuteResponse(hide=copied)

    async def context_menu(self):
        results = [
            ClearHistoryResult(
                "Clear conversation",
                sub="Stop generation and reset the session answer",
                icon=CLEAR_ICON,
            ),
        ]
        if self._answer:
            results.insert(0, CopyAnswerResult(self._answer))
        return results


# ---------------------------------------------------------------------------
# Plugin + search handler
# ---------------------------------------------------------------------------

class AIAskPlugin(Plugin):
    """Long-lived Python_v2 plugin.

    Flow injects the current settings dictionary as the second parameter of
    every query RPC; flogin exposes it as `self.settings`.
    """

    def __init__(self, **options) -> None:
        # Leave settings updates enabled so changes in Flow's settings dialog
        # apply on the next query without restarting Flow.
        super().__init__(**options)
        self._stop_event = asyncio.Event()
        self._stream_task: asyncio.Task | None = None
        self._stream_generation = 0
        self._last_answer = ""

    def clear_last_answer(self) -> None:
        self.stop_stream()
        self._last_answer = ""

    def stop_stream(self) -> bool:
        """Cancel an active stream, including a pending HTTP request."""
        # Invalidate first so a cancelled task cannot preserve stale partial text.
        self._stream_generation += 1
        self._stop_event.set()
        task = self._stream_task
        if task is not None and not task.done():
            # _update_results can run inside the stream task itself. Do not
            # self-cancel there; the stop event makes the next loop iteration
            # exit cleanly. External /stop, /clear, and a new query cancel now.
            if task is not asyncio.current_task():
                task.cancel()
            return True
        return False

    def start_stream(self, query: Query, prompt: str) -> None:
        """Cancel any previous stream, then begin exactly one new stream."""
        self.stop_stream()
        self._stop_event.clear()
        generation = self._stream_generation

        async def run():
            task = asyncio.current_task()
            try:
                answer = await self.stream_answer(query, prompt, generation)
                # Do not let a cancelled older task or /clear restore stale text.
                if self._stream_task is task and self._stream_generation == generation:
                    self._last_answer = answer
            finally:
                if self._stream_task is task:
                    self._stream_task = None
                    self._stop_event.clear()

        self._stream_task = asyncio.create_task(run(), name="aiask-stream")

    def _is_current_generation(self, generation: int) -> bool:
        return self._stream_generation == generation and not self._stop_event.is_set()

    async def _update_results(
        self, query: Query, results: list[Result], generation: int
    ) -> bool:
        """Refresh results only while this is still the active generation.

        A failed UpdateResults means the user has likely edited or dismissed the
        original `ai ...` query. Stop the now-invisible stream rather than
        consuming tokens without a usable UI update.
        """
        if not self._is_current_generation(generation):
            return False
        try:
            await query.update_results(results)
        except Exception:
            if self._is_current_generation(generation):
                self.stop_stream()
            return False
        return self._is_current_generation(generation)

    async def stream_answer(self, query: Query, prompt: str, generation: int) -> str:
        """Stream one completion and refresh the existing result in place.

        UpdateResults preserves the original `ai ...` query text, avoiding the
        re-query/cancel loop caused by ChangeQuery during a stream.
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

                chunks_since_update = 0
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
                    chunks_since_update += 1
                    if chunks_since_update >= 6:
                        updated = await self._update_results(
                            query,
                            [AIResult("Generating…", text, copy_text=text, icon=GENERATING_ICON)],
                            generation,
                        )
                        if not updated:
                            return text
                        chunks_since_update = 0

                if text and self._is_current_generation(generation):
                    await self._update_results(
                        query,
                        [
                            AIResult(
                                "Answer ready — press Ctrl+C to copy",
                                text,
                                copy_text=text,
                                icon=ANSWER_ICON,
                            )
                        ],
                        generation,
                    )
                return text
        except asyncio.CancelledError:
            # /stop or a newer user query cancelled this task. Preserve its
            # partial text only if it is still the active task (handled above).
            return text
        except Exception as error:
            if self._is_current_generation(generation):
                results: list[Result] = []
                if text:
                    results.append(
                        AIResult(
                            "Partial answer — press Ctrl+C to copy",
                            text,
                            copy_text=text,
                            icon=ANSWER_ICON,
                        )
                    )
                results.append(Result("AI Ask error", _friendly_error(error), icon=ERROR_ICON))
                await self._update_results(query, results, generation)
            return text

    async def handle_command(self, cmd: str) -> Result | None:
        if cmd == "/stop":
            stopped = self.stop_stream()
            subtitle = "Generation cancelled." if stopped else "No generation is currently active."
            return Result("Stop requested", subtitle, icon=STOP_ICON)
        if cmd == "/clear":
            self.clear_last_answer()
            return Result("Cleared", "Last answer history reset.", icon=CLEAR_ICON)
        if cmd == "/last":
            text = self._last_answer or "(nothing generated yet in this session)"
            return AIResult(
                "Last answer — Enter or Ctrl+C to copy",
                text[:120] + ("…" if len(text) > 120 else ""),
                copy_text=text,
                icon=ANSWER_ICON,
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
            return Result(
                "Ask the AI assistant",
                f"model: {getattr(plugin.settings, 'model', 'fast')} · "
                f"max_tokens: {getattr(plugin.settings, 'max_tokens', 1000)}"
                + (f" · skills: {n_skills}" if n_skills else ""),
                icon=ICON,
            )

        if not _is_configured(plugin):
            return Result(
                "Configure AI Ask",
                "Open plugin settings and set Base URL plus API Key.",
                icon=ERROR_ICON,
            )

        plugin.start_stream(query, q)
        return Result(
            "Generating…",
            "Answer is streaming below. Use ai /last, then Ctrl+C, to copy it.",
            icon=GENERATING_ICON,
        )

    async def on_error(self, query: Query, error: Exception):
        return Result("AI Ask error", _friendly_error(error), icon=ERROR_ICON)


def main():
    plugin = AIAskPlugin()
    plugin.register_search_handler(MainSearchHandler())
    # The default flogin handler logs raw incoming query/settings JSON, including
    # api_key. Do not create flogin.log; errors are shown as sanitized UI results.
    plugin.run(setup_default_log_handler=False)


if __name__ == "__main__":
    main()

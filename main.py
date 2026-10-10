# -*- coding: utf-8 -*-
"""
AI Ask - Flow Launcher plugin (Python_v2).

OpenAI-compatible streaming chat for Flow Launcher. It uses only the Python
standard library for HTTP/SSE, so one release works with both Flow's embedded
CPython 3.11 and a user's CPython 3.12+ interpreter.
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import shlex
import socket
import ssl
import sys
import threading
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Iterator

# Flow's Python_v2 runner injects these dirs itself; keep them for direct runs.
PLUGIN_DIR = Path(__file__).resolve().parent
sys.path = [str(PLUGIN_DIR / p) for p in (".", "lib", "plugin")] + sys.path

from flogin import ErrorResponse, ExecuteResponse, Plugin, Query, Result, SearchHandler  # noqa: E402

# Flogin logs raw JSON-RPC input at INFO/DEBUG; query settings contain api_key.
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

DEFAULT_SKILLS = {"version": 1, "skills": []}
# Alias may be English or Chinese/etc., but must stay a single command token.
SKILL_ALIAS_RE = re.compile(r"^[^\s:：/\\]{1,64}$", re.UNICODE)
COMMAND_SPLIT_RE = re.compile(r"(?:\s+|\s*[:：]\s*)+")


# ---------------------------------------------------------------------------
# Settings, paths, and skill store
# ---------------------------------------------------------------------------

def _as_int(value, default: int) -> int:
    try:
        parsed = int(value)
        return parsed if parsed > 0 else default
    except (TypeError, ValueError):
        return default


def _setting_text(plugin, name: str) -> str:
    value = getattr(plugin.settings, name, "")
    return value.strip() if isinstance(value, str) else ""


def _is_configured(plugin) -> bool:
    return bool(_setting_text(plugin, "base_url") and _setting_text(plugin, "api_key"))


def _settings_dir(plugin) -> Path:
    """Use Flow's real plugin settings directory when available."""
    try:
        configured = getattr(plugin.metadata, "_data", {}).get("pluginSettingsDirectoryPath")
        if configured:
            return Path(configured)
    except Exception:
        pass
    return PLUGIN_DIR / "data"


def _skills_file(plugin) -> Path:
    return _settings_dir(plugin) / "skills.json"


def _normalize_skill_path(value: str) -> Path:
    path = Path(value.strip().strip('"'))
    if not path.is_absolute():
        path = PLUGIN_DIR / path
    return path.resolve()


def _validate_skill(alias: str, path_text: str) -> tuple[str, Path]:
    alias = alias.strip().lower()
    if not SKILL_ALIAS_RE.fullmatch(alias):
        raise ValueError("Alias must not contain whitespace, :, ：, /, or \\.")
    path = _normalize_skill_path(path_text)
    if not path.is_file():
        raise ValueError(f"Skill path does not exist: {path}")
    try:
        path.read_text(encoding="utf-8")
    except UnicodeDecodeError as error:
        raise ValueError(f"Skill file must be UTF-8 text: {path}") from error
    except OSError as error:
        raise ValueError(f"Skill file cannot be read: {path}") from error
    return alias, path


def _load_skills(plugin) -> list[dict]:
    path = _skills_file(plugin)
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        skills = data.get("skills", [])
        return [skill for skill in skills if isinstance(skill, dict)] if isinstance(skills, list) else []
    except (OSError, json.JSONDecodeError):
        return []


def _save_skills(plugin, skills: list[dict]) -> None:
    path = _skills_file(plugin)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps({"version": 1, "skills": skills}, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(path)


def _skill_status(skill: dict) -> tuple[bool, str]:
    try:
        path = _normalize_skill_path(str(skill.get("path", "")))
        if not path.is_file():
            return False, "missing path"
        path.read_text(encoding="utf-8")
        return True, "valid"
    except (OSError, UnicodeDecodeError):
        return False, "unreadable"


def _read_skill(skill: dict) -> str:
    path = _normalize_skill_path(str(skill.get("path", "")))
    if not path.is_file():
        raise ValueError(f"Skill path does not exist: {path}")
    try:
        return path.read_text(encoding="utf-8").strip()
    except UnicodeDecodeError as error:
        raise ValueError(f"Skill file is not UTF-8: {path}") from error
    except OSError as error:
        raise ValueError(f"Skill file cannot be read: {path}") from error


def _active_global_skills(plugin) -> tuple[list[dict], list[str]]:
    active, problems = [], []
    for skill in _load_skills(plugin):
        if not skill.get("global", False):
            continue
        valid, status = _skill_status(skill)
        if valid:
            active.append(skill)
        else:
            problems.append(f"{skill.get('alias', '?')}: {status}")
    return active, problems


def _parse_ai_command(text: str) -> list[str]:
    normalized = text.strip()
    return [part for part in COMMAND_SPLIT_RE.split(normalized) if part] if normalized else []


def _flag_from_tail(value: str) -> tuple[str, str | None]:
    match = re.match(r"^(.*?)(?:\s*[:：]\s*)(global|on|true|1)\s*$", value, flags=re.I)
    return (match.group(1).strip(), match.group(2).lower()) if match else (value.strip(), None)


def _skills_command_args(command_text: str) -> list[str]:
    """Parse /skills without corrupting Windows drive colons."""
    text = command_text.strip()
    compact = re.match(r"^(add|edit|toggle|global|delete|remove)\s*[:：]\s*(.*)$", text, flags=re.I | re.S)
    if not compact:
        return [part.strip('"') for part in shlex.split(text, posix=False)]
    command, tail = compact.group(1).lower(), compact.group(2)
    if command in {"toggle", "global", "delete", "remove"}:
        return [command, tail.strip()]
    if command == "add":
        match = re.match(r"^([^\s:：]+)\s*[:：]\s*(.+)$", tail, flags=re.S)
        if not match:
            return [command]
        path, flag = _flag_from_tail(match.group(2))
        return [command, match.group(1), path] + ([flag] if flag else [])
    match = re.match(r"^([^\s:：]+)\s*[:：]\s*([^\s:：]+)\s*[:：]\s*(.+)$", tail, flags=re.S)
    if not match:
        return [command]
    path, flag = _flag_from_tail(match.group(3))
    return [command, match.group(1), match.group(2), path] + ([flag] if flag else [])


# ---------------------------------------------------------------------------
# OpenAI-compatible HTTP/SSE client (stdlib only; no Python-ABI dependency)
# ---------------------------------------------------------------------------

class ChatHTTPError(RuntimeError):
    pass


def _chat_completion_url(base_url: str) -> str:
    return base_url.rstrip("/") + "/chat/completions"


def _open_sse_stream(base_url: str, api_key: str, model: str, messages: list[dict], max_tokens: int, timeout: int, headers: dict[str, str]) -> Iterator[str]:
    payload = json.dumps({"model": model, "messages": messages, "max_tokens": max_tokens, "temperature": 0.7, "stream": True}, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        _chat_completion_url(base_url), data=payload,
        headers={"Content-Type": "application/json; charset=utf-8", "Accept": "text/event-stream", "Authorization": f"Bearer {api_key}", **headers},
        method="POST",
    )
    try:
        response = urllib.request.urlopen(request, timeout=timeout, context=ssl.create_default_context())
    except urllib.error.HTTPError as error:
        raise ChatHTTPError(f"HTTP {error.code}") from error
    except urllib.error.URLError as error:
        raise ChatHTTPError("connection failed") from error
    except TimeoutError as error:
        raise ChatHTTPError("timeout") from error
    try:
        with response:
            for raw in response:
                line = raw.decode("utf-8", errors="replace").strip()
                if not line.startswith("data:"):
                    continue
                data = line[5:].strip()
                if data == "[DONE]":
                    break
                try:
                    event = json.loads(data)
                except json.JSONDecodeError:
                    continue
                for choice in event.get("choices") or []:
                    content = (choice.get("delta") or {}).get("content")
                    if isinstance(content, str) and content:
                        yield content
    except (TimeoutError, socket.timeout) as error:
        raise ChatHTTPError("timeout") from error
    except OSError as error:
        raise ChatHTTPError("connection failed") from error


def _friendly_error(error: Exception) -> str:
    message = str(error).lower()
    if isinstance(error, ValueError):
        return str(error)
    if "401" in message or "403" in message or "auth" in message:
        return "Authentication failed. Check the API key in plugin settings."
    if "429" in message or "rate" in message:
        return "Rate limit reached. Please try again later."
    if "timeout" in message:
        return "The AI request timed out. Try again or increase Timeout."
    if "connection" in message or "connect" in message:
        return "Could not connect to the AI endpoint. Check Base URL and network."
    return "The AI request failed. Check endpoint, model, and plugin settings."


# ---------------------------------------------------------------------------
# Local skill-management and full-answer UI (127.0.0.1 only)
# ---------------------------------------------------------------------------

class _LocalUIHandler(BaseHTTPRequestHandler):
    manager = None

    def log_message(self, format, *args):
        return

    def _json(self, status: int, value: dict) -> None:
        raw = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def _html(self, content: str) -> None:
        raw = content.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        if self.path == "/api/skills":
            self._json(200, {"skills": self.manager.skills_for_ui()})
        elif self.path == "/api/answer":
            self._json(200, {"answer": self.manager.current_answer()})
        elif self.path == "/answer":
            self._html(_ANSWER_UI_HTML)
        else:
            self._html(_SKILLS_UI_HTML)

    def do_POST(self):
        if self.path != "/api/skills":
            self._json(404, {"error": "not found"})
            return
        try:
            size = int(self.headers.get("Content-Length", "0"))
            data = json.loads(self.rfile.read(size).decode("utf-8"))
            skill = self.manager.upsert_skill(str(data.get("alias", "")), str(data.get("path", "")), bool(data.get("global", False)), str(data.get("originalAlias", "")))
            self._json(200, {"skill": skill})
        except (ValueError, OSError) as error:
            self._json(400, {"error": str(error)})

    def do_DELETE(self):
        if not self.path.startswith("/api/skills/"):
            self._json(404, {"error": "not found"})
            return
        alias = urllib.parse.unquote(self.path.rsplit("/", 1)[-1])
        self._json(200, {"ok": True}) if self.manager.delete_skill(alias) else self._json(404, {"error": "skill not found"})


_SKILLS_UI_HTML = """<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>AI Ask Skills</title>
<style>body{font-family:"Microsoft YaHei UI",Segoe UI,sans-serif;margin:32px;background:#fafafa;color:#222}h1{margin:0 0 20px}table{width:100%;border-collapse:collapse;background:#fff}th,td{padding:13px 16px;text-align:left;border-bottom:1px solid #e5e5e5}th{color:#777;font-weight:500}button{padding:8px 18px;border:1px solid #ccc;border-radius:6px;background:#fff;font-size:15px;cursor:pointer}button.primary{background:#805100;color:#fff;border-color:#805100}button.danger{color:#b42318}.actions{display:flex;gap:8px}.status-ok{color:#067647}.status-bad{color:#b42318}#dialog{display:none;position:fixed;inset:0;background:#0004;align-items:center;justify-content:center}.panel{background:#fff;border-radius:12px;width:480px;padding:28px;box-shadow:0 12px 40px #0003}.panel label{display:block;margin:15px 0 6px}.panel input[type=text]{width:100%;box-sizing:border-box;padding:10px;border:1px solid #ccc;border-radius:6px;font-size:16px}.footer{display:flex;justify-content:flex-end;gap:10px;margin-top:24px}.error{color:#b42318;margin-top:10px}.toolbar{display:flex;justify-content:flex-end;margin-bottom:14px}</style>
<body><h1>AI Ask · Skill 管理</h1><div class="toolbar"><button class="primary" onclick="openAdd()">添加</button></div><table><thead><tr><th>别名</th><th>路径</th><th>全局加载</th><th>状态</th><th>操作</th></tr></thead><tbody id="rows"></tbody></table><div id="dialog"><div class="panel"><h2 id="dialogTitle">添加 Skill</h2><input id="original" type="hidden"><label>别名</label><input id="alias" type="text" placeholder="例如 translate"><label>Skill 文件路径</label><input id="path" type="text" placeholder="C:\\skills\\translate.md"><label><input id="global" type="checkbox"> 全局加载为 system prompt</label><div id="error" class="error"></div><div class="footer"><button onclick="closeDialog()">取消</button><button class="primary" onclick="save()">确认</button></div></div></div><script>
const esc=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));async function load(){let d=await fetch('/api/skills').then(r=>r.json());rows.innerHTML=d.skills.map(s=>`<tr><td>${esc(s.alias)}</td><td>${esc(s.path)}</td><td><input type="checkbox" ${s.global?'checked':''} onchange="toggle('${esc(s.alias)}',this.checked)"></td><td class="${s.valid?'status-ok':'status-bad'}">${s.valid?'有效':'路径不可用'}</td><td><div class="actions"><button onclick='edit(${JSON.stringify(s)})'>编辑</button><button class="danger" onclick="removeSkill('${esc(s.alias)}')">删除</button></div></td></tr>`).join('')||'<tr><td colspan="5">暂无 skill；点击“添加”创建。</td></tr>'}function openAdd(){dialogTitle.textContent='添加 Skill';original.value='';alias.value='';path.value='';global.checked=false;error.textContent='';dialog.style.display='flex'}function edit(s){dialogTitle.textContent='编辑 Skill 信息';original.value=s.alias;alias.value=s.alias;path.value=s.path;global.checked=s.global;error.textContent='';dialog.style.display='flex'}function closeDialog(){dialog.style.display='none'}async function save(){let r=await fetch('/api/skills',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({originalAlias:original.value,alias:alias.value,path:path.value,global:global.checked})});let d=await r.json();if(!r.ok){error.textContent=d.error;return}closeDialog();load()}async function toggle(a,g){let s=(await fetch('/api/skills').then(r=>r.json())).skills.find(x=>x.alias===a);await fetch('/api/skills',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({originalAlias:a,alias:a,path:s.path,global:g})});load()}async function removeSkill(a){if(confirm('删除 skill '+a+'？')){await fetch('/api/skills/'+encodeURIComponent(a),{method:'DELETE'});load()}}load();</script></body></html>"""

_ANSWER_UI_HTML = """<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>AI Ask Answer</title>
<style>body{font-family:"Microsoft YaHei UI",Segoe UI,sans-serif;margin:32px;background:#fafafa;color:#222}h1{margin:0 0 14px}p{color:#666}button{padding:9px 18px;border:1px solid #805100;border-radius:6px;background:#805100;color:#fff;font-size:15px;cursor:pointer}pre{white-space:pre-wrap;word-break:break-word;user-select:text;background:#fff;border:1px solid #ddd;border-radius:8px;padding:22px;line-height:1.65;font-family:Consolas,"Microsoft YaHei UI",monospace;font-size:15px}</style>
<body><h1>AI Ask · 完整回答</h1><p>可用鼠标选择任意片段后按 Ctrl+C 复制，或点击按钮复制全部。</p><button onclick="copyAll()">复制全部</button><pre id="answer">正在加载…</pre><script>async function load(){let d=await fetch('/api/answer').then(r=>r.json());answer.textContent=d.answer||'(暂无回答)'}async function copyAll(){await navigator.clipboard.writeText(answer.textContent)}load();</script></body></html>"""


# ---------------------------------------------------------------------------
# Result types
# ---------------------------------------------------------------------------

async def _copy_to_clipboard(result: Result, text: str) -> bool:
    if not text or result.plugin is None:
        return False
    try:
        response = await result.plugin.jsonrpc.request("CopyToClipboard", [text, False, True])
        return not isinstance(response, ErrorResponse)
    except Exception:
        return False


class ActionResult(Result):
    def __init__(self, title: str, sub: str, icon: str, action):
        super().__init__(title=title, sub=sub, icon=icon)
        self._action = action

    async def callback(self) -> ExecuteResponse:
        return ExecuteResponse(hide=await self._action())


class AIResult(Result):
    """An explicit copy action for a complete (or current) answer."""

    def __init__(self, title, sub="", copy_text=None, icon=ICON):
        super().__init__(title=title, sub=sub, icon=icon, copy_text=copy_text)
        self._answer = copy_text or ""

    async def callback(self) -> ExecuteResponse:
        return ExecuteResponse(hide=await _copy_to_clipboard(self, self._answer))


class AnswerSummaryResult(Result):
    """Clickable summary opening a browser page with selectable full text."""

    def __init__(self, plugin, title: str, sub: str, icon: str):
        super().__init__(title=title, sub=sub, icon=icon)
        self._owner = plugin

    async def callback(self) -> ExecuteResponse:
        url = self._owner.start_answer_ui()
        try:
            await self._owner.api.open_url(url)
        except Exception:
            pass
        # Keep Flow visible; the browser page is for selectable partial copying.
        return ExecuteResponse(hide=False)


def _answer_summary(text: str, limit: int = 180) -> str:
    compact = " ".join(text.split())
    return compact if len(compact) <= limit else compact[: limit - 1].rstrip() + "…"


class StartGenerationResult(Result):
    def __init__(self, query: Query, prompt: str, selected_skills: list[dict], visible_query: str, detail: str):
        super().__init__(title="Generate response", sub=f"{detail} · Press Enter or click to send", icon=GENERATING_ICON)
        self._query = query
        self._prompt = prompt
        self._selected_skills = selected_skills
        self._visible_query = visible_query

    async def callback(self) -> ExecuteResponse:
        if self.plugin is not None:
            self.plugin.start_stream(self._query, self._prompt, self._selected_skills, self._visible_query)
            await self.plugin.api.change_query(self._visible_query, False)
        return ExecuteResponse(hide=False)


# ---------------------------------------------------------------------------
# Plugin + search handler
# ---------------------------------------------------------------------------

class AIAskPlugin(Plugin):
    def __init__(self, **options) -> None:
        super().__init__(**options)
        self._stop_event = asyncio.Event()
        self._stream_task: asyncio.Task | None = None
        self._stream_generation = 0
        self._last_answer = ""
        self._visible_query = "ai"
        # Flow 2.1.x drops Python_v2 UpdateResults at the UI layer. Use clean
        # query re-entry plus this cache, never exposing an internal query key.
        self._display_generation = -1
        self._display_key = ""
        self._display_results: list[Result] = []
        self._display_text = ""
        self._display_mode = False
        self._skills_lock = threading.RLock()
        self._ui_server: ThreadingHTTPServer | None = None
        self._ui_thread: threading.Thread | None = None

    # -- Skill persistence and management -----------------------------------

    def skills_for_ui(self) -> list[dict]:
        with self._skills_lock:
            result = []
            for skill in _load_skills(self):
                valid, status = _skill_status(skill)
                result.append({"alias": str(skill.get("alias", "")), "path": str(skill.get("path", "")), "global": bool(skill.get("global", False)), "valid": valid, "status": status})
            return result

    def upsert_skill(self, alias: str, path_text: str, global_enabled: bool, original_alias: str = "") -> dict:
        alias, path = _validate_skill(alias, path_text)
        with self._skills_lock:
            skills = _load_skills(self)
            original_alias = original_alias.strip().lower()
            if any(s.get("alias") == alias and alias != original_alias for s in skills):
                raise ValueError(f"Skill alias already exists: {alias}")
            updated = {"alias": alias, "path": str(path), "global": global_enabled}
            if original_alias:
                for index, skill in enumerate(skills):
                    if str(skill.get("alias", "")).lower() == original_alias:
                        skills[index] = updated
                        break
                else:
                    raise ValueError(f"Skill alias not found: {original_alias}")
            else:
                skills.append(updated)
            _save_skills(self, skills)
            return updated

    def delete_skill(self, alias: str) -> bool:
        alias = alias.strip().lower()
        with self._skills_lock:
            skills = _load_skills(self)
            updated = [s for s in skills if str(s.get("alias", "")).lower() != alias]
            if len(updated) == len(skills):
                return False
            _save_skills(self, updated)
            return True

    def toggle_global(self, alias: str) -> dict:
        alias = alias.strip().lower()
        with self._skills_lock:
            skills = _load_skills(self)
            for skill in skills:
                if str(skill.get("alias", "")).lower() == alias:
                    skill["global"] = not bool(skill.get("global", False))
                    _save_skills(self, skills)
                    return skill
            raise ValueError(f"Skill alias not found: {alias}")

    def _ensure_ui_server(self) -> str:
        if self._ui_server is None:
            handler = type("LocalUIHandler", (_LocalUIHandler,), {"manager": self})
            self._ui_server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
            self._ui_thread = threading.Thread(target=self._ui_server.serve_forever, name="aiask-local-ui", daemon=True)
            self._ui_thread.start()
        return f"http://127.0.0.1:{self._ui_server.server_port}"

    def start_skills_ui(self) -> str:
        return self._ensure_ui_server() + "/"

    def start_answer_ui(self) -> str:
        return self._ensure_ui_server() + "/answer"

    def current_answer(self) -> str:
        return self._display_text or self._last_answer

    # -- Streaming -----------------------------------------------------------

    def clear_last_answer(self) -> None:
        self.stop_stream()
        self._last_answer = ""
        self._display_generation = -1
        self._display_key = ""
        self._display_results = []
        self._display_text = ""
        self._display_mode = False

    def stop_stream(self) -> bool:
        self._stream_generation += 1
        self._stop_event.set()
        task = self._stream_task
        if task is not None and not task.done():
            if task is not asyncio.current_task():
                task.cancel()
            return True
        return False

    def start_stream(self, query: Query, prompt: str, selected_skills: list[dict], visible_query: str) -> None:
        self.stop_stream()
        self._stop_event.clear()
        self._visible_query = visible_query
        generation = self._stream_generation

        async def run():
            task = asyncio.current_task()
            try:
                answer = await self.stream_answer(query, prompt, selected_skills, generation)
                if self._stream_task is task and self._stream_generation == generation:
                    self._last_answer = answer
            finally:
                if self._stream_task is task:
                    self._stream_task = None
                    self._stop_event.clear()

        self._display_generation = generation
        self._display_key = visible_query
        self._display_results = [Result("Generating…", "Connecting to AI…", icon=GENERATING_ICON)]
        self._display_text = ""
        self._display_mode = True
        self._stream_task = asyncio.create_task(run(), name="aiask-stream")

    def _is_current_generation(self, generation: int) -> bool:
        return self._stream_generation == generation and not self._stop_event.is_set()

    def _set_display(self, generation: int, results: list[Result], text: str) -> bool:
        if not self._is_current_generation(generation):
            return False
        self._display_generation = generation
        self._display_results = results
        self._display_text = text
        return True

    async def _refresh_display(self, generation: int, results: list[Result], text: str) -> bool:
        if not self._set_display(generation, results, text):
            return False
        try:
            self._display_key = self._visible_query
            response = await self.api.change_query(self._display_key, True)
            return not isinstance(response, ErrorResponse) and self._is_current_generation(generation)
        except Exception:
            if self._is_current_generation(generation):
                self.stop_stream()
            return False

    async def stream_answer(self, query: Query, prompt: str, selected_skills: list[dict], generation: int) -> str:
        text = ""
        try:
            contents = [_read_skill(skill) for skill in selected_skills]
            system_prompt = "\n\n---\n\n".join(part for part in contents if part)
            messages = ([{"role": "system", "content": system_prompt}] if system_prompt else []) + [{"role": "user", "content": prompt}]
            base_url, api_key = _setting_text(self, "base_url"), _setting_text(self, "api_key")
            model = _setting_text(self, "model") or "fast"
            timeout = _as_int(getattr(self.settings, "timeout", 60), 60)
            headers = {"X-Skill-Count": str(len(selected_skills))}

            loop = asyncio.get_running_loop()
            queue: asyncio.Queue = asyncio.Queue()

            def producer():
                try:
                    for content in _open_sse_stream(base_url, api_key, model, messages, _as_int(getattr(self.settings, "max_tokens", 1000), 1000), timeout, headers):
                        if self._stop_event.is_set():
                            break
                        loop.call_soon_threadsafe(queue.put_nowait, ("chunk", content))
                except Exception as error:
                    loop.call_soon_threadsafe(queue.put_nowait, ("error", error))
                finally:
                    loop.call_soon_threadsafe(queue.put_nowait, ("done", None))

            threading.Thread(target=producer, name="aiask-sse", daemon=True).start()
            chunks_since_update = 0
            while self._is_current_generation(generation):
                kind, value = await queue.get()
                if kind == "done":
                    break
                if kind == "error":
                    raise value
                text += value
                chunks_since_update += 1
                if chunks_since_update >= 6:
                    results = [
                        AIResult("Copy answer so far", "Enter copies the current complete response", copy_text=text, icon=ANSWER_ICON),
                        AnswerSummaryResult(self, "Generating…", _answer_summary(text), GENERATING_ICON),
                    ]
                    if not await self._refresh_display(generation, results, text):
                        return text
                    chunks_since_update = 0

            if text and self._is_current_generation(generation):
                results = [
                    AIResult("Copy full answer", "Enter copies the complete answer to the clipboard", copy_text=text, icon=ANSWER_ICON),
                    AnswerSummaryResult(self, "Answer ready", _answer_summary(text), ANSWER_ICON),
                ]
                await self._refresh_display(generation, results, text)
            return text
        except asyncio.CancelledError:
            if self._stream_task is asyncio.current_task() and self._stream_generation == generation:
                self._stop_event.set()
            return text
        except Exception as error:
            if self._is_current_generation(generation):
                results: list[Result] = []
                if text:
                    results.extend([
                        AIResult("Copy partial answer", "Enter copies the partial response", copy_text=text, icon=ANSWER_ICON),
                        AnswerSummaryResult(self, "Partial answer", _answer_summary(text), ANSWER_ICON),
                    ])
                results.append(Result("AI Ask error", _friendly_error(error), icon=ERROR_ICON))
                await self._refresh_display(generation, results, text)
            return text

    # -- Commands ------------------------------------------------------------

    def _skill_result(self, skill: dict) -> Result:
        alias, path = str(skill.get("alias", "?")), str(skill.get("path", ""))
        valid, status = _skill_status(skill)
        global_enabled = bool(skill.get("global", False))

        async def toggle():
            self.toggle_global(alias)
            return False

        async def delete():
            self.delete_skill(alias)
            return False

        class SkillResult(Result):
            async def callback(inner_self):
                await toggle()
                return ExecuteResponse(hide=False)
            async def context_menu(inner_self):
                return [ActionResult("Disable global load" if global_enabled else "Enable global load", "Toggle automatic system-prompt loading", ICON, toggle), ActionResult("Delete skill", "Remove this skill record", CLEAR_ICON, delete)]

        return SkillResult(title=f"{'[Global] ' if global_enabled else ''}{alias}", sub=f"{status} · {path} · Enter toggles global", icon=ICON if valid else ERROR_ICON)

    async def skills_command(self, command_text: str) -> list[Result]:
        try:
            args = _skills_command_args(command_text)
        except ValueError as error:
            return [Result("Invalid /skills command", str(error), icon=ERROR_ICON)]
        if not args:
            results = [Result("Skills", "Use /skills add, edit, delete, toggle, or /skills-ui", icon=ICON)]
            results.extend(self._skill_result(skill) for skill in _load_skills(self))
            return results
        command = args[0].lower()
        try:
            if command == "add":
                if len(args) < 3:
                    return [Result("Usage: /skills add <alias> <path> [global]", "Path may be relative to the plugin folder.", icon=ERROR_ICON)]
                enabled = len(args) > 3 and args[3].lower() in {"global", "on", "true", "1"}
                skill = self.upsert_skill(args[1], args[2], enabled)
                return [Result("Skill added", f"{skill['alias']} · {skill['path']}", icon=ANSWER_ICON)]
            if command == "edit":
                if len(args) < 4:
                    return [Result("Usage: /skills edit <old-alias> <new-alias> <path> [global]", "Edits metadata only, not skill-file contents.", icon=ERROR_ICON)]
                enabled = len(args) > 4 and args[4].lower() in {"global", "on", "true", "1"}
                skill = self.upsert_skill(args[2], args[3], enabled, args[1])
                return [Result("Skill updated", f"{skill['alias']} · {skill['path']}", icon=ANSWER_ICON)]
            if command in {"delete", "remove"}:
                if len(args) != 2:
                    return [Result("Usage: /skills delete <alias>", "", icon=ERROR_ICON)]
                deleted = self.delete_skill(args[1])
                return [
                    Result(
                        "Skill deleted" if deleted else "Skill not found",
                        args[1],
                        icon=CLEAR_ICON if deleted else ERROR_ICON,
                    )
                ]
            if command in {"toggle", "global"}:
                if len(args) != 2:
                    return [Result("Usage: /skills toggle <alias>", "", icon=ERROR_ICON)]
                skill = self.toggle_global(args[1])
                return [Result(f"Global load {'enabled' if skill['global'] else 'disabled'}", skill["alias"], icon=ICON)]
            return [Result("Unknown /skills command", command, icon=ERROR_ICON)]
        except ValueError as error:
            return [Result("Skill change failed", str(error), icon=ERROR_ICON)]

    def _parse_add(self, command_text: str) -> tuple[list[dict] | None, str | None, str | None]:
        rest = command_text.strip()
        if not rest:
            return None, None, "Choose one or more skills below, then add your question."
        available = {str(s.get("alias", "")).lower(): s for s in _load_skills(self)}
        first = re.match(r"^([^\s:：]+)(.*)$", rest, flags=re.S)
        first_alias, remainder = (first.group(1).lower(), first.group(2)) if first else ("", "")
        aliases = [first_alias] if first_alias in available else []
        if remainder.lstrip().startswith((":", "：")) and aliases:
            remainder = remainder.lstrip()
            while remainder.startswith((":", "：")):
                tail = remainder[1:].lstrip()
                next_part = re.match(r"^([^\s:：]+)(.*)$", tail, flags=re.S)
                if not next_part or next_part.group(1).lower() not in available:
                    remainder = tail
                    break
                aliases.append(next_part.group(1).lower())
                remainder = next_part.group(2).lstrip()
                if not remainder.startswith((":", "：")):
                    break
        prompt = remainder.strip()
        if not aliases:
            return None, None, "Specify a valid skill alias after /add."
        if not prompt:
            return None, None, "Add your question after the selected skill alias."
        selected = [available[alias] for alias in aliases]
        for skill in selected:
            valid, status = _skill_status(skill)
            if not valid:
                return None, None, f"Skill {skill['alias']} has {status}: {skill['path']}"
        return selected, prompt, None

    async def command_results(self, command_text: str) -> list[Result] | None:
        tokens = _parse_ai_command(command_text)
        if not tokens:
            return None
        head = tokens[0].lower()
        tail = command_text[len(tokens[0]):].strip(" \t:：")
        if head == "/skills-ui":
            url = self.start_skills_ui()
            async def open_ui():
                try:
                    response = await self.api.open_url(url)
                    return not isinstance(response, ErrorResponse)
                except Exception:
                    return False
            return [ActionResult("Open Skill manager", url, ICON, open_ui)]
        if head == "/skills":
            match = re.match(r"^\s*/skills(?:[\s:：]+)?(.*)$", command_text, flags=re.I)
            return await self.skills_command(match.group(1) if match else "")
        if head == "/add":
            if not tail:
                results = [Result("Choose a temporary skill", "Use /add <alias> <question>", icon=ICON)]
                for skill in _load_skills(self):
                    valid, status = _skill_status(skill)
                    results.append(Result(str(skill.get("alias", "?")), f"{status} · {skill.get('path', '')}", icon=ICON if valid else ERROR_ICON))
                return results
            return None
        return None


class MainSearchHandler(SearchHandler):
    async def callback(self, query: Query):
        plugin: AIAskPlugin = self.plugin
        raw = query.text.strip().lstrip(" \t:：")

        # Stream refresh uses a clean visible query and normal Flow query routing.
        if query.keyword == "ai" and plugin._display_mode and plugin._display_generation >= 0:
            if query.raw_text != plugin._display_key:
                plugin.stop_stream()
                plugin._display_mode = False
            else:
                return plugin._display_results

        # Wildcard only claims compact ai:/... and ai：/... forms.
        if query.keyword != "ai":
            compact = re.match(r"^ai\s*[:：]\s*(.+)$", raw, flags=re.I | re.S)
            if not compact:
                return []
            raw = compact.group(1).strip()

        if not raw:
            global_skills, problems = _active_global_skills(plugin)
            subtitle = f"global skills: {len(global_skills)}" + (f" · invalid: {', '.join(problems)}" if problems else "")
            return Result("Ask the AI assistant", subtitle, icon=ICON)

        tokens = _parse_ai_command(raw)
        command = tokens[0].lower() if tokens else ""
        if command != "/add":
            command_results = await plugin.command_results(raw)
            if command_results is not None:
                return command_results
        if command == "/stop":
            stopped = plugin.stop_stream()
            return Result("Stop requested", "Generation cancelled." if stopped else "No generation is currently active.", icon=STOP_ICON)
        if command == "/clear":
            plugin.clear_last_answer()
            return Result("Cleared", "Last answer history reset.", icon=CLEAR_ICON)
        if command == "/last":
            text = plugin._last_answer or "(nothing generated yet in this session)"
            return AIResult("Last answer — Enter or Ctrl+C to copy", _answer_summary(text), copy_text=text, icon=ANSWER_ICON)
        if not _is_configured(plugin):
            return Result("Configure AI Ask", "Open plugin settings and set Base URL plus API Key.", icon=ERROR_ICON)

        dynamic_skills: list[dict] = []
        prompt = raw
        if command == "/add":
            match = re.match(r"^\s*/add(?:[\s:：]+)?(.*)$", raw, flags=re.I)
            add_text = match.group(1) if match else ""
            if not add_text:
                return await plugin.command_results(raw)
            dynamic_skills, prompt, error = plugin._parse_add(add_text)
            if error:
                return Result("Dynamic skill not loaded", error, icon=ERROR_ICON)
        global_skills, problems = _active_global_skills(plugin)
        if problems:
            return Result("Global skill path error", "; ".join(problems), icon=ERROR_ICON)
        selected = global_skills + (dynamic_skills or [])
        detail = f"global: {len(global_skills)} · temporary: {len(dynamic_skills or [])}"
        visible_query = "ai" if not dynamic_skills else "ai /add " + " ".join(str(s.get("alias", "")) for s in dynamic_skills)
        return StartGenerationResult(query, prompt, selected, visible_query, detail)

    async def on_error(self, query: Query, error: Exception):
        return Result("AI Ask error", _friendly_error(error), icon=ERROR_ICON)


def main():
    plugin = AIAskPlugin()
    plugin.register_search_handler(MainSearchHandler())
    plugin.run(setup_default_log_handler=False)


if __name__ == "__main__":
    main()

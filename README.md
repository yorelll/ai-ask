# AI Ask

AI Ask is a Flow Launcher plugin for OpenAI-compatible AI conversations. It provides configurable API settings, streaming answers, global and per-request skills, and a native Flow preview for selecting and copying answer text.

## Features

- **Explicit sending** — type `ai <question>`, then press Enter or select **Generate response**. No network request starts while typing.
- **Streaming responses** — responses update as tokens arrive; start a new request to replace an older one, or use `/stop` to cancel.
- **Native answer preview** — open an answer result to view the full answer in Flow, select any portion, and copy it with `Ctrl+C`. A separate result copies the entire answer.
- **OpenAI-compatible API** — configure Base URL, API Key, Model, Max Token (default `100000`), and Timeout (default `60` seconds).
- **Native Skill settings** — configure skills in the Flow Plugin Settings panel with alias, UTF-8 file path, global toggle, status, add, edit, and delete controls.
- **Global Skills** — checked skills are combined into the system prompt for each request.
- **Dynamic Skills** — use `/add` to add one or more skills to only the current request.
- **Safe handling** — validates Skill storage/path/encoding and maps network/API failures to safe user messages.

## Requirements

- Windows
- A current Flow Launcher release compatible with `Flow.Launcher.Plugin` 5.3.2
- An OpenAI-compatible chat-completions endpoint

No Python runtime is required.

## Installation

1. Download `AIAsk-<version>.zip` from the GitHub Release page.
2. Extract the **contents** of the archive into a Flow Launcher plugin directory.
3. Open Flow Launcher Plugin Settings, select **AI Ask**, then configure:
   - Base URL
   - API Key
   - Model
   - Max Token
   - Timeout
   - Skills
4. Enter `ai <question>` in Flow Launcher and select **Generate response**.

> The API key uses a password field in Settings but is stored by Flow Launcher in its plugin settings storage. Use a dedicated, least-privilege key.

## Query commands

| Input | Behavior |
|---|---|
| `ai <question>` | Show **Generate response**; press Enter/click to send. |
| `ai /add` | List skills available for the current request. |
| `ai /add translate <question>` | Add one temporary Skill to the request. |
| `ai /add translate:review:<question>` | Add multiple temporary Skills. Full-width `：` is also supported. |
| `ai:/add:translate:<question>` | Compact dynamic Skill form. `ai：/add：translate：<question>` also works. |
| `ai /stop` | Cancel the active request. |
| `ai /clear` | Clear the current answer and stop the active request. |
| `ai /last` | Show the most recent answer and copy it. |

For a space-delimited `/add` command, only the first token after `/add` is treated as the Skill alias; the rest is preserved as the prompt. Use `:` or `：` for multiple Skills so natural-language colons remain intact.

## Skills

Skills are stored under Flow Launcher’s plugin settings directory in `skills.json`:

```json
{
  "version": 1,
  "skills": [
    {
      "alias": "translate",
      "path": "C:\\skills\\translate.md",
      "global": true
    }
  ]
}
```

- Skill aliases may use Unicode but cannot contain whitespace, `:`, `：`, `/`, or `\`.
- Skill files must be readable UTF-8 text files.
- Relative paths resolve from the AI Ask plugin directory.
- Editing a skill changes its alias/path/global metadata, not the Skill file contents.

## Release tracks

Python and managed implementation tags are independent:

```text
Python tag: v<semver>
Managed tag: csharp-v<semver>
```

The `csharp-v` prefix is only an automation tag used to avoid overwriting a Python release with the same semantic version. User-facing release names remain normal AI Ask product names.

## Development and CI

The C# project is validated in GitHub Actions on Windows. The workflow performs restore, Release build, and xUnit test execution. The package verification workflow stages the plugin DLL, managed dependencies, manifest, and images, then validates archive contents before release.

Local planning files in `task/` are intentionally gitignored and are not part of the source or release artifact.

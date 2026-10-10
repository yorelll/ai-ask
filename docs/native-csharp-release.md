# AI Ask release packaging

The release workflow creates an **AI Ask** plugin archive for Flow Launcher.

## Version and tag

The plugin manifest defines the user-visible version, for example:

```json
"Version": "1.0.0"
```

The release workflow uses the technical automation tag:

```text
csharp-v1.0.0
```

The implementation language and automation tag are internal details; the user-visible GitHub release and archive are named simply:

```text
AI Ask v1.0.0
AIAsk-1.0.0.zip
```

## Publish flow

`.github/workflows/csharp-release.yml`:

1. Runs on Windows;
2. restores, builds, and tests the solution;
3. stages the Flow plugin manifest, executable DLL, managed dependencies, and images;
4. verifies staging and ZIP contents;
5. publishes the `AI Ask` GitHub release on a matching `csharp-v<version>` tag.

A manual workflow run performs packaging validation by default. It only publishes
when the operator explicitly sets its `publish` input to `true`.

## Artifact layout

The ZIP root contains:

```text
plugin.json
AIAsk.Plugin.dll
*.dll
Images/plugin.png
Images/*.png
```

The packaging verifier rejects obsolete Python runtime files and folders:

```text
main.py
requirements.txt
SettingsTemplate.yaml
test_v2_protocol.py
lib/
skill_files/
.venv/
task/
```

Extract the archive contents directly into a Flow Launcher plugin directory.

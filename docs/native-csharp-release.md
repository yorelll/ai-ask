# Native C# release

The repository currently contains two intentionally distinct plugin lines:

| Line | Manifest language | Release tag | Runtime |
|---|---|---|---|
| Existing Python release | `python_v2` | `v0.0.1` | Python 3.11+ |
| Native C# release | `csharp` | `csharp-v1.0.0-csharp.1` | Flow/.NET only |

The native C# release must **never overwrite** the Python `v0.0.1` release.

## Triggering a native release

The workflow `.github/workflows/csharp-release.yml` publishes from:

```text
csharp-v<version>
```

A manual workflow run performs build/test/package validation by default. It only
publishes if the operator explicitly sets its `publish` input to `true`.

For example, when `src/AIAsk.Plugin/plugin.json` contains:

```json
"Version": "1.0.0-csharp.1"
```

publish:

```text
csharp-v1.0.0-csharp.1
```

The workflow runs on Windows, restores/builds/tests the C# solution, stages only
native DLL output plus the C# manifest and images, verifies the staged contents,
and publishes `AIAsk.Native.CSharp-<version>.zip`.

## Native artifact layout

The zip root contains:

```text
plugin.json                 # Language: csharp
AIAsk.Plugin.dll            # ExecuteFileName
*.dll                       # required managed dependencies
Images/plugin.png
Images/*.png
```

It deliberately excludes the Python runtime assets:

```text
main.py
requirements.txt
SettingsTemplate.yaml
lib/
skill_files/
test_v2_protocol.py
```

Install by extracting the archive into a Flow Launcher plugin directory. Do not
install the Python and native editions under the same plugin directory, because
they share the same plugin ID.

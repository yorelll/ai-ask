# AI Ask

AI Ask 是一个 Flow Launcher 插件，用于通过任何 **OpenAI-compatible** API 进行 AI 对话。插件提供 API 配置、流式回答、全局/动态 Skill、Flow 原生设置面板，以及可在 Flow 内选择和复制部分回答的原生预览。

## 主要功能

- **显式发送**：输入 `ai <问题>` 后，先显示 `Generate response`；按 Enter 或点击该结果才会发起网络请求，输入过程中不会提前发送。
- **流式回答**：模型 token 到达时持续更新结果；新请求会替换旧请求，`/stop` 可以取消当前生成。
- **原生回答预览**：点击具体回答结果后，在 Flow 内展开完整回答。可以滚动、鼠标选择任意片段、按 `Ctrl+C` 复制选中内容，也可以使用 `Copy all` 复制全文。
- **完整回答复制**：结果列表提供 `Copy full answer`，按 Enter 即复制完整回答。
- **OpenAI-compatible API**：可在设置中配置 Base URL、API Key、Model、Max Token（默认 `100000`）、Timeout（默认 `60` 秒）。
- **Flow 原生 Skill 设置**：在 AI Ask 的 Flow 插件设置页面中管理 Skill：别名、UTF-8 文件路径、全局开关、有效状态、添加、编辑、删除。
- **全局 Skill**：勾选的 Skill 会合并为每次请求的 system prompt。
- **动态 Skill**：使用 `/add` 为当前请求临时加载一条或多条 Skill，不改变全局配置。
- **安全处理**：校验 Skill 存储、路径、编码；对网络/API 错误显示安全的用户提示，不暴露 API Key、服务响应正文或 endpoint 细节。

## 环境要求

- Windows。
- 兼容 `Flow.Launcher.Plugin` 5.3.2 的较新 Flow Launcher 版本。
- 一个支持 Chat Completions 的 OpenAI-compatible API endpoint。

本版本是原生 .NET 插件，**不需要 Python 运行环境**。

## 安装

1. 在 GitHub Release 下载：

   ```text
   AIAsk-<版本号>.zip
   ```

2. 将压缩包中的**内容**直接解压到 Flow Launcher 的插件目录。
3. 打开 Flow Launcher 的插件设置，选择 **AI Ask**，填写：
   - Base URL
   - API Key
   - Model
   - Max Token
   - Timeout
   - Skills
4. 在 Flow Launcher 输入 `ai <问题>`，再选择 `Generate response` 发送。

> API Key 在设置页使用密码输入框显示，但 Flow Launcher 会将其保存在插件设置存储中。请使用专用、低权限的 API Key。

## 对话与复制

### 发送问题

```text
ai 帮我解释这段代码
```

输入期间只会出现 `Generate response`，不会立即请求模型。按 Enter 或点击该结果后开始生成。

### 查看和复制回答

生成后结果列表中：

1. `Answer ready`：点击或按 Enter，在 **Flow 内** 打开完整回答预览；可以选择任意文本后按 `Ctrl+C` 复制部分内容。
2. `Copy full answer`：按 Enter 复制完整回答。

### 对话命令

| 输入 | 行为 |
|---|---|
| `ai <问题>` | 显示 `Generate response`；按 Enter/点击后发送。 |
| `ai /add` | 显示当前请求可用的 Skill；选择后会自动回填 `ai /add <别名> `，继续输入问题。 |
| `ai /add translate <问题>` | 为本次请求加载一条临时 Skill。 |
| `ai /add translate:review:<问题>` | 为本次请求加载多条临时 Skill。全角冒号 `：` 也支持。 |
| `ai:/add:translate:<问题>` | 紧凑动态 Skill 写法；`ai：/add：translate：<问题>` 也支持。 |
| `ai /stop` | 取消当前生成。 |
| `ai /clear` | 清空当前回答并停止生成。 |
| `ai /last` | 显示最近一次回答并复制。 |

对于以空格分隔的 `/add`，`/add` 后的第一个 token 被视为 Skill 别名，其余内容完整作为问题保留。需要加载多条临时 Skill 时，使用 `:` 或 `：` 分隔别名，避免自然语言中的冒号被误解。

## Skill 设置

Skill 在 AI Ask 的 Flow 原生设置页面管理，不需要浏览器、网页或 `/skills` 命令。

每条 Skill 包含：

```json
{
  "alias": "translate",
  "path": "C:\\skills\\translate.md",
  "global": true
}
```

- `alias`：用于 `/add` 的别名，例如 `translate`。
- `path`：Skill 的 UTF-8 文本文件路径；相对路径以 AI Ask 插件目录为基准。
- `global`：启用后，Skill 内容会在每次对话中作为 system prompt 加载。

设置页面支持：

```text
Alias / Path / Global / Status
添加 / 编辑 / 删除
```

编辑只改变别名、路径和全局状态，不会修改 Skill 文件内容。

Skill 别名允许 Unicode，但不能包含：

```text
空白字符  :  ：  /  \
```

## Release 轨道

Python 与 C# 的发布进度相互独立：

```text
Python tag: v<semver>
C# tag:     csharp-v<semver>
```

例如：

```text
Python: v0.0.2
C#:     csharp-v0.0.2
```

`csharp-v` 仅用于 Git/CI 自动化，避免覆盖 Python 的同版本 tag。用户可见名称始终使用正常产品名称：

```text
AI Ask v<版本号>
AIAsk-<版本号>.zip
```

## 开发与 CI

C# 项目只通过 GitHub Actions Windows CI 验证，CI 会执行：

```text
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

打包验证 workflow 会 staging：

```text
AIAsk.Plugin.dll
必要的 managed dependencies
plugin.json
Images/
```

然后验证 ZIP 内容后才允许发布。

本地 `task/` 目录存放迁移/验收规划文件，已被 gitignore，不属于源代码或 Release 产物。

# AI Ask — Flow Launcher 插件

在 Flow Launcher 中使用任意 **OpenAI-compatible** API 进行流式 AI 对话，并提供可同时通过命令与本地 UI 管理的 Skill 系统。

## 解决当前 v0.0.1 的加载问题

此前 `v0.0.1` 把由 CPython 3.11 构建的 `openai` / `pydantic_core` 二进制依赖打进了 `lib/`。如果 Flow 配置为 Python 3.12，例如：

```text
C:\Users\lawrence_lv\AppData\Local\Programs\Python\Python312\pythonw.exe
```

则 `cp311-win_amd64.pyd` 无法加载，Python_v2 子进程会退出，Flow 会显示：

```text
The JSON-RPC connection with the remote party was lost before the request could complete.
```

修复版 `v0.0.1` 改用**仅 Python 标准库**实现 HTTP/SSE 流式请求；不再依赖 CPython ABI 绑定的 `openai`、`pydantic_core` 或 `jiter`。它支持 **64-bit CPython 3.11+**，包括 Flow 内置 Python 3.11.4 与用户的 Python 3.12。

## C# 原生迁移分支

C# 原生实现正在 `feature/csharp-native` 分支开发。该分支的 C# 构建和测试只通过 GitHub Actions 的 **C# CI** 验证；当前本地环境不具备 C# 编译工具。

## 功能

- `ai <问题>`：先显示“Generate response”；按 Enter 或点击后才开始流式回答，避免用户仍在输入时提前发送；发送后输入框保留干净的 `ai` 或 `ai /add <skill>`。
- API 配置：Base URL、API Key、Model、Max Token、Timeout；默认 Max Token 为 `100000`。
- 全局 Skill：启用后自动合并为每个请求的 system prompt，可同时启用多条。
- 动态 Skill：`/add` 仅为当前请求加载，不改变全局配置。
- Skill 双管理入口：
  - `ai /skills`：Flow 命令和结果列表管理；
  - `ai /skills-ui`：浏览器中的表格、添加、编辑、删除与启用开关。
- 六个状态图标：默认、生成中、回答就绪、停止、清空、错误。
- 支持 `ai /add translate 问题` 的空格/英文冒号/中文冒号混用分隔形式。

## 环境要求

- Windows x64；支持 Python_v2 的 Flow Launcher。
- **64-bit CPython 3.11+**：Flow 内置 3.11.4 或自定义 Python 3.11/3.12 都可用。
- Release 包中仅需打包 `flogin` 及其纯 Python 依赖；用户不需要 `pip install`。

## 安装

1. 从 [Releases](https://github.com/yorelll/ai-ask/releases) 下载最新 `AIAsk-<version>.zip`。
2. 解压到：

   ```text
   D:\Program Files\FlowLauncher\app-<version>\UserData\Plugins\AIAsk-<version>\
   ```

   或你的 Flow UserData Plugins 目录。**不要保留 zip 的额外嵌套目录。**
3. 在 Flow 插件管理中启用 AI Ask。
4. 打开 AI Ask 设置，填写 Base URL、API Key、Model、Max Token、Timeout。
5. 输入 `ai 你好` 开始对话。

> API Key 在设置界面中被掩码，但 Flow 的插件设置文件仍以明文保存。请使用专用、低权限 API key。

## Skill 数据与同步

Skills 存放在 Flow 的插件设置目录：

```text
<UserData>\Settings\Plugins\AI Ask\skills.json
```

这份 JSON 是 **`/skills` 与 `/skills-ui` 唯一共享数据源**：无论从命令还是浏览器 UI 添加、编辑、删除、启用，另一入口下一次读取时都会立即显示相同结果。

每条 Skill 数据：

```json
{
  "alias": "translate",
  "path": "C:\\skills\\translate.md",
  "global": true
}
```

| 字段 | 含义 |
|---|---|
| `alias` | `/add` 使用的别名，如 `translate` |
| `path` | UTF-8 Skill 文件路径；相对路径以插件根目录为基准 |
| `global` | `true` 时每次 AI 对话自动加入 system prompt |

Skill 文件内容本身不会被 `/skills edit` 修改。**edit 只修改别名、路径及全局启用状态**。

## 命令式 Skill 管理：`ai /skills`

```text
ai /skills
ai /skills add translate skill_files/translate.md global
ai /skills add review C:\skills\review.md
ai /skills edit translate translator C:\skills\translator.md global
ai /skills toggle review
ai /skills delete review
```

- `add <alias> <path> [global]`：添加；最后参数是 `global` / `on` / `true` / `1` 时全局启用。路径含空格时使用英文双引号，例如 `ai /skills add translate "C:\\My Skills\\translate.md" global`。
- `edit <旧别名> <新别名> <路径> [global]`：仅编辑 skill 元信息；路径含空格时同样使用双引号。
- `toggle <alias>`：切换全局加载。
- `delete <alias>`：删除记录，不删除原 skill 文件。
- 列表中的每项提供右键 / Shift+Enter 菜单，用于切换全局加载或删除。
- 每次新增和编辑都会校验路径是否存在、是否是可读 UTF-8 文本；路径失效会显示错误。

## 图形化 Skill 管理：`ai /skills-ui`

输入：

```text
ai /skills-ui
```

然后按 Enter 打开仅本机可访问的 `127.0.0.1` 页面，页面有：

- 别名、路径、全局加载、路径有效状态；
- 添加、编辑、删除；
- 全局加载 checkbox；
- 编辑弹窗；
- 保存时路径和 alias 校验（alias 可用中文或英文，但不能包含空格、`:`、`：`、`/`、`\\`）。

该 UI 与 `/skills` 同步使用同一 `skills.json`，无需导入或导出。

## 全局与动态 Skill

### 全局 Skill

启用一条或多条 global skill 后：

```text
ai 解释这个命令
```

所有启用项的文件内容按配置顺序合并为 system prompt。

### 动态 Skill：`/add`

```text
ai /add
```

显示可选 skill；随后使用别名临时加入本次请求：

```text
ai /add translate 这段英文是什么意思
ai /add translate:review:这段代码有什么问题
ai /add translate：review：这段代码有什么问题
```

空格形式只把 `/add` 后的第一个别名作为动态 skill，后续文字完整保留为问题；如果需要多条动态 skill，请使用 `:` 或 `：` 分隔多个别名。动态 skill 不会写入全局状态，也不会影响下一次对话。

### 分隔符支持

进入插件后，命令 parser 支持：空格、英文冒号 `:`、中文冒号 `：`，以及它们前后的任意空格和混用：

```text
ai /add translate xxx 命令是什么意思
ai : /add : translate : xxx 命令是什么意思
ai ： /add：translate : xxx 命令是什么意思
ai /add translate：review：xxx 命令是什么意思
```

> 插件同时注册受控 wildcard 路由，仅接收以 `ai:` 或 `ai：` 开头的查询并将其标准化为同一命令。因此 `ai:/add:translate:问题`、`ai：/add：translate：问题` 与空格形式都支持；其他全局查询会立即返回空结果，不影响其它插件。

## 示例 Skill 文件

Release 内含可直接尝试的文件：

```text
skill_files/translate.md
skill_files/review.md
```

例如：

```text
ai /skills add translate skill_files/translate.md global
ai /add review 请检查这段代码
```

## 交互命令

| 输入 | 行为 |
|---|---|
| `ai <问题>` | 显示 Generate response；按 Enter 或点击才发送并开始流式回答；第一项 `Copy full answer` 可 Enter 复制完整回答，第二项 `Answer ready` 可 Enter 打开可选择/部分复制的完整回答页 |
| `ai /stop` | 取消当前生成 |
| `ai /clear` | 清空当前 session 最近回答并停止生成 |
| `ai /last` | 显示最近回答；Enter 或 `Ctrl+C` 复制 |
| `ai /skills` | 列出与命令管理 skills |
| `ai /skills-ui` | 打开图形化 Skill 管理页 |
| `ai /add` | 显示可临时加载的 skills |
| `ai /add <alias...> <问题>` | 为本次对话加载动态 skills |

## 技术设计

- `Language: python_v2`：长驻插件进程，NewLineDelimited JSON-RPC。
- HTTP：标准库 `urllib.request` + SSE parser，后台线程生产 chunk、async queue 消费；无 CPython ABI 绑定第三方网络 SDK。
- 流式：标准库 SSE parser 每 6 个 chunk 刷新一次。Flow 2.1.x 会丢弃 Python_v2 `UpdateResults`，因此插件通过保留的 `ai` / `ai /add <skill>` 输入进行受控 requery 来刷新结果；内部路由不会显示给用户。第二项摘要可打开仅本机的完整回答页，允许鼠标选择任意片段并复制。
- Skill UI：标准库 `ThreadingHTTPServer` 仅监听 `127.0.0.1`，浏览器管理页面无外网暴露。
- 打包：GitHub Action 将纯 Python 依赖装入 `lib/`，Release 可直接复制进 Plugins 目录。

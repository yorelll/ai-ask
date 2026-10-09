# AI Ask — Flow Launcher 插件

通过 Flow Launcher 对话框与任意 **OpenAI 兼容**大模型（如 Realtek realgpt、OpenAI、DeepSeek、通义等）进行**流式**对话。

采用 **Python_v2** 协议（长驻进程 + 流式 JSON-RPC），回答会**实时逐字刷新**到 Flow 的结果列表中。

## 功能

- 🔎 输入 `ai <你的问题>` 发起对话
- ⚡ **流式输出**：回答逐字实时刷新到结果列表（基于 Python_v2 的 `UpdateResults` 推送，不会破坏输入框中的 `ai` 关键字）
- 🧠 **Skills**：配置多个技能，请求时：
  - ASCII skill 作为请求头注入：`X-Skill-1`, `X-Skill-2`, …（自动剥离控制字符，保持原始序号）
  - 所有 skill（包括中文）均追加到提示词**尾部**：`[Skills] 1. … 2. …`
- ⚙️ 可配置：`base_url`、`api_key`、`model`、`max_tokens`（默认 1000）、`timeout`（默认 60s）、`system_prompt`、`skills`
- 📋 `/last` 显示最近回答（`Ctrl+C` 复制）、`/clear` 清空、`/stop` 停止生成
- 🎨 六个状态图标：默认、生成中、回答就绪、停止、清空与错误
- 📦 依赖全部打进 `lib/`，用户**无需手动安装任何包**

## 环境要求

- **64-bit CPython 3.11.x**。Flow Launcher 的 Python_v2 内置嵌入式 Python 3.11.4 是推荐且经过打包验证的运行时；如改用自定义解释器，也必须使用 64-bit Python 3.11.x。
- 支持 **Python_v2** 的较新 Flow Launcher 版本，以及 Windows x64。
- 无需手动安装依赖 —— release 压缩包内的 `lib/` 已包含全部依赖。

## 安装到 Flow Launcher

1. 从 [Releases](https://github.com/yorelll/ai-ask/releases) 下载 `AIAsk-<version>.zip`
2. 解压到：
   ```
   %APPDATA%\FlowLauncher\Plugins\AIAsk\
   ```
3. 在 Flow 中输入 `pm`（插件管理）打开 AI Ask 的**设置**，填写：
   - **Base URL**：如 `https://devops.realtek.com/realgpt-api/openai-compatible/v1`
   - **API Key**：你的密钥
   - **Model**：如 `fast`
   - **Max Token**：默认 `1000`
   - **Timeout**：默认 `60`（秒）
   - **System Prompt**：可选
   - **Skills**：每行一个（可选）
4. 在 Flow 输入 `ai 你好` 开始对话

> 设置模板来自 `SettingsTemplate.yaml`。Flow 会把填写的值保存到
> `%AppData%\FlowLauncher\Settings\Plugins\AI Ask\Settings.json`，并在每次 `query` 时注入给插件。
> `API Key` 在设置窗口中会被掩码，但 Flow Launcher 的插件设置文件本身以明文保存；请使用专用于此插件、权限最小化的 API key。

## 交互方式

| 输入 | 行为 |
|---|---|
| `ai <问题>` | 开始流式对话，回答实时更新在结果列表中 |
| `ai`（空） | 显示当前配置提示 |
| `ai /stop` | 停止正在进行的生成 |
| `ai /clear` | 清空当前会话的最近回答 |
| `ai /last` | 显示最近回答；选择结果后按 `Ctrl+C` 复制 |
| 右键 / Shift+Enter | 菜单：清空当前会话 |

## 项目结构

```
ai-ask/
├── plugin.json              # 插件清单（Language: python_v2）
├── SettingsTemplate.yaml    # Flow 设置面板模板
├── main.py                  # 插件逻辑（flogin + AsyncOpenAI 流式）
├── requirements.txt         # 依赖：flogin, openai
├── Images/                  # 状态图标：plugin / generating / answer / stop / clear / error
├── test_v2_protocol.py      # V2 协议端到端测试（可打真实 API）
├── .github/workflows/Publish Release.yml  # 构建 lib + 发布 release
└── (构建产物) lib/           # 由 GitHub Action 生成，随 release 打包
```

## 开发 / 测试

```bash
# 本地开发环境
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt

# V2 协议端到端测试（真实 API）
python test_v2_protocol.py
```

`test_v2_protocol.py` 会扮演 Flow Launcher 的 V2 客户端：启动 main.py，发送
`initialize` / `query`，并验证插件是否能通过 `UpdateResults` **流式**刷新结果。测试使用真实 API 时须先设置 `AI_API_KEY` 环境变量；未设置时会安全跳过。

## 技术说明

- **协议**：Python_v2（`Language: "python_v2"`）—— 长驻进程，stdin/stdout 上的
  NewLineDelimited JSON-RPC（StreamJsonRpc 兼容）。基于 [flogin](https://github.com/cibere/flogin)
  客户端库，官方认可插件（如 rtfm）同样采用。
- **流式**：`AsyncOpenAI.chat.completions.create(stream=True)` + `async for`，每 6 个
  chunk 通过 Python_v2 的 `UpdateResults` 原地更新结果；输入框和 `ai` 关键字保持不变，`/stop` 会取消当前协程。
- **设置**：由 `SettingsTemplate.yaml` 生成设置面板，值存入 `%AppData%` 并在每次
  `query` 的第二参数注入，插件无需直接读写文件。
- **打包**：GitHub Action 用 `pip install -r requirements.txt -t lib` 生成 Windows x64 / CPython 3.11 对应的 `lib/` 随 release 分发；`main.py` 在 import 时把 `lib` 加入 `sys.path`。

## 发布

推送与 `plugin.json` 版本匹配的 tag（例如 `Version: "0.0.1"` 对应 tag `v0.0.1`）即可自动构建并发布 release；tag 触发时 Action 会验证版本一致。也可手动触发 `Publish Release` Action，Action 会使用 `plugin.json` 的版本号创建或更新对应 tag 的 release。

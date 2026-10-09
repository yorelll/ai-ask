# AI Ask — Flow Launcher 插件

通过 Flow Launcher 对话框与任意 **OpenAI 兼容**大模型（如 Realtek realgpt、OpenAI、DeepSeek、通义等）进行**流式**对话。

采用 **Python_v2** 协议（长驻进程 + 流式 JSON-RPC），回答会**实时逐字刷新**到 Flow 的输入框中。

## 功能

- 🔎 输入 `ai <你的问题>` 发起对话
- ⚡ **流式输出**：回答逐字实时刷新到输入框（基于 Python_v2 的 `ChangeQuery` 推送）
- 🧠 **Skills**：配置多个技能，请求时：
  - 作为请求头注入：`X-Skill-1`, `X-Skill-2`, …（自动剥离控制字符，保持原始序号）
  - 追加到提示词**尾部**：`[Skills] 1. … 2. …`
- ⚙️ 可配置：`base_url`、`api_key`、`model`、`max_tokens`（默认 1000）、`timeout`（默认 60s）、`system_prompt`、`skills`
- 📋 `/last` 复制最近回答、`/clear` 清空、`/stop` 停止生成
- 📦 依赖全部打进 `lib/`，用户**无需手动安装任何包**

## 环境要求

- **Python 3.10+**（建议 3.11/3.12）。Flow Launcher 1.8+ 可自动为插件安装内置嵌入式 Python；也可在 Flow 设置中指定你自己的 Python 安装目录。
- Windows（Flow Launcher 平台）。
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

> 设置模板来自 `SettingsTemplate.yaml`，Flow 会把填写的值保存到
> `%AppData%\FlowLauncher\Settings\Plugins\AIAsk\Settings.json`，并在每次 `query` 时注入给插件。

## 交互方式

| 输入 | 行为 |
|---|---|
| `ai <问题>` | 开始流式对话，回答实时刷新到输入框 |
| `ai`（空） | 显示当前配置提示 |
| `ai /stop` | 停止正在进行的生成 |
| `ai /clear` | 清空已保存的最近回答 |
| `ai /last` | 显示最近回答（Enter 复制到剪贴板） |
| 右键 / Shift+Enter | 菜单：复制最近回答 / 清空 |

## 项目结构

```
ai-ask/
├── plugin.json              # 插件清单（Language: python_v2）
├── SettingsTemplate.yaml    # Flow 设置面板模板
├── main.py                  # 插件逻辑（flogin + AsyncOpenAI 流式）
├── requirements.txt         # 依赖：flogin, openai
├── Images/plugin.png        # 图标（Flow 必需）
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
`initialize` / `query`，并验证插件是否能通过 `ChangeQuery` **流式**推送回答。

## 技术说明

- **协议**：Python_v2（`Language: "python_v2"`）—— 长驻进程，stdin/stdout 上的
  NewLineDelimited JSON-RPC（StreamJsonRpc 兼容）。基于 [flogin](https://github.com/cibere/flogin)
  客户端库，官方认可插件（如 rtfm）同样采用。
- **流式**：`AsyncOpenAI.chat.completions.create(stream=True)` + `async for`，每 6 个
  chunk 通过 `ChangeQuery` 把增量推到查询框；`/stop` 通过事件立即中断。
- **设置**：由 `SettingsTemplate.yaml` 生成设置面板，值存入 `%AppData%` 并在每次
  `query` 的第二参数注入，插件无需直接读写文件。
- **打包**：GitHub Action 用 `pip install -r requirements.txt -t lib` 生成 `lib/` 随
  release 分发；`main.py` 在 import 时把 `lib` 加入 `sys.path`。

## 发布

推送与 `plugin.json` 版本匹配的 tag（例如 `Version: "0.0.1"` 对应 tag `v0.0.1`）或手动触发
`Publish Release` Action，即可自动构建并发布 release。Action 会从 `plugin.json` 读取版本号并验证 tag 一致。

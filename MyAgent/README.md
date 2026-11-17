# MyAgent

一个基于 C# / .NET 9 的**本地编码智能体（coding agent）**，带 WPF 桌面界面。

MyAgent 参照 [deepseek-harness](https://github.com/deepseek-ai) 的核心设计，用 C# 重新实现了一套精简但完整的 Agent 内核，并配套自研的 Windows 桌面客户端。它把「事件溯源会话 + 流式大模型 + 工具调用 + 上下文自动压缩」这套机制装进一个可直接运行的本地应用里。

> 定位：单机、单用户、本地优先。所有会话与配置保存在用户目录，不依赖任何服务端。

---

## 功能特性

### Agent 内核
- **事件溯源（Event Sourcing）**：所有交互以 append-only 的 `SessionEvent` 记录，LLM 消息历史由事件实时派生（`DeriveMessages`），而非单独存储。
- **多步工具循环**：`系统提示词组装 → 流式请求 → 工具并行执行 → 结果回灌` 的完整 turn/step 循环，支持排队追问、取消、并发工具调度（独占工具串行）。
- **上下文自动压缩**：接近上下文窗口阈值时，自动把旧历史压缩为检查点摘要，长对话不易触顶。
- **崩溃恢复**：启动时自动闭合上次遗留的未完成 turn，为未应答的工具调用补写错误结果。

### 大模型接入
支持三种 Provider，可在设置中切换：

| Provider | 说明 |
| --- | --- |
| `openai-compat` | OpenAI 兼容协议，默认对接 DeepSeek（`https://api.deepseek.com`） |
| `anthropic` | Anthropic Messages 协议，支持 thinking / 推理强度 |
| `mock` | 无需 API Key 的本地模拟，用于演示与调试 |

- SSE 流式输出，支持文本、思考（reasoning）、工具调用分片增量拼接。
- 指数退避重试（含 429 `Retry-After`）、超时与错误码映射。
- 设置面板支持**在线获取模型列表**并缓存。

### 内置工具

| 工具 | 能力 |
| --- | --- |
| `bash` | 执行命令（Windows 下走 bash，无则回退 cmd），带超时与退出码反馈 |
| `read` | 带行号读取文件，支持 offset/limit |
| `write` | 写入文件（自动创建父目录） |
| `edit` | 精确字符串替换，支持 replace_all |
| `glob` | 按 glob 模式查找文件 |
| `grep` | 正则搜索文件内容 |
| `todo_write` | 维护会话级任务清单（pending / in_progress / completed） |

### 桌面客户端（WPF）
- 三栏布局：工作区/会话侧栏（可折叠）、聊天区、文件树面板。
- Markdown 渲染：代码块（语言标签 + 复制 + diff 染色）、表格、列表、任务列表、引用、链接。
- 消息形态：可折叠的思考过程、可展开的工具调用与结果、任务清单、按 turn 分组的进程视图、压缩提示。
- 交互：回车发送 / Shift+Enter 换行、运行时一键取消、智能自动滚动、会话搜索 / 重命名 / 归档 / 删除、工作区管理、文件树懒加载。
- 外观：自绘无边框窗口、矢量图标、明暗主题（跟随系统 / 浅色 / 深色）即时切换。
- 自研纤细滚动条（`SlimScrollBar`），拖拽直接驱动宿主 `ScrollViewer` 偏移。

---

## 架构

```
MyAgent.Core       核心契约：事件、内容块、工具接口、LLM 契约、Agent 循环
   ├── MyAgent.Llm         OpenAI 兼容 / Anthropic / Mock 三种 Provider
   ├── MyAgent.Fs          文件读写（read/write/edit）与搜索（glob/grep）
   ├── MyAgent.Shell       bash 命令执行
   ├── MyAgent.Todo        todo_write 任务清单工具
   ├── MyAgent.Session     JSONL 会话持久化
   ├── MyAgent.Context     系统提示词装配
   └── MyAgent.Compaction  上下文压缩
            ↓ 全部汇聚到
       MyAgent.App         会话管理、设置、工作区、Provider 路由
            ↓
       MyAgent.Desktop     WPF 桌面界面（WinExe）
```

一次 turn 的执行流程：

```
用户输入
  → Followup 追加 user/message 事件
  → 组装 system prompt（身份 + 工具章节 + AGENTS.md/CLAUDE.md）
  → 派生 LLM 消息 → 流式请求
  → 解析 text / thinking / tool_use 内容块
  → 工具并行执行 → 结果以 tool/result 事件回灌
  → 循环直到无工具调用 → turn/end
```

---

## 构建与运行

环境要求：**.NET 9 SDK**、Windows（WPF 仅支持 Windows）。

```bash
# 还原并编译
dotnet restore MyAgent.sln
dotnet build MyAgent.sln -c Release

# 运行桌面客户端
dotnet run --project src/MyAgent.Desktop/MyAgent.Desktop.csproj
```

首次启动若未配置 API Key，默认使用 `mock` Provider，可直接体验界面与工具调用流程。

---

## 配置

设置保存到 `~/.myagent/settings.json`，会话保存到 `~/.myagent/sessions/`（JSONL v4，按工作区分组）。旧版 `~/.dsh` 目录会自动迁移到 `~/.myagent`。

主要配置项：

| 配置 | 默认值 | 说明 |
| --- | --- | --- |
| `provider` | `mock` | `mock` / `anthropic` / `openai-compat` |
| `apiKey` | 空 | 留空时读取环境变量 `MYAGENT_API_KEY` |
| `baseUrl` | 空 | 留空时读取环境变量 `MYAGENT_BASE_URL`，否则用 Provider 默认端点 |
| `model` | `deepseek-flash` | 模型 ID |
| `thinking` | `true` | 是否启用思考 |
| `effort` | `high` | 推理强度：low / medium / high |
| `theme` | `system` | system / light / dark |

支持的环境变量：
- `MYAGENT_HOME`：自定义数据存储根目录。
- `MYAGENT_API_KEY` / `MYAGENT_BASE_URL`：API 凭据与端点。

在工作目录放置 `AGENTS.md` 或 `CLAUDE.md`，其内容会自动注入系统提示词（上限 32KB，找到第一个即使用）。

---

## 目录结构

```
MyAgent.sln
src/
  MyAgent.Core/        核心契约与 Agent 循环
  MyAgent.Llm/         大模型 Provider
  MyAgent.Fs/          文件工具
  MyAgent.Shell/       命令执行
  MyAgent.Todo/        任务清单
  MyAgent.Session/     会话持久化
  MyAgent.Context/     系统提示词
  MyAgent.Compaction/  上下文压缩
  MyAgent.App/         应用编排层
  MyAgent.Desktop/     WPF 客户端
```

---

## 说明

本项目为个人学习与本地使用而构建。它借鉴了 deepseek-harness 的事件溯源与工具循环设计，但实现为独立的 C# 精简版本，两者不共享代码。

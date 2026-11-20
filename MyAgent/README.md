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

### 高级协作能力（参照 deepseek-harness，C# 实现）

| 能力 | 工具 | 说明 |
| --- | --- | --- |
| **目标 Goal** | `get_goal` / `create_goal` / `update_goal` | 每会话一个持久化目标，带 revision CAS；状态 active/paused/blocked/complete；支持自动续跑轮次（goal-round）。 |
| **计划 Plan** | `exit_plan_mode` | 计划协作模式：开启后先规划不执行，提交 Markdown 方案经人工批准后再继续。空闲时切换立即生效并写入 `plan/mode` 事件；运行中排队到下一步生效（芯片显示"下一步生效"）；芯片按有效目标态渲染，点击即退出。 |
| **团队 Team** | `team_task_create` / `team_task_list` / `team_task_get` / `team_task_update` | 共享任务板：任务认领/完成/重排、blockedBy 依赖图与就绪判定、writeScopes 重叠提示。 |
| **流程 Workflow** | `workflow` | 模型编写 JavaScript 脚本编排子 Agent（`agent` / `pipeline` / `parallel` / `phase` / `log` / `args` 钩子），由 Jint 沙箱执行；`run_in_background` 注册后台 job。 |
| **定时 Schedule** | `schedule_create` / `schedule_list` / `schedule_view` / `schedule_edit` | 一次性（after/at）与周期任务（every/daily/weekly/cron），到点把提示投递到目标会话并自动推进。 |
| **子代理 Subagent** | `subagent` / `subagent_fork` / `list_agents` / `send_message` / `wait_agent` / `interrupt_agent` | 真正的独立子 Agent 派生（spawn 空历史 / fork 继承父历史）、成员名解析、消息中继、结果通知。 |
| **后台任务 Jobs** | `job_output` / `job_list` / `job_kill` | 有界输出 ring、结算通知、取消。 |
| **权限 Permission** | 权限预设选择器（UI）/ `/permission` 语义 | `workspace-write`（工作区可写+需审批）与 `danger-full-access`（完全访问+免审批）两档预设；预设=沙箱模式+审批策略的组合，切换写入持久事件，会话各自独立、重启还原。 |
| **审批 Approval** | `approval/asked` + `approval/decided` 审计对 | ask/never 两档策略：ask 弹面板等用户决定（仅本次允许/拒绝），never 确定性自动拒绝；无应答者/异常/取消一律 fail-closed；每次问答在会话日志留下成对审计事件。 |
| **沙箱 Sandbox** | `sandbox_permissions` + `justification`（bash/write/edit） | read-only / workspace-write / danger-full-access 三档文件策略；Windows 上用 WRITE_RESTRICTED 受限令牌 + 工作区/私有临时目录 ACL 授权 + Low 完整性标签约束子进程写入；文件工具走进程内 fence；被拒后可申请"严格更宽"的一次性升级（经审批）。 |
| **性能与用量 Stats** | 输入框下方状态行（UI） | 参照 harness StatsPills / ContextMeter / TurnUsagePanel：活动胶囊（轮/步数 · tok/s 输出速度）、用量胶囊（全日志 token 总量 · 缓存命中%）、环形上下文占用表（已用/窗口 + 系统提示词/工具定义/对话消息分解）；点击弹出明细面板；compact/detailed 两档显示模式。 |

这些能力均以事件溯源实现：状态从会话事件流折叠而来，重启/恢复后自动还原；数据存放在 `~/.myagent/`。驱动层与 deepseek-harness 对齐：Agent 具备 `next-turn` / `next-step` inbox 与 `followup` / `steer` / `inject` / `whenIdle`；`exit_plan_mode` 通过宿主提问服务等待人工批准；Goal 走 idle 后的自动续跑；Schedule 用定时器投递；Workflow 的 `agent()` 真正派生子 Agent。

UI 亦按 harness 客户端形态复刻：输入框上方的目标条（编辑/暂停/恢复/清除）、计划模式芯片与审批面板、团队任务板与模式开关、流程/成员/后台任务页签、定时任务列表。

#### 怎么使用

这些能力是**模型工具**，由 Agent 在对话中按需调用，无需手动敲命令。直接在聊天框用自然语言提出需求即可：

| 想做的事 | 对 Agent 这样说 | 实际调用 |
| --- | --- | --- |
| 设一个长期目标 | "帮我设个目标：完成项目重构" | `create_goal` |
| 查看/修改目标 | "当前目标是什么？" / "把目标标记为完成" | `get_goal` / `update_goal` |
| 进入计划模式 | 点右侧栏「计划」页签的「切换计划模式」 | 切换 plan/mode |
| 提交计划审批 | 计划模式下说"提交计划让我审批" | `exit_plan_mode` |
| 建共享任务板 | "建立团队任务板，开始分工" | `team_task_create` |
| 建定时提醒 | "60 秒后提醒我检查构建" / "每天早上 9 点生成报告" | `schedule_create` |
| 编排多个子任务 | "用工作流编排分析任务" | `workflow`（生成 JS 脚本） |
| 派生独立子 Agent | "派一个子 Agent 去审查这份代码" | `subagent` / `subagent_fork` |
| 体验沙箱升级审批 | "演示沙箱升级"（mock 关键词） | bash 携带 `sandbox_permissions` + `justification` → 弹出审批面板 |
| 切换会话权限 | 输入框左下角的权限预设下拉 | `permission/preset` + `sandbox/mode` + `approval/policy` 事件 |
| 查看性能用量 | 输入框下方状态行：点活动/用量胶囊或右侧环形占用表 | 全日志折叠：tok/s、缓存命中%、上下文占用与分解 |

`workflow` 现在执行的是**模型编写的 JavaScript**（由 Jint 沙箱运行，无 fs/network/timer/Node API），可用钩子：`agent(prompt, opts?)`、`pipeline(items, ...stages)`、`parallel(thunks)`、`phase(title)`、`log(message)`、`args`。`run_in_background` 会返回 job id，结果通过 `job_output` 收取。

- **查看状态**：右侧栏「文件 / 目标 / 计划 / 团队 / 定时 / 流程 / 成员 / 后台」八个页签实时展示各能力状态，随会话事件自动刷新。
- **无需 API Key 也能体验**：默认 `mock` Provider 内置关键词触发（目标/团队/定时/工作流/提交计划），可直接看到工具调用与事件流转；接入真实模型后由模型自由判断何时调用。
- **Goal 自动续跑**：目标 active 且就绪时，每轮结束自动带 `<goal_round>` 提示继续，直到完成、暂停或达到轮次上限；complete/blocked 会注入 `<goal_complete>` / `<goal_blocked>` 收尾提示。
- **Plan 审批**：`exit_plan_mode` 会阻塞等待，输入框上方弹出审批面板，点「批准计划」退出计划模式，点「继续讨论」把反馈带回模型。目标条（编辑/暂停/恢复/清除）也在输入框上方。
- **Team 模式**：默认关闭，右侧「团队」页签可开关（关闭只隐藏工具与策略，不删任务）。

#### 权限与沙箱（参照 deepseek-harness permission-presets / user-approval / sandbox，C# 实现）

**三档文件策略**（`sandbox/mode` 事件，会话日志即存储，最后一条事件为会话覆盖）：

| 模式 | 含义 |
| --- | --- |
| `read-only` | 任何受控操作不得修改文件 |
| `workspace-write` | 可写会话工作区 + 显式额外目录 + 平台临时区（Windows 进程沙箱使用每会话随机私有临时目录） |
| `danger-full-access` | 不限制文件修改（显式选择才生效） |

**两档审批策略**（`approval/policy` 事件）：`ask` 弹出审批面板等待用户（无应答者 fail-closed）；`never` 确定性自动拒绝（CI/无人值守姿态）。

**权限预设** = 沙箱模式 + 审批策略的组合，输入框左下角下拉切换：
- `workspace-write`：工作区可写 + 需审批（新会话默认，可在设置改）
- `danger-full-access`：完全访问 + 免审批
- 两个旋钮不匹配任何预设时显示「自定义」（只读展示，不可选）

**执行链路**：
- `bash`：read-only / workspace-write 下通过 **Windows ACL 受限令牌**执行——`CreateRestrictedToken(WRITE_RESTRICTED)` + 工作区/私有临时目录的 capability-SID 授权 ACE（含 Everyone 的 FILE_DELETE_CHILD 拒绝 + Low no-write-up 完整性标签）+ Low 完整性令牌 + 默认 DACL 合并。工作区 ACE 按规范化路径确定性 SID 常驻复用（跨会话 O(1) 命中）；临时授权随宿主退出撤销。任何 Win32 失败一律 fail-closed（`SANDBOX_UNAVAILABLE`），绝不静默降级为不限制执行。读、网络、进程可见性不在约束范围（与 harness 相同的 partial 边界）。
- `write` / `edit`：进程内路径 fence（规范化 + 祖先身份比较，防符号链接/8.3 别名/`..` 穿越），拒绝返回 `FS_SANDBOX_DENIED` + `[sandbox: …]` 标记。
- **升级编排**：被拒后模型可携带 `sandbox_permissions`（严格更宽档位）+ `justification`（一句人话理由）重试**同一操作一次**；执行前先走审批（`approval/asked`→`approval/decided` 成对审计事件，turn 内强制），批准仅授予本次调用；拒绝/取消/不可用都在执行前失败。bash 与文件工具共享同一套词表与编排（`MyAgent.Sandbox/Escalation.cs`）。
- 策略叙述随每次请求的 runtime-context 快照下发（不重写系统提示词缓存前缀）；运行中切换审批策略会向模型注入变更通知。
- 子代理委派时从父会话种子继承两旋钮（`source: delegation`），恢复/分叉会话保留既有取值。

**UI**：输入框上方红色「沙箱权限审批」面板（工具名 + 理由 + 拒绝/仅本次允许）；输入框左下角权限预设下拉；设置 →「权限与沙箱」页配置新会话默认预设、沙箱默认模式、额外可写目录。mock 模式说"演示沙箱升级"即可免 Key 体验完整审批流。

### 桌面客户端（WPF）
- 三栏布局：工作区/会话侧栏（可折叠）、聊天区、右侧八个能力页签。
- 输入框上方按 harness 形态呈现目标条、计划模式芯片与审批面板。
- 输入框下方性能用量状态行（参照 harness StatsPills + ContextMeter）：活动胶囊「N 轮 M 步 · X tok/s」、用量胶囊「总量 · 缓存命中 X%」、右侧环形上下文占用表「X%」；点击弹明细（模型/工具用时、TTFT、TPS；未缓存输入/缓存读取/缓存写入/输出精确值 + provider·model；上下文已用/窗口 + 系统提示词/工具定义/对话消息分解条）。数字语义与 harness token-format 一比一（K/M 缩写、逗号分组、缓存命中永不把部分命中舍成 100%、占用四舍五入封顶 100）。设置 → 外观 →「性能与用量」可切 compact（只留速度与命中读数）/ detailed（默认，含计数与弹窗）。
- Markdown 渲染：代码块（语言标签 + 复制 + diff 染色）、表格、列表、任务列表、引用、链接。
- 消息形态：可折叠的思考过程、可展开的工具调用与结果、任务清单、按 turn 分组的进程视图、压缩提示。
- 交互：回车发送 / Shift+Enter 换行、运行时一键取消、智能自动滚动、会话搜索 / 重命名 / 归档 / 删除、工作区管理、文件树懒加载。
- 聊天列表虚拟化：只实例化视口附近的轮次（VirtualizingStackPanel 像素滚动），大会话（数千事件）启动与切换秒开；贴底跟随/上滚暂停/回底恢复与纤细滚动条语义不变。
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
   ├── MyAgent.Compaction  上下文压缩
   ├── MyAgent.Subagents   子 Agent 派生、成员名与消息中继
   ├── MyAgent.Jobs        后台任务注册与输出 ring
   ├── MyAgent.Goal        目标状态机与自动续跑
   ├── MyAgent.Plan        计划协作模式与审批
   ├── MyAgent.Team        共享任务板与依赖图
   ├── MyAgent.Workflow    Jint PTC 脚本编排引擎
   ├── MyAgent.Schedule    定时与周期任务
   ├── MyAgent.Sandbox     文件策略/fence/升级词表 + Windows ACL 受限令牌后端
   ├── MyAgent.Approval    ask/never 审批服务与成对审计事件
   └── MyAgent.Permissions 权限预设（沙箱模式 × 审批策略）与投影
            ↓ 全部汇聚到
       MyAgent.App         会话管理、设置、工作区、Provider 路由、能力装配
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
| `performanceUsage` | `detailed` | 性能用量状态行显示模式：detailed（计数+弹窗）/ compact（仅速度与缓存命中读数） |
| `permissionPreset` | `workspace-write` | 新会话钉入的权限预设：workspace-write / danger-full-access |
| `sandboxMode` | `workspace-write` | 会话未显式切换时的文件沙箱默认模式：read-only / workspace-write / danger-full-access |
| `sandboxWritableRoots` | `[]` | workspace-write 额外可写的绝对目录（Windows 上仅文件工具生效） |

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
  MyAgent.Subagents/   子 Agent 运行时
  MyAgent.Jobs/        后台任务运行时
  MyAgent.Goal/        目标
  MyAgent.Plan/        计划模式与审批
  MyAgent.Team/        团队任务板
  MyAgent.Workflow/    Jint PTC 脚本编排
  MyAgent.Schedule/    定时任务
  MyAgent.Sandbox/     文件策略与 Windows ACL 沙箱
  MyAgent.Approval/    审批服务与审计
  MyAgent.Permissions/ 权限预设
  MyAgent.App/         应用编排层
  MyAgent.Desktop/     WPF 客户端
```

---

## 说明

本项目为个人学习与本地使用而构建。它借鉴了 deepseek-harness 的事件溯源与工具循环设计，但实现为独立的 C# 精简版本，两者不共享代码。

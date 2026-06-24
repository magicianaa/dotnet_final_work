# SmartStudy Web / CLI 双端手动测试验收文档

本文档用于手动验收 SmartStudy 智能学习助手是否满足《.NET 程序设计课程期末项目：基于 .NET 的 AI Agent 开发》的基本要求、评分维度和加分项。验收覆盖 CLI、Web、项目 / 对话工作区、RAG、工具调用、记忆、流式输出、可观测性、MCP、Multi-Agent、Plan-and-Execute、测试与文档交付。

> 安全要求：不要把真实 API Key 写入本文档、README、提交记录或截图。真实配置应放在 `src/SmartStudy.Cli/appsettings.Local.json` 或环境变量中。

## 1. 验收环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows，PowerShell |
| 工作目录 | `D:\dotnet_final_work` |
| .NET SDK | .NET 8+ |
| Python | 如需处理 PDF，优先使用 `C:\Users\21125\AppData\Local\Programs\Python\Python313\python.exe` |
| 默认 Web 地址 | `http://localhost:5178/` |
| 默认 Embedding | `zhipu / embedding-3` |
| 项目数据目录 | `data/web-projects/<project-id>/` |

## 2. PDF 要求覆盖总表

| PDF 要求 / 评分点 | 验收方式 | 预期结论 |
| --- | --- | --- |
| 使用 C# / .NET 8+ | 构建解决方案，检查 `.csproj` | 通过 |
| LLM 集成 | `doctor` 查看当前 LLM profile；CLI / Web 提问 | 通过 |
| Agent Loop | CLI/Web 问答触发 Thought / Action / Observation | 通过 |
| Tool Calling 至少 3 个工具 | `tools` 查看工具数量 | 17 个工具，超额通过 |
| Memory | 新建对话、切换对话、笔记、学习画像、学习进度 | 通过 |
| 用户交互界面 | CLI + Web 双端可用 | 超额通过 |
| async/await | 构建与代码检查；LLM/RAG/工具/MCP/Streaming 均异步 | 通过 |
| 错误处理与日志 | `doctor`、工具异常提示、trace JSONL | 通过 |
| README | 检查根目录 README | 通过 |
| 架构设计文档 | 检查 `docs/architecture.md` | 通过 |
| 反思报告 | 检查 `docs/reflection-report.md` | 通过 |
| 现场演示材料 | 检查 `docs/demo-script.md` | 通过 |
| Multi-Agent 加分 | `multi` 命令 / Web CLI 命令 | 通过 |
| RAG 加分 | `index`、`knowledge_search`、Web 知识库状态 | 通过 |
| MCP 加分 | 启动 `SmartStudy.Mcp`，检查 MCP 工具代码与测试 | 通过 |
| 单元测试加分 | `dotnet test` | 43/43 通过 |
| 创新性应用加分 | 学习项目、对话、画像、错题、进度、复习计划 | 通过 |

## 3. 基础构建与自动化测试

### 3.1 清理旧进程

目的：避免旧的 Web / CLI 进程占用端口或锁定 DLL。

```powershell
.\scripts\stop-all.ps1
Get-Process SmartStudy.Web,SmartStudy.Cli -ErrorAction SilentlyContinue
```

预期结果：

- `stop-all.ps1` 正常结束。
- `Get-Process` 不再列出 `SmartStudy.Web` 或 `SmartStudy.Cli`。

通过标准：

- 没有残留 SmartStudy 进程。
- 如果仍有残留进程，可手动结束后继续。

### 3.2 构建解决方案

```powershell
dotnet build SmartStudy.sln --no-restore
```

预期结果：

- 所有项目构建成功。
- 输出包含 `0 个警告`、`0 个错误`，或至少 `0 个错误`。

通过标准：

- `SmartStudy.Core`、`SmartStudy.Cli`、`SmartStudy.Mcp`、`SmartStudy.Web`、`SmartStudy.Tests` 均能生成。

### 3.3 运行单元测试

```powershell
dotnet test SmartStudy.sln --no-build --no-restore
```

预期结果：

- 测试全部通过。
- 当前参考结果为 `43/43 passed`。

通过标准：

- 没有失败测试。
- 能说明测试覆盖 Agent Loop、工具、RAG、本地 embedding、CLI 输入、MCP、学习画像、学习进度、错题、Multi-Agent、Plan-and-Execute 等核心能力。

## 4. CLI 基础验收

### 4.1 健康检查

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- doctor
```

预期结果：

- 显示 `SmartStudy Doctor` 表格。
- 能看到当前 LLM profile、LLM BaseUrl、Embedding provider、Knowledge、Vector Store、Index、Tools。
- 当前默认应显示 `Embedding = zhipu (embedding-3)`。
- `Tools` 应显示 `17`。

通过标准：

- 所有关键检查项为 `OK`。
- `Vector Store` 显示项目级持久化向量库，例如 `ProjectJsonPersistent`。
- `Index` 不是 missing；如果 missing，应先运行 `index`。

### 4.2 工具清单

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- tools
```

预期结果：

- 显示 17 个工具。
- 至少包含：
  `knowledge_search`、`read_course_material`、`import_course_materials`、`add_note`、`list_notes`、`update_learning_profile`、`show_learning_profile`、`study_plan`、`add_study_task`、`mark_task_done`、`show_progress`、`review_history`、`make_quiz`、`submit_quiz_answer`、`record_quiz_result`、`show_mistakes`、`calculate`。

通过标准：

- 工具数量大于 PDF 要求的 3 个。
- 每个工具都有清晰 description。

### 4.3 构建 / 刷新当前项目知识库索引

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- index
```

预期结果：

- 显示当前 Project 名称和 Embedding Provider。
- 索引构建成功。
- 索引写入当前学习项目目录：`data/web-projects/<project-id>/data/index.json`。

通过标准：

- 没有异常退出。
- 后续 `doctor` 能看到 Index 文件和 chunk 数。

### 4.4 单次问答

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请用一句话解释 ReAct Agent"
```

预期结果：

- Agent 返回关于 ReAct Agent 的回答。
- 如果触发工具调用，控制台会显示 Thought / Action / Observation / FinalAnswer 事件。

通过标准：

- CLI 不崩溃。
- 回答内容与问题相关。

### 4.5 计算工具

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请计算 (45+15)/6"
```

预期结果：

- Agent 调用 `calculate` 或给出正确计算结果 `10`。

通过标准：

- 结果正确。
- 如果有工具事件，Action 应显示 `calculate`。

## 5. CLI 项目 / 对话工作区验收

### 5.1 查看当前项目

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- project current
```

预期结果：

- 显示当前项目名称、项目 ID、资料目录、当前对话标题和对话 ID。

通过标准：

- 当前项目与 Web 左侧栏选中的项目一致。
- 资料目录指向用户选择的课程资料目录或默认 `knowledge`。

### 5.2 列出学习项目

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- project list
```

预期结果：

- 表格显示所有学习项目。
- 当前项目在 Active 列显示 `*`。

通过标准：

- 能看到默认项目和用户创建的项目。
- Web 创建的项目能被 CLI 看到。

### 5.3 新建学习项目

> 该命令会读取资料目录并构建项目索引，可能调用 embedding API。

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- project new "C:\Users\21125\Desktop\SEM & SEP\ppts | SEM课程"
```

预期结果：

- CLI 创建新项目并切换到该项目。
- 自动导入课程资料。
- 自动构建该项目自己的 RAG 索引。

通过标准：

- `project list` 能看到新项目。
- `doctor` 的 Index 路径位于该项目目录下。
- Web 左侧栏刷新后能看到同一项目。

### 5.4 切换学习项目

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- project switch <项目ID或名称>
```

预期结果：

- 当前项目切换成功。
- 当前对话切换为该项目的 active conversation。
- 项目索引被加载；如果无索引，给出友好提示。

通过标准：

- `project current` 显示已切换项目。
- `conversation list` 显示该项目下的对话列表。
- 切换项目不会清空该项目当前对话已有记忆；再次提问时仍能使用该对话上下文。

### 5.5 新建和切换对话

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- conversation list
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- conversation new "期末复习"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- conversation switch "期末复习"
```

预期结果：

- 新对话创建成功。
- Active 对话切换到 `期末复习`。
- 不同对话拥有独立短期记忆文件。

通过标准：

- CLI 和 Web 显示同一当前对话。
- 在新对话中询问的问题不会污染其他对话短期上下文。
- 切回旧对话后，旧对话内容可从 `data/web-projects/<project-id>/data/memory/<conversation-id>.json` 恢复。

### 5.6 删除对话

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- conversation delete <对话ID或标题>
```

预期结果：

- 当前项目下指定对话被删除。
- 如果删除的是当前对话，系统自动切换到剩余最近对话。
- 如果删除最后一个对话，系统自动创建一个新的默认对话。

通过标准：

- `conversation list` 不再显示被删除的对话。
- 被删除对话对应的 `data/memory/<conversation-id>.json` 被删除。
- 其他对话的记忆文件仍保留。

### 5.7 删除项目

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- project delete <项目ID或名称>
```

预期结果：

- 指定项目被删除。
- 项目目录 `data/web-projects/<project-id>/` 被同步删除。
- 如果删除当前项目，系统自动切换到剩余最近项目。
- 至少保留一个学习项目；删除最后一个项目应给出友好错误。

通过标准：

- `project list` 不再显示被删除项目。
- 被删除项目下的 `knowledge/`、`data/index.json`、笔记、学习画像、进度、错题、对话和记忆全部随项目目录删除。
- Web 刷新后项目列表与 CLI 一致。

## 6. CLI 交互模式验收

### 6.1 启动交互模式

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- chat
```

预期结果：

- 显示 `SmartStudy AI 学习助手` 标题。
- 显示当前 LLM、当前项目和当前对话。
- 输入提示符为 `>`。

通过标准：

- 可正常输入中文。
- 左 / 右箭头、Home、End、Backspace、Delete 正常工作。

### 6.2 交互命令

在 chat 中逐条输入：

```text
:help
:models
:project current
:projects
:conversations
:stream
:reset
:q
```

预期结果：

- `:help` 显示完整冒号命令表。
- `:models` 显示 LLM profiles，不应产生模型幻觉。
- `:project current` 显示当前项目。
- `:projects` 显示项目列表。
- `:conversations` 显示当前项目下的对话。
- `:stream` 切换流式输出。
- `:reset` 清空当前对话记忆。
- `:q` 退出。

通过标准：

- 所有冒号命令由 CLI 命令处理逻辑直接响应，不应交给 LLM 幻觉回答。

## 7. CLI 工具快捷命令验收

在 `chat` 模式中输入：

```text
:search ReAct Agent 工具调用
:read Lesson00 1-3
:note ReAct | Thought Action Observation | final,agent
:notes #final
:profile
:plan 3 天复习 Agent 项目
:task 复习 ReAct 循环 | ReAct | 40
:done ReAct | 能解释 Thought Action Observation | 35
:progress
:history 5
:quiz ReAct 包含 Thought、Action、Observation | 1
:answer latest | 1 | A | ReAct
:mistake ReAct 的 Observation 是什么？ | ReAct | 模型思考 | 工具返回结果 | Observation 是工具执行后的返回结果
:mistakes ReAct
:calc (45+15)/6
```

预期结果：

- 每条命令显示对应工具结果面板。
- `:search` 显示来源、证据编号、ChunkId 和来源汇总。
- `:read` 能读取已导入课件指定页面或连续内容。
- `:note` 后 `:notes` 能查到笔记。
- `:task`、`:done`、`:progress` 能更新学习进度。
- `:quiz` 只展示题目和选项，不泄露答案。
- `:answer` 判分并给解析。
- `:mistake` / `:mistakes` 能形成错题记录。
- `:calc` 输出 `10`。

通过标准：

- 工具输出与输入相关。
- 数据写入当前项目目录，而不是全局共享文件。

## 8. CLI RAG 与本地资料验收

### 8.1 检索课程知识库

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请先检索课程资料，再解释 ReAct Agent 的循环流程"
```

预期结果：

- Agent 优先调用 `knowledge_search`。
- 回答中包含课程资料依据。

通过标准：

- 能看到检索来源、证据编号或 ChunkId。

### 8.2 导入本地课程资料

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "我的课程资料在 C:\Users\21125\Desktop\SEM & SEP\ppts，请你导入并建立索引"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "还有一份课程资料 C:\Course\Chp6TestTool.pdf，请你导入并建立索引"
```

预期结果：

- Agent 调用 `import_course_materials`。
- 支持单个文件路径，也支持目录路径。
- 支持 `.pdf/.pptx/.docx/.xlsx/.csv/.tsv/.html/.htm/.md/.txt`。
- 导入后重建当前项目索引。

通过标准：

- 不回答“无法访问个人文件夹”。
- `doctor` 中 imported 数量和 chunk 数增加或保持合理。
- 单文件 PDF 不会被误判为目录，也不应出现智谱 embedding `400 / code 1210` 参数错误。

### 8.3 精读 PDF / PPT 内容

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请按页详细讲讲 2026_Slides Lesson00_Introduction to SEME.pdf 的前 3 页内容，不要省略"
```

预期结果：

- Agent 优先调用 `read_course_material`。
- 回答包含多页内容，不只停留在第一页。

通过标准：

- 输出按页或按段组织。
- 能说明资料来源。

## 9. CLI 模型切换与 Streaming 验收

### 9.1 查看模型

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- chat
```

进入后输入：

```text
:models
```

预期结果：

- 显示真实配置的 LLM profiles，例如 GLM 和 DeepSeek profiles。
- 不显示 BERT、GPT-2、T5 这类幻觉 profile。

通过标准：

- 输出与 `appsettings.json` / `appsettings.Local.json` 中配置一致。

### 9.2 命令行指定模型

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请用一句话说明当前模型可用" --llm glm-4-flash
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请用一句话说明当前模型可用" --llm deepseek-chat
```

预期结果：

- 能切换指定模型 profile。
- 切换失败时给出明确错误，不崩溃。

通过标准：

- 当前 profile 名称与命令参数一致。

### 9.3 流式输出

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- chat --stream
```

输入：

```text
请解释 ReAct Agent
```

预期结果：

- 回答逐步流式输出。
- 仍保留 Thought / Action / Observation / FinalAnswer 事件链。

通过标准：

- 无乱码、无卡死。
- 工具调用路径在流式模式下仍可继续执行。

## 10. Multi-Agent 加分项验收

### 10.1 CLI 顶层命令

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- multi "解释 ReAct Agent 并准备答辩"
```

预期结果：

- 显示 `SmartStudy Multi-Agent 协作` 标题。
- 表格包含：
  `PlannerAgent`、`ResearchAgent`、`TutorAgent`、`ReviewerAgent`。
- Reviewer 显示 `PASS` 或明确指出需要补充内容。

通过标准：

- 四个专业 Agent 均出现。
- 最终答案包含资料依据和下一步建议。

### 10.2 Chat 内触发

进入 chat 后输入：

```text
:multi 解释 ReAct Agent 并准备答辩
```

预期结果：

- 与顶层 `multi` 命令一致。

通过标准：

- chat 模式可以触发同一套 Multi-Agent 协作。

## 11. Plan-and-Execute / Reflection 验收

### 11.1 CLI 顶层命令

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- plan-execute "解释 ReAct Agent"
```

预期结果：

- 显示 `SmartStudy Plan-and-Execute`。
- 表格包含：
  `Plan`、`Execute: RAG 检索`、`Execute: 生成答复`、`Review: 答案质量检查`。
- 末尾显示 `Quality Review: PASS` 或明确失败原因。

通过标准：

- 展示“先规划、再执行、最后审查”的架构模式。
- 最终回答有资料依据、覆盖目标、包含下一步。

### 11.2 Chat 内触发

进入 chat 后输入：

```text
:plan-execute 解释 ReAct Agent
```

预期结果：

- 与顶层 `plan-execute` 命令一致。

通过标准：

- chat 模式可以触发 Plan-and-Execute。

## 12. Web 端启动验收

### 12.1 一键启动

```powershell
.\scripts\start-all.ps1
```

预期结果：

- 构建解决方案。
- 构建或刷新知识库索引。
- 启动 Web 前端。
- 打开 CLI 交互窗口。

通过标准：

- 终端没有未处理异常。
- Web 地址可访问：`http://localhost:5178/`。

### 12.2 仅启动 Web

```powershell
.\scripts\start-all.ps1 -NoCli
```

或：

```powershell
dotnet run --project src\SmartStudy.Web\SmartStudy.Web.csproj --urls http://localhost:5178
```

预期结果：

- Web 服务监听 `http://localhost:5178/`。
- 浏览器打开后显示 SmartStudy Web 前端。

通过标准：

- 页面不显示 Blazor 错误。
- 控制台没有致命异常。

### 12.3 端口切换

```powershell
.\scripts\start-all.ps1 -WebPort 5188
```

预期结果：

- Web 在 `http://localhost:5188/` 可访问。

通过标准：

- 原端口被占用时可以换端口启动。

## 13. Web 基础界面验收

打开浏览器访问：

```text
http://localhost:5178/
```

预期可见区域：

- 左侧项目 / 对话栏。
- 中间对话区。
- 输入框和发送按钮。
- 流式输出开关。
- 清空按钮。
- Agent 时间线。
- CLI 指令参考。
- 工具列表。
- 知识库 / 指标 / 最近笔记等面板。

通过标准：

- 页面布局稳定，无明显重叠。
- Agent 时间线展开后内部可滚动，不被下方 `CLI 指令参考` 或 `工具` 栏遮挡。
- 页面刷新后仍能显示当前项目 / 对话状态。

## 14. Web 项目 / 对话验收

### 14.1 新建项目

操作：

1. 在左侧项目栏点击新建项目。
2. 选择或输入课程资料目录，例如 `C:\Users\21125\Desktop\SEM & SEP\ppts`。
3. 输入项目名称。
4. 确认创建。

预期结果：

- 左侧项目列表出现新项目。
- 当前项目切换到新项目。
- 系统导入资料并构建项目索引。

通过标准：

- CLI 执行 `project list` 能看到 Web 创建的新项目。
- `data/web-projects/<project-id>/` 下有项目数据。

### 14.2 切换项目

操作：

1. 点击左侧已有项目。
2. 观察当前对话和知识库状态。

预期结果：

- 当前项目切换。
- 中间对话和右侧状态随项目变化。
- 若该项目当前对话已有历史消息，切换后中间对话区会恢复该对话历史。

通过标准：

- CLI 执行 `project current` 与 Web 当前项目一致。

### 14.3 新建对话

操作：

1. 在当前项目下点击新建对话。
2. 输入标题或使用默认标题。
3. 发送一条测试消息。

预期结果：

- 对话列表出现新对话。
- 当前对话切换到新对话。
- 新对话有独立短期记忆。

通过标准：

- CLI 执行 `conversation list` 能看到 Web 新建的对话。
- 切回旧对话后，上下文不会混淆。
- 页面刷新后仍能恢复当前对话记录。

### 14.4 删除对话

操作：

1. 在左侧对话条目点击删除按钮。
2. 观察对话列表和中间聊天区。

预期结果：

- 被删除对话从列表消失。
- 若删除当前对话，系统自动切换到剩余对话或默认新对话。
- 中间聊天区显示当前对话的历史，或在新默认对话中为空。

通过标准：

- CLI 执行 `conversation list` 不再显示被删除对话。
- 对应 `data/memory/<conversation-id>.json` 被删除。

### 14.5 删除项目

操作：

1. 在左侧项目条目点击删除按钮。
2. 观察项目列表、当前项目和当前对话。

预期结果：

- 被删除项目从列表消失。
- 若删除当前项目，系统自动切换到剩余项目。
- 最后一个项目的删除按钮不可用或删除失败时显示友好提示。

通过标准：

- CLI 执行 `project list` 不再显示被删除项目。
- `data/web-projects/<project-id>/` 已被删除。

## 15. Web Chat 与 CLI 命令兼容验收

### 15.1 Web 普通问答

在 Web 输入框输入：

```text
请解释 ReAct Agent
```

预期结果：

- Web 显示用户消息和 SmartStudy 回复。
- 右侧 Agent 时间线出现 Thought / Streaming Answer / FinalAnswer 或工具事件。

通过标准：

- 回答与问题相关。
- 时间线事件顺序合理。

### 15.2 Web 冒号命令

在 Web 输入框分别输入：

```text
:models
:tools
:project current
:projects
:conversations
:multi 解释 ReAct Agent
:plan-execute 解释 ReAct Agent
```

预期结果：

- `:models` 显示真实配置的 LLM profiles，不出现 BERT、GPT-2、T5 等幻觉内容。
- `:tools` 显示工具清单。
- `:project current`、`:projects`、`:conversations` 显示项目 / 对话状态。
- `:multi` 显示 Multi-Agent 协作结果。
- `:plan-execute` 显示 Plan / Execute / Review 结果。

通过标准：

- Web 冒号命令由 `WebChatCommandService` 处理，而不是直接交给 LLM。
- 输出与 CLI 对应命令一致。

### 15.3 Web 流式输出

操作：

1. 打开 `流式输出` 开关。
2. 输入：

```text
请详细解释 RAG 在本项目中的作用
```

预期结果：

- 回复逐步出现。
- 时间线中出现 Streaming Answer。

通过标准：

- 不出现空白卡死。
- 流式过程中 UI 不明显错位。

## 16. Web RAG 与工具验收

### 16.1 Web 知识库检索

输入：

```text
请检索课程资料并解释 Semantic Kernel 的核心思想
```

预期结果：

- Agent 调用知识库检索。
- 回复包含资料依据。
- 时间线出现 Action / Observation。

通过标准：

- 回答不只是常识回答，有来源或证据。

### 16.2 Web 笔记和画像

输入：

```text
请把 ReAct 的核心思想记成一条笔记，标签是 final
```

再输入：

```text
列出 final 标签的笔记
```

再输入：

```text
我对 ReAct 和 MCP 比较薄弱，目标是完成期末答辩，请更新我的学习画像
```

预期结果：

- 第一次调用 `add_note`。
- 第二次调用 `list_notes`。
- 第三次调用 `update_learning_profile`。

通过标准：

- 当前项目目录下生成或更新 `notes.json`、`learning-profile.json`。
- CLI `doctor` 后 Notes / Learning Profile 状态变为 created。

### 16.3 Web 学习进度和错题

输入：

```text
把复习 ReAct 循环加入学习任务，预计 40 分钟
```

再输入：

```text
我完成了 ReAct 复习，用了 35 分钟，反思是能解释 Thought Action Observation
```

再输入：

```text
查看我的学习进度
```

预期结果：

- 分别触发添加任务、完成任务、查看进度。

通过标准：

- 当前项目目录下 `study-progress.json` 被更新。

错题验收输入：

```text
记录一道错题：ReAct 的 Observation 是什么？我的答案是模型思考，正确答案是工具返回结果，主题是 ReAct
```

预期结果：

- 触发错题记录工具。
- 错题主题写入薄弱项。

通过标准：

- `quiz-results.json` 或学习画像被更新。

## 17. MCP Server 验收

### 17.1 启动 MCP Server

```powershell
dotnet run --project src\SmartStudy.Mcp\SmartStudy.Mcp.csproj
```

预期结果：

- MCP stdio server 能启动。
- 如果没有 MCP Host 输入，进程退出或等待输入都属于正常现象。

通过标准：

- 没有编译错误。
- 不出现无法解析配置、无法构造服务等异常。

### 17.2 MCP 工具能力

代码和测试应覆盖以下 MCP 工具：

| MCP 工具 | 功能 |
| --- | --- |
| `AddNote` | 保存学习笔记 |
| `ListNotes` | 查询学习笔记 |
| `SearchKnowledge` | 检索课程知识库 |
| `ListImportedMaterials` | 列出已导入课程资料 |

通过标准：

- `dotnet test` 中 MCP 相关测试通过。
- 代码中可见 `ModelContextProtocol` 和 `[McpServerTool]`。

## 18. 可观测性验收

### 18.1 控制台事件

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请先检索资料，再解释 ReAct Agent"
```

预期结果：

- 控制台显示 Thought / Action / Observation / FinalAnswer。

通过标准：

- 能向老师解释 Action 是工具调用，Observation 是工具返回。

### 18.2 JSONL Trace

执行一次 CLI chat 或 ask 后检查：

```powershell
Get-ChildItem src\SmartStudy.Cli\bin\Debug\net8.0\data\trace-*.jsonl | Sort-Object LastWriteTime -Descending | Select-Object -First 3
```

预期结果：

- 生成 trace JSONL 文件。

通过标准：

- 文件中有结构化 Agent 事件。

## 19. 文档交付验收

| 文档 | 验收点 |
| --- | --- |
| `README.md` | 构建、运行、CLI、Web、MCP、模型配置、验收命令完整 |
| `docs/architecture.md` | 包含架构图、工具设计、ReAct 推理流程、RAG、Streaming、Plan-and-Execute |
| `docs/reflection-report.md` | 包含 Agent 原理、核心循环解读、设计决策、AI 使用透明度 |
| `docs/demo-script.md` | 包含 10 分钟演示流程和答辩 FAQ |
| `docs/team-iteration-plan.md` | 包含团队分工、风险、最终交付清单 |
| `docs/manual-acceptance-test.md` | 本手动验收文档 |

通过标准：

- 文档内容与当前实现一致。
- 工具数量、测试数量、命令名称不冲突。
- 没有真实 API Key。

## 20. 评分维度自评

| 评分维度 | 分值 | 验收结论 |
| --- | ---: | --- |
| Agent 核心功能 | 30 | ReAct Loop、工具调用、RAG、记忆、复杂任务均可演示，建议自评优秀 |
| 技术实现质量 | 20 | Core / CLI / Web / MCP 分层，DI、Options、async、错误处理齐全，建议自评优秀 |
| .NET 技术深度 | 15 | .NET 8、Host、DI、HttpClientFactory、Options、Logging、Blazor、MCP SDK，建议自评优秀 |
| 架构设计文档 | 15 | 有 Mermaid 架构图、流程图、工具表、失败模式，建议自评优秀 |
| 反思报告与答辩 | 20 | 有核心循环解读和 AI 使用说明；最终得分取决于现场讲解熟练度 |
| 加分项 | +10 | Multi-Agent、RAG、MCP、单元测试、创新应用均覆盖 |

## 21. 最终手动验收清单

验收前逐项打勾：

- [ ] `dotnet build SmartStudy.sln --no-restore` 成功。
- [ ] `dotnet test SmartStudy.sln --no-build --no-restore` 全部通过。
- [ ] `doctor` 显示 LLM、Embedding、RAG、Tools 状态正常。
- [ ] `tools` 显示 17 个工具。
- [ ] `index` 能构建当前项目索引。
- [ ] CLI `ask` 能正常回答。
- [ ] CLI `chat` 支持中文输入、方向键、冒号命令。
- [ ] CLI `:models` 不产生幻觉 profile。
- [ ] CLI 项目 / 对话命令可用。
- [ ] CLI 删除项目 / 删除对话命令可用，并同步删除持久化文件。
- [ ] Web 能在 `http://localhost:5178/` 打开。
- [ ] Web 左侧项目 / 对话栏可新建和切换。
- [ ] Web 左侧项目 / 对话栏可删除项目和对话。
- [ ] 切换项目 / 对话不会清空已有对话记忆。
- [ ] Web Agent 时间线内部滚动正常。
- [ ] Web 冒号命令与 CLI 行为一致。
- [ ] Web 流式输出正常。
- [ ] RAG 检索包含来源、证据编号或 ChunkId。
- [ ] 本地资料目录导入成功，不回答“无法访问个人文件夹”。
- [ ] PDF/PPT 精读能输出多页内容。
- [ ] 笔记、学习画像、进度、错题按项目持久化。
- [ ] Multi-Agent 命令显示四个 Agent 并通过 Reviewer。
- [ ] Plan-and-Execute 命令显示 Plan / Execute / Review 并通过质量检查。
- [ ] MCP Server 可启动，MCP 工具代码和测试存在。
- [ ] trace JSONL 生成，能展示 Thought / Action / Observation。
- [ ] README 和 docs 与当前命令一致。
- [ ] 文档和提交中没有真实 API Key。

## 22. 常见异常与处理

| 现象 | 可能原因 | 处理 |
| --- | --- | --- |
| Web 端口打不开 | 旧进程占用端口 | 运行 `.\scripts\stop-all.ps1`，或使用 `-WebPort 5188` |
| `doctor` 显示 Index missing | 当前项目尚未索引 | 运行 `dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- index` |
| `Embedding` API 失败 | 网络、额度或 API Key 问题 | 临时设置 `$env:SMARTSTUDY_Agent__Embedding__Provider = "local"` |
| `:models` 输出奇怪模型 | 命令被 LLM 处理 | 检查 Web/CLI 命令服务是否拦截冒号命令 |
| Web 与 CLI 项目不一致 | 项目数据目录未共享或旧数据未迁移 | 检查 `data/web-projects`，重新运行 `project current` 和刷新 Web |
| 资料导入失败 | 路径不存在或文件被占用 | 确认路径存在，关闭占用文件的软件后重试 |
| MCP 单独启动后退出 | stdio server 没有 Host 输入 | 正常现象；答辩时说明 MCP 通常由 Host 拉起 |

## 23. 建议演示顺序

1. `dotnet build`
2. `dotnet test`
3. `doctor`
4. `tools`
5. Web 打开并展示项目 / 对话 / 时间线
6. CLI `project current` 证明 Web/CLI 共享项目状态
7. RAG 检索和课件精读
8. 笔记、学习画像、进度、错题
9. Streaming
10. Multi-Agent
11. Plan-and-Execute
12. MCP Server 说明
13. 反思报告中 ReActAgent 核心循环讲解

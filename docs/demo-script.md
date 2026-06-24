# SmartStudy 10 分钟演示脚本与答辩 FAQ

## 1. 演示目标

这份脚本用于期末答辩、录屏或验收。演示时重点证明 SmartStudy 不是普通聊天程序，而是一个在 .NET 8 中实现的 AI Agent：

- 有真实 LLM 调用。
- 有 ReAct 循环和工具调用。
- 有 17 个自定义工具，超过课程要求的 3 个。
- 有短期对话记忆、学习笔记和长期学习画像。
- 有 Multi-Agent 协作命令，展示 Planner、Researcher、Tutor、Reviewer 分工。
- 有 RAG、本地课程资料导入、MCP、Streaming、可观测性和单元测试。

## 2. 演示前准备

在项目根目录执行：

```powershell
dotnet restore SmartStudy.sln
dotnet build SmartStudy.sln --no-restore
dotnet test SmartStudy.sln --no-build --nologo
```

期望结果：

```text
Build: 0 warning, 0 error
Tests: 43/43 passed
```

如果现场网络不稳定，建议切到本地 RAG：

```powershell
$env:SMARTSTUDY_Agent__Embedding__Provider = "local"
```

## 3. 10 分钟演示流程

### 0:00 - 1:00 项目开场

讲解要点：

SmartStudy 是一个面向课程学习场景的 .NET 8 控制台 AI Agent。用户可以提问课程资料、导入本地课件、精读 PDF、保存笔记、维护学习画像、生成复习计划和练习题。项目核心使用手写 ReAct 循环，不依赖黑盒 Planner，因此适合逐行解释 Agent 内部工作原理。

可以展示文件：

```text
README.md
docs/architecture.md
```

### 1:00 - 2:00 构建、测试和诊断

执行：

```powershell
dotnet build SmartStudy.sln --no-restore
dotnet test SmartStudy.sln --no-build --nologo
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- doctor
```

讲解要点：

- `doctor` 会显示当前 LLM profile、Embedding provider、知识库、索引、笔记、学习画像和工具数量。
- 如果看到 `Tools = 17`，说明所有 Agent 工具都已成功注册。
- 如果看到 `Embedding = local`，说明 RAG 检索不依赖云端 embedding，适合现场演示。
- 如果看到 `Vector Store = JsonPersistent`，说明 RAG 索引已经持久化到 `data/index.json`，程序重启后可以直接加载。

### 2:00 - 3:00 展示工具注册

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- tools
```

讲解要点：

每个工具都实现 `ITool`，并提供 `Name`、`Description` 和 `ParametersSchema`。`ToolRegistry` 把这些工具转换为 OpenAI function calling 格式，随请求发给 LLM。模型通过 `tool_calls` 指定要调用的工具，C# 代码执行后把 observation 写回上下文。

聊天模式里也可以用冒号快捷指令直接调用同一批工具，例如：

```text
:search ReAct Agent
:calc (45+15)/6
:note ReAct | Thought Action Observation | final,agent
:notes #final
:profile
:plan 3 天复习 Agent 项目
```

这说明工具有两种入口：一种是用户输入普通自然语言后由 LLM 自主选择工具，另一种是用户用 `:` 指令显式触发工具，二者底层都调用同一个 `ITool.InvokeAsync`。

重点点名这几类工具：

- `knowledge_search`：RAG 检索。
- `read_course_material` / `import_course_materials`：本地课程资料工作流。
- `add_note` / `list_notes`：学习笔记长期存储。
- `update_learning_profile` / `show_learning_profile` / `study_plan`：长期学习画像和复习计划。
- `add_study_task` / `mark_task_done` / `show_progress` / `review_history`：学习进度追踪。
- `make_quiz` / `submit_quiz_answer`：先出题隐藏答案，再提交答案判分。
- `record_quiz_result` / `show_mistakes`：错题本和测验反馈。

### 3:00 - 4:00 RAG 问答

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请先检索课程资料，再用两句话解释 ReAct Agent 的循环流程。"
```

讲解要点：

控制台会展示 `Thought -> Action knowledge_search -> Observation -> FinalAnswer`。这证明 Agent 不是直接回答，而是先检索课程知识库，再基于资料回答。

### 4:00 - 5:00 本地课件精读

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请按页详细讲讲 2026_Slides Lesson00_Introduction to SEME.pdf 的具体内容，不要省略。"
```

讲解要点：

当用户点名具体文件时，Agent 应优先调用 `read_course_material`，而不是只做模糊检索。这个工具解决了“只读第一页然后说后面省略”的问题，会按文件名匹配已导入课件，并返回连续页内容。

### 5:00 - 6:00 笔记与长期学习画像

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请把 ReAct 的核心思想记成一条笔记，标签是 final。"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "我对 ReAct 和 MCP 比较薄弱，目标是准备期末答辩，偏好先讲概念再举例。请更新我的学习画像。"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请查看我的学习画像。"
```

讲解要点：

- 笔记写入当前项目的 `data/notes.json`。
- 学习画像写入当前项目的 `data/learning-profile.json`。
- 这属于长期记忆，不会随着一次对话结束而消失。

### 6:00 - 7:00 个性化复习计划

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请基于我的学习画像制定一个 3 天复习计划。"
```

讲解要点：

`study_plan` 会读取学习画像中的薄弱项、目标和偏好，同时调用知识库检索服务取得资料线索，然后生成按天拆分的复习安排。这一步展示了“长期记忆 + RAG + 工具调用”的组合能力。

### 7:00 - 8:00 结构化出题

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请基于 ReAct 的资料生成 3 道选择题，先不要给答案，等我回答后再判分。"
```

讲解要点：

`make_quiz` 内部会再次调用 LLM，并要求输出结构化 JSON。工具会把标准答案保存到 `data/quiz-sessions.json`，但展示给用户时只显示题目和选项；用户回答后再由 `submit_quiz_answer` 判分、展示标准答案和解析，并把错题写入错题本。

### 8:00 - 9:00 进度追踪、错题本与来源标注

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- chat
```

进入聊天后输入：

```text
:search ReAct Agent 工具调用
:task 复习 ReAct 循环 | ReAct | 40
:done ReAct | 能解释 Thought Action Observation | 35
:progress
:quiz ReAct 包含 Thought、Action、Observation | 1
:answer latest | 1 | A | ReAct
:mistakes
:profile
```

讲解要点：

- `knowledge_search` 输出包含来源、证据编号、ChunkId、相似度和来源汇总，便于说明回答依据。
- 进度追踪写入 `data/study-progress.json`，能展示完成率、累计时长和学习历史。
- 出题会写入 `data/quiz-sessions.json`，判分结果会写入 `data/quiz-results.json`，答错时会把主题加入学习画像的薄弱项。

### 9:00 - 9:40 模型切换与 Streaming

执行：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请用一句话说明当前模型可用。" --llm glm-4-flash
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请用一句话说明当前模型可用。" --llm deepseek-chat
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- chat --stream
```

讲解要点：

- `--llm` 或交互模式下的 `:model <name>` 可以切换 LLM profile。
- `--stream` 走 SSE 流式输出。
- 交互模式支持中文输入、左右箭头、Home、End、Backspace 和 Delete。

### 9:40 - 10:00 MCP 与总结

执行：

```powershell
dotnet run --project src\SmartStudy.Mcp\SmartStudy.Mcp.csproj
```

讲解要点：

`SmartStudy.Mcp` 把笔记、知识库检索和已导入课程资料清单暴露为 MCP 工具。它证明同一套领域能力不仅能被 CLI 使用，也能被外部 MCP Host 复用。

总结时回扣课程要求：

| 要求 | SmartStudy 对应实现 |
| --- | --- |
| LLM 集成 | `OpenAiLlmClient` 支持 GLM / DeepSeek OpenAI 兼容接口 |
| Agent Loop | `ReActAgent` 手写循环 |
| Tool Calling | 17 个自定义工具 |
| Memory | 对话记忆、笔记、学习画像 |
| 用户交互 | `SmartStudy.Cli` 控制台 UI |
| 加分项 | Multi-Agent、RAG、MCP、Streaming、可观测性、单元测试 |

### 可替换演示：Multi-Agent 协作

如果老师重点关注加分项，可以用下面命令替换部分普通问答演示：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- multi "解释 ReAct Agent 并准备答辩"
```

也可以在流式聊天模式中触发：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- chat --stream
```

进入聊天后输入：

```text
:multi 解释 ReAct Agent 并准备答辩
```

前端展示方式：

1. 控制台顶部显示 `SmartStudy Multi-Agent 协作`。
2. 中间表格逐行展示 `PlannerAgent`、`ResearchAgent`、`TutorAgent`、`ReviewerAgent`。
3. 每行包含 Agent 名称、职责、状态和输出摘要。
4. 底部 `Final Answer` 面板展示 TutorAgent 产出的最终答复。
5. Reviewer 通过时显示 `Reviewer: PASS`，否则显示 `NEEDS ATTENTION` 并给出修正建议。

验收标准：

1. 四个 Agent 都出现在控制台表格中。
2. `ResearchAgent` 能使用已有 RAG 索引返回课程资料来源。
3. `TutorAgent` 的最终答复包含目标理解、建议执行路径、资料依据摘要和下一步。
4. `ReviewerAgent` 能检查是否覆盖目标、是否有资料依据、是否包含下一步。
5. `dotnet test SmartStudy.sln --no-build --nologo` 中 Multi-Agent 相关测试通过。

### 可替换演示：Plan-and-Execute + Reflection

这段适合在讲完 ReAct 后展示“超越 ReAct 的架构模式”。它先规划任务，再检索资料，最后用确定性 Reviewer 做质量检查。

```powershell
$env:SMARTSTUDY_Agent__Embedding__Provider = "local"
dotnet run --no-build --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- plan-execute "解释 ReAct Agent"
```

运行效果应包含：

```text
SmartStudy Plan-and-Execute
Plan                         OK
Execute: RAG 检索            OK
Execute: 生成答复            OK
Review: 答案质量检查         OK
Quality Review: PASS
```

验收标准：

1. 控制台表格必须出现 Plan、Execute、Review 三类步骤。
2. RAG 检索输出包含来源、证据编号、ChunkId 和相似度。
3. Review 输出包含“质量检查通过”，并列出 OK 检查项。
4. 即使云端 embedding 或网络不可用，命令也应降级展示 `检索失败`，而不是异常退出。
5. `AnswerQualityReviewerTests` 和 `PlanExecuteAgentTests` 通过。

### 可替换演示：更多资料格式导入

如果老师问“你的 RAG 能不能导入非 PDF 资料”，可以展示 `import_course_materials` 已支持：

```text
.pdf / .pptx / .docx / .xlsx / .csv / .tsv / .html / .htm / .md / .txt
```

验收标准：

1. CSV/TSV 会抽取表格行。
2. HTML 会去除标签并保留正文。
3. XLSX 会抽取工作表名和单元格文本。
4. 导入后会在 `knowledge/imported` 生成 Markdown，并重建 RAG 索引。
5. `CourseMaterialImporterExtendedFormatTests.ImportsCsvHtmlAndXlsxMaterials` 通过。

## 4. 常见答辩问题

### Q1：你的 Agent 和普通聊天机器人有什么区别？

普通聊天机器人通常只把用户输入发给 LLM，然后返回文本。SmartStudy 会把工具定义发给 LLM，让模型判断是否需要调用检索、读文件、记笔记、出题、更新学习画像等工具。C# 程序执行工具后把结果作为 observation 写回上下文，模型再继续推理。这就是 ReAct。

### Q2：为什么不用 Semantic Kernel？

本课程要求理解 Agent 内部工作原理，并能逐行解释核心代码。Semantic Kernel 可以简化开发，但会隐藏部分 tool loop 细节。SmartStudy 直接使用 OpenAI 兼容协议和手写 ReAct 循环，便于展示 `tool_calls` 如何变成真实的 C# 工具调用。

### Q3：工具是怎么注册给模型的？

每个工具实现 `ITool`，提供 `Name`、`Description`、`ParametersSchema`。`ToolRegistry.ToOpenAiDefinitions()` 把这些信息转换为 OpenAI function calling 的 JSON 格式。LLM 响应中的 `tool_calls` 指定工具名和参数，Agent 根据工具名找到 C# 实现并执行。

### Q4：RAG 是怎么实现的？

`KnowledgeIndexer` 读取 `knowledge/*.md` 并切分 chunk，`IEmbeddingClient` 生成向量，`JsonPersistentVectorStore` 把向量快照保存到 `data/index.json`，启动后重新加载，再用余弦相似度检索最相关片段。Embedding 可以使用智谱 `embedding-3`，也可以切到本地 `LocalHashEmbeddingClient`，避免现场演示受网络影响。

### Q4.1：为什么这个功能叫 Multi-Agent？

因为它不是单个 Agent 一次性完成全部工作，而是由 `MultiAgentOrchestrator` 编排四个专业角色：`PlannerAgent` 负责拆解目标，`ResearchAgent` 负责检索资料，`TutorAgent` 负责生成学习答复，`ReviewerAgent` 负责质量检查。每个角色都有独立职责和可展示输出，控制台前端会把它们按执行顺序展示出来。

### Q5：长期记忆在哪里？

短期对话由 `ProjectConversationMemory` 按项目 / 对话持久化到 `data/web-projects/<project-id>/data/memory/<conversation-id>.json`，切换项目或对话会恢复对应历史。长期记忆分为学习笔记、学习画像、学习进度和错题记录，均写入当前项目的 `data/` 目录。学习画像包含薄弱知识点、优势知识点、学习目标和偏好讲解方式。

### Q6：如果 LLM 输出了错误 JSON 怎么办？

Agent 主循环会捕获工具参数 JSON 的解析错误，并把错误作为 observation 交给模型自纠。`make_quiz` 额外做了结构化输出校验：先提取 fenced JSON 或数组片段，再校验题目字段；如果失败，会要求 LLM 修复一次。校验通过后，答案只保存到本地练习会话，不在题面泄露。

### Q7：为什么可观测性重要？

可观测性让老师能看到 Agent 每一步到底做了什么。项目通过 `IAgentTracer` 同时把 Thought、Action、Observation、FinalAnswer 输出到控制台，并写入 `data/trace-*.jsonl`，便于答辩展示和事后排查。

### Q8：项目如何证明稳定性？

项目包含 xUnit 测试，覆盖 Agent Loop、工具调用、记忆、RAG、本地 embedding、流式 tool call、中文输入编辑、课件精读、MCP 工具、学习画像、画像补参兜底、复习计划、Multi-Agent 编排、Plan-and-Execute、答案质量检查和多格式资料导入。测试使用 `FakeLlmClient` 或本地 embedding，不依赖真实 LLM API，因此可稳定运行。

## 5. 网络异常时的备用演示

如果 LLM API 因网络或额度问题不可用，可以演示不依赖外部 LLM 的部分：

```powershell
$env:SMARTSTUDY_Agent__Embedding__Provider = "local"
dotnet build SmartStudy.sln --no-restore
dotnet test SmartStudy.sln --no-build --nologo
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- doctor
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- tools
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- index
```

然后打开以下文件解释实现：

```text
src/SmartStudy.Core/Agent/ReActAgent.cs
src/SmartStudy.Core/Tools/ITool.cs
src/SmartStudy.Core/Tools/Builtin/LearningProfileTools.cs
src/SmartStudy.Core/Rag/EmbeddingClient.cs
docs/architecture.md
docs/reflection-report.md
```



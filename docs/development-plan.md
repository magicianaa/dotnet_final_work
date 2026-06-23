# SmartStudy 后续开发计划

## 1. 计划依据

本计划依据 `dotnet-ai-agent-project.pdf` 中的期末项目要求制定。PDF 对项目提出了五类硬性核心要素：

| 要求 | 说明 | 当前状态 |
| --- | --- | --- |
| LLM 集成 | 通过 API 调用大语言模型 | 已完成，支持智谱 GLM 与 DeepSeek 的 OpenAI 兼容接口 |
| Agent Loop | 实现 Thought -> Action -> Observation 循环 | 已完成，`ReActAgent` 手写循环，支持普通与流式模式 |
| Tool Calling | 至少 3 个自定义工具 | 已完成，当前注册 17 个工具 |
| Memory | 对话记忆或工作记忆 | 已完成，`ConversationMemory` 滑动窗口 + system prompt 保留，另有笔记与学习画像长期记忆 |
| 用户交互界面 | 控制台 / Web / 桌面 / 移动端均可 | 已完成，`SmartStudy.Cli` 控制台 UI |

PDF 还鼓励实现 RAG、MCP、Streaming、可观测性和单元测试。当前项目已经覆盖这些加分项，因此后续开发重点不再是堆功能数量，而是提高稳定性、可解释性、可验收性和答辩材料一致性。

## 2. 当前项目状态审计

| 模块 | 已完成能力 | 需要继续改进 |
| --- | --- | --- |
| LLM | 支持多 profile 切换，`--llm` 与 `:model` 可切换模型 | 增加模型连通性自检命令 |
| Agent Loop | 支持多步工具调用、最大步数限制、错误 observation | 增加更细的失败诊断与演示脚本 |
| Tools | 已有检索、精读资料、导入资料、笔记、学习画像、学习进度、错题本、复习计划、出题、计算 | 后续可继续增加模型连通性自检 |
| RAG | 支持云端 embedding 与本地 Hash Embedding，`JsonPersistentVectorStore` 持久化索引快照，检索输出含来源标注 | 可以增加更细的页码/章节引用 |
| CLI | 支持中文行内编辑、流式输出、模型切换 | 可以增加 `/help` 或命令帮助面板 |
| MCP | 暴露笔记、知识库检索和资料清单 MCP 工具 | 可以继续扩展学习画像 MCP 工具 |
| Tests | 43 个测试覆盖核心逻辑 | 继续覆盖新功能和边界情况 |
| Docs | README、架构文档、反思报告、开发计划已同步 | 后续可补充更完整的答辩讲稿 |

## 3. 后续迭代路线

### 迭代 1：结构化输出可靠性增强

目标：让 `make_quiz` 不只是“要求模型输出 JSON”，而是由 C# 代码实际校验 JSON 结构，并采用先出题、隐藏答案、用户提交后再判分的练习流程。如果模型输出 Markdown、解释文字或字段缺失，工具应自动发起修复请求；如果仍失败，应返回可读错误。

当前状态：已完成。

验收标准：

- `make_quiz` 能解析纯 JSON 数组。
- `make_quiz` 能从 ```json fenced block 中提取 JSON。
- `make_quiz` 展示题目时不泄露正确答案和解析，答案保存到练习会话。
- `submit_quiz_answer` 能根据用户答案判分，并在判分后展示标准答案和解析。
- 字段缺失或非法 JSON 时会重试修复。
- 连续失败时返回明确错误，而不是把坏 JSON 传给用户。
- 新增单元测试覆盖成功、修复、失败路径。

### 迭代 2：演示与诊断能力增强

目标：降低现场演示失败概率，让老师能快速看到项目核心能力。

当前状态：已完成。

计划内容：

- 增加 `doctor` / `status` 命令，显示配置、当前 LLM profile、Embedding provider、索引文件、笔记文件、工具列表。
- 增加 `tools` 命令，列出所有工具名和描述。
- README 增加标准演示脚本。

### 迭代 3：MCP 能力扩展

目标：让 MCP 不只证明笔记工具可暴露，还能展示课程资料检索能力。

当前状态：已完成。

计划内容：

- 将 `knowledge_search` 以 MCP 工具形式暴露。
- 暴露 `ListImportedMaterials`，方便外部 Host 查询资料文件。
- 补充 MCP 启动和调用说明。

### 迭代 4：学习画像与长期记忆

目标：让学习助手更像“学习助手”，而不是一次性问答工具。

当前状态：已完成。

计划内容：

- 增加 `JsonLearningProfileStore`，使用 `data/learning-profile.json` 保存薄弱知识点、优势知识点、学习目标和讲解偏好。
- 增加 `update_learning_profile` 工具，让 Agent 在用户表达薄弱点、目标或偏好时更新长期画像。
- 增加 `show_learning_profile` 工具，支持查看当前学习画像。
- 增加 `study_plan` 工具，根据课程资料检索结果和学习画像生成复习计划。
- 补充 3 个单元测试，覆盖画像持久化、画像工具和复习计划工具。

### 迭代 5：文档与答辩材料收口

目标：保证交付物与源码完全一致，减少答辩时被问到“文档和代码不一致”的风险。

当前状态：已完成。

计划内容：

- 更新 `docs/architecture.md`：补充 `read_course_material`、`import_course_materials`、学习画像、本地资料工作流。
- 更新 `docs/reflection-report.md`：把工具数量、测试数量、中文输入修复、结构化 quiz 校验、学习画像和复习计划写入反思。
- 增加 `docs/demo-script.md`：提供 10 分钟演示脚本、验收命令、备用演示路径和常见答辩问题答案。
- 增加 `docs/team-iteration-plan.md`：提供 4 人 7 天团队分工、每日节奏、责任矩阵和最终交付清单。

## 4. 本轮执行范围

已完成的执行范围：

1. 改造 `MakeQuizTool`，增加 JSON 提取、结构校验、自动修复和错误返回。
2. 增加对应单元测试。
3. 更新架构文档和反思报告中已经过时的工具数量与测试描述。
4. 新增 `SmartStudyDoctor`，实现 `doctor` / `status` 健康检查命令。
5. 新增 `tools` 命令，快速展示 Agent 已注册工具。
6. 新增 `KnowledgeSearchService` 与 `CourseMaterialCatalog`，让 CLI 工具和 MCP 工具复用同一套知识库查询逻辑。
7. 扩展 `SmartStudy.Mcp`，暴露 `SearchKnowledge` 和 `ListImportedMaterials`。
8. 更新 README、架构文档、反思报告的 MCP 说明与当前状态。
9. 运行构建、测试、真实 CLI 命令和 MCP 启动烟测，记录验收结果。
10. 新增 `JsonLearningProfileStore` 与 `LearningProfileTools.cs`，实现长期学习画像。
11. 新增 `update_learning_profile`、`show_learning_profile`、`study_plan` 三个 Agent 工具。
12. 在 CLI DI 和 system prompt 中注册学习画像与复习计划能力。
13. 更新 `doctor/status`，展示 `learning-profile.json` 是否已创建。
14. 将单元测试扩展到 41 个，覆盖学习画像、画像补参兜底、复习计划、学习进度、错题本、来源标注、Multi-Agent、Plan-and-Execute、多格式资料导入和持久化向量存储。
15. 新增 `docs/demo-script.md`，覆盖构建、测试、doctor、tools、RAG、课件精读、笔记、学习画像、复习计划、Streaming、MCP 和答辩 FAQ。
16. 更新 README 和开发计划，使交付文档目录与当前源码能力一致。
17. 新增 `docs/team-iteration-plan.md`，按照 4 人分工设计 7 天迭代节奏，突出以人分任务而不是机械按天分任务。

选择这个范围的原因是：PDF 的评分中 Agent 核心功能占 30 分，技术实现质量占 20 分。前几轮已经把工具调用、RAG、MCP 和可观测性打稳，本轮加入学习画像后，Agent 从一次性问答进一步变成能长期跟踪学生薄弱项和学习目标的学习助手。

## 5. 推荐最终验收命令

```powershell
dotnet restore SmartStudy.sln
dotnet build SmartStudy.sln --no-restore
dotnet test SmartStudy.sln --no-build --nologo

dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- doctor
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- tools
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- index
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请解释 ReAct Agent 的循环流程"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请按页详细讲讲 2026_Slides Lesson00_Introduction to SEME.pdf"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请基于 ReAct 的资料生成 3 道选择题"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请把 ReAct 的核心思想记成 final 标签笔记"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "列出 final 标签的笔记"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "我对 ReAct 和 MCP 比较薄弱，目标是完成期末答辩，请更新我的学习画像"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请基于我的学习画像制定一个 3 天复习计划"
$env:SMARTSTUDY_Agent__Embedding__Provider = "local"
dotnet run --no-build --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- plan-execute "解释 ReAct Agent"
```

## 6. 本轮新增扩展验收

### 6.1 Reflection Agent / 答案质量检查

实现文件：

- `src/SmartStudy.Core/Agent/AnswerQualityReviewer.cs`
- `src/SmartStudy.Core/Agent/PlanExecuteAgent.cs`

验收标准：

1. `AnswerQualityReviewer` 能检查非空回答、回答充分、覆盖目标、资料依据、下一步、占位文本。
2. 合格回答返回 `Passed = true`。
3. 缺少来源或包含占位文本时返回明确 TODO 项。
4. `AnswerQualityReviewerTests` 通过。

### 6.2 Plan-and-Execute

实现入口：

```powershell
dotnet run --no-build --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- plan-execute "解释 ReAct Agent"
```

验收标准：

1. 输出 `SmartStudy Plan-and-Execute`。
2. 表格包含 `Plan`、`Execute: RAG 检索`、`Execute: 生成答复`、`Review: 答案质量检查`。
3. 检索成功时最终显示 `Quality Review: PASS`。
4. 检索失败时步骤显示 WARN 并保留失败原因，程序不崩溃。
5. `PlanExecuteAgentTests` 通过。

本次实测结果：

```text
SmartStudy Plan-and-Execute
Plan                         OK
Execute: RAG 检索            OK
Execute: 生成答复            OK
Review: 答案质量检查         OK
Quality Review: PASS
```

### 6.3 资料导入格式扩展

扩展格式：

```text
.pdf, .pptx, .docx, .xlsx, .csv, .tsv, .html, .htm, .md, .txt
```

验收标准：

1. CSV/TSV 抽取表格行。
2. HTML 移除标签并保留正文。
3. XLSX 抽取 Sheet 名称和单元格文本。
4. 导入后生成 Markdown 并重建 RAG 索引。
5. `CourseMaterialImporterExtendedFormatTests.ImportsCsvHtmlAndXlsxMaterials` 通过。

### 6.4 持久化向量存储

实现文件：

- `src/SmartStudy.Core/Rag/VectorStore.cs`
- `src/SmartStudy.Cli/SmartStudyDoctor.cs`
- `src/SmartStudy.Cli/Program.cs`
- `src/SmartStudy.Mcp/Program.cs`

验收标准：

1. CLI 和 MCP 默认注入 `JsonPersistentVectorStore`。
2. 执行 `index` 后生成 `data/index.json` 持久化快照，包含 `version`、`createdUtc`、`chunkCount`、`chunks`。
3. 新建 `JsonPersistentVectorStore` 实例可以从同一个 `index.json` 重新加载 chunk 并完成相似度检索。
4. 旧数组格式 `index.json` 仍能加载，避免破坏已有索引。
5. `doctor` 输出包含 `Vector Store = JsonPersistent`、持久化路径和已加载 chunk 数。
6. `JsonPersistentVectorStoreTests` 和 `SmartStudyDoctorTests` 通过。

## 7. 风险与应对

| 风险 | 应对 |
| --- | --- |
| API Key 或网络导致现场演示失败 | 保留本地 RAG，准备不依赖 embedding API 的演示路径 |
| LLM 输出格式不稳定 | 对关键工具做 JSON 校验和修复重试 |
| 文档与代码不同步 | 每轮迭代后同步 README、架构文档和反思报告 |
| 答辩抽查代码解释 | 保持核心循环手写且文档中逐块解释 |
| 课程资料路径变化 | 支持用户通过自然语言导入任意本地目录 |



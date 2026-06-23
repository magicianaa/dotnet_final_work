# SmartStudy 4 人 7 天迭代计划

## 1. 计划定位

本计划基于当前 SmartStudy 项目状态制定。当前项目已经具备：

- .NET 8 解决方案可构建。
- ReAct Agent 主循环已完成。
- 支持 GLM / DeepSeek 模型切换。
- 支持 17 个 Agent 工具。
- 支持本地 RAG、智谱 embedding、课件导入、课件精读、学习笔记、学习画像、学习进度、错题本、复习计划、MCP、Streaming、doctor/tools 诊断命令。
- 当前单元测试 43/43 通过。

因此这份 7 天计划不是从零开发，而是面向“课程交付前最后一轮团队迭代”：提高稳定性、展示效果、答辩解释质量和团队分工可信度。

本计划以 4 人分工为主，天数只是协作节奏。每个人都对一个可验收的模块负责，最后两天集中联调和答辩材料收口。

## 2. 团队角色分工

| 成员 | 角色 | 主要负责范围 | 核心交付物 |
| --- | --- | --- | --- |
| 成员 A | Agent 核心与 LLM 负责人 | ReAct 主循环、模型 profile、tool calling 策略、system prompt、streaming | 更稳定的工具触发策略、模型切换验收记录、核心代码讲解稿 |
| 成员 B | RAG 与课程资料负责人 | 本地/云端 embedding、知识库索引、课件导入、课件精读、资料质量 | 索引统计、资料导入验收记录、课件精读样例 |
| 成员 C | 工具、MCP 与 CLI 负责人 | 17 个工具、doctor/tools/status、MCP server、交互式 CLI | 工具清单验收、MCP 启动说明、CLI 演示脚本 |
| 成员 D | 测试、文档与答辩负责人 | xUnit、端到端验收、README、架构图、反思报告、答辩 FAQ | 测试报告、演示脚本、最终文档一致性检查 |

## 3. 总体验收目标

7 天结束时，项目应满足以下可验收目标：

| 目标 | 验收方式 |
| --- | --- |
| 构建稳定 | `dotnet build SmartStudy.sln --no-restore` 为 0 warning / 0 error |
| 测试稳定 | `dotnet test SmartStudy.sln --no-build --nologo` 全部通过 |
| 工具链完整 | `doctor` 显示 17 个工具，`tools` 能列出所有工具 |
| RAG 可演示 | `index` 成功，`knowledge_search` 能检索 ReAct / MCP 资料 |
| 课件工作流可演示 | `import_course_materials` 能导入本地资料，`read_course_material` 能按页讲解课件 |
| 长期记忆可演示 | `add_note/list_notes` 和 `update_learning_profile/show_learning_profile/study_plan` 可用 |
| 模型切换可演示 | `--llm glm-4-flash` 与 `--llm deepseek-chat` 可通过 doctor 或 ask 验证 |
| MCP 可说明 | `SmartStudy.Mcp` 能启动，文档说明 stdio 无输入退出是正常行为 |
| 答辩材料一致 | README、architecture、reflection、demo-script、team-plan 中数字和工具名称一致 |

## 4. 7 天节奏安排

### Day 1：现状冻结与任务拆分

| 成员 | 任务 | 输出 |
| --- | --- | --- |
| A | 阅读 `ReActAgent.cs`、`AgentOptions.cs`、`OpenAiLlmClient.cs`，整理 Agent 主链路 | Agent 核心讲解提纲 |
| B | 检查 `knowledge/`、`knowledge/imported/`、`data/index.json`，确认索引规模与资料来源 | RAG 资料清单与风险记录 |
| C | 运行 `doctor`、`tools`、`chat --stream`，确认 CLI 和工具注册状态 | CLI 功能清单截图或记录 |
| D | 对照 `dotnet-ai-agent-project.pdf` 和现有 docs，列出交付物覆盖表 | 评分点覆盖表 |

统一验收命令：

```powershell
dotnet build SmartStudy.sln --no-restore
dotnet test SmartStudy.sln --no-build --nologo
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- doctor
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- tools
```

### Day 2：Agent 行为稳定性增强

| 成员 | 任务 | 输出 |
| --- | --- | --- |
| A | 检查 system prompt 中工具触发规则，重点看 `study_plan`、`make_quiz`、`read_course_material` | Prompt 调整记录 |
| B | 提供 3 个 RAG 检索样例：ReAct、MCP、Semantic Kernel | RAG query 样例 |
| C | 用 `ask` 验证每个工具是否能被真实 LLM 触发 | 工具触发验收表 |
| D | 为新增/调整行为补测试或验收记录 | 测试变更记录 |

重点验收：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请先检索 ReAct 的课程资料，再基于检索结果生成 2 道选择题。"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请基于我的学习画像制定一个 2 天复习计划。"
```

### Day 3：RAG 与本地资料工作流

| 成员 | 任务 | 输出 |
| --- | --- | --- |
| A | 确认 Agent 对本地路径请求不会回答“无法访问” | 路径请求验收记录 |
| B | 负责 `import_course_materials`、`KnowledgeIndexer`、`ReadCourseMaterialTool` 的端到端验收 | 资料导入和精读样例 |
| C | 检查 CLI 输出是否适合录屏，必要时调整提示文案 | CLI 展示优化建议 |
| D | 更新 README / demo script 中的本地资料命令 | 文档补丁 |

重点验收：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- index
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请按页详细讲讲 2026_Slides Lesson00_Introduction to SEME.pdf 的前 2 页内容。"
```

### Day 4：长期记忆与个性化学习能力

| 成员 | 任务 | 输出 |
| --- | --- | --- |
| A | 验证 Agent 是否能根据用户表达更新学习画像 | 学习画像工具触发记录 |
| B | 为学习画像提供课程主题建议，如 ReAct、MCP、软件经济、估算 | 复习主题清单 |
| C | 验证 `add_note/list_notes/update_learning_profile/show_learning_profile/study_plan` | 长期记忆验收表 |
| D | 补充相关单元测试或文档说明 | 测试和文档记录 |

重点验收：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "我已经掌握 calculate 和 read_course_material，但仍需要加强 make_quiz。请更新我的学习画像。"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请查看我的学习画像。"
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请基于我的学习画像制定一个 3 天复习计划。"
```

### Day 5：MCP、Streaming 与模型切换

| 成员 | 任务 | 输出 |
| --- | --- | --- |
| A | 验证 GLM / DeepSeek profile 切换 | 模型切换记录 |
| B | 确认 MCP 侧能加载或复用 RAG 索引 | MCP-RAG 说明 |
| C | 验证 `chat --stream`、`:models`、`:model <name>`、中文输入编辑 | CLI 交互验收记录 |
| D | 把 MCP 和 Streaming 的答辩说法写进 FAQ | FAQ 更新 |

重点验收：

```powershell
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- doctor --llm deepseek-chat
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- ask "请用一句话说明 streaming 验收通过。" --stream
dotnet run --project src\SmartStudy.Mcp\SmartStudy.Mcp.csproj
```

说明：MCP Server 是 stdio 服务，如果没有 MCP Host 输入，启动后退出或完成是正常现象。答辩时重点解释它暴露了哪些工具、如何复用 Core 能力。

### Day 6：完整端到端演示彩排

| 成员 | 任务 | 输出 |
| --- | --- | --- |
| A | 主讲 Agent Loop 和 LLM tool calling | 讲解稿定稿 |
| B | 主讲 RAG 与课件导入/精读 | 资料演示片段 |
| C | 主讲 CLI、MCP、Streaming 和工具清单 | 操作演示片段 |
| D | 主持测试、文档、风险应对和答辩 FAQ | 最终演示脚本 |

彩排顺序：

1. `build/test`
2. `doctor/tools`
3. RAG 问答
4. 课件精读
5. 笔记和学习画像
6. 复习计划
7. 结构化出题
8. Streaming
9. MCP
10. 总结课程要求覆盖情况

### Day 7：最终冻结与提交前检查

| 成员 | 任务 | 输出 |
| --- | --- | --- |
| A | 检查核心代码是否能逐行解释 | 核心代码讲解确认 |
| B | 检查知识库、索引、导入资料是否一致 | 数据状态确认 |
| C | 检查 CLI/MCP 命令是否能按脚本运行 | 命令验收确认 |
| D | 统一 README、文档、测试数字和最终验收记录 | 最终交付清单 |

最终验收命令：

```powershell
dotnet build SmartStudy.sln --no-restore
dotnet test SmartStudy.sln --no-build --nologo
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- doctor
dotnet run --project src\SmartStudy.Cli\SmartStudy.Cli.csproj -- tools
```

最终冻结标准：

- 没有编译错误。
- 测试全部通过。
- README 和 docs 中的工具数量、测试数量、命令一致。
- 真实演示路径至少成功跑过一次。
- API Key 不出现在 README、docs、提交记录或截图中。

## 5. 成员责任矩阵

| 交付项 | A Agent/LLM | B RAG/资料 | C CLI/MCP | D 测试/文档 |
| --- | --- | --- | --- | --- |
| ReAct 主循环讲解 | R | C | I | C |
| LLM profile 切换 | R | I | C | C |
| 工具触发策略 | R | C | C | C |
| RAG 索引与检索 | C | R | C | C |
| 本地课件导入 | I | R | C | C |
| CLI 交互体验 | C | I | R | C |
| MCP Server | C | C | R | C |
| 单元测试 | C | C | C | R |
| README / 文档 | C | C | C | R |
| 答辩演示 | R | R | R | R |

说明：

- R = Responsible，主要负责人。
- C = Consulted，参与确认。
- I = Informed，保持同步。

## 6. 每日站会模板

每人每天用 3 分钟回答：

1. 昨天完成了什么可验收结果？
2. 今天要完成哪个命令、代码或文档？
3. 是否有阻塞？阻塞会影响谁？
4. 今天结束前要交给谁继续？

建议每晚固定运行：

```powershell
dotnet build SmartStudy.sln --no-restore
dotnet test SmartStudy.sln --no-build --nologo
```

## 7. 风险与应对

| 风险 | 负责人 | 应对 |
| --- | --- | --- |
| 网络或 API Key 导致 LLM 调用失败 | A | 准备本地 RAG + 测试/doctor 演示路径，避免完全依赖在线调用 |
| Embedding 云端不可用 | B | 使用 `SMARTSTUDY_Agent__Embedding__Provider=local` |
| 工具触发不稳定 | A/C | 加强 system prompt、工具 description，并用真实 ask 反复验收 |
| 课件路径变化 | B | 使用 `import_course_materials` 支持用户自然语言提供目录 |
| MCP stdio 行为被误解 | C/D | 在文档中说明无 Host 输入时退出是正常现象 |
| 文档数字过时 | D | 最终一天用 `rg` 搜索工具数、测试数、命令是否一致 |
| API Key 泄漏 | 全员 | 只使用环境变量或 `appsettings.Local.json`，截图前检查输出 |

## 8. 最终交付清单

| 类别 | 文件或命令 |
| --- | --- |
| 解决方案 | `SmartStudy.sln` |
| 核心代码 | `src/SmartStudy.Core` |
| CLI | `src/SmartStudy.Cli` |
| MCP | `src/SmartStudy.Mcp` |
| 测试 | `tests/SmartStudy.Tests` |
| README | `README.md` |
| 架构文档 | `docs/architecture.md` |
| 开发计划 | `docs/development-plan.md` |
| 团队迭代计划 | `docs/team-iteration-plan.md` |
| 演示脚本 | `docs/demo-script.md` |
| 反思报告 | `docs/reflection-report.md` |
| 最终验证 | `dotnet build`、`dotnet test`、`doctor`、`tools` |



# SmartStudy 架构设计文档

## 1. 系统总览

SmartStudy 采用 **领域核心 + 适配层** 的 Clean Architecture 风格。
所有 LLM、工具、记忆、检索、追踪相关的"业务"集中在 `SmartStudy.Core`，
两个可执行项目（CLI 与 MCP Server）只是不同的"宿主"。

```mermaid
flowchart LR
    subgraph User["用户"]
        U[终端]
    end
    subgraph CLI["SmartStudy.Cli (控制台宿主)"]
        UI[Spectre.Console UI]
        DI[Host + DI + Config]
        Tracer1[SpectreConsoleTracer]
    end
    subgraph Core["SmartStudy.Core (领域核心)"]
        Agent[ReActAgent]
        Mem[ConversationMemory]
        Reg[ToolRegistry]
        T1[KnowledgeSearchTool]
        T2[AddNoteTool]
        T3[ListNotesTool]
        T4[CalculatorTool]
        T5[MakeQuizTool]
        LLM[OpenAiLlmClient]
        Emb[IEmbeddingClient\nLocalHash / Zhipu]
        VS[InMemoryVectorStore]
        Idx[KnowledgeIndexer]
        TraceI[IAgentTracer]
        FileT[JsonlFileTracer]
    end
    subgraph MCP["SmartStudy.Mcp (MCP Server)"]
        McpHost[Stdio Host]
        NoteMcp[NoteMcpTools]
    end
    subgraph External["外部"]
        ZhipuLLM[(智谱 GLM \n /chat/completions)]
        ZhipuEmb[(可选：智谱 embedding-3)]
        LocalEmb[(本地 Hash Embedding)]
        KB[(knowledge/*.md)]
        Notes[(data/notes.json)]
        TraceFile[(data/trace-*.jsonl)]
    end

    U --> UI --> Agent
    DI --> Agent
    Agent --> Mem
    Agent --> Reg
    Reg --> T1 & T2 & T3 & T4 & T5
    Agent --> LLM --> ZhipuLLM
    T1 --> Emb
    Emb --> ZhipuEmb
    Emb --> LocalEmb
    T1 --> VS
    Idx --> Emb
    Idx --> KB
    Idx --> VS
    T2 & T3 --> Notes
    T5 --> LLM
    Agent --> TraceI
    TraceI -.-> Tracer1
    TraceI -.-> FileT --> TraceFile
    McpHost --> NoteMcp --> Notes
```

## 2. ReAct 推理流程

```mermaid
sequenceDiagram
    autonumber
    participant User
    participant Agent as ReActAgent
    participant LLM
    participant Tool
    participant Mem as ConversationMemory
    participant Tracer

    User->>Agent: RunAsync(userInput)
    Agent->>Mem: AddUser(userInput)
    loop step ≤ MaxLoopSteps
        Agent->>Tracer: Thought
        Agent->>LLM: ChatAsync(messages, tools)
        LLM-->>Agent: assistant 消息 (content / tool_calls)
        Agent->>Mem: AddAssistant
        alt 无 tool_calls
            Agent->>Tracer: FinalAnswer
            Agent-->>User: AgentResult(content)
        else 有 tool_calls
            loop 每个 tool_call
                Agent->>Tracer: Action(tool, args)
                Agent->>Tool: InvokeAsync(args)
                Tool-->>Agent: observation
                Agent->>Mem: AddToolResult
                Agent->>Tracer: Observation
            end
        end
    end
    Agent-->>User: 达到最大步数提示
```

## 3. 关键设计决策

### 3.1 为什么直接 HTTP 调 OpenAI 兼容协议而不是 Semantic Kernel？

* **可教学性**：手写循环能在答辩时一行一行解释，符合评分细则 4.3 "逐行解读核心代码"。
* **可移植性**：任何兼容端点（智谱 / DeepSeek / OpenAI / Ollama）改 `BaseUrl` 即可，无须额外 connector。
* **避免框架黑盒**：SK 的 `AgentChat`、`FunctionCallingStepwisePlanner` 隐藏了 tool-loop 细节，
  不利于本课程要求的"理解 Agent 内部工作原理"。

### 3.2 为什么记忆做成接口 + 单文件实现？

* 满足 SOLID 中 DSP 与 OCP：未来若要换 SQLite / 向量检索做长期记忆，只需新增实现。
* 单元测试可直接构造 `ConversationMemory` 替代真实存储。

### 3.3 工具如何被 LLM "看见"？

每个 `ITool` 暴露三件事：`Name`、`Description`、`ParametersSchema (JSON Schema)`。
`ToolRegistry.ToOpenAiDefinitions()` 把它们打包成 OpenAI Function Calling 期望的：

```json
{
  "type": "function",
  "function": { "name": "...", "description": "...", "parameters": { /* JSON Schema */ } }
}
```

LLM 看到这些定义后，会在 `tool_calls` 字段里告诉 Agent "调用 calculate({expression:'2+3'})"。
Agent 用 `ToolRegistry.TryGet` 找到对应的 C# 工具，把结果以 `role=tool, tool_call_id=xxx` 灌回历史。

### 3.4 RAG 为什么用内存向量库？

* 课程演示规模 < 1000 个 chunk，余弦相似度 O(N·D) 完全够用，**零外部依赖**（不用 Docker / Qdrant）。
* 索引序列化为 JSON 写盘，下次启动直接 `LoadIfExistsAsync` 复用。
* 接口 `IVectorStore` 仍然抽象，未来切到 Qdrant 仅替换实现。

### 3.5 为什么支持本地 Embedding？

云端 embedding 质量更好，但现场演示可能受余额、网络、限流影响。项目通过 `Embedding.Provider` 支持两种模式：

| Provider | 实现 | 特点 |
| --- | --- | --- |
| `local` | `LocalHashEmbeddingClient` | 完全离线，基于中英文 token 哈希生成固定维度向量，适合课程资料小规模演示 |
| `zhipu` | `ZhipuEmbeddingClient` | 调用智谱 `embedding-3`，语义质量更好，但依赖 API Key 和资源额度 |

这样 RAG 的“索引、向量库、检索工具”仍然是同一套代码，只替换向量生成来源。

### 3.6 流式与可观测性如何同时存在？

* `IAgentTracer` 是单一事件总线，事件类型枚举 `Thought / Action / Observation / FinalAnswer / StreamDelta / Error`。
* `CompositeAgentTracer` 把同一事件多播到 Spectre 控制台和 JSONL 文件。
* 流式模式下，Agent 主循环改用 `IAsyncEnumerable<AgentEvent>`，
  把 `StreamDelta` 实时 yield 出去，CLI 直接 `await foreach` 渲染。

## 4. 工具一览

| Name               | 描述                            | 调用时机                | 关键文件                       |
| ------------------ | ----------------------------- | ------------------- | -------------------------- |
| `knowledge_search` | RAG 检索课程资料                    | 回答事实性问题之前           | `KnowledgeSearchTool.cs`   |
| `add_note`         | 把要点持久化为 JSON 笔记               | 用户说"记一下""保存"        | `NoteTools.cs`             |
| `list_notes`       | 按标签 / 关键字查询笔记                 | 用户问"我之前记了什么"        | `NoteTools.cs`             |
| `calculate`        | 安全表达式求值（`DataTable.Compute`） | 学习时长 / 复习天数等计算      | `CalculatorTool.cs`        |
| `make_quiz`        | 二次调用 LLM 生成结构化练习题             | 用户说"出几道题""帮我复习"     | `MakeQuizTool.cs`          |

## 5. 失败模式与防御

| 风险             | 处理                                                                          |
| -------------- | --------------------------------------------------------------------------- |
| LLM 返回非法 JSON   | `JsonDocument.Parse` 包 `try/catch`，把错误写回 observation 让 LLM 自纠            |
| 工具自身抛异常        | `try/catch` 把异常文本作为 observation，循环继续                                  |
| 死循环 / 无意义反复调工具 | `MaxLoopSteps` 硬上限 + `AgentResult.ReachedLimit` 标志                       |
| 上下文过长          | `ConversationMemory` 滑动窗口保留最近 N 条非 system 消息                          |
| 网络瞬时失败         | `HttpClient` 由 `IHttpClientFactory` 管理，CLI 层 `try/catch` 捕获并友好提示       |
| API Key 泄漏       | 通过 `appsettings.Local.json` 或环境变量注入；示例配置中 `ApiKey` 留空                  |

# Microsoft Agent Framework Overview

Source: https://learn.microsoft.com/en-us/agent-framework/overview/

Agent Framework offers two primary categories of capabilities:

|  | Description |
| --- | --- |
| __Agents__ | Individual agents that use LLMs to process inputs, call tools and MCP servers, and generate responses. Supports Microsoft Foundry, Anthropic, Azure OpenAI, OpenAI, Ollama, and more. |
| __Workflows__ | Graph-based workflows that connect agents and functions for multi-step tasks with type-safe routing, checkpointing, and human-in-the-loop support. |

The framework also provides foundational building blocks, including model clients (chat completions and responses), an agent session for state management, context providers for agent memory, middleware for intercepting agent actions, and MCP clients for tool integration. Together, these components give you the flexibility and power to build interactive, robust, and safe AI applications.

## Get started

### .NET

```
dotnet add package Microsoft.Agents.AI.Foundry --prerelease
```

```csharp
using System;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

AIAgent agent = new AIProjectClient(
        new Uri("https://your-foundry-service.services.ai.azure.com/api/projects/your-foundry-project"),
        new AzureCliCredential())
    .AsAIAgent(
        model: "gpt-5.4-mini",
        instructions: "You are a friendly assistant. Keep your answers brief.");

Console.WriteLine(await agent.RunAsync("What is the largest city in France?"));
```

### Python

```
pip install agent-framework
```

```python
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential

credential = AzureCliCredential()
client = FoundryChatClient(
    project_endpoint="https://your-foundry-service.services.ai.azure.com/api/projects/your-foundry-project",
    model="gpt-5.4-mini",
    credential=credential,
)

agent = client.as_agent(
    name="HelloAgent",
    instructions="You are a friendly assistant. Keep your answers brief.",
)
```

```python
# Non-streaming: get the complete response at once
result = await agent.run("What is the largest city in France?")
print(f"Agent: {result}")
```

That's it — an agent that calls an LLM and returns a response. From here you can add tools, multi-turn conversations, middleware, and workflows to build production applications.

> **Note**: Agent Framework does __not__ automatically load `.env` files. To use a `.env` file, call `load_dotenv()` at the start of your application, or set environment variables directly in your shell or IDE.

## When to use agents vs workflows

| Use an agent when… | Use a workflow when… |
| --- | --- |
| The task is open-ended or conversational | The process has well-defined steps |
| You need autonomous tool use and planning | You need explicit control over execution order |
| A single LLM call (possibly with tools) suffices | Multiple agents or functions must coordinate |

_If you can write a function to handle the task, do that instead of using an AI agent._

## Why Agent Framework?

Agent Framework combines AutoGen's simple agent abstractions with Semantic Kernel's enterprise features — session-based state management, type safety, middleware, telemetry — and adds graph-based workflows for explicit multi-agent orchestration.

Semantic Kernel and AutoGen pioneered the concepts of AI agents and multi-agent orchestration. The Agent Framework is the direct successor, created by the same teams. It combines AutoGen's simple abstractions for single- and multi-agent patterns with Semantic Kernel's enterprise-grade features such as session-based state management, type safety, filters, telemetry, and extensive model and embedding support. Beyond merging the two, Agent Framework introduces workflows that give developers explicit control over multi-agent execution paths, plus a robust state management system for long-running and human-in-the-loop scenarios. In short, Agent Framework is the next generation of both Semantic Kernel and AutoGen.

Both Semantic Kernel and AutoGen have benefited significantly from the open-source community, and the same is expected for Agent Framework. Microsoft Agent Framework welcomes contributions and will keep improving with new features and capabilities.

## Next steps

- Agents overview — architecture, providers, tools
- Workflows overview — sequential, concurrent, branching
- Integrations — A2A, AG-UI, Azure Functions, M365

# Introducing Microsoft Agent Framework (Preview): Making AI Agents Simple for Every Developer

Source: https://devblogs.microsoft.com/dotnet/introducing-microsoft-agent-framework-preview/

Building AI agents shouldn't be rocket science. Yet many developers find themselves wrestling with complex orchestration logic, struggling to connect multiple AI models, or spending weeks building hosting infrastructure just to get a simple agent into production.

What if building an AI agent could be as straightforward as creating a web API or console application?

## Agents and Workflows

Before we dive deeper, let's define the two core building blocks of agent systems: __agents__ and __workflows__.

### Agents

Across the web, you'll find many definitions of agents. Some conflicting, some overlapping.

For this post, we'll keep it simple:

__Agents are systems that combine reasoning, context, and tools to pursue objectives.__

Let's break down each of those components:

- __Reasoning and decision-making__: Agents need reasoning capabilities to decide which actions to take toward their objectives. Today, this is often powered by large language models (LLMs). However, other techniques such as search algorithms, planning systems, or classical AI approaches can also be used.
- __Context awareness__: Context is the external data or state that informs decision-making. Because models don't have built-in access to real-time or system-specific information, additional inputs like conversation history, knowledge bases, or enterprise data are required to make informed decisions.
- __Tool usage__: Tools are discrete, callable capabilities such as APIs, Model Context Protocol (MCP) tools, code execution, or data queries. They typically extend what a system can do but do not make decisions themselves. For example, a weather API provides data, but it does not decide how that data is used. Tools can be used to carry out actions or to gather additional context that informs future decisions.

Agents bring these components together to maintain awareness, make decisions, and act dynamically in pursuit of their goals.

### Workflows

As objectives grow in complexity, they need to be broken down into manageable steps. That's where workflows come in.

__Workflows structure complex objectives into sequences of steps, coordinating tasks across people or systems to reach a goal efficiently.__

Imagine you're launching a new feature on your business website. If it's a simple update, you might go from idea to production in a few hours. But for more complex initiatives, the process might include:

- Requirement gathering
- Design and architecture
- Implementation
- Testing
- Deployment

A few important observations:

- Each step may contain subtasks.
- Different specialists may own different phases.
- Progress isn't always linear. Bugs found during testing may send you back to implementation.

### Agents + Workflows

While workflows can function purely as predetermined sequences, integrating agents adds dynamic decision-making and adaptability, enabling more intelligent and autonomous process management.

Agents, workflows, and their underlying components are all highly composable:

- Agents can call multiple tools
- Tools may encapsulate agent-like behaviors in narrow scopes
- Workflows can sequence agents, tools, and other workflows
- Agents themselves may internally run workflows

## Meet Microsoft Agent Framework

Microsoft Agent Framework is a comprehensive set of .NET libraries that reduces the complexity of agent development. Whether you're building a simple chatbot or orchestrating multiple AI agents in complex workflows, Microsoft Agent Framework provides the tools you need to:

- __Build__ agents with minimal boilerplate code
- __Orchestrate__ multi-agent workflows with ease
- __Host__ and deploy agents using familiar .NET patterns
- __Monitor__ and observe agent behavior in production

### Built on Proven Foundations

Microsoft Agent Framework leverages established technologies to simplify agent development for .NET developers:

- __Semantic Kernel__ – Provides robust orchestration
- __AutoGen__ – Enables advanced multi-agent collaboration and cutting-edge research-driven techniques
- __Microsoft.Extensions.AI__ – Delivers standardized AI building blocks for .NET.

## Start Simple: Build Your First Agent in Minutes

### Step 0: Configure prerequisites

- .NET 9 SDK or greater
- A GitHub Personal Access Token (PAT) with `models` scope

### Step 1: Set Up Your Project

```bash
dotnet new console -o HelloWorldAgents
cd HelloWorldAgents
dotnet add package Microsoft.Agents.AI --prerelease
dotnet add package OpenAI
dotnet add package Microsoft.Extensions.AI.OpenAI --prerelease
dotnet add package Microsoft.Extensions.AI
```

### Step 2: Write Your Agent

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

IChatClient chatClient =
    new ChatClient(
            "gpt-4o-mini",
            new ApiKeyCredential(Environment.GetEnvironmentVariable("GITHUB_TOKEN")!),
            new OpenAIClientOptions { Endpoint = new Uri("https://models.github.ai/inference") })
        .AsIChatClient();

AIAgent writer = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "Writer",
        Instructions = "Write stories that are engaging and creative."
    });

AgentRunResponse response = await writer.RunAsync("Write a short story about a haunted house.");

Console.WriteLine(response.Text);
```

### The Power of Abstraction

The `ChatClientAgent` implementation of `AIAgent` accepts any `IChatClient`, allowing you to easily choose between providers such as:

- OpenAI
- Azure OpenAI
- Foundry Local
- Ollama
- GitHub Models
- Many others

## Scale Up: Orchestrate Multiple Agents

### Adding Specialized Agents

```csharp
// Create a specialized editor agent
AIAgent editor = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "Editor",
        Instructions = "Make the story more engaging, fix grammar, and enhance the plot."
    });
```

### Building Workflows

```bash
dotnet add package Microsoft.Agents.AI.Workflows --prerelease
```

```csharp
// Create a workflow that connects writer to editor
Workflow workflow =
    AgentWorkflowBuilder
        .BuildSequential(writer, editor);

AIAgent workflowAgent = await workflow.AsAgentAsync();

AgentRunResponse workflowResponse =
    await workflowAgent.RunAsync("Write a short story about a haunted house.");

Console.WriteLine(workflowResponse.Text);
```

### All Types of Workflows

- __Sequential__: Agents execute in order, passing results along the chain.
- __Concurrent__: Multiple agents work in parallel, addressing different aspects of a task simultaneously.
- __Handoff__: Responsibility shifts between agents based on context or outcomes.
- __GroupChat__: Agents collaborate in a shared, real-time conversational space.

## Empower Agents with Tools

### Creating Agent Tools

```csharp
[Description("Gets the author of the story.")]
string GetAuthor() => "Jack Torrance";

[Description("Formats the story for display.")]
string FormatStory(string title, string author, string story) =>
    $"Title: {title}\nAuthor: {author}\n\n{story}";
```

### Connecting Tools to Agents

```csharp
AIAgent writer = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "Writer",
        Instructions = "Write stories that are engaging and creative.",
        ChatOptions = new ChatOptions
        {
            Tools = [
                AIFunctionFactory.Create(GetAuthor),
                AIFunctionFactory.Create(FormatStory)
            ],
        }
    });
```

## Deploy with Confidence: Hosting Made Simple

### Minimal Web API Integration

```csharp
builder.AddAIAgent("Writer", (sp, key) =>
{
    var chatClient = sp.GetRequiredService<IChatClient>();

    return new ChatClientAgent(
        chatClient,
        name: key,
        instructions: "You are a creative writing assistant...",
        tools: [
            AIFunctionFactory.Create(GetAuthor),
            AIFunctionFactory.Create(FormatStory)
        ]
    );
});
```

## Observe and Improve: Built-in Monitoring

### OpenTelemetry Integration

```csharp
// Enhanced telemetry for all your agents
writer.WithOpenTelemetry();
editor.WithOpenTelemetry();
```

## Ensure Quality: Evaluation and Testing

Microsoft Agent Framework can integrate with Microsoft.Extensions.AI.Evaluations to help you build reliable, trustworthy agent systems:

- __Automated testing__ – Run evaluation suites as part of your CI/CD pipeline
- __Quality metrics__ – Measure relevance, coherence, and safety
- __Regression detection__ – Catch quality degradation before deployment
- __A/B testing__ – Compare different configurations

## Key Takeaways

- __Simple by Design__: Get started with just a few lines of code.
- __Scales with You__: Start with a single agent, then easily add workflows, tools, hosting, and monitoring.
- __Built on Proven Technology__: Brings together the best from AutoGen and Semantic Kernel.
- __Production Ready__: Deploy using familiar .NET patterns with built-in observability, evaluation, and hosting capabilities.

# Configuring Agents with Semantic Kernel Plugins

Source: https://learn.microsoft.com/en-us/semantic-kernel/frameworks/agent/agent-functions

## Functions and Plugins in Semantic Kernel

Function calling is a powerful tool that allows developers to add custom functionalities and expand the capabilities of AI applications. The Semantic Kernel Plugin architecture offers a flexible framework to support Function Calling. For an `Agent`, integrating Plugins and Function Calling is built on this foundational Semantic Kernel feature.

Once configured, an agent will choose when and how to call an available function, as it would in any usage outside of the `Agent Framework`.

## Adding Plugins to an Agent

Any Plugin available to an `Agent` is managed within its respective `Kernel` instance. This setup enables each `Agent` to access distinct functionalities based on its specific role.

Plugins can be added to the `Kernel` either before or after the `Agent` is created. The process of initializing Plugins follows the same patterns used for any Semantic Kernel implementation, allowing for consistency and ease of use in managing AI capabilities.

### C# Example

```csharp
// Factory method to produce an agent with a specific role.
// Could be incorporated into DI initialization.
ChatCompletionAgent CreateSpecificAgent(Kernel kernel, string credentials)
{
    // Clone kernel instance to allow for agent specific plug-in definition
    Kernel agentKernel = kernel.Clone();

    // Import plug-in from type
    agentKernel.ImportPluginFromType<StatelessPlugin>();

    // Import plug-in from object
    agentKernel.ImportPluginFromObject(new StatefulPlugin(credentials));

    // Create the agent
    return
        new ChatCompletionAgent()
        {
            Name = "<agent name>",
            Instructions = "<agent instructions>",
            Kernel = agentKernel,
            Arguments = new KernelArguments(
                new OpenAIPromptExecutionSettings()
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                })
        };
}
```

### Python Example - Method 1: Specify Plugins via the Constructor

```python
from semantic_kernel.agents import ChatCompletionAgent

# Create the Chat Completion Agent instance by specifying a list of plugins
agent = ChatCompletionAgent(
    service=AzureChatCompletion(),
    instructions="<instructions>",
    plugins=[SamplePlugin()]
)
```

### Python Example - Method 2: Configure the Kernel Manually

```python
from semantic_kernel.agents import ChatCompletionAgent
from semantic_kernel.connectors.ai import FunctionChoiceBehavior
from semantic_kernel.connectors.ai.open_ai import AzureChatCompletion, AzureChatPromptExecutionSettings
from semantic_kernel.functions import KernelFunctionFromPrompt
from semantic_kernel.kernel import Kernel

# Create the instance of the Kernel
kernel = Kernel()

# Add the chat completion service to the Kernel
kernel.add_service(AzureChatCompletion())

# Get the AI service settings
settings = kernel.get_prompt_execution_settings_from_service_id()

# Configure the function choice behavior to auto invoke kernel functions
settings.function_choice_behavior = FunctionChoiceBehavior.Auto()

# Add the Plugin to the Kernel
kernel.add_plugin(SamplePlugin(), plugin_name="<plugin name>")

# Create the agent
agent = ChatCompletionAgent(
    kernel=kernel,
    name=<agent name>,
    instructions=<agent instructions>,
    arguments=KernelArguments(settings=settings),
)
```

### Java Example

```java
var chatCompletion = OpenAIChatCompletion.builder()
    .withModelId("<model-id>")
    .withOpenAIAsyncClient(new OpenAIClientBuilder()
            .credential(new AzureKeyCredential("<api-key>"))
            .endpoint("<endpoint>")
            .buildAsyncClient())
    .build();

Kernel kernel = Kernel.builder()
    .withAIService(ChatCompletionService.class, chatCompletion)
    .withPlugin(KernelPluginFactory.createFromObject(new SamplePlugin(), "<plugin name>"))
    .build();

var agent = ChatCompletionAgent.builder()
    .withKernel(kernel)
    .withName("<agent name>")
    .withInstructions("<agent instructions>")
    .build();
```

## Adding Functions to an Agent

A Plugin is the most common approach for configuring Function Calling. However, individual functions can also be supplied independently including prompt functions.

### C# Example

```csharp
ChatCompletionAgent CreateSpecificAgent(Kernel kernel)
{
    Kernel agentKernel = kernel.Clone();

    var functionFromMethod = agentKernel.CreateFunctionFromMethod(StatelessPlugin.AStaticMethod);
    var functionFromPrompt = agentKernel.CreateFunctionFromPrompt("<your prompt instructions>");

    agentKernel.ImportPluginFromFunctions("my_plugin", [functionFromMethod, functionFromPrompt]);

    return
        new ChatCompletionAgent()
        {
            Name = "<agent name>",
            Instructions = "<agent instructions>",
            Kernel = agentKernel,
            Arguments = new KernelArguments(
                new OpenAIPromptExecutionSettings()
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
                })
        };
}
```

### Python Example

```python
from semantic_kernel.agents import ChatCompletionAgent
from semantic_kernel.connectors.ai import FunctionChoiceBehavior
from semantic_kernel.connectors.ai.open_ai import AzureChatCompletion, AzureChatPromptExecutionSettings
from semantic_kernel.functions import KernelFunctionFromPrompt
from semantic_kernel.kernel import Kernel

kernel = Kernel()
kernel.add_service(AzureChatCompletion())

settings = AzureChatPromptExecutionSettings()
settings.function_choice_behavior = FunctionChoiceBehavior.Auto()

kernel.add_function(
    plugin_name="<plugin_name>",
    function=KernelFunctionFromPrompt(
        function_name="<function_name>",
        prompt="<your prompt instructions>",
    )
)

agent = ChatCompletionAgent(
    kernel=kernel,
    name=<agent name>,
    instructions=<agent instructions>,
    arguments=KernelArguments(settings=settings),
)
```

## Limitations for Agent Function Calling

When directly invoking a `ChatCompletionAgent`, all Function Choice Behaviors are supported. However, when using an `OpenAIAssistant`, only Automatic Function Calling is currently available.

## Next steps

How to Stream Agent Responses

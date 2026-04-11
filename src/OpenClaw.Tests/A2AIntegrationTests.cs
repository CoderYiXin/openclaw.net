#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using A2A;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.A2A;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;
using Xunit;

namespace OpenClaw.Tests;

public sealed class A2AIntegrationTests
{
    [Fact]
    public void AgentCardFactory_Creates_DefaultSkill_When_NoneConfigured()
    {
        var factory = new OpenClawAgentCardFactory(Options.Create(CreateOptions()));

        var card = factory.Create("http://localhost:5000/a2a");

        Assert.Equal("TestAgent", card.Name);
        Assert.Equal("1.0.0", card.Version);
        Assert.Single(card.Skills!);
        Assert.Equal("general", card.Skills[0].Id);
        Assert.Equal("http://localhost:5000/a2a", Assert.Single(card.SupportedInterfaces!).Url);
    }

    [Fact]
    public async Task AgentHandler_ExecuteAsync_Completes_With_Bridged_Text()
    {
        var handler = new OpenClawA2AAgentHandler(
            Options.Create(CreateOptions()),
            new FakeExecutionBridge(),
            NullLogger<OpenClawA2AAgentHandler>.Instance);
        var queue = new AgentEventQueue();
        var context = new RequestContext
        {
            Message = new Message
            {
                Role = Role.User,
                Parts = [Part.FromText("Hello A2A")]
            },
            TaskId = "task-1",
            ContextId = "ctx-1",
            StreamingResponse = false
        };

        var events = new List<StreamResponse>();
        await handler.ExecuteAsync(context, queue, CancellationToken.None);
        queue.Complete();
        await foreach (var evt in queue)
            events.Add(evt);

        var completed = events.LastOrDefault(item => item.StatusUpdate?.Status.State == TaskState.Completed);
        Assert.NotNull(completed);
        Assert.Contains("bridge:Hello A2A", completed!.StatusUpdate!.Status.Message!.Parts![0].Text);
    }

    [Fact]
    public void AddOpenClawA2AServices_Registers_RequestHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<MafOptions>>(_ => Options.Create(CreateOptions()));
        services.AddOpenClawA2AServices();
        services.AddSingleton<IOpenClawA2AExecutionBridge>(new FakeExecutionBridge());

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<OpenClawA2AAgentHandler>());
        Assert.NotNull(provider.GetService<OpenClawAgentCardFactory>());
        Assert.NotNull(provider.GetService<ITaskStore>());
        Assert.NotNull(provider.GetService<IA2ARequestHandler>());
    }

    [Fact]
    public void MafServiceCollectionExtensions_Parses_A2A_Config()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MafOptions.SectionName}:EnableA2A"] = "true",
                [$"{MafOptions.SectionName}:A2APathPrefix"] = "/agents/a2a",
                [$"{MafOptions.SectionName}:A2AVersion"] = "2.0.0-beta",
                [$"{MafOptions.SectionName}:A2ASkills:0:Id"] = "search",
                [$"{MafOptions.SectionName}:A2ASkills:0:Name"] = "Web Search",
                [$"{MafOptions.SectionName}:A2ASkills:0:Tags:0"] = "web"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicrosoftAgentFrameworkExperiment(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MafOptions>>().Value;

        Assert.True(options.EnableA2A);
        Assert.Equal("/agents/a2a", options.A2APathPrefix);
        Assert.Equal("2.0.0-beta", options.A2AVersion);
        Assert.Single(options.A2ASkills);
        Assert.Equal("search", options.A2ASkills[0].Id);
        Assert.Equal("Web Search", options.A2ASkills[0].Name);
        Assert.Equal(["web"], options.A2ASkills[0].Tags);
    }

    private static MafOptions CreateOptions()
        => new()
        {
            AgentName = "TestAgent",
            AgentDescription = "Test agent for A2A integration tests.",
            EnableStreaming = true,
            EnableA2A = true,
            A2AVersion = "1.0.0"
        };

    private sealed class FakeExecutionBridge : IOpenClawA2AExecutionBridge
    {
        public async Task ExecuteStreamingAsync(
            OpenClawA2AExecutionRequest request,
            Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
            CancellationToken cancellationToken)
        {
            await onEvent(AgentStreamEvent.TextDelta($"bridge:{request.UserText}"), cancellationToken);
            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
        }
    }
}
#endif

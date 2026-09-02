using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for <see cref="ConfiguredModelProfileRegistry"/> covering the fix for
/// <see href="https://github.com/clawdotnet/openclaw.net/issues/166">issue #166</see>:
/// Dynamic Native LLM Provider Unavailable During Model Profile Construction.
/// </summary>
[Collection(DynamicProviderRegistryCollection.Name)]
public sealed class ConfiguredModelProfileRegistryTests
{
    [Fact]
    public void Constructor_WhenDynamicProvidersNotYetRegistered_DoesNotBuildRegistrations()
    {
        // Issue #166: The constructor used to call BuildRegistrations immediately during
        // DI resolution, before LoadPluginCompositionAsync had registered dynamic providers.
        // After the fix, the constructor defers profile building to SetDefaultProfileId().
        LlmClientFactory.ResetDynamicProviders();

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "custom-dynamic-provider",
                Model = "custom-model"
            },
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "dynamic-profile",
                        Provider = "custom-dynamic-provider",
                        Model = "custom-model"
                    }
                ]
            }
        };

        var providerRegistry = new LlmProviderRegistry();

        // The constructor no longer calls BuildRegistrations.
        var registry = new ConfiguredModelProfileRegistry(
            config,
            NullLogger<ConfiguredModelProfileRegistry>.Instance,
            providerRegistry);

        // Registrations should NOT be built yet — SetDefaultProfileId hasn't been called.
        Assert.Null(registry.DefaultProfileId);
        var statuses = registry.ListStatuses();
        Assert.NotNull(statuses);
        Assert.Empty(statuses);
    }

    [Fact]
    public void SetDefaultProfileId_BuildsRegistrationsAndSetsDefaultProfileId()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("custom-dynamic-provider", Substitute.For<IChatClient>());

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "custom-dynamic-provider",
                Model = "default-model"
            },
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "dynamic-profile",
                        Provider = "custom-dynamic-provider",
                        Model = "custom-model"
                    }
                ]
            }
        };

        var providerRegistry = new LlmProviderRegistry();
        var registry = new ConfiguredModelProfileRegistry(
            config,
            NullLogger<ConfiguredModelProfileRegistry>.Instance,
            providerRegistry);

        // DefaultProfileId should be null until SetDefaultProfileId is called.
        Assert.Null(registry.DefaultProfileId);

        // Act: Build registrations after providers are available.
        registry.SetDefaultProfileId();

        // Assert: Registrations are now built.
        Assert.NotNull(registry.DefaultProfileId);
        Assert.True(registry.TryGet("dynamic-profile", out var profile));
        Assert.NotNull(profile);
        Assert.Equal("custom-dynamic-provider", profile.ProviderId);
        Assert.Equal("custom-model", profile.ModelId);

        var statuses = registry.ListStatuses();
        Assert.NotEmpty(statuses);
        Assert.Contains(statuses, s => s.Id == "dynamic-profile");
    }

    [Fact]
    public void SetDefaultProfileId_ResolvesDynamicProviderRegisteredAfterConstruction()
    {
        // This is the core scenario from issue #166.
        //
        // Timeline (before fix):
        //   1. DI creates ConfiguredModelProfileRegistry → BuildRegistrations runs
        //      → TryResolveRegisteredClient fails (dynamic provider not registered yet)
        //      → falls back to LlmClientFactory.CreateChatClient → throws
        //
        // Timeline (after fix):
        //   1. DI creates ConfiguredModelProfileRegistry → constructor stores config only
        //   2. LoadPluginCompositionAsync registers dynamic providers into LlmProviderRegistry
        //   3. SetDefaultProfileId() → BuildRegistrations runs
        //      → TryResolveRegisteredClient succeeds (dynamic provider IS registered now)

        LlmClientFactory.ResetDynamicProviders();

        var dynamicClient = Substitute.For<IChatClient>();
        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "custom-dynamic-provider",
                Model = "custom-model"
            },
            Models = new ModelsConfig
            {
                DefaultProfile = "custom-profile",
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "custom-profile",
                        Provider = "custom-dynamic-provider",
                        Model = "custom-model"
                    }
                ]
            }
        };

        // Step 1: DI creates registry — constructor does NOT build registrations.
        var providerRegistry = new LlmProviderRegistry();
        var registry = new ConfiguredModelProfileRegistry(
            config,
            NullLogger<ConfiguredModelProfileRegistry>.Instance,
            providerRegistry);

        Assert.Null(registry.DefaultProfileId);
        Assert.Empty(registry.ListStatuses());

        // Step 2: Simulate LoadPluginCompositionAsync registering the dynamic provider.
        providerRegistry.RegisterDefault(
            new LlmProviderConfig
            {
                Provider = "custom-dynamic-provider",
                Model = "custom-model"
            },
            dynamicClient);

        // Step 3: Now build registrations — dynamic provider IS available.
        registry.SetDefaultProfileId();

        // Assert: The dynamic provider was successfully resolved.
        Assert.NotNull(registry.DefaultProfileId);
        Assert.Equal("custom-profile", registry.DefaultProfileId);

        Assert.True(registry.TryGet("custom-profile", out var profile));
        Assert.NotNull(profile);
        Assert.Equal("custom-dynamic-provider", profile.ProviderId);
        Assert.Equal("custom-model", profile.ModelId);

        var status = Assert.Single(registry.ListStatuses());
        Assert.Equal("custom-profile", status.Id);
        Assert.True(status.IsAvailable);
        Assert.Empty(status.ValidationIssues);
    }

    [Fact]
    public void SetDefaultProfileId_WithProviderRegistry_ResolvesRegisteredClient()
    {
        // Verify that TryResolveRegisteredClient finds the client from LlmProviderRegistry
        // when SetDefaultProfileId runs after provider registration.

        LlmClientFactory.ResetDynamicProviders();

        var registeredClient = Substitute.For<IChatClient>();
        var providerRegistry = new LlmProviderRegistry();
        providerRegistry.RegisterDefault(
            new LlmProviderConfig
            {
                Provider = "registered-provider",
                Model = "registered-model"
            },
            registeredClient);

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "registered-provider",
                Model = "registered-model"
            },
            Models = new ModelsConfig
            {
                DefaultProfile = "reg-profile",
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "reg-profile",
                        Provider = "registered-provider",
                        Model = "registered-model"
                    }
                ]
            }
        };

        var registry = new ConfiguredModelProfileRegistry(
            config,
            NullLogger<ConfiguredModelProfileRegistry>.Instance,
            providerRegistry);

        Assert.Null(registry.DefaultProfileId);

        registry.SetDefaultProfileId();

        Assert.Equal("reg-profile", registry.DefaultProfileId);
        Assert.True(registry.TryGet("reg-profile", out var profile));
        Assert.NotNull(profile);

        var status = Assert.Single(registry.ListStatuses());
        Assert.True(status.IsAvailable);
        Assert.Empty(status.ValidationIssues);
    }

    [Fact]
    public void SetDefaultProfileId_UnregisteredDynamicProvider_RecordsValidationIssue()
    {
        // When a profile references a provider that is NOT registered (neither built-in
        // nor dynamically), BuildRegistrations should record a validation issue rather
        // than throwing at startup.

        LlmClientFactory.ResetDynamicProviders();

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "openai",
                Model = "gpt-4.1"
            },
            Models = new ModelsConfig
            {
                DefaultProfile = "missing-provider-profile",
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "missing-provider-profile",
                        Provider = "completely-unknown-provider",
                        Model = "unknown-model"
                    }
                ]
            }
        };

        var providerRegistry = new LlmProviderRegistry();
        var registry = new ConfiguredModelProfileRegistry(
            config,
            NullLogger<ConfiguredModelProfileRegistry>.Instance,
            providerRegistry);

        registry.SetDefaultProfileId();

        // The profile should exist but with validation issues since the provider
        // cannot be resolved (not registered, not a built-in known to LlmClientFactory).
        var status = Assert.Single(registry.ListStatuses());
        Assert.Equal("missing-provider-profile", status.Id);
        // It may be unavailable due to unregistered provider.
        // The key point: this should NOT throw — it should surface through
        // validation issues so startup can continue (or fail with an informative
        // error at the MarkDefault check, not here).
        Assert.False(status.IsAvailable);
        Assert.NotEmpty(status.ValidationIssues);
    }

    [Fact]
    public void SetDefaultProfileId_WhenCalledMultipleTimes_IsIdempotent()
    {
        // Ensure calling SetDefaultProfileId more than once does not corrupt state.
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("stable-provider", Substitute.For<IChatClient>());

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "stable-provider",
                Model = "stable-model"
            },
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "stable-profile",
                        Provider = "stable-provider",
                        Model = "stable-model"
                    }
                ]
            }
        };

        var providerRegistry = new LlmProviderRegistry();
        var registry = new ConfiguredModelProfileRegistry(
            config,
            NullLogger<ConfiguredModelProfileRegistry>.Instance,
            providerRegistry);

        registry.SetDefaultProfileId();
        var firstDefaultId = registry.DefaultProfileId;
        var firstCount = registry.ListStatuses().Count;

        // Call again.
        registry.SetDefaultProfileId();

        // Should be idempotent: same default ID, no duplicate registrations.
        Assert.Equal(firstDefaultId, registry.DefaultProfileId);
        Assert.Equal(firstCount, registry.ListStatuses().Count);
    }

    [Fact]
    public void SetDefaultProfileId_WithoutProfilesConfigured_CreatesImplicitDefaultFromLlmSection()
    {
        // When no explicit profiles are configured, the registry should create an
        // implicit default profile from the Llm section — but only after
        // SetDefaultProfileId is called, not during construction.

        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("builtin-fake", Substitute.For<IChatClient>());

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "builtin-fake",
                Model = "default-model"
            }
        };

        var registry = new ConfiguredModelProfileRegistry(
            config,
            NullLogger<ConfiguredModelProfileRegistry>.Instance);

        // Before SetDefaultProfileId, no registrations.
        Assert.Null(registry.DefaultProfileId);
        Assert.Empty(registry.ListStatuses());

        registry.SetDefaultProfileId();

        // After SetDefaultProfileId, implicit default profile created.
        Assert.NotNull(registry.DefaultProfileId);
        var status = Assert.Single(registry.ListStatuses());
        Assert.True(status.IsImplicit);
        Assert.Equal("default-model", status.ModelId);
    }
}

using Microsoft.Extensions.Configuration;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayBootstrapExtensionsTests
{
    [Fact]
    public void LoadGatewayConfig_ConfiguredToolRootsReplaceWildcardDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenClaw:BindAddress"] = "0.0.0.0",
                ["OpenClaw:Tooling:AllowShell"] = "false",
                ["OpenClaw:Tooling:AllowedReadRoots:0"] = "/app/workspace",
                ["OpenClaw:Tooling:AllowedWriteRoots:0"] = "/app/workspace",
                ["OpenClaw:Plugins:Enabled"] = "false"
            })
            .Build();

        var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

        Assert.Equal(["/app/workspace"], config.Tooling.AllowedReadRoots);
        Assert.Equal(["/app/workspace"], config.Tooling.AllowedWriteRoots);
        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind: true);
    }
}

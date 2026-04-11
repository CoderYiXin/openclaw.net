#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using A2A;
using A2A.AspNetCore;
using Microsoft.Extensions.Options;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

namespace OpenClaw.Gateway.A2A;

internal static class A2AEndpointExtensions
{
    public static void MapOpenClawA2AEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var options = app.Services.GetRequiredService<IOptions<MafOptions>>().Value;
        if (!options.EnableA2A)
            return;

        var pathPrefix = NormalizePathPrefix(options.A2APathPrefix);
        var requestHandler = app.Services.GetRequiredService<IA2ARequestHandler>();
        var cardFactory = app.Services.GetRequiredService<OpenClawAgentCardFactory>();
        var publicBase = $"http://{startup.Config.BindAddress}:{startup.Config.Port}";
        var agentCard = cardFactory.Create(publicBase + pathPrefix);

        app.MapHttpA2A(requestHandler, agentCard, pathPrefix);
        app.MapWellKnownAgentCard(agentCard, pathPrefix);
        app.Logger.LogInformation("A2A endpoints enabled at {PathPrefix}.", pathPrefix);
    }

    public static void UseOpenClawA2AAuth(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var options = app.Services.GetRequiredService<IOptions<MafOptions>>().Value;
        if (!options.EnableA2A)
            return;

        var pathPrefix = NormalizePathPrefix(options.A2APathPrefix);

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(pathPrefix, StringComparison.OrdinalIgnoreCase) ||
                ctx.Request.Path.StartsWithSegments("/.well-known", StringComparison.OrdinalIgnoreCase))
            {
                if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (!runtime.Operations.ActorRateLimits.TryConsume(
                        "ip",
                        EndpointHelpers.GetRemoteIpKey(ctx),
                        "a2a_http",
                        out _))
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }
            }

            await next(ctx);
        });
    }

    private static string NormalizePathPrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "/a2a";

        return value.StartsWith('/') ? value.TrimEnd('/') : "/" + value.TrimEnd('/');
    }
}
#endif

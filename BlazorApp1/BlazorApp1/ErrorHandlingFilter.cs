using Azure;
using Microsoft.AspNetCore.SignalR;

namespace BlazorApp1;

public class ErrorHandlingFilter : IHubFilter
{
public async ValueTask<object> InvokeMethodAsync(
    HubInvocationContext invocationContext,
    Func<HubInvocationContext, ValueTask<object>> next)
{
    try
    {
        return await next(invocationContext);
    }
    catch (RequestFailedException ex) when (ex.ErrorCode == "string_above_max_length")
    {
        await invocationContext.Hub.Clients.Caller.SendAsync(
            "ReceiveMessage",
            "System",
            "Error técnico: Por favor formula tu solicitud de nuevo"
        );
        return null;
    }
}
}

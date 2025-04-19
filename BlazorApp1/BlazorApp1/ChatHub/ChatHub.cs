using Microsoft.AspNetCore.SignalR;
using Azure;
using Azure.AI.Inference;
using System.Text.Json;

namespace Chat.AI.ChatHub;

public class ChatHub : Hub
{
    private readonly ChatCompletionsClient _chatClient;
    private readonly ChatCompletionsToolDefinition _toolDefinition;
    private readonly string _modelName = "gpt-4o";

    public ChatHub(IConfiguration config)
    {
        var endpoint = new Uri("");
        var key = "";

        _chatClient = new ChatCompletionsClient(endpoint, new AzureKeyCredential(key), new AzureAIInferenceClientOptions());

        var functionDef = new FunctionDefinition("get_future_temperature")
        {
            Description = "Get future temperature for vacation planning",
            Parameters = BinaryData.FromObjectAsJson(new
            {
                Type = "object",
                Properties = new
                {
                    LocationName = new
                    {
                        Type = "string",
                        Description = "Location name for weather info"
                    },
                    DaysInAdvance = new
                    {
                        Type = "integer",
                        Description = "How many days ahead to check"
                    }
                }
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        };

        _toolDefinition = new ChatCompletionsToolDefinition(functionDef);
    }

    public async Task SendMessage(string user, string message)
    {
        var requestOptions = new ChatCompletionsOptions()
        {
            Messages =
            {
                new ChatRequestSystemMessage("You are a helpful assistant."),
                new ChatRequestUserMessage(message),
            },
            Tools = { _toolDefinition },
            Model = _modelName
        };

        Response<ChatCompletions> response = await _chatClient.CompleteAsync(requestOptions);

        string reply = response.Value.Content;

        // Optional: if tool call exists, emulate function execution
        if (response.Value.ToolCalls.Count > 0)
        {
            var toolCall = response.Value.ToolCalls[0];

            var followupOptions = new ChatCompletionsOptions()
            {
                Tools = { _toolDefinition },
                Model = _modelName,
            };
            foreach (var msg in requestOptions.Messages)
                followupOptions.Messages.Add(msg);

            followupOptions.Messages.Add(new ChatRequestAssistantMessage(response.Value));
            followupOptions.Messages.Add(new ChatRequestToolMessage(toolCall.Id, "31 celsius"));

            var followupResponse = await _chatClient.CompleteAsync(followupOptions);
            reply = followupResponse.Value.Content;
        }

        // Enviar mensaje al cliente con el nombre "Assistant"
        await Clients.All.SendAsync("ReceiveMessage", "Assistant", reply);
    }
}

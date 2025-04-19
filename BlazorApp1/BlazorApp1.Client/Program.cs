using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;  // Necesario para SignalR en el cliente

var builder = WebAssemblyHostBuilder.CreateDefault(args);


await builder.Build().RunAsync();

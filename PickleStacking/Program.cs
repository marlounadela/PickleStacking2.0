using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PickleStacking;
using PickleStacking.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<StackingService>();
builder.Services.AddScoped<PlayerService>();
builder.Services.AddScoped<CourtService>();
builder.Services.AddScoped<QueueService>();
builder.Services.AddScoped<SessionService>();

await builder.Build().RunAsync();
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Clocktail.Mcp;

// stdio is the protocol: every log line goes to stderr, never to stdout.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<Card>();
builder.Services.AddSingleton<Residency>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Residency>());
builder.Services.AddSingleton<LiveWatch>();
builder.Services.AddMcpServer(o => o.ServerInstructions = ManualTools.Instructions)
    .WithStdioServerTransport().WithTools<TuningTools>().WithTools<SettingsTools>().WithTools<LiveTools>().WithTools<ManualTools>();
_ = Task.Run(UpdateCheck.Latest);   // ask npm once at start, so the first card_report need not wait
await builder.Build().RunAsync();

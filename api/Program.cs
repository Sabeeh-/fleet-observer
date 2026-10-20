using System.Text.Json.Serialization;
using Api.Fleet;

var builder = WebApplication.CreateBuilder(args);

var fleetOptions = builder.Configuration.GetSection("Fleet").Get<FleetOptions>() ?? new FleetOptions();
var isTesting = builder.Environment.IsEnvironment("Testing");

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(fleetOptions);
builder.Services.AddSingleton(TimeProvider.System);
if (isTesting)
{
    builder.Services.AddSingleton<ScriptedFaultSource>();
    builder.Services.AddSingleton<IFaultSource>(sp => sp.GetRequiredService<ScriptedFaultSource>());
}
else
{
    builder.Services.AddSingleton<IFaultSource, RandomFaultSource>();
}
builder.Services.AddSingleton<Observer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Observer>());

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/fleet", (Observer observer, TimeProvider time) =>
    Results.Ok(observer.Current with { ServerNow = time.GetUtcNow() }));

app.MapPost("/api/fleet/start", async (Observer observer) => ToHttp(await observer.StartFleetAsync()));
app.MapPost("/api/fleet/stop", async (Observer observer) => ToHttp(await observer.StopFleetAsync()));
app.MapPost("/api/workers/{id}/restart", async (string id, Observer observer) => ToHttp(await observer.RestartAsync(id)));
app.MapPost("/api/incidents/{id:int}/ack", async (int id, Observer observer) => ToHttp(await observer.AcknowledgeAsync(id)));

if (isTesting)
{
    // Deterministic fault control for automated tests only (SPEC: simulated vs real).
    app.MapPost("/api/test/workers/{id}/fault", (string id, FaultRequest request, ScriptedFaultSource faults) =>
    {
        if (!FleetState.WorkerIds.Contains(id)) return Results.NotFound();
        faults.Set(id, request.Mode);
        return Results.NoContent();
    });

    app.MapPost("/api/test/reset", async (Observer observer, ScriptedFaultSource faults) =>
    {
        faults.Clear();
        return ToHttp(await observer.ResetAsync());
    });
}

app.Run();

static IResult ToHttp(CommandResult result) => result switch
{
    CommandResult.Ok => Results.NoContent(),
    CommandResult.NotFound => Results.NotFound(),
    _ => Results.Conflict(),
};

record FaultRequest(FaultMode Mode);

// Exposed so WebApplicationFactory<Program> can reach the entry point from api.tests.
public partial class Program;

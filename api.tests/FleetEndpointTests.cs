using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Api.Tests;

public class TestingApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
}

public class FleetEndpointTests(TestingApiFactory factory) : IClassFixture<TestingApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed record Snapshot(long Version, bool Running, Api.Fleet.WorkerView[] Workers, Api.Fleet.IncidentView[] Incidents);

    private static async Task<Snapshot> GetFleet(HttpClient client) =>
        (await client.GetFromJsonAsync<Snapshot>("/api/fleet", Json))!;

    private static async Task<Snapshot> WaitFor(HttpClient client, Func<Snapshot, bool> condition, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var snapshot = await GetFleet(client);
            if (condition(snapshot)) return snapshot;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Wrong_answer_fault_opens_incident_and_ack_plus_recovery_resolves_it()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/test/reset", null)).StatusCode);

        var initial = await GetFleet(client);
        Assert.False(initial.Running);
        Assert.Equal(5, initial.Workers.Length);

        await client.PostAsync("/api/fleet/start", null);
        await WaitFor(client, s => s.Workers.All(w => w.Status == Api.Fleet.WorkerStatus.Healthy));

        await client.PostAsJsonAsync("/api/test/workers/worker-2/fault", new { mode = "WrongAnswer" }, Json);
        var faulty = await WaitFor(client, s => s.Incidents.Length == 1);
        Assert.Equal(("worker-2", Api.Fleet.IncidentKind.WrongAnswer), (faulty.Incidents[0].WorkerId, faulty.Incidents[0].Kind));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/incidents/{faulty.Incidents[0].Id}/ack", null)).StatusCode);
        await client.PostAsJsonAsync("/api/test/workers/worker-2/fault", new { mode = "None" }, Json);

        await WaitFor(client, s => s.Incidents.Length == 0);
        await client.PostAsync("/api/fleet/stop", null);
    }

    [Fact]
    public async Task Unknown_ids_return_not_found()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/incidents/9999/ack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/test/workers/worker-9/fault", new { mode = "Slow" }, Json)).StatusCode);
    }

    [Fact]
    public async Task Test_endpoints_are_absent_outside_testing_environment()
    {
        using var devFactory = new WebApplicationFactory<Program>();
        var client = devFactory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/test/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/fleet")).StatusCode);
    }
}

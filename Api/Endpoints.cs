using MissionControl.Agents;
using MissionControl.Communication;
using MissionControl.Config;
using MissionControl.Missions;
using MissionControl.Orchestrator;
using MissionControl.Tasks;

namespace MissionControl.Api;

public static class Endpoints
{
    public static void MapMissionControlApi(this WebApplication app)
    {
        // Agents
        app.MapGet("/api/agents", async (AgentRegistry registry) =>
            Results.Ok(await registry.GetAllAsync()));

        app.MapPost("/api/agents", async (AgentDefinition agent, AgentRegistry registry) =>
            Results.Created($"/api/agents/{agent.Id}", await registry.CreateAsync(agent)));

        app.MapPut("/api/agents/{id}", async (string id, AgentDefinition agent, AgentRegistry registry) =>
        {
            agent.Id = id;
            return Results.Ok(await registry.UpdateAsync(agent));
        });

        app.MapDelete("/api/agents/{id}", async (string id, AgentRegistry registry) =>
        {
            await registry.DeleteAsync(id);
            return Results.NoContent();
        });

        // Tasks
        app.MapGet("/api/tasks", async (TaskManager taskManager) =>
            Results.Ok(await taskManager.GetAllAsync()));

        app.MapGet("/api/tasks/{id}", async (string id, TaskManager taskManager) =>
        {
            var task = await taskManager.GetByIdAsync(id);
            return task is not null ? Results.Ok(task) : Results.NotFound();
        });

        app.MapPost("/api/tasks", async (TaskItem task, TaskManager taskManager) =>
            Results.Created($"/api/tasks/{task.Id}", await taskManager.CreateAsync(task)));

        app.MapPut("/api/tasks/{id}", async (string id, TaskItem task, TaskManager taskManager) =>
        {
            task.Id = id;
            return Results.Ok(await taskManager.UpdateAsync(task));
        });

        app.MapDelete("/api/tasks/{id}", async (string id, TaskManager taskManager) =>
        {
            await taskManager.DeleteAsync(id);
            return Results.NoContent();
        });

        // Missions
        app.MapGet("/api/missions", async (MissionOrchestrator orchestrator) =>
            Results.Ok(await orchestrator.GetAllAsync()));

        app.MapPost("/api/missions", async (Mission mission, MissionOrchestrator orchestrator) =>
            Results.Created($"/api/missions/{mission.Id}", await orchestrator.CreateAsync(mission)));

        // Inbox
        app.MapGet("/api/inbox", async (CommunicationManager comms) =>
            Results.Ok(await comms.GetInboxAsync()));

        // Decisions
        app.MapGet("/api/decisions", async (CommunicationManager comms) =>
            Results.Ok(await comms.GetDecisionsAsync()));

        app.MapPost("/api/decisions/{id}", async (string id, DecisionResponse response, CommunicationManager comms) =>
        {
            await comms.ResolveDecisionAsync(id, response.Response);
            return Results.Ok();
        });

        // Activity Log
        app.MapGet("/api/activity-log", async (CommunicationManager comms) =>
            Results.Ok(await comms.GetActivityLogAsync()));

        // Daemon control
        app.MapGet("/api/daemon/status", (AgentRunner runner) =>
        {
            var runs = runner.GetActiveRunsAsync().GetAwaiter().GetResult();
            return Results.Ok(new { status = "running", activeRuns = runs.Count });
        });

        app.MapPost("/api/emergency-stop", async (AgentRunner runner) =>
        {
            await runner.KillAllRunsAsync();
            return Results.Ok(new { message = "All agents stopped" });
        });
    }
}

public record DecisionResponse(string Response);

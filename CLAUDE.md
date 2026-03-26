# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build              # Build the project
dotnet run                # Start daemon + API + Blazor dashboard on port 5100
```

Dashboard: `http://localhost:5100` | API: `http://localhost:5100/api/*`

There are no tests in this project currently.

## Claude Plugins

### Global (User-Level)
- **superpowers@claude-plugins-official** (v5.0.6) — Core skills: TDD, debugging, brainstorming, planning workflows
- **context7@claude-plugins-official** — MCP server (`@upstash/context7-mcp`) for retrieving up-to-date library documentation
- **frontend-design@claude-plugins-official** — Frontend design skill for UI/UX implementation
- **mobile@paddo-tools** (v2.1.1) — AI-driven mobile testing with Appium

### Project-Scoped
- **playwright@claude-plugins-official** — Playwright MCP server for browser automation

## Architecture

MissionControl is an AI agent orchestration system for test automation. It coordinates multiple Claude Code agents to write, debug, and maintain tests. Built with .NET 8, ASP.NET Core Minimal API, and Blazor Server.

**Single-project solution** — all code lives in `MissionControl.csproj`.

### Core Layers

- **Orchestrator/** — The engine. `Dispatcher` is a `BackgroundService` that polls every N seconds for ready tasks, spawns Claude Code subprocesses via `AgentRunner`, and reconciles mission state. `PromptBuilder` assembles layered markdown prompts (agent identity → task details → guardrails → retry/mission context).
- **Agents/** — `AgentDefinition` models (role, instructions, capabilities, working directory) and `AgentRegistry` for CRUD. Six built-in agents are seeded on startup (appium/playwright/api test writers, page-object builder, test-debugger, test-reviewer).
- **Tasks/** — `TaskItem` with Kanban statuses (NotStarted → InProgress → Done/Failed/Skipped), priority-based dispatch, dependency blocking via `BlockedBy`, and retry tracking.
- **Missions/** — Groups of tasks. `MissionOrchestrator.ReconcileAsync()` marks missions Completed or Stalled based on task outcomes.
- **Communication/** — `CommunicationManager` handles inbox messages, human decision requests (blocking — dispatcher skips tasks with pending decisions), and an append-only activity log.
- **Data/** — `JsonDataStore` provides file-based JSON persistence with per-file `SemaphoreSlim` locks. `MutateAsync` does atomic read-modify-write. `DataPaths` centralizes all file paths under `data/`.
- **Api/** — All REST endpoints defined in `Api/Endpoints.cs` via Minimal API (`MapGet`/`MapPost`/`MapPut`/`MapDelete`).
- **Dashboard/** — Blazor Server UI. `DashboardDataService` is the facade for all data access. Pages: TaskBoard (Kanban), Agents, Missions, ActivityFeed, Decisions, Inbox, Settings.
- **Config/** — `MissionControlConfig` model (max parallel agents, dispatch interval, task timeout, Claude Code path, working directory). Loaded from `data/config.json`.

### Key Patterns

- All services registered as **singletons** in `Program.cs`.
- Agent execution: `Dispatcher` → `AgentRunner.RunTaskAsync()` → spawns `claude --dangerously-skip-permissions -p "<prompt>"` as a subprocess with timeout.
- Data files are seeded on startup if missing (agents, tasks, missions, inbox, decisions, activity-log, active-runs, config).
- Fire-and-forget via `Task.Run()` for agent spawning; active runs tracked in `active-runs.json`.
- Emergency stop kills all running agent processes (`POST /api/emergency-stop`).

### Data Flow

Task created → Dispatcher finds it dispatchable (correct status, dependencies met, no pending decisions) → AgentRunner builds prompt and spawns Claude Code → on completion, task status updated, report posted to inbox, activity logged → MissionOrchestrator reconciles mission state.

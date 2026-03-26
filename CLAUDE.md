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

## Project Structure

```
MissionControl/
├── Program.cs                          # Entry point, DI registration, agent seeding
├── MissionControl.csproj
├── Agents/
│   ├── AgentDefinition.cs              # Agent model (id, name, role, instructions, AgentType)
│   ├── AgentRegistry.cs                # CRUD on agents.json
│   └── AgentType.cs                    # Enum: Coordinator, Gate, Analyst, Builder
├── Api/
│   └── Endpoints.cs                    # All REST endpoints (Minimal API)
├── Communication/
│   ├── ActivityEvent.cs                # Activity log entry model
│   ├── CommunicationManager.cs         # Inbox, decisions, activity log operations
│   ├── Decision.cs                     # Human decision request model
│   └── InboxMessage.cs                 # Inbox message model
├── Config/
│   └── MissionControlConfig.cs         # Runtime config model (loaded from data/config.json)
├── Dashboard/
│   ├── Services/
│   │   └── DashboardDataService.cs     # Facade for all data access from UI
│   ├── Components/
│   │   ├── Layout/
│   │   │   ├── App.razor               # Root component
│   │   │   ├── MainLayout.razor        # Shell layout with sidebar
│   │   │   ├── NavMenu.razor           # Sidebar navigation
│   │   │   └── Routes.razor            # Router config
│   │   └── Pages/
│   │       ├── TaskBoard.razor         # Kanban board (home page)
│   │       ├── Agents.razor            # Agent management
│   │       ├── Missions.razor          # Mission management
│   │       ├── ActivityFeed.razor      # Activity log viewer
│   │       ├── Decisions.razor         # Human decision queue
│   │       ├── Inbox.razor             # Agent message inbox
│   │       └── Settings.razor          # Config editor
│   └── wwwroot/css/
│       └── app.css                     # All styles (dark theme, void aesthetic)
├── Data/
│   ├── DataPaths.cs                    # Centralized file paths under data/
│   └── JsonDataStore.cs               # JSON file persistence with semaphore locks
├── Missions/
│   ├── Mission.cs                      # Mission model + MissionHistoryEntry
│   ├── MissionOrchestrator.cs          # Create, reconcile, history tracking
│   └── MissionStatus.cs               # Enum: Running, Completed, Stalled
├── Orchestrator/
│   ├── ActiveRun.cs                    # Active agent process tracking model
│   ├── AgentRunner.cs                  # Spawns Claude Code, handles Coordinator/Gate/Builder output
│   ├── CoordinatorSubtaskDefinition.cs # DTO for parsing Overseer JSON output
│   ├── Dispatcher.cs                   # BackgroundService: dispatch loop, validation routing, parent reconciliation
│   └── PromptBuilder.cs               # Assembles agent-type-specific prompts
└── Tasks/
    ├── SubTask.cs                      # Simple checklist item model
    ├── TaskItem.cs                     # Task model (status, parent/child, validation fields)
    ├── TaskManager.cs                  # CRUD, dispatch filtering, batch creation
    ├── TaskPriority.cs                 # Enum: Low, Normal, High, Critical
    └── TaskStatus.cs                   # Enum: NotStarted, InProgress, Done, Failed, Skipped, AwaitingValidation
```

## Architecture

MissionControl is an AI agent orchestration system for test automation. It coordinates multiple Claude Code agents to write, debug, and maintain tests. Built with .NET 8, ASP.NET Core Minimal API, and Blazor Server.

**Single-project solution** — all code lives in `MissionControl.csproj`.

### Core Layers

- **Orchestrator/** — The engine. `Dispatcher` is a `BackgroundService` that polls every N seconds for ready tasks, spawns Claude Code subprocesses via `AgentRunner`, and reconciles mission state. Three dispatch passes per tick: (1) normal tasks, (2) AwaitingValidation → Oracle, (3) parent task reconciliation. `PromptBuilder` assembles agent-type-specific prompts (Coordinator gets decomposition instructions, Gate gets validation template, Analyst gets read-only guardrails).
- **Agents/** — `AgentDefinition` models (role, instructions, capabilities, AgentType, working directory) and `AgentRegistry` for CRUD. Nine built-in agents seeded on startup: 6 Builders (appium/playwright/api test writers, page-object builder, test-debugger, test-reviewer), 1 Coordinator (overseer), 1 Gate (oracle), 1 Analyst (researcher).
- **Tasks/** — `TaskItem` with Kanban statuses (NotStarted → InProgress → AwaitingValidation → Done/Failed/Skipped), priority-based dispatch, dependency blocking via `BlockedBy`, parent/child relationships via `ParentTaskId`, validation tracking (`ValidationResult`, `ValidationFeedback`), and retry tracking.
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

### Orchestration Paths

- **Path A (Coordinator):** Task assigned to Overseer → decomposes into JSON subtask array → Dispatcher creates child TaskItems with `ParentTaskId` → children dispatched normally → parent completes when all children Done.
- **Path B (Builder + Gate):** Builder completes task → status set to `AwaitingValidation` → Dispatcher routes to Oracle → Oracle outputs PASS/FAIL → PASS=Done, FAIL=Failed with feedback for retry.
- **Path C (Analyst):** Researcher runs with read-only guardrails → Done on completion (no validation gate).

### Data Flow

Task created → Dispatcher finds it dispatchable (correct status, dependencies met, no pending decisions) → AgentRunner builds prompt and spawns Claude Code → on completion, agent-type-specific handling (Coordinator creates subtasks, Builder routes to validation if applicable, Gate issues verdict) → task status updated, report posted to inbox, activity logged → Dispatcher reconciles parent tasks → MissionOrchestrator reconciles mission state.

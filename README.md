# Mission Control — AI Agent Orchestration for Test Automation

Mission Control is an AI agent orchestration system that coordinates test writing, debugging, and maintenance across the Accelerator test automation solution. It dispatches tasks to specialized Claude Code agents, tracks progress through a Kanban board, and provides human-in-the-loop decision making.

## Architecture

```
                    ┌─────────────────────────────────┐
                    │        Blazor Dashboard          │
                    │  (Task Board, Agents, Missions)  │
                    └──────────────┬──────────────────┘
                                   │
                    ┌──────────────▼──────────────────┐
                    │     ASP.NET Core Minimal API     │
                    │   /api/tasks, /api/agents, ...   │
                    └──────────────┬──────────────────┘
                                   │
              ┌────────────────────▼────────────────────┐
              │            Dispatcher (Daemon)           │
              │  Polls tasks → gates deps → dispatches   │
              └────┬───────────┬───────────┬────────────┘
                   │           │           │
            ┌──────▼──┐ ┌─────▼───┐ ┌─────▼───┐
            │ Agent 1  │ │ Agent 2  │ │ Agent 3  │
            │ (claude) │ │ (claude) │ │ (claude) │
            └──────────┘ └─────────┘ └─────────┘
                   │           │           │
            ┌──────▼───────────▼───────────▼────────┐
            │  Accelerator (external repo)           │
            │  AppiumTests / PlaywrightTests / ApiTests │
            └───────────────────────────────────────┘
```

## Prerequisites

- **.NET 8 SDK** — `dotnet --version` should return 8.x
- **Claude Code CLI** — installed and authenticated (`claude --version`)
- **Accelerator solution** — cloned and buildable at a known path (agents execute Claude Code inside Accelerator project directories)

## Quick Start

```bash
# Build
dotnet build

# Start Mission Control (daemon + API + dashboard on port 5100)
dotnet run

# Open the dashboard
# → http://localhost:5100

# Create a task via API
curl -X POST http://localhost:5100/api/tasks \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Write login tests for PlaywrightTests",
    "description": "Create a test fixture for the login page with valid/invalid credential scenarios",
    "assignedTo": "playwright-test-writer",
    "priority": "high",
    "acceptanceCriteria": "Tests compile, follow naming conventions, have Allure attributes"
  }'

# Check daemon status
curl http://localhost:5100/api/daemon/status

# Emergency stop all agents
curl -X POST http://localhost:5100/api/emergency-stop
```

**Important:** On first run, Mission Control seeds `data/config.json` with a default `workingDirectory` pointing to `C:\Users\akotr\Source\Repos\Accelerator`. If your Accelerator repo is at a different path, update `workingDirectory` in `data/config.json` or via the Settings page in the dashboard.

## Architecture Details

### Data Layer (`Data/`)

All state is persisted in local JSON files under `MissionControl/data/`:

| File | Purpose |
|------|---------|
| `agents.json` | Agent definitions (role, instructions, capabilities) |
| `tasks.json` | Task queue with status, assignments, dependencies |
| `missions.json` | Multi-task mission tracking |
| `inbox.json` | Agent-to-agent messaging and delegations |
| `decisions.json` | Human decision requests and responses |
| `activity-log.json` | Audit trail of all system events |
| `active-runs.json` | Currently executing agent sessions |
| `config.json` | Runtime settings |

**Concurrency:** Each file has a dedicated `SemaphoreSlim` lock. All writes go through `JsonDataStore.MutateAsync()` which atomically reads, modifies, and writes. Reads are non-blocking.

### Agent System (`Agents/`)

Agents are role-based AI workers with specific instructions, capabilities, and project scope. Each agent gets a tailored system prompt when spawned.

**`AgentDefinition` properties:**
- `Id` — unique identifier (e.g. `appium-test-writer`)
- `Name` — display name
- `Role` — `qa`, `developer`, `researcher`
- `Instructions` — detailed system prompt (up to 20K chars)
- `Capabilities` — what this agent can do (list of strings)
- `SkillIds` — linked CLAUDE.md skill files
- `WorkingDirectory` — which project this agent operates in
- `Status` — `active` or `inactive`

### Task Management (`Tasks/`)

Tasks follow a Kanban lifecycle:

```
NotStarted ──→ InProgress ──→ Done
                    │
                    ├──→ Failed (attempt ≤ max) ──→ NotStarted (retry)
                    │
                    └──→ Failed (attempt > max) ──→ terminal
```

**Key features:**
- **Dependencies:** `BlockedBy` array — task won't dispatch until all dependencies are `Done`
- **Subtasks:** Checklist within a task, tracked by completion count
- **Priority:** Critical > High > Normal > Low — dispatcher picks highest priority first
- **Retry:** Failed tasks are retried up to `maxTaskRetries` times with different approach context

### Mission Orchestration (`Missions/`)

Missions coordinate multiple related tasks across agents:

- **Dependency gating:** Tasks only dispatch when their `BlockedBy` tasks are complete
- **Concurrency control:** Respects `MaxParallelAgents` limit
- **Progress tracking:** Total/completed/failed/skipped task counts
- **Terminal states:**
  - `Completed` — all tasks done or skipped
  - `Stalled` — a failed task blocks further progress (after max retries)

### Dispatcher (`Orchestrator/Dispatcher.cs`)

Background service that polls for ready tasks every `dispatchIntervalSeconds`:

1. Check how many agent slots are available (`maxParallelAgents - activeRuns`)
2. Find dispatchable tasks (not started + deps satisfied + not at max retries + no pending decisions)
3. Sort by priority (critical first)
4. For each task: resolve assigned agent → build prompt → spawn Claude Code subprocess
5. On completion: update task status, post to inbox, log activity, reconcile missions

### AgentRunner (`Orchestrator/AgentRunner.cs`)

Spawns Claude Code CLI as a subprocess:

```
claude --dangerously-skip-permissions -p "<prompt>" --output-format json --max-turns 50
```

- **Timeout:** Kills process after `taskTimeoutMinutes`
- **Output capture:** Streams stdout/stderr for result analysis
- **Run tracking:** Registers/unregisters in `active-runs.json`
- **Failure handling:** Posts failure report to inbox, logs activity event

### PromptBuilder (`Orchestrator/PromptBuilder.cs`)

Constructs layered prompts for each agent session:

1. **Agent Persona** — name, role, description, instructions, capabilities
2. **Task Details** — title, description, acceptance criteria, subtasks, priority, time estimate
3. **Guardrails** — don't modify data files, follow code conventions, run `dotnet build`
4. **Context Overlays:**
   - Retry context (attempt count, instruction to try different approach)
   - Mission context (history of completed/failed tasks)
   - Decision context (resolved human decisions)

### Communication Layer (`Communication/`)

**Inbox:** Messages between agents and humans
- `delegation` — task assignment
- `report` — completion or failure report
- `notification` — informational message

**Decisions:** Human-in-the-loop when an agent is stuck
- Agent posts a question with options and context
- Dispatcher pauses the task until the decision is resolved
- Human responds via dashboard or API
- Agent's next session receives the decision as context

**Activity Log:** Immutable audit trail
- Event types: `agent_spawned`, `task_completed`, `task_failed`, `decision_requested`, `mission_completed`, `emergency_stop`
- Auto-rotates at 500 entries

## Configuration Reference

Edit `MissionControl/data/config.json`:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `maxParallelAgents` | int | 2 | Max concurrent Claude Code sessions |
| `dispatchIntervalSeconds` | int | 10 | Polling interval for ready tasks |
| `maxTaskRetries` | int | 3 | Attempts before marking a task as terminal failure |
| `taskTimeoutMinutes` | int | 30 | Wall-clock limit per agent session |
| `maxTurnsPerSession` | int | 50 | Claude Code conversation turn limit |
| `maxContinuations` | int | 3 | Chained sessions for long-running tasks |
| `claudeCodePath` | string | `"claude"` | Path to Claude Code CLI |
| `workingDirectory` | string | auto-detected | Root of the Accelerator repo |
| `apiPort` | int | 5100 | Port for API server and dashboard |

Settings can be edited via the dashboard (Settings page) or by directly modifying `config.json`. Restart the daemon after changing settings.

## Built-in Agents

### `appium-test-writer` (QA)
- **Directory:** `AppiumTests/`
- **Capabilities:** Write NUnit test fixtures, create page objects, use PhotoUploadHelper and PaymentHelper, handle platform-specific branching
- **Skills:** `appium-test-writer`, `appium-page-object`

### `playwright-test-writer` (QA)
- **Directory:** `PlaywrightTests/`
- **Capabilities:** Write NUnit test fixtures, create page objects with SyncLocator, handle tracing and video capture, cross-browser testing

### `api-test-writer` (QA)
- **Directory:** `ApiTests/`
- **Capabilities:** Write NUnit test fixtures, create API client classes, define POCO response models, validate status codes and response bodies
- **Skills:** `api-client-writer`

### `page-object-builder` (Developer)
- **Directory:** `AppiumTests/`
- **Capabilities:** Create AppiumTests and PlaywrightTests page objects, map accessibility IDs to elements, follow Page Object Model patterns
- **Skills:** `appium-page-object`

### `test-debugger` (Developer)
- **Directory:** Any project
- **Capabilities:** Diagnose test failures, fix broken locators, update page objects, resolve timing/wait issues, debug API response mismatches
- **Skills:** `appium-debug`

### `test-reviewer` (QA)
- **Directory:** Any project
- **Capabilities:** Review test code quality, check naming conventions, verify Allure attributes, flag anti-patterns, suggest improvements

### Adding a Custom Agent

POST to `/api/agents`:

```json
{
  "id": "my-custom-agent",
  "name": "My Custom Agent",
  "role": "developer",
  "description": "Does something specific",
  "instructions": "Detailed instructions for the agent...",
  "capabilities": ["capability 1", "capability 2"],
  "skillIds": [],
  "status": "active",
  "workingDirectory": "AppiumTests"
}
```

Or edit `data/agents.json` directly.

## API Reference

### Agents
- `GET /api/agents` — List all agents
- `POST /api/agents` — Create a new agent
- `PUT /api/agents/{id}` — Update an agent
- `DELETE /api/agents/{id}` — Delete an agent

### Tasks
- `GET /api/tasks` — List all tasks
- `GET /api/tasks/{id}` — Get a single task
- `POST /api/tasks` — Create a new task
- `PUT /api/tasks/{id}` — Update a task
- `DELETE /api/tasks/{id}` — Delete a task

**Create task example:**
```json
POST /api/tasks
{
  "title": "Write deposit card tests",
  "description": "Create test fixtures for the card deposit flow",
  "assignedTo": "appium-test-writer",
  "priority": "high",
  "acceptanceCriteria": "Tests compile and follow project conventions",
  "subTasks": [
    { "title": "Create DepositCardTests fixture", "completed": false },
    { "title": "Add test for valid card deposit", "completed": false },
    { "title": "Add test for invalid card", "completed": false }
  ],
  "blockedBy": []
}
```

### Missions
- `GET /api/missions` — List all missions
- `POST /api/missions` — Create a new mission

**Create mission example:**
```json
POST /api/missions
{
  "title": "Implement deposit testing",
  "description": "Full test coverage for deposit flows",
  "taskIds": ["task-id-1", "task-id-2", "task-id-3"],
  "maxParallelAgents": 2
}
```

### Communication
- `GET /api/inbox` — List all inbox messages
- `GET /api/decisions` — List all decisions
- `POST /api/decisions/{id}` — Resolve a decision: `{ "response": "your answer" }`
- `GET /api/activity-log` — View activity audit trail

### Daemon Control
- `GET /api/daemon/status` — Get daemon status and active run count
- `POST /api/emergency-stop` — Kill all running agent sessions immediately

## Dashboard

Access the Blazor dashboard at `http://localhost:5100` when Mission Control is running.

**Pages:**

| Page | Path | Description |
|------|------|-------------|
| Task Board | `/` | Kanban view of all tasks by status |
| Agents | `/agents` | Agent cards with status and capabilities |
| Missions | `/missions` | Mission progress bars and task lists |
| Activity Feed | `/activity` | Real-time scrolling event log |
| Decisions | `/decisions` | Pending decision queue with response form |
| Inbox | `/inbox` | Filterable message list |
| Settings | `/settings` | Config editor + emergency stop button |

## Extending

### Adding a new agent type
1. POST to `/api/agents` or add to `data/agents.json`
2. Set `workingDirectory` to the target project
3. Write detailed `instructions` referencing the project's CLAUDE.md and skills
4. List specific `capabilities` the agent should have

### Integrating with CI/CD
- Add a GitHub Actions step that creates tasks via the API after test failures
- Use the activity log endpoint to report orchestration results
- The dispatcher can run as a service in CI environments

### Custom task workflows
- Use `blockedBy` to create dependency chains (e.g. page objects before tests)
- Group related tasks into missions for coordinated execution
- Set `estimatedMinutes` for time tracking and agent performance monitoring

# Enriched Agent Roles & Overseer-Driven Orchestration

## Problem

Current agent structure is flat — 6 test-specific agents all dispatched identically with no coordination layer, no quality gates, and no task decomposition. The reference project (nemss/claude-mission-control) demonstrates a structured orchestration pattern with specialized roles (Overseer, Builder, Oracle, Researcher) that we want to adopt while keeping our existing test automation agents.

## Design

### 1. AgentType Enum

New enum classifying agents by their orchestration behavior:

```csharp
public enum AgentType
{
    Coordinator,  // Overseer: decomposes tasks, delegates, never codes
    Builder,      // Implements code: test writers, page-object builder, debugger
    Gate,         // Oracle: validates builder output, PASS/FAIL verdicts
    Analyst       // Researcher: explores, analyzes, outputs findings only
}
```

**File:** `Agents/AgentType.cs` (new)

### 2. Enriched AgentDefinition Model

Add fields to `AgentDefinition`:

```csharp
public AgentType AgentType { get; set; } = AgentType.Builder;
public string Model { get; set; } = "";                        // e.g. "opus" for Overseer
public List<string> AllowedTools { get; set; } = [];           // tool whitelist
public List<string> DisallowedTools { get; set; } = [];        // tool blacklist
public List<string> ReadScope { get; set; } = [];              // what this agent reads
public List<string> WriteScope { get; set; } = [];             // what this agent writes
public List<string> CoreLoop { get; set; } = [];               // ordered workflow steps
```

**File:** `Agents/AgentDefinition.cs` (modify)

### 3. Nine Agents (3 new + 6 existing reclassified)

#### New Orchestration Agents

**Overseer** (Coordinator):
- Id: `overseer`
- Model: `opus`
- AgentType: `Coordinator`
- Role: Decomposes incoming tasks into subtasks, assigns to builders, monitors progress. Never writes code.
- CoreLoop: Read context → Analyze task → Break into subtasks → Assign agents → Monitor execution → Report results
- AllowedTools: Read, Write, Edit, Grep, Glob
- DisallowedTools: (none — but Instructions forbid code changes)
- ReadScope: everything
- WriteScope: tasks, decisions, activity-log

**Oracle** (Gate):
- Id: `oracle`
- Model: (default)
- AgentType: `Gate`
- Role: Validates builder deliverables against acceptance criteria. Issues PASS/FAIL verdicts with specific feedback.
- CoreLoop: Read task + acceptance criteria → Read code changes → Run validation checklist → Issue verdict → Log result
- AllowedTools: Read, Grep, Glob, Bash
- DisallowedTools: Write, Edit (cannot modify code, only read and run tests)
- ReadScope: everything
- WriteScope: decisions (verdicts only)

**Researcher** (Analyst):
- Id: `researcher`
- Model: (default)
- AgentType: `Analyst`
- Role: Explores codebase, APIs, docs. Outputs structured findings. Never changes code.
- CoreLoop: Understand question → Check existing findings → Explore sources → Structure findings → Report
- AllowedTools: Read, Grep, Glob, Bash, WebSearch, WebFetch
- DisallowedTools: Write, Edit
- ReadScope: codebase, docs, web
- WriteScope: findings (reports only)

#### Existing Agents (reclassified as Builder)

All 6 existing agents get `AgentType = AgentType.Builder`. No other changes to their definitions — they keep their current Instructions, Capabilities, SkillIds, and WorkingDirectory.

- `appium-test-writer` → Builder
- `playwright-test-writer` → Builder
- `api-test-writer` → Builder
- `page-object-builder` → Builder
- `test-debugger` → Builder
- `test-reviewer` → Builder (note: reviewer is a builder that produces review reports)

### 4. Dispatcher: Overseer-Driven Flow

Current flow: task → dispatch to assigned agent → done/failed.

New flow adds two orchestration paths based on `AgentType`:

#### Path A: Task assigned to Overseer (Coordinator)

```
Task (assigned to overseer)
  → Dispatcher spawns Overseer with --output-format json
  → Overseer outputs a JSON array of subtask objects
  → Dispatcher parses the output, creates TaskItem for each subtask (with ParentTaskId set)
  → Original task set to InProgress
  → Subtasks enter normal dispatch queue
  → When all subtasks Done → parent task marked Done
  → When any subtask fails at max retries → parent task marked Failed
```

The Overseer's prompt instructs it to output a JSON array:
```json
[
  {
    "title": "...",
    "description": "...",
    "acceptanceCriteria": "...",
    "assignedTo": "appium-test-writer",
    "priority": "high",
    "estimatedMinutes": 15
  }
]
```

The Dispatcher's `HandleCoordinatorResult` method parses this, creates TaskItems via `TaskManager.CreateAsync`, and sets `ParentTaskId` on each.

#### Path B: Task assigned to Builder (with Oracle gate)

```
Task (assigned to builder, AgentType == Builder)
  → Dispatcher spawns Builder
  → Builder completes (exit 0)
  → Instead of marking Done immediately:
    → Task status set to AwaitingValidation (new status)
    → Dispatcher spawns Oracle agent for this task
    → Oracle validates and sets ValidationResult on task
    → PASS → task marked Done
    → FAIL → task marked Failed (can retry via normal mechanism)
```

#### Path C: Task assigned to Analyst or other types

```
Task (assigned to researcher/analyst)
  → Dispatcher spawns agent
  → Completes → Done (no Oracle gate)
```

### 5. New TaskItem Fields

```csharp
public string? ParentTaskId { get; set; }          // links subtask to parent
public string? ValidationResult { get; set; }       // "pass" or "fail" (set by Oracle)
public string? ValidationFeedback { get; set; }     // Oracle's structured feedback
```

**File:** `Tasks/TaskItem.cs` (modify)

### 6. New TaskItemStatus Value

Add `AwaitingValidation` to the enum:

```csharp
public enum TaskItemStatus
{
    NotStarted,
    InProgress,
    AwaitingValidation,  // NEW: builder done, waiting for Oracle
    Done,
    Failed,
    Skipped
}
```

**File:** `Tasks/TaskItem.cs` (modify)

### 7. Dispatcher Changes

**File:** `Orchestrator/Dispatcher.cs` (modify)

In `DispatchTickAsync`, after a builder agent completes successfully:

1. Check if the assigned agent's `AgentType == Builder`
2. If yes: set task status to `AwaitingValidation` instead of `Done`
3. In the dispatch loop, also look for `AwaitingValidation` tasks and spawn the Oracle agent for them
4. Oracle result handling: parse exit, set `ValidationResult` and `ValidationFeedback` on the task
5. PASS → Done, FAIL → Failed (normal retry mechanism applies)

For Coordinator tasks:
1. After Overseer completes, check for new subtasks (tasks with `ParentTaskId` matching this task)
2. Track parent task: when all subtasks are Done → mark parent Done
3. When any subtask fails at max retries → mark parent Failed

### 8. AgentRunner Changes

**File:** `Orchestrator/AgentRunner.cs` (modify)

- `RunTaskAsync` returns a result object instead of bool, containing: success, output, agentType
- After builder success: don't immediately set Done — return and let Dispatcher handle the Oracle gate
- New method: `RunValidationAsync(oracle, task)` — spawns Oracle with validation-specific prompt

### 9. PromptBuilder Changes

**File:** `Orchestrator/PromptBuilder.cs` (modify)

Add agent-type-specific prompt sections:

- **Coordinator prompt**: Include decomposition instructions, list available agents and their capabilities, instruct to create subtasks via structured output
- **Builder prompt**: Add "stage changes only, do not commit" guardrail, include Oracle feedback from previous attempts if `ValidationFeedback` is set
- **Gate prompt**: Include acceptance criteria, validation checklist template, structured feedback format (PASS/FAIL with specific file:line references)
- **Analyst prompt**: Add "read-only, output findings only" guardrail

### 10. Seed Data Update

**File:** `Program.cs` (modify)

Add the 3 new agents to `SeedDataAsync()`. Existing agents get `AgentType = AgentType.Builder` added.

Note: seed only runs when `agents.json` doesn't exist. For existing installations, agents must be updated via the API or by deleting `data/agents.json` to re-seed.

## Files Modified

| File | Change |
|------|--------|
| `Agents/AgentType.cs` | **New** — enum |
| `Agents/AgentDefinition.cs` | Add 7 new properties |
| `Tasks/TaskItem.cs` | Add `ParentTaskId`, `ValidationResult`, `ValidationFeedback`; add `AwaitingValidation` status |
| `Orchestrator/Dispatcher.cs` | Overseer decomposition flow, Oracle validation gate, parent-task tracking |
| `Orchestrator/AgentRunner.cs` | Return result object, add `RunValidationAsync` |
| `Orchestrator/PromptBuilder.cs` | Agent-type-specific prompt sections |
| `Program.cs` | Seed 3 new agents, add AgentType to existing 6 |
| `Dashboard/Components/Pages/Agents.razor` | Display new fields (AgentType, Model, tools) |
| `Dashboard/Components/Pages/TaskBoard.razor` | Show AwaitingValidation column, ParentTaskId |
| `Dashboard/Services/DashboardDataService.cs` | No changes needed (already generic) |

## Verification

1. `dotnet build` — compiles without errors
2. Delete `data/agents.json` and run — verify 9 agents seeded with correct types
3. Create a task assigned to overseer via API — verify it spawns Overseer and creates subtasks
4. Create a task assigned to a builder — verify Oracle validation runs after builder completes
5. Create a task assigned to researcher — verify it completes without Oracle gate
6. Oracle FAIL scenario — verify task goes to Failed and retry includes feedback
7. Dashboard — verify agents page shows new fields, task board shows AwaitingValidation

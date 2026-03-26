using MissionControl.Agents;
using MissionControl.Api;
using MissionControl.Communication;
using MissionControl.Config;
using MissionControl.Data;
using MissionControl.Missions;
using MissionControl.Orchestrator;
using MissionControl.Tasks;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine("Dashboard", "wwwroot")
});

// Load config from data/config.json
var config = await JsonDataStore.ReadAsync<MissionControlConfig>(DataPaths.Config);
if (string.IsNullOrWhiteSpace(config.WorkingDirectory))
    config.WorkingDirectory = Directory.GetCurrentDirectory();

// Register services
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<TaskManager>();
builder.Services.AddSingleton<CommunicationManager>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<MissionOrchestrator>();

builder.Services.AddSingleton<MissionControl.Dashboard.Services.DashboardDataService>();

// Register dispatcher as a hosted service (background daemon)
builder.Services.AddHostedService<Dispatcher>();

// Blazor Server for dashboard
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Ensure data directory exists with seed files
await SeedDataAsync();

// Map API endpoints
app.MapMissionControlApi();

// Blazor dashboard
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<MissionControl.Dashboard.Components.Layout.App>()
    .AddInteractiveServerRenderMode();

Console.WriteLine($"Mission Control started on http://localhost:{config.ApiPort}");
app.Run($"http://localhost:{config.ApiPort}");

static async Task SeedDataAsync()
{
    Directory.CreateDirectory(DataPaths.BaseDir);

    if (!File.Exists(DataPaths.Agents))
    {
        var agents = new List<AgentDefinition>
        {
            new()
            {
                Id = "appium-test-writer",
                Name = "Appium Test Writer",
                Role = "qa",
                Description = "Writes and maintains Appium mobile tests for Android and iOS.",
                Instructions = "Follow the patterns in AppiumTests/.claude/CLAUDE.md. Use BaseTest, BaseAppiumPage, AppiumElement. Apply Allure attributes. Use the appium-test-writer skill for test generation.",
                Capabilities = ["Write NUnit test fixtures", "Create page objects", "Use PhotoUploadHelper and PaymentHelper", "Handle platform-specific branching"],
                SkillIds = ["appium-test-writer", "appium-page-object"],
                WorkingDirectory = "AppiumTests"
            },
            new()
            {
                Id = "playwright-test-writer",
                Name = "Playwright Test Writer",
                Role = "qa",
                Description = "Writes and maintains Playwright browser tests.",
                Instructions = "Follow the patterns in CLAUDE.md PlaywrightTests section. Use BasePage with SyncLocator. Prefer FindByTestId, then FindByRole, then Find. Apply Allure attributes.",
                Capabilities = ["Write NUnit test fixtures", "Create page objects with SyncLocator", "Handle tracing and video capture", "Cross-browser testing"],
                SkillIds = [],
                WorkingDirectory = "PlaywrightTests"
            },
            new()
            {
                Id = "api-test-writer",
                Name = "API Test Writer",
                Role = "qa",
                Description = "Writes and maintains RestSharp API tests.",
                Instructions = "Follow the patterns in ApiTests/.claude/CLAUDE.md. Create client classes extending BaseApiClient. Models are plain POCOs with JsonPropertyName. Deserialize in tests, not clients.",
                Capabilities = ["Write NUnit test fixtures", "Create API client classes", "Define POCO response models", "Validate status codes and response bodies"],
                SkillIds = ["api-client-writer"],
                WorkingDirectory = "ApiTests"
            },
            new()
            {
                Id = "page-object-builder",
                Name = "Page Object Builder",
                Role = "developer",
                Description = "Creates and updates page objects from app screen definitions.",
                Instructions = "Use the appium-page-object skill. All pages extend BaseAppiumPage. Use FindByAccessibilityId first. Every page implements VerifyPageLoaded().",
                Capabilities = ["Create AppiumTests page objects", "Create PlaywrightTests page objects", "Map accessibility IDs to elements", "Follow Page Object Model patterns"],
                SkillIds = ["appium-page-object"],
                WorkingDirectory = "AppiumTests"
            },
            new()
            {
                Id = "test-debugger",
                Name = "Test Debugger",
                Role = "developer",
                Description = "Investigates and fixes failing tests across all projects.",
                Instructions = "Use the appium-debug skill for mobile test failures. Read test logs, analyze screenshots, check element locators. Fix the root cause, not symptoms.",
                Capabilities = ["Diagnose test failures", "Fix broken locators", "Update page objects", "Resolve timing and wait issues", "Debug API response mismatches"],
                SkillIds = ["appium-debug"],
                WorkingDirectory = ""
            },
            new()
            {
                Id = "test-reviewer",
                Name = "Test Reviewer",
                Role = "qa",
                Description = "Reviews test code for quality, patterns, and anti-patterns.",
                Instructions = "Check for anti-patterns: Thread.Sleep, raw IWebElement, hardcoded capabilities, text-based selectors, inter-test dependencies. Verify Allure attributes. Verify naming conventions.",
                Capabilities = ["Review test code quality", "Check naming conventions", "Verify Allure attributes", "Flag anti-patterns", "Suggest improvements"],
                SkillIds = [],
                WorkingDirectory = ""
            }
        };

        await JsonDataStore.WriteAsync(DataPaths.Agents, agents);
    }

    if (!File.Exists(DataPaths.Config))
    {
        await JsonDataStore.WriteAsync(DataPaths.Config, new MissionControlConfig
        {
            WorkingDirectory = @"C:\Users\akotr\Source\Repos\Accelerator"
        });
    }

    // Initialize empty data files
    foreach (var path in new[] { DataPaths.Tasks, DataPaths.Missions, DataPaths.Inbox,
        DataPaths.Decisions, DataPaths.ActivityLog, DataPaths.ActiveRuns })
    {
        if (!File.Exists(path))
            await File.WriteAllTextAsync(path, "[]");
    }
}

namespace MissionControl.Agents;

public enum AgentType
{
    Coordinator,  // Overseer: decomposes tasks, delegates
    Gate,         // Oracle: validates builder output
    Analyst,      // Researcher: explores, analyzes
    Builder       // Implements code
}

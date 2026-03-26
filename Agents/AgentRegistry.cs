using MissionControl.Data;

namespace MissionControl.Agents;

public class AgentRegistry
{
    public async Task<List<AgentDefinition>> GetAllAsync() =>
        await JsonDataStore.ReadAsync<List<AgentDefinition>>(DataPaths.Agents);

    public async Task<AgentDefinition?> GetByIdAsync(string id)
    {
        var agents = await GetAllAsync();
        return agents.FirstOrDefault(a => a.Id == id);
    }

    public async Task<AgentDefinition> CreateAsync(AgentDefinition agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Id))
            agent.Id = Guid.NewGuid().ToString("N")[..8];

        await JsonDataStore.MutateAsync<List<AgentDefinition>>(DataPaths.Agents, agents =>
        {
            if (agents.Any(a => a.Id == agent.Id))
                throw new InvalidOperationException($"Agent '{agent.Id}' already exists");
            agents.Add(agent);
            return agents;
        });

        return agent;
    }

    public async Task<AgentDefinition> UpdateAsync(AgentDefinition agent)
    {
        await JsonDataStore.MutateAsync<List<AgentDefinition>>(DataPaths.Agents, agents =>
        {
            var idx = agents.FindIndex(a => a.Id == agent.Id);
            if (idx < 0) throw new KeyNotFoundException($"Agent '{agent.Id}' not found");
            agents[idx] = agent;
            return agents;
        });

        return agent;
    }

    public async Task DeleteAsync(string id)
    {
        await JsonDataStore.MutateAsync<List<AgentDefinition>>(DataPaths.Agents, agents =>
        {
            agents.RemoveAll(a => a.Id == id);
            return agents;
        });
    }

    public async Task<List<AgentDefinition>> GetActiveAsync()
    {
        var agents = await GetAllAsync();
        return agents.Where(a => a.Status == "active").ToList();
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MissionControl.Data;

public static class JsonDataStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static SemaphoreSlim GetLock(string filePath) =>
        _locks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));

    public static async Task<T> ReadAsync<T>(string filePath) where T : new()
    {
        if (!File.Exists(filePath))
            return new T();

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? new T();
    }

    public static async Task WriteAsync<T>(string filePath, T data)
    {
        var sem = GetLock(filePath);
        await sem.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, SerializerOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            sem.Release();
        }
    }

    public static async Task<T> MutateAsync<T>(string filePath, Func<T, T> mutator) where T : new()
    {
        var sem = GetLock(filePath);
        await sem.WaitAsync();
        try
        {
            var data = File.Exists(filePath)
                ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(filePath), SerializerOptions) ?? new T()
                : new T();

            data = mutator(data);

            var dir = Path.GetDirectoryName(filePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(data, SerializerOptions);
            await File.WriteAllTextAsync(filePath, json);
            return data;
        }
        finally
        {
            sem.Release();
        }
    }
}

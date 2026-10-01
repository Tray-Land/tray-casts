using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TrayCasts.Storage;

/// <summary>
/// JSON files for the library and progress. Writes go to a temp file and replace the original, so
/// a crash mid-save leaves the previous version intact; a missing or corrupt file loads as empty.
/// </summary>
public static class LibraryStore
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo) where T : new()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new T();
            }

            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new T();
        }
    }

    /// <summary>Serializes now (so later changes on the caller's thread can't race the write) and
    /// writes on the thread pool. Writes are serialized, so the newest call lands last.</summary>
    public static async Task SaveAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        await WriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => WriteAtomically(path, bytes)).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>Synchronous save for shutdown, when there's no time to await.</summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        WriteGate.Wait();
        try
        {
            WriteAtomically(path, bytes);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the in-memory copy is still right, and the next save tries again.
        }
    }
}

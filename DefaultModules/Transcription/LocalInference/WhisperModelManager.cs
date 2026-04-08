using System.Collections.Concurrent;
using Whisper.net;

namespace SharpClaw.Modules.Transcription.LocalInference;

/// <summary>
/// Manages loaded Whisper.net model instances (GGML/GGUF weights).
/// Each unique model file path maps to a single <see cref="WhisperFactory"/>
/// that can create lightweight <see cref="WhisperProcessor"/> instances
/// for concurrent transcription requests.
/// <para>
/// Thread-safe. Model loading is serialised per file path via a
/// per-key <see cref="SemaphoreSlim"/> to avoid duplicate loads.
/// </para>
/// </summary>
public sealed class WhisperModelManager : IDisposable
{
    private readonly ConcurrentDictionary<string, WhisperFactory> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the cached <see cref="WhisperFactory"/> for the given model
    /// file, loading it from disk on first access.
    /// </summary>
    public WhisperFactory GetOrLoad(string modelFilePath)
    {
        if (_factories.TryGetValue(modelFilePath, out var existing))
            return existing;

        var loadLock = _loadLocks.GetOrAdd(modelFilePath, _ => new SemaphoreSlim(1, 1));
        loadLock.Wait();
        try
        {
            if (_factories.TryGetValue(modelFilePath, out existing))
                return existing;

            var factory = WhisperFactory.FromPath(modelFilePath);
            _factories[modelFilePath] = factory;
            return factory;
        }
        finally
        {
            loadLock.Release();
        }
    }

    /// <summary>
    /// Disposes and removes the factory for a model file path.
    /// </summary>
    public void Unload(string modelFilePath)
    {
        if (_factories.TryRemove(modelFilePath, out var factory))
            factory.Dispose();
    }

    public bool IsLoaded(string modelFilePath) =>
        _factories.ContainsKey(modelFilePath);

    public void Dispose()
    {
        foreach (var factory in _factories.Values)
            factory.Dispose();
        _factories.Clear();
    }
}

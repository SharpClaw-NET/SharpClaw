using System.Collections.Concurrent;
using LLama;
using LLama.Common;
using LLama.Native;

namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Manages locally-loaded GGUF models via LLamaSharp (in-process llama.cpp).
/// Each loaded model holds a <see cref="LLamaWeights"/> instance that is
/// shared across concurrent requests.
/// <para>
/// Two loading modes:
/// <list type="bullet">
///   <item><b>Acquire/Release</b> – auto-load on chat request; after the
///         last active request completes the model idles for
///         <see cref="IdleCooldown"/> before being disposed.</item>
///   <item><b>Pin/Unpin</b> – manual <c>model load</c>/<c>model unload</c>;
///         the model stays in memory regardless of active requests or cooldown.</item>
/// </list>
/// </para>
/// </summary>
public sealed class LocalInferenceProcessManager : IAsyncDisposable
{
    /// <summary>
    /// A loaded model that can create inference contexts on demand.
    /// </summary>
    public sealed class LoadedModel : IDisposable
    {
        public LLamaWeights Weights { get; }
        public ModelParams Params { get; }

        /// <summary>
        /// Optional CLIP / mmproj projector for multimodal (LLaVA-style) inference.
        /// Null when the model is text-only.
        /// </summary>
        public MtmdWeights? ClipModel { get; }

        internal LoadedModel(LLamaWeights weights, ModelParams modelParams, MtmdWeights? clipModel = null)
        {
            Weights = weights;
            Params = modelParams;
            ClipModel = clipModel;
        }

        /// <summary>
        /// Creates a fresh context for a single inference request.
        /// Caller is responsible for disposing the returned context.
        /// Throws <see cref="InvalidOperationException"/> when the
        /// KV cache cannot be allocated (e.g. insufficient VRAM).
        /// </summary>
        public LLamaContext CreateContext()
        {
            LLamaContext ctx;
            try
            {
                ctx = Weights.CreateContext(Params);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to create inference context. " +
                    "Try reducing Local__ContextSize or Local__GpuLayerCount.", ex);
            }

            try
            {
                if (ctx.NativeHandle.IsInvalid || ctx.NativeHandle.IsClosed)
                {
                    ctx.Dispose();
                    throw new InvalidOperationException(
                        "KV cache allocation failed (not enough VRAM for the requested context size). " +
                        "Try reducing Local__ContextSize or Local__GpuLayerCount.");
                }
            }
            catch (InvalidOperationException) { throw; }
            catch
            {
                ctx.Dispose();
                throw new InvalidOperationException(
                    "Context handle validation failed (KV cache allocation likely failed). " +
                    "Try reducing Local__ContextSize or Local__GpuLayerCount.");
            }

            return ctx;
        }

        public void Dispose()
        {
            ClipModel?.Dispose();
            Weights.Dispose();
        }
    }

    private readonly ConcurrentDictionary<Guid, LoadedModel> _loaded = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _loadLocks = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, int> _refCounts = new();
    private readonly HashSet<Guid> _pinnedModels = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cooldownTimers = new();

    /// <summary>
    /// How long an unpinned model stays in memory after the last request
    /// completes before being disposed. Default 5 minutes.
    /// Configurable via .env key <c>Local__IdleCooldownMinutes</c>.
    /// Ignored when <see cref="KeepLoaded"/> is <c>true</c>.
    /// </summary>
    public TimeSpan IdleCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When <c>true</c> (the default), models that are auto-loaded by a
    /// chat request stay resident indefinitely — equivalent to being
    /// pinned on first use.  When <c>false</c>, idle models are disposed
    /// after <see cref="IdleCooldown"/>.
    /// Configurable via .env key <c>Local__KeepLoaded</c>.
    /// </summary>
    public bool KeepLoaded { get; set; } = true;

    /// <summary>
    /// Default GPU layer count for auto-loaded models.
    /// -1 means offload all layers. 0 means CPU only.
    /// Configurable via .env key <c>Local__GpuLayerCount</c>.
    /// </summary>
    public int DefaultGpuLayerCount { get; set; } = -1;

    /// <summary>
    /// Default context window size (in tokens). Default 16384.
    /// Configurable via .env key <c>Local__ContextSize</c>.
    /// Can be overridden per-request via <c>model load --ctx</c>.
    /// </summary>
    public uint DefaultContextSize { get; set; } = 16384;

    // ── Auto-load lifecycle (chat requests) ───────────────────────

    /// <summary>
    /// Ensures the model is loaded and increments the active-request
    /// reference count. Cancels any pending cooldown timer.
    /// Call <see cref="Release"/> when the request completes.
    /// </summary>
    public async Task<LoadedModel> AcquireAsync(
        Guid modelId, string modelFilePath, int? gpuLayers = null,
        uint? contextSize = null, string? mmprojPath = null, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            _refCounts[modelId] = _refCounts.GetValueOrDefault(modelId) + 1;
            CancelCooldown(modelId);
        }

        try
        {
            return await EnsureLoadedAsync(modelId, modelFilePath, gpuLayers, contextSize, mmprojPath, ct);
        }
        catch
        {
            lock (_stateLock)
            {
                var count = Math.Max(0, _refCounts.GetValueOrDefault(modelId) - 1);
                _refCounts[modelId] = count;
            }
            throw;
        }
    }

    /// <summary>
    /// Decrements the active-request reference count. When it reaches zero
    /// and the model is not pinned (and <see cref="KeepLoaded"/> is
    /// <c>false</c>), a cooldown timer starts — the model is disposed
    /// after <see cref="IdleCooldown"/> unless a new request arrives first.
    /// </summary>
    public void Release(Guid modelId)
    {
        lock (_stateLock)
        {
            var count = Math.Max(0, _refCounts.GetValueOrDefault(modelId) - 1);
            _refCounts[modelId] = count;

            if (count == 0 && !KeepLoaded && !_pinnedModels.Contains(modelId))
                StartCooldown(modelId);
        }
    }

    // ── Manual load lifecycle (CLI model load / model unload) ─────

    /// <summary>
    /// Loads the model and marks it as pinned so it stays in memory
    /// between requests until <see cref="Unpin"/> is called.
    /// Cancels any pending cooldown timer.
    /// </summary>
    public async Task<LoadedModel> PinAsync(
        Guid modelId, string modelFilePath, int? gpuLayers = null,
        uint? contextSize = null, string? mmprojPath = null, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            _pinnedModels.Add(modelId);
            CancelCooldown(modelId);
        }
        return await EnsureLoadedAsync(modelId, modelFilePath, gpuLayers, contextSize, mmprojPath, ct);
    }

    /// <summary>
    /// Removes the pin. If <see cref="KeepLoaded"/> is <c>false</c> and
    /// no active requests are using the model, a cooldown timer starts.
    /// When <see cref="KeepLoaded"/> is <c>true</c> the model stays
    /// resident; use <see cref="Unload"/> to force-dispose.
    /// </summary>
    public void Unpin(Guid modelId)
    {
        lock (_stateLock)
        {
            _pinnedModels.Remove(modelId);

            if (!KeepLoaded && _refCounts.GetValueOrDefault(modelId) <= 0)
                StartCooldown(modelId);
        }
    }

    // ── Core model management ─────────────────────────────────────

    /// <summary>
    /// Loads a model into memory (or returns the existing instance).
    /// Uses a per-model lock to prevent duplicate loads when multiple
    /// requests arrive concurrently for a cold model.
    /// </summary>
    public async Task<LoadedModel> EnsureLoadedAsync(
        Guid modelId, string modelFilePath, int? gpuLayers = null,
        uint? contextSize = null, string? mmprojPath = null, CancellationToken ct = default)
    {
        if (_loaded.TryGetValue(modelId, out var existing))
            return existing;

        var loadLock = _loadLocks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));
        await loadLock.WaitAsync(ct);
        try
        {
            if (_loaded.TryGetValue(modelId, out existing))
                return existing;

            Console.Write("Loading model into memory...");

            var modelParams = new ModelParams(modelFilePath)
            {
                ContextSize = contextSize ?? DefaultContextSize,
                GpuLayerCount = gpuLayers ?? DefaultGpuLayerCount,
            };

            var weights = await LLamaWeights.LoadFromFileAsync(modelParams, ct);

            MtmdWeights? clipModel = null;
            if (!string.IsNullOrWhiteSpace(mmprojPath))
            {
                Console.Write(" loading mmproj...");
                clipModel = MtmdWeights.LoadFromFile(mmprojPath, weights, MtmdContextParams.Default());
            }

            var loaded = new LoadedModel(weights, modelParams, clipModel);
            _loaded[modelId] = loaded;

            Console.WriteLine(" ready.");

            return loaded;
        }
        finally
        {
            loadLock.Release();
        }
    }

    public void Unload(Guid modelId)
    {
        if (_loaded.TryRemove(modelId, out var loaded))
            loaded.Dispose();
    }

    public bool IsLoaded(Guid modelId) => _loaded.ContainsKey(modelId);

    /// <summary>
    /// Gets the loaded model instance, or null if not loaded.
    /// </summary>
    public LoadedModel? GetLoaded(Guid modelId) =>
        _loaded.TryGetValue(modelId, out var m) ? m : null;

    // ── Cooldown timer ────────────────────────────────────────────

    private void StartCooldown(Guid modelId)
    {
        CancelCooldown(modelId);

        var cts = new CancellationTokenSource();
        _cooldownTimers[modelId] = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IdleCooldown, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool shouldUnload;
            lock (_stateLock)
            {
                _cooldownTimers.Remove(modelId);
                shouldUnload = _refCounts.GetValueOrDefault(modelId) <= 0
                               && !_pinnedModels.Contains(modelId);
            }

            if (shouldUnload)
                Unload(modelId);
        });
    }

    private void CancelCooldown(Guid modelId)
    {
        if (_cooldownTimers.Remove(modelId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ── Disposal ──────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            foreach (var (_, cts) in _cooldownTimers)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _cooldownTimers.Clear();
        }

        foreach (var (id, _) in _loaded)
            Unload(id);

        await ValueTask.CompletedTask;
    }
}

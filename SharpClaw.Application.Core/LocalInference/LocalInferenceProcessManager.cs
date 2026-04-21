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

    /// <summary>
    /// Item #3 — a cached <see cref="InteractiveExecutor"/> pinned to a
    /// particular <c>(modelId, threadId)</c> pair. Preserves the KV
    /// cache across conversational turns so we only need to feed the
    /// suffix of the prompt that was not already processed. Callers
    /// must serialise inference against a single session using
    /// <see cref="Gate"/>.
    /// </summary>
    public sealed class CachedSession : IDisposable
    {
        public Guid ModelId { get; }
        public Guid ThreadId { get; }
        public LLamaContext Context { get; }
        public InteractiveExecutor Executor { get; }

        /// <summary>
        /// Full rendered prompt text that has already been consumed by
        /// the executor. The client diffs this against the next turn's
        /// rendered prompt and feeds only the suffix on a prefix match;
        /// a mismatch forces a full reset.
        /// </summary>
        public string AccumulatedPrompt { get; set; } = string.Empty;

        public DateTime LastUsedUtc { get; internal set; } = DateTime.UtcNow;

        internal readonly SemaphoreSlim Gate = new(1, 1);

        internal CachedSession(Guid modelId, Guid threadId, LLamaContext ctx)
        {
            ModelId = modelId;
            ThreadId = threadId;
            Context = ctx;
            Executor = new InteractiveExecutor(ctx);
        }

        /// <summary>
        /// Test-only constructor that skips native-context allocation so
        /// session-bookkeeping behaviour (LRU, invalidation, dispose)
        /// can be covered without loading a real GGUF model.
        /// </summary>
        internal CachedSession(Guid modelId, Guid threadId)
        {
            ModelId = modelId;
            ThreadId = threadId;
            Context = null!;
            Executor = null!;
        }

        public void Dispose()
        {
            Gate.Dispose();
            Context?.Dispose();
        }
    }

    private readonly ConcurrentDictionary<Guid, LoadedModel> _loaded = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _loadLocks = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _multimodalLocks = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, int> _refCounts = new();
    private readonly HashSet<Guid> _pinnedModels = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cooldownTimers = new();

    // Item #3 — KV cache reuse across turns.
    // Keyed on (modelId, threadId). Guarded by _sessionLock for LRU eviction;
    // individual sessions serialise their own inference via CachedSession.Gate.
    private readonly Dictionary<(Guid Model, Guid Thread), CachedSession> _sessions = new();
    private readonly object _sessionLock = new();

    /// <summary>
    /// Raised after a model has been removed from <see cref="_loaded"/>
    /// and disposed. Subscribers can use this to invalidate caches that
    /// key off the model ID (for example the VRAM probe TTL in
    /// <see cref="Clients.LocalInferenceApiClient"/>). See finding
    /// <c>L-007</c>.
    /// </summary>
    public event Action<Guid>? ModelUnloaded;

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

    /// <summary>
    /// Maximum number of <see cref="CachedSession"/> entries kept alive
    /// at once. When a new session would exceed this cap the least
    /// recently used entry is evicted. Default 8.
    /// Configurable via .env key <c>Local__MaxCachedSessions</c>.
    /// </summary>
    public int MaxCachedSessions { get; set; } = 8;

    // ── Cached session lifecycle (KV cache reuse, item #3) ────────

    /// <summary>
    /// Returns an existing <see cref="CachedSession"/> for the given
    /// <paramref name="modelId"/>/<paramref name="threadId"/> pair, or
    /// creates a fresh one. The session's <see cref="CachedSession.Gate"/>
    /// is acquired before returning; the caller must invoke
    /// <see cref="ReleaseSession"/> in a <c>finally</c> block.
    /// </summary>
    public async Task<CachedSession> AcquireSessionAsync(
        Guid modelId, Guid threadId, LoadedModel loaded, CancellationToken ct = default)
    {
        CachedSession session;
        lock (_sessionLock)
        {
            var key = (modelId, threadId);
            if (!_sessions.TryGetValue(key, out var existing))
            {
                EvictLruIfNeeded();
                var ctx = loaded.CreateContext();
                existing = new CachedSession(modelId, threadId, ctx);
                _sessions[key] = existing;
            }
            existing.LastUsedUtc = DateTime.UtcNow;
            session = existing;
        }
        await session.Gate.WaitAsync(ct);
        return session;
    }

    /// <summary>
    /// Releases the gate previously acquired by
    /// <see cref="AcquireSessionAsync"/>.
    /// </summary>
    public void ReleaseSession(CachedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        try { session.Gate.Release(); }
        catch (ObjectDisposedException) { /* evicted concurrently — fine */ }
    }

    /// <summary>
    /// Drops the cached session for the given
    /// <paramref name="modelId"/>/<paramref name="threadId"/> pair (for
    /// example when the prompt no longer prefix-matches the accumulated
    /// history and the KV cache must be rebuilt from scratch).
    /// </summary>
    public void InvalidateSession(Guid modelId, Guid threadId)
    {
        CachedSession? removed = null;
        lock (_sessionLock)
        {
            if (_sessions.Remove((modelId, threadId), out var existing))
                removed = existing;
        }
        removed?.Dispose();
    }

    /// <summary>
    /// Drops every cached session for the given thread, regardless of
    /// model.
    /// </summary>
    public void InvalidateThread(Guid threadId)
    {
        List<CachedSession> removed = new();
        lock (_sessionLock)
        {
            var keys = _sessions.Keys.Where(k => k.Thread == threadId).ToList();
            foreach (var k in keys)
            {
                if (_sessions.Remove(k, out var s))
                    removed.Add(s);
            }
        }
        foreach (var s in removed)
            s.Dispose();
    }

    private void EvictLruIfNeeded()
    {
        // Caller must hold _sessionLock.
        while (_sessions.Count >= MaxCachedSessions && _sessions.Count > 0)
        {
            var oldestKey = _sessions
                .OrderBy(kvp => kvp.Value.LastUsedUtc)
                .First().Key;
            if (_sessions.Remove(oldestKey, out var victim))
                victim.Dispose();
        }
    }

    private void InvalidateSessionsForModel(Guid modelId)
    {
        List<CachedSession> removed = new();
        lock (_sessionLock)
        {
            var keys = _sessions.Keys.Where(k => k.Model == modelId).ToList();
            foreach (var k in keys)
            {
                if (_sessions.Remove(k, out var s))
                    removed.Add(s);
            }
        }
        foreach (var s in removed)
            s.Dispose();
    }

    // Test-only helpers. The production session API calls
    // LoadedModel.CreateContext which needs a real GGUF file; these
    // seams let unit tests drive the bookkeeping (LRU, invalidation,
    // dispose) with synthetic sessions.
    internal int CachedSessionCount
    {
        get { lock (_sessionLock) return _sessions.Count; }
    }

    internal IReadOnlyList<(Guid Model, Guid Thread)> CachedSessionKeys()
    {
        lock (_sessionLock) return _sessions.Keys.ToList();
    }

    internal CachedSession SeedSessionForTest(Guid modelId, Guid threadId)
    {
        lock (_sessionLock)
        {
            EvictLruIfNeeded();
            var session = new CachedSession(modelId, threadId);
            _sessions[(modelId, threadId)] = session;
            return session;
        }
    }

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
    /// <para>
    /// If <see cref="EnsureLoadedAsync"/> fails (for example: bad path,
    /// insufficient VRAM, mmproj mismatch) the pin is rolled back so a
    /// failed <c>model load</c> does not leave a phantom pinned entry
    /// that blocks later cooldown or reload attempts. See finding
    /// <c>L-006</c>.
    /// </para>
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
        try
        {
            return await EnsureLoadedAsync(modelId, modelFilePath, gpuLayers, contextSize, mmprojPath, ct);
        }
        catch
        {
            lock (_stateLock)
            {
                _pinnedModels.Remove(modelId);
            }
            throw;
        }
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
            try
            {
                if (!string.IsNullOrWhiteSpace(mmprojPath))
                {
                    Console.Write(" loading mmproj...");
                    clipModel = MtmdWeights.LoadFromFile(mmprojPath, weights, MtmdContextParams.Default());
                }
            }
            catch
            {
                // L-003 — if the projector fails to load, dispose the
                // already-loaded base weights so we don't leak VRAM /
                // native memory. Without this, a failed mmproj load
                // orphans the weights because they never make it into
                // _loaded and therefore never reach Unload.
                weights.Dispose();
                throw;
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
        {
            // Item #3 — drop cached sessions for this model before the
            // weights are disposed; the sessions' LLamaContext instances
            // depend on those weights.
            InvalidateSessionsForModel(modelId);

            loaded.Dispose();
            // L-009 — dispose the per-model load lock when the model
            // leaves memory so a long-running process that loads many
            // distinct models over time does not accumulate one
            // SemaphoreSlim per historical model ID.
            if (_loadLocks.TryRemove(modelId, out var loadSem))
                loadSem.Dispose();
            if (_multimodalLocks.TryRemove(modelId, out var mmSem))
                mmSem.Dispose();
            // L-007 — notify subscribers (VRAM probe cache, etc.) so
            // they can invalidate state keyed on this model ID.
            ModelUnloaded?.Invoke(modelId);
        }
    }

    public bool IsLoaded(Guid modelId) => _loaded.ContainsKey(modelId);

    /// <summary>
    /// Gets the loaded model instance, or null if not loaded.
    /// </summary>
    public LoadedModel? GetLoaded(Guid modelId) =>
        _loaded.TryGetValue(modelId, out var m) ? m : null;

    /// <summary>
    /// Returns a per-model semaphore that callers can use to serialise
    /// multimodal (MTMD) inference against the same
    /// <see cref="LoadedModel.ClipModel"/>. Two concurrent requests
    /// sharing the same projector corrupt each other's staged image
    /// because <see cref="LLama.Native.MtmdWeights.LoadMedia"/> /
    /// <see cref="LLama.Native.MtmdWeights.ClearMedia"/> mutate global
    /// state on the native handle. See finding <c>L-005</c>.
    /// </summary>
    public SemaphoreSlim GetMultimodalLock(Guid modelId) =>
        _multimodalLocks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));

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
            catch (ObjectDisposedException)
            {
                // L-014 — the token source was disposed underneath us
                // (e.g. another thread called CancelCooldown and then
                // disposed). Treat as cancellation; do not unload.
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
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* already disposed — fine */ }
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
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
                cts.Dispose();
            }
            _cooldownTimers.Clear();
        }

        // Item #3 — dispose cached sessions before their underlying
        // weights are torn down by Unload().
        List<CachedSession> sessions;
        lock (_sessionLock)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }
        foreach (var s in sessions)
            s.Dispose();

        foreach (var (id, _) in _loaded)
            Unload(id);

        // L-009 — dispose any remaining per-model locks. Unload() already
        // removes and disposes the ones keyed to models that were loaded;
        // this catches entries that were created but never made it to a
        // successful load (e.g. load failed inside EnsureLoadedAsync).
        foreach (var (_, sem) in _loadLocks)
            sem.Dispose();
        _loadLocks.Clear();
        foreach (var (_, sem) in _multimodalLocks)
            sem.Dispose();
        _multimodalLocks.Clear();

        await ValueTask.CompletedTask;
    }
}

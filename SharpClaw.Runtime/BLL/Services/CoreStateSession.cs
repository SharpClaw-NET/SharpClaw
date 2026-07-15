using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Core.State;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Scoped adapter between Runtime's EF entities and Core's provider-neutral
/// state graph. Core never sees an EF entity; Runtime remains responsible for
/// materialization, change tracking, relationship synchronization, and IDs.
/// </summary>
internal sealed class CoreStateSession(SharpClawDbContext db)
{
    private readonly Dictionary<BaseEntity, DomainState> _entityToState =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DomainState, BaseEntity> _stateToEntity =
        new(ReferenceEqualityComparer.Instance);

    public AgentState Map(AgentDB entity) => (AgentState)MapEntity(entity);
    public ModelState Map(ModelDB entity) => (ModelState)MapEntity(entity);
    public ProviderState Map(ProviderDB entity) => (ProviderState)MapEntity(entity);
    public ToolAwarenessSetState Map(ToolAwarenessSetDB entity) =>
        (ToolAwarenessSetState)MapEntity(entity);
    public UserState Map(UserDB entity) => (UserState)MapEntity(entity);
    public RoleState Map(RoleDB entity) => (RoleState)MapEntity(entity);
    public PermissionSetState Map(PermissionSetDB entity) =>
        (PermissionSetState)MapEntity(entity);
    public ChannelState Map(ChannelDB entity) => (ChannelState)MapEntity(entity);
    public ChannelContextState Map(ChannelContextDB entity) =>
        (ChannelContextState)MapEntity(entity);
    public ChatThreadState Map(ChatThreadDB entity) =>
        (ChatThreadState)MapEntity(entity);
    public ChatMessageState Map(ChatMessageDB entity) =>
        (ChatMessageState)MapEntity(entity);
    public DefaultResourceSetState Map(DefaultResourceSetDB entity) =>
        (DefaultResourceSetState)MapEntity(entity);
    public DefaultResourceEntryState Map(DefaultResourceEntryDB entity) =>
        (DefaultResourceEntryState)MapEntity(entity);
    public TaskDefinitionState Map(TaskDefinitionDB entity) =>
        (TaskDefinitionState)MapEntity(entity);
    public TaskTriggerBindingState Map(TaskTriggerBindingDB entity) =>
        (TaskTriggerBindingState)MapEntity(entity);
    public DomainState Map(BaseEntity entity) => MapEntity(entity);

    public IReadOnlyList<AgentState> Map(IEnumerable<AgentDB> entities) =>
        entities.Select(Map).ToList();
    public IReadOnlyList<ModelState> Map(IEnumerable<ModelDB> entities) =>
        entities.Select(Map).ToList();
    public IReadOnlyList<ProviderState> Map(IEnumerable<ProviderDB> entities) =>
        entities.Select(Map).ToList();
    public IReadOnlyList<ToolAwarenessSetState> Map(
        IEnumerable<ToolAwarenessSetDB> entities) => entities.Select(Map).ToList();
    public IReadOnlyList<RoleState> Map(IEnumerable<RoleDB> entities) =>
        entities.Select(Map).ToList();
    public IReadOnlyList<ChannelState> Map(IEnumerable<ChannelDB> entities) =>
        entities.Select(Map).ToList();
    public IReadOnlyList<ChannelContextState> Map(
        IEnumerable<ChannelContextDB> entities) => entities.Select(Map).ToList();
    public IReadOnlyList<ChatThreadState> Map(IEnumerable<ChatThreadDB> entities) =>
        entities.Select(Map).ToList();
    public IReadOnlyList<ChatMessageState> Map(IEnumerable<ChatMessageDB> entities) =>
        entities.Select(Map).ToList();
    public IReadOnlyList<TaskDefinitionState> Map(
        IEnumerable<TaskDefinitionDB> entities) => entities.Select(Map).ToList();
    public IReadOnlyList<TaskTriggerBindingState> Map(
        IEnumerable<TaskTriggerBindingDB> entities) => entities.Select(Map).ToList();

    public void Track(DomainState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _ = EnsureEntity(state);
        ApplyAll();
    }

    public void Remove(DomainState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_stateToEntity.TryGetValue(state, out var entity))
            throw new InvalidOperationException("Core state is not bound to an EF entity.");
        db.Remove(entity);
    }

    public void Refresh(PermissionSetState state)
    {
        var entity = Entity<PermissionSetDB>(state);
        state.GlobalFlags = entity.GlobalFlags.Select(MapGlobalFlag).ToList();
        state.ResourceAccesses = entity.ResourceAccesses
            .Select(MapResourceAccess)
            .ToList();
        state.ClearanceUserWhitelist = entity.ClearanceUserWhitelist
            .Select(entry => entry.UserId)
            .ToHashSet();
        state.ClearanceAgentWhitelist = entity.ClearanceAgentWhitelist
            .Select(entry => entry.AgentId)
            .ToHashSet();
    }

    public TEntity Entity<TEntity>(DomainState state)
        where TEntity : BaseEntity
    {
        if (!_stateToEntity.TryGetValue(state, out var entity)
            || entity is not TEntity typed)
        {
            throw new InvalidOperationException(
                $"Core state is not bound to {typeof(TEntity).Name}.");
        }
        return typed;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        ApplyAll();
        var saved = await db.SaveChangesAsync(ct);
        RefreshAll();
        return saved;
    }

    public void ApplyAll()
    {
        var applied = 0;
        while (applied < _stateToEntity.Count)
        {
            var batch = _stateToEntity.ToArray();
            for (; applied < batch.Length; applied++)
                Apply(batch[applied].Key, batch[applied].Value);
        }
    }

    private DomainState MapEntity(BaseEntity entity)
    {
        if (_entityToState.TryGetValue(entity, out var existing))
            return existing;

        return entity switch
        {
            AgentDB value => MapAgent(value),
            ModelDB value => MapModel(value),
            ProviderDB value => MapProvider(value),
            ToolAwarenessSetDB value => MapToolAwarenessSet(value),
            UserDB value => MapUser(value),
            RoleDB value => MapRole(value),
            PermissionSetDB value => MapPermissionSet(value),
            GlobalFlagDB value => MapGlobalFlag(value),
            ResourceAccessDB value => MapResourceAccess(value),
            ChannelDB value => MapChannel(value),
            ChannelContextDB value => MapContext(value),
            ChatThreadDB value => MapThread(value),
            ChatMessageDB value => MapMessage(value),
            DefaultResourceSetDB value => MapDefaultResourceSet(value),
            DefaultResourceEntryDB value => MapDefaultResourceEntry(value),
            TaskDefinitionDB value => MapTaskDefinition(value),
            TaskTriggerBindingDB value => MapTaskTriggerBinding(value),
            _ => throw new NotSupportedException(
                $"No Core state mapping exists for {entity.GetType().FullName}.")
        };
    }

    private AgentState MapAgent(AgentDB entity)
    {
        var state = Register(entity, new AgentState
        {
            Name = entity.Name,
            Model = null!
        });
        state.SystemPrompt = entity.SystemPrompt;
        state.MaxCompletionTokens = entity.MaxCompletionTokens;
        state.Temperature = entity.Temperature;
        state.TopP = entity.TopP;
        state.TopK = entity.TopK;
        state.FrequencyPenalty = entity.FrequencyPenalty;
        state.PresencePenalty = entity.PresencePenalty;
        state.Stop = entity.Stop?.ToArray();
        state.Seed = entity.Seed;
        state.ResponseFormat = entity.ResponseFormat;
        state.ReasoningEffort = entity.ReasoningEffort;
        state.ProviderParameters = entity.ProviderParameters is null
            ? null
            : new Dictionary<string, System.Text.Json.JsonElement>(
                entity.ProviderParameters,
                StringComparer.Ordinal);
        state.CustomChatHeader = entity.CustomChatHeader;
        state.DisableToolSchemas = entity.DisableToolSchemas;
        state.ToolAwarenessSetId = entity.ToolAwarenessSetId;
        state.ToolAwarenessSet = entity.ToolAwarenessSet is null
            ? null
            : Map(entity.ToolAwarenessSet);
        state.ModelId = entity.ModelId;
        if (entity.Model is not null)
            state.Model = Map(entity.Model);
        state.RoleId = entity.RoleId;
        state.Role = entity.Role is null ? null : Map(entity.Role);
        state.Contexts = entity.Contexts.Select(Map).ToList();
        state.Channels = entity.Channels.Select(Map).ToList();
        state.AllowedChannels = entity.AllowedChannels.Select(Map).ToList();
        state.AllowedContexts = entity.AllowedContexts.Select(Map).ToList();
        return state;
    }

    private ModelState MapModel(ModelDB entity)
    {
        var state = Register(entity, new ModelState
        {
            Name = entity.Name,
            Provider = null!
        });
        state.CapabilityTagsRaw = entity.CapabilityTagsRaw;
        state.ProviderId = entity.ProviderId;
        if (entity.Provider is not null)
            state.Provider = Map(entity.Provider);
        state.Agents = entity.Agents.Select(Map).ToList();
        return state;
    }

    private ProviderState MapProvider(ProviderDB entity)
    {
        var state = Register(entity, new ProviderState { Name = entity.Name });
        state.ProviderKey = entity.ProviderKey;
        state.ApiEndpoint = entity.ApiEndpoint;
        state.EncryptedApiKey = entity.EncryptedApiKey;
        state.Models = entity.Models.Select(Map).ToList();
        return state;
    }

    private ToolAwarenessSetState MapToolAwarenessSet(ToolAwarenessSetDB entity)
    {
        var state = Register(
            entity,
            new ToolAwarenessSetState { Name = entity.Name });
        state.Tools = new Dictionary<string, bool>(
            entity.Tools,
            StringComparer.OrdinalIgnoreCase);
        return state;
    }

    private UserState MapUser(UserDB entity)
    {
        var state = Register(entity, new UserState
        {
            Username = entity.Username,
            PasswordHash = entity.PasswordHash.ToArray(),
            PasswordSalt = entity.PasswordSalt.ToArray()
        });
        state.IsUserAdmin = entity.IsUserAdmin;
        state.Bio = entity.Bio;
        state.AccessTokensInvalidatedAt = entity.AccessTokensInvalidatedAt;
        state.RoleId = entity.RoleId;
        state.Role = entity.Role is null ? null : Map(entity.Role);
        return state;
    }

    private RoleState MapRole(RoleDB entity)
    {
        var state = Register(entity, new RoleState { Name = entity.Name });
        state.PermissionSetId = entity.PermissionSetId;
        state.PermissionSet = entity.PermissionSet is null
            ? null
            : Map(entity.PermissionSet);
        state.Users = entity.Users.Select(Map).ToList();
        return state;
    }

    private PermissionSetState MapPermissionSet(PermissionSetDB entity)
    {
        var state = Register(entity, new PermissionSetState());
        state.GlobalFlags = entity.GlobalFlags.Select(MapGlobalFlag).ToList();
        state.ResourceAccesses = entity.ResourceAccesses
            .Select(MapResourceAccess).ToList();
        state.ClearanceUserWhitelist = entity.ClearanceUserWhitelist
            .Select(entry => entry.UserId).ToHashSet();
        state.ClearanceAgentWhitelist = entity.ClearanceAgentWhitelist
            .Select(entry => entry.AgentId).ToHashSet();
        return state;
    }

    private GlobalFlagState MapGlobalFlag(GlobalFlagDB entity)
    {
        var state = Register(
            entity,
            new GlobalFlagState { FlagKey = entity.FlagKey });
        state.Clearance = entity.Clearance;
        state.PermissionSetId = entity.PermissionSetId;
        return state;
    }

    private ResourceAccessState MapResourceAccess(ResourceAccessDB entity)
    {
        var state = Register(entity, new ResourceAccessState
        {
            ResourceType = entity.ResourceType
        });
        state.ResourceId = entity.ResourceId;
        state.Clearance = entity.Clearance;
        state.PermissionSetId = entity.PermissionSetId;
        state.SubType = entity.SubType;
        state.AccessLevel = entity.AccessLevel;
        state.IsDefault = entity.IsDefault;
        return state;
    }

    private ChannelState MapChannel(ChannelDB entity)
    {
        var state = Register(entity, new ChannelState { Title = entity.Title });
        state.AgentId = entity.AgentId;
        state.Agent = entity.Agent is null ? null : Map(entity.Agent);
        state.AgentContextId = entity.AgentContextId;
        state.AgentContext = entity.AgentContext is null
            ? null
            : Map(entity.AgentContext);
        state.PermissionSetId = entity.PermissionSetId;
        state.PermissionSet = entity.PermissionSet is null
            ? null
            : Map(entity.PermissionSet);
        state.DefaultResourceSetId = entity.DefaultResourceSetId;
        state.DefaultResourceSet = entity.DefaultResourceSet is null
            ? null
            : Map(entity.DefaultResourceSet);
        state.DisableChatHeader = entity.DisableChatHeader;
        state.CustomChatHeader = entity.CustomChatHeader;
        state.DisableToolSchemas = entity.DisableToolSchemas;
        state.ToolAwarenessSetId = entity.ToolAwarenessSetId;
        state.ToolAwarenessSet = entity.ToolAwarenessSet is null
            ? null
            : Map(entity.ToolAwarenessSet);
        state.AllowedAgents = entity.AllowedAgents.Select(Map).ToList();
        state.ChatMessages = entity.ChatMessages.Select(Map).ToList();
        state.Threads = entity.Threads.Select(Map).ToList();
        return state;
    }

    private ChannelContextState MapContext(ChannelContextDB entity)
    {
        var state = Register(entity, new ChannelContextState
        {
            Name = entity.Name,
            Agent = null!
        });
        state.AgentId = entity.AgentId;
        if (entity.Agent is not null)
            state.Agent = Map(entity.Agent);
        state.PermissionSetId = entity.PermissionSetId;
        state.PermissionSet = entity.PermissionSet is null
            ? null
            : Map(entity.PermissionSet);
        state.DefaultResourceSetId = entity.DefaultResourceSetId;
        state.DefaultResourceSet = entity.DefaultResourceSet is null
            ? null
            : Map(entity.DefaultResourceSet);
        state.DisableChatHeader = entity.DisableChatHeader;
        state.AllowedAgents = entity.AllowedAgents.Select(Map).ToList();
        state.Channels = entity.Channels.Select(Map).ToList();
        return state;
    }

    private ChatThreadState MapThread(ChatThreadDB entity)
    {
        var state = Register(entity, new ChatThreadState
        {
            Name = entity.Name,
            Channel = null!
        });
        state.MaxMessages = entity.MaxMessages;
        state.MaxCharacters = entity.MaxCharacters;
        state.ChannelId = entity.ChannelId;
        if (entity.Channel is not null)
            state.Channel = Map(entity.Channel);
        state.ChatMessages = entity.ChatMessages.Select(Map).ToList();
        return state;
    }

    private ChatMessageState MapMessage(ChatMessageDB entity)
    {
        var state = Register(entity, new ChatMessageState
        {
            Role = entity.Role,
            Content = entity.Content,
            Channel = null!
        });
        state.Origin = entity.Origin;
        state.ProviderMetadataJson = entity.ProviderMetadataJson;
        state.ChannelId = entity.ChannelId;
        if (entity.Channel is not null)
            state.Channel = Map(entity.Channel);
        state.ThreadId = entity.ThreadId;
        state.Thread = entity.Thread is null ? null : Map(entity.Thread);
        state.SenderUserId = entity.SenderUserId;
        state.SenderUsername = entity.SenderUsername;
        state.SenderAgentId = entity.SenderAgentId;
        state.SenderAgentName = entity.SenderAgentName;
        state.PermissionRoleId = entity.PermissionRoleId;
        state.PermissionRoleName = entity.PermissionRoleName;
        state.ClientType = entity.ClientType;
        state.PromptTokens = entity.PromptTokens;
        state.CompletionTokens = entity.CompletionTokens;
        return state;
    }

    private DefaultResourceSetState MapDefaultResourceSet(DefaultResourceSetDB entity)
    {
        var state = Register(entity, new DefaultResourceSetState());
        state.Entries = entity.Entries.Select(Map).ToList();
        return state;
    }

    private DefaultResourceEntryState MapDefaultResourceEntry(
        DefaultResourceEntryDB entity)
    {
        var state = Register(entity, new DefaultResourceEntryState());
        state.DefaultResourceSetId = entity.DefaultResourceSetId;
        state.ResourceKey = entity.ResourceKey;
        state.ResourceId = entity.ResourceId;
        return state;
    }

    private TaskDefinitionState MapTaskDefinition(TaskDefinitionDB entity)
    {
        var state = Register(entity, new TaskDefinitionState
        {
            Name = entity.Name,
            SourceText = entity.SourceText
        });
        state.Description = entity.Description;
        state.OutputTypeName = entity.OutputTypeName;
        state.ParametersJson = entity.ParametersJson;
        state.RequirementsJson = entity.RequirementsJson;
        state.TriggersJson = entity.TriggersJson;
        state.IsActive = entity.IsActive;
        state.TriggerBindings = entity.TriggerBindings.Select(Map).ToList();
        return state;
    }

    private TaskTriggerBindingState MapTaskTriggerBinding(TaskTriggerBindingDB entity)
    {
        var state = Register(entity, new TaskTriggerBindingState
        {
            Kind = entity.Kind,
            DefinitionJson = entity.DefinitionJson
        });
        state.TaskDefinitionId = entity.TaskDefinitionId;
        state.TriggerValue = entity.TriggerValue;
        state.Filter = entity.Filter;
        state.IsEnabled = entity.IsEnabled;
        return state;
    }

    private TState Register<TState>(BaseEntity entity, TState state)
        where TState : DomainState
    {
        state.Id = entity.Id;
        state.CreatedAt = entity.CreatedAt;
        state.UpdatedAt = entity.UpdatedAt;
        state.CustomId = entity.CustomId;
        RegisterAssociation(entity, state);
        return state;
    }

    private void RegisterAssociation(BaseEntity entity, DomainState state)
    {
        _entityToState.Add(entity, state);
        _stateToEntity.Add(state, entity);
    }

    private BaseEntity EnsureEntity(DomainState state)
    {
        if (_stateToEntity.TryGetValue(state, out var existing))
            return existing;

        BaseEntity created = state switch
        {
            AgentState value => new AgentDB
            {
                Name = value.Name,
                Model = null!
            },
            ModelState value => new ModelDB
            {
                Name = value.Name,
                Provider = null!
            },
            ProviderState value => new ProviderDB { Name = value.Name },
            ToolAwarenessSetState value => new ToolAwarenessSetDB { Name = value.Name },
            UserState value => new UserDB
            {
                Username = value.Username,
                PasswordHash = value.PasswordHash.ToArray(),
                PasswordSalt = value.PasswordSalt.ToArray()
            },
            RoleState value => new RoleDB { Name = value.Name },
            PermissionSetState => new PermissionSetDB(),
            GlobalFlagState value => new GlobalFlagDB
            {
                FlagKey = value.FlagKey,
                PermissionSet = null!
            },
            ResourceAccessState value => new ResourceAccessDB
            {
                ResourceType = value.ResourceType,
                PermissionSet = null!
            },
            ChannelState value => new ChannelDB { Title = value.Title },
            ChannelContextState value => new ChannelContextDB
            {
                Name = value.Name,
                Agent = null!
            },
            ChatThreadState value => new ChatThreadDB
            {
                Name = value.Name,
                Channel = null!
            },
            ChatMessageState value => new ChatMessageDB
            {
                Role = value.Role,
                Content = value.Content,
                Channel = null!
            },
            DefaultResourceSetState => new DefaultResourceSetDB(),
            DefaultResourceEntryState => new DefaultResourceEntryDB(),
            TaskDefinitionState value => new TaskDefinitionDB
            {
                Name = value.Name,
                SourceText = value.SourceText
            },
            TaskTriggerBindingState value => new TaskTriggerBindingDB
            {
                TaskDefinitionId = value.TaskDefinitionId,
                Kind = value.Kind,
                DefinitionJson = value.DefinitionJson
            },
            _ => throw new NotSupportedException(
                $"No EF entity mapping exists for {state.GetType().FullName}.")
        };

        RegisterAssociation(created, state);
        db.Add(created);
        return created;
    }

    private void Apply(DomainState state, BaseEntity entity)
    {
        ApplyBase(state, entity);
        switch (state, entity)
        {
            case (AgentState source, AgentDB target):
                ApplyAgent(source, target);
                break;
            case (ModelState source, ModelDB target):
                target.Name = source.Name;
                target.CapabilityTagsRaw = source.CapabilityTagsRaw;
                target.ProviderId = source.ProviderId;
                if (ReferenceMatches(source.ProviderId, source.Provider))
                    target.Provider = (ProviderDB)EnsureEntity(source.Provider);
                break;
            case (ProviderState source, ProviderDB target):
                target.Name = source.Name;
                target.ProviderKey = source.ProviderKey;
                target.ApiEndpoint = source.ApiEndpoint;
                target.EncryptedApiKey = source.EncryptedApiKey;
                break;
            case (ToolAwarenessSetState source, ToolAwarenessSetDB target):
                target.Name = source.Name;
                target.Tools = new Dictionary<string, bool>(
                    source.Tools,
                    StringComparer.OrdinalIgnoreCase);
                break;
            case (UserState source, UserDB target):
                target.Username = source.Username;
                target.PasswordHash = source.PasswordHash.ToArray();
                target.PasswordSalt = source.PasswordSalt.ToArray();
                target.IsUserAdmin = source.IsUserAdmin;
                target.Bio = source.Bio;
                target.AccessTokensInvalidatedAt = source.AccessTokensInvalidatedAt;
                target.RoleId = source.RoleId;
                if (source.RoleId is null)
                    target.Role = null;
                else if (ReferenceMatches(source.RoleId, source.Role))
                    target.Role = (RoleDB)EnsureEntity(source.Role!);
                break;
            case (RoleState source, RoleDB target):
                target.Name = source.Name;
                target.PermissionSetId = source.PermissionSetId;
                if (source.PermissionSetId is null)
                {
                    if (ReferenceMatches(source.PermissionSetId, source.PermissionSet))
                        target.PermissionSet = (PermissionSetDB)EnsureEntity(
                            source.PermissionSet!);
                    else
                        target.PermissionSet = null;
                }
                else if (ReferenceMatches(source.PermissionSetId, source.PermissionSet))
                {
                    target.PermissionSet = (PermissionSetDB)EnsureEntity(
                        source.PermissionSet!);
                }
                break;
            case (PermissionSetState source, PermissionSetDB target):
                ApplyPermissionSet(source, target);
                break;
            case (GlobalFlagState source, GlobalFlagDB target):
                target.FlagKey = source.FlagKey;
                target.Clearance = source.Clearance;
                if (source.PermissionSetId != Guid.Empty)
                    target.PermissionSetId = source.PermissionSetId;
                break;
            case (ResourceAccessState source, ResourceAccessDB target):
                target.ResourceType = source.ResourceType;
                target.ResourceId = source.ResourceId;
                target.Clearance = source.Clearance;
                if (source.PermissionSetId != Guid.Empty)
                    target.PermissionSetId = source.PermissionSetId;
                target.SubType = source.SubType;
                target.AccessLevel = source.AccessLevel;
                target.IsDefault = source.IsDefault;
                break;
            case (ChannelState source, ChannelDB target):
                ApplyChannel(source, target);
                break;
            case (ChannelContextState source, ChannelContextDB target):
                ApplyContext(source, target);
                break;
            case (ChatThreadState source, ChatThreadDB target):
                target.Name = source.Name;
                target.MaxMessages = source.MaxMessages;
                target.MaxCharacters = source.MaxCharacters;
                target.ChannelId = source.ChannelId;
                break;
            case (ChatMessageState source, ChatMessageDB target):
                ApplyMessage(source, target);
                break;
            case (DefaultResourceSetState source, DefaultResourceSetDB target):
                SyncOwnedCollection(
                    source.Entries,
                    target.Entries,
                    entry => (DefaultResourceEntryDB)EnsureEntity(entry));
                break;
            case (DefaultResourceEntryState source, DefaultResourceEntryDB target):
                if (source.DefaultResourceSetId != Guid.Empty)
                    target.DefaultResourceSetId = source.DefaultResourceSetId;
                target.ResourceKey = source.ResourceKey;
                target.ResourceId = source.ResourceId;
                break;
            case (TaskDefinitionState source, TaskDefinitionDB target):
                target.Name = source.Name;
                target.Description = source.Description;
                target.SourceText = source.SourceText;
                target.OutputTypeName = source.OutputTypeName;
                target.ParametersJson = source.ParametersJson;
                target.RequirementsJson = source.RequirementsJson;
                target.TriggersJson = source.TriggersJson;
                target.IsActive = source.IsActive;
                break;
            case (TaskTriggerBindingState source, TaskTriggerBindingDB target):
                target.TaskDefinitionId = source.TaskDefinitionId;
                target.Kind = source.Kind;
                target.TriggerValue = source.TriggerValue;
                target.Filter = source.Filter;
                target.DefinitionJson = source.DefinitionJson;
                target.IsEnabled = source.IsEnabled;
                break;
        }
    }

    private static void ApplyBase(DomainState source, BaseEntity target)
    {
        if (source.Id != Guid.Empty)
            target.Id = source.Id;
        if (source.CreatedAt != default)
            target.CreatedAt = source.CreatedAt;
        if (source.UpdatedAt != default)
            target.UpdatedAt = source.UpdatedAt;
        target.CustomId = source.CustomId;
    }

    private void ApplyAgent(AgentState source, AgentDB target)
    {
        target.Name = source.Name;
        target.SystemPrompt = source.SystemPrompt;
        target.MaxCompletionTokens = source.MaxCompletionTokens;
        target.Temperature = source.Temperature;
        target.TopP = source.TopP;
        target.TopK = source.TopK;
        target.FrequencyPenalty = source.FrequencyPenalty;
        target.PresencePenalty = source.PresencePenalty;
        target.Stop = source.Stop?.ToArray();
        target.Seed = source.Seed;
        target.ResponseFormat = source.ResponseFormat;
        target.ReasoningEffort = source.ReasoningEffort;
        target.ProviderParameters = source.ProviderParameters is null
            ? null
            : new Dictionary<string, System.Text.Json.JsonElement>(
                source.ProviderParameters,
                StringComparer.Ordinal);
        target.CustomChatHeader = source.CustomChatHeader;
        target.DisableToolSchemas = source.DisableToolSchemas;
        target.ToolAwarenessSetId = source.ToolAwarenessSetId;
        target.ModelId = source.ModelId;
        if (ReferenceMatches(source.ModelId, source.Model))
            target.Model = (ModelDB)EnsureEntity(source.Model);
        target.RoleId = source.RoleId;
        if (source.RoleId is null)
            target.Role = null;
        else if (ReferenceMatches(source.RoleId, source.Role))
            target.Role = (RoleDB)EnsureEntity(source.Role!);
    }

    private void ApplyPermissionSet(
        PermissionSetState source,
        PermissionSetDB target)
    {
        SyncOwnedCollection(
            source.GlobalFlags,
            target.GlobalFlags,
            flag =>
            {
                var entity = (GlobalFlagDB)EnsureEntity(flag);
                entity.PermissionSet = target;
                return entity;
            });
        SyncOwnedCollection(
            source.ResourceAccesses,
            target.ResourceAccesses,
            access =>
            {
                var entity = (ResourceAccessDB)EnsureEntity(access);
                entity.PermissionSet = target;
                return entity;
            });
        SyncUserWhitelist(source.ClearanceUserWhitelist, target);
        SyncAgentWhitelist(source.ClearanceAgentWhitelist, target);
    }

    private void ApplyChannel(ChannelState source, ChannelDB target)
    {
        target.Title = source.Title;
        target.AgentId = source.AgentId;
        if (source.AgentId is null)
            target.Agent = null;
        else if (ReferenceMatches(source.AgentId, source.Agent))
            target.Agent = (AgentDB)EnsureEntity(source.Agent!);
        target.AgentContextId = source.AgentContextId;
        if (source.AgentContextId is null)
            target.AgentContext = null;
        else if (ReferenceMatches(source.AgentContextId, source.AgentContext))
            target.AgentContext = (ChannelContextDB)EnsureEntity(
                source.AgentContext!);
        target.PermissionSetId = source.PermissionSetId;
        if (source.PermissionSetId is null)
            target.PermissionSet = null;
        else if (ReferenceMatches(source.PermissionSetId, source.PermissionSet))
            target.PermissionSet = (PermissionSetDB)EnsureEntity(
                source.PermissionSet!);
        target.DefaultResourceSetId = source.DefaultResourceSetId;
        if (source.DefaultResourceSet is null)
            target.DefaultResourceSet = null;
        else if (ReferenceMatches(
                     source.DefaultResourceSetId,
                     source.DefaultResourceSet))
            target.DefaultResourceSet = (DefaultResourceSetDB)EnsureEntity(
                source.DefaultResourceSet!);
        else
            target.DefaultResourceSet = null;
        target.DisableChatHeader = source.DisableChatHeader;
        target.CustomChatHeader = source.CustomChatHeader;
        target.DisableToolSchemas = source.DisableToolSchemas;
        target.ToolAwarenessSetId = source.ToolAwarenessSetId;
        if (source.ToolAwarenessSetId is null)
            target.ToolAwarenessSet = null;
        else if (ReferenceMatches(
                     source.ToolAwarenessSetId,
                     source.ToolAwarenessSet))
            target.ToolAwarenessSet = (ToolAwarenessSetDB)EnsureEntity(
                source.ToolAwarenessSet!);
        SyncRelationship(
            source.AllowedAgents,
            target.AllowedAgents,
            agent => (AgentDB)EnsureEntity(agent));
    }

    private void ApplyContext(ChannelContextState source, ChannelContextDB target)
    {
        target.Name = source.Name;
        target.AgentId = source.AgentId;
        if (ReferenceMatches(source.AgentId, source.Agent))
            target.Agent = (AgentDB)EnsureEntity(source.Agent);
        target.PermissionSetId = source.PermissionSetId;
        if (source.PermissionSetId is null)
            target.PermissionSet = null;
        else if (ReferenceMatches(source.PermissionSetId, source.PermissionSet))
            target.PermissionSet = (PermissionSetDB)EnsureEntity(
                source.PermissionSet!);
        target.DefaultResourceSetId = source.DefaultResourceSetId;
        if (source.DefaultResourceSet is null)
            target.DefaultResourceSet = null;
        else if (ReferenceMatches(
                     source.DefaultResourceSetId,
                     source.DefaultResourceSet))
            target.DefaultResourceSet = (DefaultResourceSetDB)EnsureEntity(
                source.DefaultResourceSet!);
        else
            target.DefaultResourceSet = null;
        target.DisableChatHeader = source.DisableChatHeader;
        SyncRelationship(
            source.AllowedAgents,
            target.AllowedAgents,
            agent => (AgentDB)EnsureEntity(agent));
    }

    private void ApplyMessage(ChatMessageState source, ChatMessageDB target)
    {
        target.Role = source.Role;
        target.Origin = source.Origin;
        target.Content = source.Content;
        target.ProviderMetadataJson = source.ProviderMetadataJson;
        target.ChannelId = source.ChannelId;
        target.ThreadId = source.ThreadId;
        target.SenderUserId = source.SenderUserId;
        target.SenderUsername = source.SenderUsername;
        target.SenderAgentId = source.SenderAgentId;
        target.SenderAgentName = source.SenderAgentName;
        target.PermissionRoleId = source.PermissionRoleId;
        target.PermissionRoleName = source.PermissionRoleName;
        target.ClientType = source.ClientType;
        target.PromptTokens = source.PromptTokens;
        target.CompletionTokens = source.CompletionTokens;
    }

    private void SyncOwnedCollection<TState, TEntity>(
        ICollection<TState> source,
        ICollection<TEntity> target,
        Func<TState, TEntity> resolve)
        where TState : DomainState
        where TEntity : BaseEntity
    {
        var desired = Enumerable.ToHashSet<TEntity>(
            source.Select(resolve),
            ReferenceEqualityComparer.Instance);
        foreach (var existing in target.ToList())
        {
            if (desired.Contains(existing))
                continue;
            target.Remove(existing);
            db.Remove(existing);
        }
        foreach (var entity in desired)
        {
            if (!target.Contains(entity))
                target.Add(entity);
        }
    }

    private static void SyncRelationship<TState, TEntity>(
        ICollection<TState> source,
        ICollection<TEntity> target,
        Func<TState, TEntity> resolve)
        where TState : DomainState
        where TEntity : BaseEntity
    {
        var desired = Enumerable.ToHashSet<TEntity>(
            source.Select(resolve),
            ReferenceEqualityComparer.Instance);
        foreach (var existing in target.ToList())
        {
            if (!desired.Contains(existing))
                target.Remove(existing);
        }
        foreach (var entity in desired)
        {
            if (!target.Contains(entity))
                target.Add(entity);
        }
    }

    private void SyncUserWhitelist(
        ISet<Guid> source,
        PermissionSetDB target)
    {
        foreach (var existing in target.ClearanceUserWhitelist.ToList())
        {
            if (source.Contains(existing.UserId))
                continue;
            target.ClearanceUserWhitelist.Remove(existing);
            db.Remove(existing);
        }

        var existingIds = target.ClearanceUserWhitelist
            .Select(entry => entry.UserId)
            .ToHashSet();
        foreach (var userId in source.Where(id => !existingIds.Contains(id)))
        {
            target.ClearanceUserWhitelist.Add(
                new ClearanceUserWhitelistEntryDB
                {
                    PermissionSet = target,
                    UserId = userId,
                    User = null!
                });
        }
    }

    private void SyncAgentWhitelist(
        ISet<Guid> source,
        PermissionSetDB target)
    {
        foreach (var existing in target.ClearanceAgentWhitelist.ToList())
        {
            if (source.Contains(existing.AgentId))
                continue;
            target.ClearanceAgentWhitelist.Remove(existing);
            db.Remove(existing);
        }

        var existingIds = target.ClearanceAgentWhitelist
            .Select(entry => entry.AgentId)
            .ToHashSet();
        foreach (var agentId in source.Where(id => !existingIds.Contains(id)))
        {
            target.ClearanceAgentWhitelist.Add(
                new ClearanceAgentWhitelistEntryDB
                {
                    PermissionSet = target,
                    AgentId = agentId,
                    Agent = null!
                });
        }
    }

    private static bool ReferenceMatches(
        Guid foreignKey,
        DomainState? reference) =>
        reference is not null
        && (reference.Id == Guid.Empty || reference.Id == foreignKey);

    private static bool ReferenceMatches(
        Guid? foreignKey,
        DomainState? reference) =>
        reference is not null
        && (reference.Id == Guid.Empty
            ? foreignKey is null || foreignKey == Guid.Empty
            : foreignKey == reference.Id);

    private void RefreshAll()
    {
        foreach (var (state, entity) in _stateToEntity)
        {
            state.Id = entity.Id;
            state.CreatedAt = entity.CreatedAt;
            state.UpdatedAt = entity.UpdatedAt;
            state.CustomId = entity.CustomId;
            switch (state, entity)
            {
                case (AgentState source, AgentDB target):
                    source.ModelId = target.ModelId;
                    source.RoleId = target.RoleId;
                    source.ToolAwarenessSetId = target.ToolAwarenessSetId;
                    break;
                case (ModelState source, ModelDB target):
                    source.ProviderId = target.ProviderId;
                    break;
                case (RoleState source, RoleDB target):
                    source.PermissionSetId = target.PermissionSetId;
                    break;
                case (GlobalFlagState source, GlobalFlagDB target):
                    source.PermissionSetId = target.PermissionSetId;
                    break;
                case (ResourceAccessState source, ResourceAccessDB target):
                    source.PermissionSetId = target.PermissionSetId;
                    break;
                case (ChannelState source, ChannelDB target):
                    source.AgentId = target.AgentId;
                    source.AgentContextId = target.AgentContextId;
                    source.PermissionSetId = target.PermissionSetId;
                    source.DefaultResourceSetId = target.DefaultResourceSetId;
                    source.ToolAwarenessSetId = target.ToolAwarenessSetId;
                    break;
                case (ChannelContextState source, ChannelContextDB target):
                    source.AgentId = target.AgentId;
                    source.PermissionSetId = target.PermissionSetId;
                    source.DefaultResourceSetId = target.DefaultResourceSetId;
                    break;
                case (ChatThreadState source, ChatThreadDB target):
                    source.ChannelId = target.ChannelId;
                    break;
                case (ChatMessageState source, ChatMessageDB target):
                    source.ChannelId = target.ChannelId;
                    source.ThreadId = target.ThreadId;
                    break;
                case (DefaultResourceEntryState source, DefaultResourceEntryDB target):
                    source.DefaultResourceSetId = target.DefaultResourceSetId;
                    break;
                case (TaskTriggerBindingState source, TaskTriggerBindingDB target):
                    source.TaskDefinitionId = target.TaskDefinitionId;
                    break;
            }
        }
    }
}

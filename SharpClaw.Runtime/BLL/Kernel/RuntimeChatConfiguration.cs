using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

internal static class RuntimeChatConfiguration
{
    public const string RequiredErrorMessage = "Provider setup required. Choose a provider and model in Settings before sending chat requests.";

    public static string ResolveModel(ChatProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ModelName))
            return profile.ModelName;
        if (profile.ModelId != Guid.Empty)
            return profile.ModelId.ToString();
        throw new InvalidOperationException(RequiredErrorMessage);
    }
}

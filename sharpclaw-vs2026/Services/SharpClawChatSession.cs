using System;
using System.Threading;
using System.Threading.Tasks;
using SharpClaw.VS2026Extension.ToolWindows;

namespace SharpClaw.VS2026Extension.Services;

internal sealed class SharpClawChatSession
{
    private readonly object _gate = new();
    private WeakReference<SharpClawChatViewModel>? _activeViewModel;

    public void Register(SharpClawChatViewModel viewModel)
    {
        lock (_gate)
        {
            _activeViewModel = new WeakReference<SharpClawChatViewModel>(viewModel);
        }
    }

    public bool TryGetActiveViewModel(out SharpClawChatViewModel viewModel)
    {
        lock (_gate)
        {
            if (_activeViewModel is not null && _activeViewModel.TryGetTarget(out viewModel!))
                return !viewModel.IsDisposed;
        }

        viewModel = null!;
        return false;
    }

    public async Task<bool> RefreshActiveAsync(CancellationToken ct)
    {
        if (!TryGetActiveViewModel(out var viewModel))
            return false;

        await viewModel.RefreshAllAsync(preserveSelection: true, ct).ConfigureAwait(false);
        return true;
    }
}

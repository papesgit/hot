using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.Replays;

/// <summary>
/// Selects the active transport without leaking vMix or OBS details into replay policy.
/// </summary>
public sealed class ReplayMarkBackendRouter : IReplayBackend
{
    private readonly ReplaySettings _selection;
    private readonly IReplayBackend _vmix;
    private readonly IReplayBackend _obs;

    public ReplayMarkBackendRouter(ReplaySettings selection, IReplayBackend vmix, IReplayBackend obs)
    {
        _selection = selection;
        _vmix = vmix;
        _obs = obs;
    }

    private IReplayBackend? Active => _selection.IsObsReplaysSelected ? _obs : _selection.IsVmixSelected ? _vmix : null;
    public bool IsEnabled => Active?.IsEnabled == true;
    public bool IsAvailable => Active?.IsAvailable == true;
    public Task<ReplayEventHandle?> CreateAsync(ReplayCreateRequest request, CancellationToken token) => Active?.CreateAsync(request, token) ?? Task.FromResult<ReplayEventHandle?>(null);
    public Task<bool> ExtendAsync(ReplayEventHandle handle, ReplayExtendRequest request, CancellationToken token) => Active?.ExtendAsync(handle, request, token) ?? Task.FromResult(false);
}

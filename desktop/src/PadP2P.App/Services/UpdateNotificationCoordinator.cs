namespace PadP2P.App.Services;

internal sealed class UpdateNotificationCoordinator
{
    private const int MaximumRememberedChecks = 32;
    private readonly object _sync = new();
    private readonly HashSet<Guid> _claimedChecks = [];
    private readonly Queue<Guid> _recentChecks = new();

    public bool TryClaim(UpdateRelease release)
    {
        lock (_sync)
        {
            if (!_claimedChecks.Add(release.CheckId)) return false;

            _recentChecks.Enqueue(release.CheckId);
            while (_recentChecks.Count > MaximumRememberedChecks)
                _claimedChecks.Remove(_recentChecks.Dequeue());

            return true;
        }
    }
}

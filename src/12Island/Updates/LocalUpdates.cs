using TwelveIsland.Core;

namespace TwelveIsland.Updates;

// This copy of the panel is built and updated by hand: it asks no server for a version. The update page still shows the
// running version and its notes, and a check simply finds nothing newer.
internal sealed class LocalUpdates : IAppUpdateBackend
{
    public string CurrentVersion => ApplicationVersion.Current;
    // Nothing is offered to install: the copy that runs is the one that was built.
    public bool CanInstall => false;
    public AppRelease? PendingRelease => null;

    public Task<AppRelease?> CheckAsync(CancellationToken cancellation) => Task.FromResult<AppRelease?>(null);

    public Task DownloadAsync(AppRelease release, Action<int> progress, CancellationToken cancellation) =>
        Task.FromException(new InvalidOperationException("This copy is updated by hand."));

    public void PrepareRestart() { }
}

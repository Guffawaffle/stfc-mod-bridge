using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal sealed class LauncherProviderSessionRecomposer<TSession> : IDisposable
    where TSession : class, IDisposable
{
    private readonly object sync = new();
    private readonly LauncherDistributionProviderCatalog catalog;
    private readonly Func<LauncherProviderSelectionResolution, TSession> compose;
    private TSession current;
    private LauncherProviderSelectionResolution currentResolution;
    private LauncherProviderSelectionResolution? pendingResolution;
    private bool isComposing;
    private bool isDisposed;

    public LauncherProviderSessionRecomposer(
        LauncherDistributionProviderCatalog catalog,
        LauncherProviderSelectionResolution initialResolution,
        Func<LauncherProviderSelectionResolution, TSession> compose)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.compose = compose ?? throw new ArgumentNullException(nameof(compose));
        currentResolution = initialResolution
            ?? throw new ArgumentNullException(nameof(initialResolution));
        current = compose(currentResolution)
            ?? throw new InvalidOperationException("Provider-session composition returned no session.");
    }

    public TSession Current
    {
        get
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return current;
            }
        }
    }

    public LauncherProviderSelectionResolution CurrentResolution
    {
        get
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return currentResolution;
            }
        }
    }

    public bool HasPendingRecomposition
    {
        get
        {
            lock (sync)
            {
                return pendingResolution is not null;
            }
        }
    }

    public TSession Recompose(LauncherProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Recompose(RequireResolved(LauncherProviderSelectionResolver.Resolve(catalog, selection)));
    }

    public TSession Retry()
    {
        LauncherProviderSelectionResolution resolution;
        lock (sync)
        {
            ThrowIfDisposed();
            resolution = pendingResolution
                ?? throw new InvalidOperationException("No provider-session recomposition is waiting to be retried.");
        }
        return Recompose(resolution);
    }

    public void Dispose()
    {
        TSession session;
        lock (sync)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            session = current;
            pendingResolution = null;
        }
        session.Dispose();
    }

    private TSession Recompose(LauncherProviderSelectionResolution resolution)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (isComposing)
            {
                throw new InvalidOperationException("Another provider-session recomposition is already active.");
            }

            isComposing = true;
            pendingResolution = resolution;
        }

        TSession next;
        try
        {
            next = compose(resolution)
                ?? throw new InvalidOperationException("Provider-session composition returned no session.");
        }
        catch
        {
            lock (sync)
            {
                isComposing = false;
            }
            throw;
        }

        TSession? previous = null;
        var disposedDuringComposition = false;
        lock (sync)
        {
            if (isDisposed)
            {
                disposedDuringComposition = true;
            }
            else
            {
                previous = current;
                current = next;
                currentResolution = resolution;
                pendingResolution = null;
            }
            isComposing = false;
        }
        if (disposedDuringComposition)
        {
            next.Dispose();
            throw new ObjectDisposedException(GetType().FullName);
        }

        previous!.Dispose();
        return next;
    }

    private static LauncherProviderSelectionResolution RequireResolved(
        LauncherProviderSelectionResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return resolution.IsResolved
            ? resolution
            : throw new InvalidOperationException(
                $"The provider session cannot be composed: {resolution.Message}");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
    }
}

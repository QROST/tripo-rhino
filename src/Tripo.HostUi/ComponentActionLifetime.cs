namespace Tripo.HostUi;

// Tracks one component attachment independently of the lifetime of an already
// dispatched request. Removing and restoring a component must not revive work
// that was queued before removal, or allow a second action to overlap it.
internal sealed class ComponentActionLifetime
{
    private readonly object _gate = new();
    private long _attachment;
    private long? _actionAttachment;
    private bool _attached;

    public bool IsActionCurrent
    {
        get
        {
            lock (_gate)
            {
                return _attached && _actionAttachment == _attachment;
            }
        }
    }

    public long? ActionAttachment
    {
        get
        {
            lock (_gate)
            {
                return _actionAttachment;
            }
        }
    }

    public void Attach()
    {
        lock (_gate)
        {
            if (!_attached)
            {
                _attachment++;
                _attached = true;
            }
        }
    }

    public void Remove(Action onIdleRemoval)
    {
        lock (_gate)
        {
            _attached = false;
            if (_actionAttachment is null)
            {
                onIdleRemoval();
            }
        }
    }

    public bool TryBeginAction()
    {
        lock (_gate)
        {
            if (!_attached || _actionAttachment is not null)
            {
                return false;
            }

            _actionAttachment = _attachment;
            return true;
        }
    }

    public void EndAction(Action onIdleRemoval)
    {
        lock (_gate)
        {
            _actionAttachment = null;
            if (!_attached)
            {
                onIdleRemoval();
            }
        }
    }

    public bool MatchesAttachment(long? attachment)
    {
        lock (_gate)
        {
            return _attached && attachment == _attachment;
        }
    }
}

namespace CompanionDeck.Host.Monitoring;

public enum PowerStep { None, Apply, Restore }

/// <summary>
/// "Performance while a game runs on AC, then back." Called once a second. It only ever puts back what
/// it changed, and only if nobody has changed it since: a mode picked by hand during the game is theirs
/// and is left alone. Reading the current mode can be costly, so it is asked for only at the moments a
/// decision depends on it, never once a second.
/// </summary>
public sealed class AutoPower
{
    public const int ApplyAfter = 5;
    public const int RestoreAfter = 15;

    private int _gaming, _idle;
    private bool _applied, _decided;
    private string? _previous;
    private string? _target;

    public bool Applied => _applied;

    /// <summary>The change was refused, so nothing was changed and there is nothing to put back.</summary>
    public void Refused() { _applied = false; _previous = null; _target = null; }

    public (PowerStep step, string? mode) Tick(bool enabled, string target, bool gaming, bool onAc, Func<string?> currentMode)
    {
        if (gaming) { _gaming++; _idle = 0; }
        else { _idle++; _gaming = 0; _decided = false; }

        if (_applied)
        {
            // Turned off, or the game is over: hand the machine back.
            if (!enabled || _idle >= RestoreAfter)
            {
                string? now = currentMode();
                bool untouched = now is null || now == _target;
                string? back = _previous;
                _applied = false; _previous = null; _target = null;
                if (untouched && back is not null) return (PowerStep.Restore, back);
            }
            return (PowerStep.None, null);
        }

        if (enabled && gaming && onAc && _gaming >= ApplyAfter && !_decided)
        {
            // Once per game: if the machine is already where we want it there is nothing to do or undo.
            _decided = true;
            string? now = currentMode();
            if (now is not null && now != target)
            {
                _applied = true;
                _previous = now;
                _target = target;
                return (PowerStep.Apply, target);
            }
        }
        return (PowerStep.None, null);
    }
}

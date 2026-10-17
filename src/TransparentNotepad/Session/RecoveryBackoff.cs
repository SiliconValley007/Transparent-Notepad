namespace TransparentNotepad.Session;

/// <summary>Retry schedule for re-opening a lost audio device: quick first retry, then backs off to a 5 s ceiling.</summary>
public static class RecoveryBackoff
{
    public static TimeSpan Delay(int failures) => failures switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromMilliseconds(400),
        2 => TimeSpan.FromSeconds(1),
        3 => TimeSpan.FromSeconds(2),
        4 => TimeSpan.FromSeconds(4),
        _ => TimeSpan.FromSeconds(5),
    };
}

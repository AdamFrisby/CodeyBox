namespace CodeyBox.Majordomo;

/// <summary>
/// Classification of a tool in the majordomo vocabulary. <see cref="Read"/>
/// tools observe the queue and always execute; <see cref="Mutate"/> tools
/// change it and pass through the autonomy-mode gate plus the per-turn
/// blast-radius bound in <see cref="MajordomoAuthorization"/>.
/// </summary>
public enum MajordomoToolClass
{
    Read,
    Mutate,
}

namespace CodeyBox.Majordomo;

/// <summary>
/// Base of the closed result union for the majordomo tool vocabulary. Like
/// <see cref="MajordomoToolArgs"/> the constructor is <c>internal</c> so the
/// set of result types is closed to this assembly.
/// </summary>
public abstract record MajordomoToolResult
{
    internal MajordomoToolResult() { }
}

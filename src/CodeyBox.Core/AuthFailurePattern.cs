namespace CodeyBox.Core;

/// <summary>
/// One config-driven auth/login-prompt pattern entry. Operators can append
/// stream-scoped entries via configuration (e.g.
/// <c>CodeyBox:AuthFailurePatterns:antigravity</c>) so newly-observed CLI
/// login prompts are recognised without recompilation. Patterns default to
/// stderr only because stdout can contain model-controlled task text.
/// </summary>
public sealed record AuthFailurePattern(
    string Pattern,
    AuthFailurePatternStream Stream = AuthFailurePatternStream.Stderr)
{
    public bool MatchesStderr => (Stream & AuthFailurePatternStream.Stderr) == AuthFailurePatternStream.Stderr;
    public bool MatchesStdout => (Stream & AuthFailurePatternStream.Stdout) == AuthFailurePatternStream.Stdout;
}

[Flags]
public enum AuthFailurePatternStream
{
    None = 0,
    Stderr = 1,
    Stdout = 2,
    StderrAndStdout = Stderr | Stdout,
}

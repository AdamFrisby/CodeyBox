namespace CodeyBox.Core;

/// <summary>
/// One config-driven auth/login-prompt pattern entry. Operators can append
/// entries via configuration (e.g. <c>CodeyBox:AuthFailurePatterns:antigravity</c>) so
/// newly-observed CLI login prompts are recognised without recompilation.
/// </summary>
public sealed record AuthFailurePattern(string Pattern);

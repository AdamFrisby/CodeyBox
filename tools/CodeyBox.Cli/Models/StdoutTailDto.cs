namespace CodeyBox.Cli.Models;

/// <summary>
/// CLI-side wrapper for the plain-text <c>/workitems/{id}/stdout-tail</c> response, used only to
/// render <c>queue logs --json</c>. The endpoint returns the raw tail as <c>text/plain</c>; the
/// id is echoed back from the request argument so <c>--json</c> output is self-describing.
/// </summary>
internal sealed record StdoutTailDto(string Id, string Tail);

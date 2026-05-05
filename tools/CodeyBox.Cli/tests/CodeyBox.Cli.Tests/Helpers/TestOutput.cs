namespace CodeyBox.Cli.Tests.Helpers;

/// <summary>
/// Captures Console.Out and Console.Error for the duration of a test,
/// restoring them on dispose.
/// </summary>
internal sealed class TestOutput : IDisposable
{
    private readonly TextWriter _prevOut;
    private readonly TextWriter _prevError;

    internal StringWriter Out { get; } = new();
    internal StringWriter Error { get; } = new();

    internal TestOutput()
    {
        _prevOut = Console.Out;
        _prevError = Console.Error;
        Console.SetOut(Out);
        Console.SetError(Error);
    }

    public void Dispose()
    {
        Console.SetOut(_prevOut);
        Console.SetError(_prevError);
        Out.Dispose();
        Error.Dispose();
    }
}

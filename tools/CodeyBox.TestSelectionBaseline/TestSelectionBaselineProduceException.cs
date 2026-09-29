namespace CodeyBox.TestSelectionProducer;

/// <summary>
/// Operational failure of the test-selection baseline producer. Cap misses use
/// the same wording as <see cref="CodeyBox.Core.TestSelectionBaselineParser"/>
/// so operators can match on "size cap" / "test cap" / "covered-line cap".
/// </summary>
public sealed class TestSelectionBaselineProduceException : InvalidOperationException
{
    public TestSelectionBaselineProduceException(string message)
        : base(message)
    {
    }

    public TestSelectionBaselineProduceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

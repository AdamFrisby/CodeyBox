using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Optional contributor that derives fixer-specific inputs from the active
/// auditor set. The generic Core fixer context sees only typed inputs; audit
/// and preset layers own any mapping from auditor implementation details to
/// those inputs.
/// </summary>
public interface IMechanicalFixerInputProvider
{
    IReadOnlyList<IMechanicalFixerInput> BuildInputs(IReadOnlyList<IAuditor> auditors);
}

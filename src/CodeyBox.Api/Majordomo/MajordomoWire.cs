namespace CodeyBox.Api.Majordomo;

/// <summary>
/// The machine-readable refusal <c>reason</c> values the majordomo surface
/// emits. This is the vocabulary a calling model branches on — a typo in a
/// scattered literal would silently change the contract, so every emission
/// site references these constants.
/// </summary>
internal static class MajordomoRefusalReasons
{
    public const string UnknownTool = "unknown_tool";
    public const string ArgumentContractMismatch = "argument_contract_mismatch";
    public const string TooManyItemsInOneCall = "too_many_items_in_one_call";
    public const string TurnMutationBudgetExhausted = "turn_mutation_budget_exhausted";
    public const string Refused = "refused";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string ValidationFailed = "validation_failed";
    public const string InvalidArguments = "invalid_arguments";
    public const string InvalidChain = "invalid_chain";
    public const string DependencyNotFound = "dependency_not_found";
    public const string DependencyTerminal = "dependency_terminal";
    public const string UnknownAgent = "unknown_agent";
}

/// <summary>
/// The envelope <c>outcome</c> labels a call settles into — the same strings
/// appear in the structured payload and the outcome audit record.
/// </summary>
internal static class MajordomoOutcomes
{
    public const string Executed = "executed";
    public const string DryRun = "dry_run";
    public const string Proposed = "proposed";
    public const string Refused = "refused";

    /// <summary>The call threw before it could settle — writes may have landed.</summary>
    public const string Error = "error";
}

/// <summary>The <c>decision</c> labels written to the pre-execution audit record.</summary>
internal static class MajordomoDecisions
{
    public const string Execute = "execute";
    public const string Propose = "propose";
    public const string Refuse = "refuse";
    public const string Unknown = "unknown";
}

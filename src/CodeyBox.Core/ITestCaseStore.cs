using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeyBox.Core;

/// <summary>
/// Durable store of test cases.
/// </summary>
public interface ITestCaseStore
{
    /// <summary>
    /// Creates a single test case.
    /// </summary>
    Task CreateAsync(TestCase testCase, CancellationToken ct = default);

    /// <summary>
    /// Creates multiple test cases in a single atomic transaction.
    /// </summary>
    Task BulkCreateAsync(IReadOnlyList<TestCase> testCases, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing test case. Returns true if a row was affected, false if no row with
    /// the given id existed (the typical signal for an HTTP 404).
    /// </summary>
    Task<bool> UpdateAsync(TestCase testCase, CancellationToken ct = default);

    /// <summary>
    /// Updates only the last-run fields for an existing test case. This avoids
    /// clobbering unrelated operator edits made while a long-running replay is active.
    /// </summary>
    Task<bool> UpdateLastRunAsync(string id, bool passed, DateTimeOffset ranAt, string result, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a test case by its unique ID.
    /// </summary>
    Task<TestCase?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Lists all test cases in the database, ordered by CreatedAt ASC.
    /// </summary>
    IAsyncEnumerable<TestCase> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all test cases linked to a specific work item, ordered by CreatedAt ASC.
    /// </summary>
    IAsyncEnumerable<TestCase> ListByWorkItemAsync(string workItemId, CancellationToken ct = default);

    /// <summary>
    /// Physically deletes a test case from the store. Returns true if a row was affected, false
    /// if no row with the given id existed.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

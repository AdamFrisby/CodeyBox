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
    /// Updates an existing test case.
    /// </summary>
    Task UpdateAsync(TestCase testCase, CancellationToken ct = default);

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
    /// Physically deletes a test case from the store.
    /// </summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}

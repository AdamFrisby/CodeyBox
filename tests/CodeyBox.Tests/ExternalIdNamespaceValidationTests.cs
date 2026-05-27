using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="Validation.ValidateExternalIdNamespace"/> and
/// <see cref="Validation.TryParseNamespacedExternalId"/>. These two helpers
/// drive the routing and validation of namespaced external-ID surface area
/// (POST/PATCH external-ids, dep resolution, list filter, route resolver) —
/// so the regex anchors and the colonIdx checks need direct coverage.
/// </summary>
public sealed class ExternalIdNamespaceValidationTests
{
    // ── ValidateExternalIdNamespace ──────────────────────────────────────────

    [Theory]
    [InlineData("github")]
    [InlineData("jobtrack")]
    [InlineData("linear")]
    [InlineData("custom-name")]
    [InlineData("a")]                 // 1-char minimum
    [InlineData("0")]                 // leading digit allowed
    [InlineData("a1-b2-c3")]
    public void ValidNamespaces_Accepted(string ns)
        => Validation.ValidateExternalIdNamespace(ns, "ns");

    [Fact]
    public void ExactlyMaxLength_Accepted()
        => Validation.ValidateExternalIdNamespace(new string('a', 32), "ns");

    [Theory]
    [InlineData("")]                  // empty
    [InlineData("Github")]            // uppercase
    [InlineData("FOO")]               // all uppercase
    [InlineData("foo_bar")]           // underscore not allowed
    [InlineData("foo.bar")]           // dot not allowed
    [InlineData("foo:bar")]           // colon not allowed (would collide with split char)
    [InlineData("-foo")]              // leading dash not allowed
    [InlineData("foo!")]              // punctuation not allowed
    [InlineData("foo bar")]           // space not allowed
    public void InvalidNamespaces_Rejected(string ns)
        => Assert.Throws<ArgumentException>(() => Validation.ValidateExternalIdNamespace(ns, "ns"));

    [Fact]
    public void OnePastMaxLength_Rejected()
        => Assert.Throws<ArgumentException>(() => Validation.ValidateExternalIdNamespace(new string('a', 33), "ns"));

    // ── TryParseNamespacedExternalId ─────────────────────────────────────────

    [Theory]
    [InlineData("github:PROJ-42", "github", "PROJ-42")]
    [InlineData("jobtrack:jt-178", "jobtrack", "jt-178")]
    [InlineData("custom-name:abc:def", "custom-name", "abc:def")] // only the first colon splits
    public void Parses_NamespacedForm(string input, string expectedNs, string expectedValue)
    {
        Assert.True(Validation.TryParseNamespacedExternalId(input, out var ns, out var value));
        Assert.Equal(expectedNs, ns);
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("bare-value")]        // no colon → bare
    [InlineData("alphanum1234")]
    public void BareValues_ReturnFalse_AndEcho(string input)
    {
        Assert.False(Validation.TryParseNamespacedExternalId(input, out var ns, out var value));
        Assert.Null(ns);
        Assert.Equal(input, value);
    }

    [Theory]
    [InlineData(":value")]            // colon at index 0
    [InlineData("ns:")]               // colon at the end
    [InlineData(":")]                 // colon is the only char
    [InlineData("")]                  // empty string
    public void EdgeColonPositions_ReturnFalse(string input)
    {
        // Either the namespace part is empty (colon-first) or the value is empty
        // (colon-last) — both must route to the bare-lookup path, not produce a
        // (ns, value) parse with an empty side.
        Assert.False(Validation.TryParseNamespacedExternalId(input, out var ns, out _));
        Assert.Null(ns);
    }

    [Theory]
    [InlineData("Github:foo")]        // uppercase namespace → not parsed
    [InlineData("foo_bar:baz")]       // underscore in namespace → not parsed
    [InlineData("-foo:baz")]          // leading dash in namespace → not parsed
    [InlineData("ns space:v")]        // space in namespace → not parsed
    public void InvalidNamespaceChars_FallThroughToBare(string input)
    {
        // The whole string must be treated as a bare value when the leading
        // token doesn't match the namespace regex; the route resolver relies
        // on this to avoid mis-routing colon-bearing legacy values.
        Assert.False(Validation.TryParseNamespacedExternalId(input, out var ns, out var value));
        Assert.Null(ns);
        Assert.Equal(input, value);
    }
}

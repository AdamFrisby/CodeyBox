using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class ExternalIdValidationTests
{
    // ── Valid values ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("JIRA-1234")]
    [InlineData("GH-456")]
    [InlineData("internal-tracker-ID")]
    [InlineData("a")]
    [InlineData("abc_123")]
    [InlineData("foo:bar")]
    [InlineData("A.B.C")]
    [InlineData("some-id-with-many-parts")]
    [InlineData("!bang")]
    [InlineData("0123456789")]
    public void ValidExternalIds(string value) => Validation.ValidateExternalId(value, "externalId");

    // ── Empty / null ──────────────────────────────────────────────────────────

    [Fact]
    public void EmptyString_Rejected()
        => Assert.Throws<ArgumentException>(() => Validation.ValidateExternalId("", "externalId"));

    // ── Length ────────────────────────────────────────────────────────────────

    [Fact]
    public void ExactlyMaxLength_Accepted()
    {
        var value = new string('a', 256);
        Validation.ValidateExternalId(value, "externalId"); // must not throw
    }

    [Fact]
    public void OnePastMaxLength_Rejected()
    {
        var value = new string('a', 257);
        Assert.Throws<ArgumentException>(() => Validation.ValidateExternalId(value, "externalId"));
    }

    // ── Disallowed characters ─────────────────────────────────────────────────

    [Theory]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    [InlineData("has\nnewline")]
    [InlineData("with/slash")]
    [InlineData("with?question")]
    [InlineData("ctrl\x01char")]
    [InlineData("del\x7Fchar")]
    [InlineData("non-ascii-\x80")]
    public void DisallowedCharacters_Rejected(string value)
        => Assert.Throws<ArgumentException>(() => Validation.ValidateExternalId(value, "externalId"));

    // ── Reserved prefix ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("wi-anything")]
    [InlineData("wi-1234")]
    [InlineData("WI-abc")]   // case-insensitive check
    public void ReservedPrefix_Rejected(string value)
        => Assert.Throws<ArgumentException>(() => Validation.ValidateExternalId(value, "externalId"));

    // ── UUID collision ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12345678-1234-1234-1234-123456789012")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
    public void UuidLikeSyntax_Rejected(string value)
        => Assert.Throws<ArgumentException>(() => Validation.ValidateExternalId(value, "externalId"));

    // ── Non-UUID strings that look similar but aren't ─────────────────────────

    [Theory]
    [InlineData("12345678-1234-1234-1234-12345678901")]   // one char short
    [InlineData("abcdef")]
    [InlineData("JIRA-1234-5678")]
    public void NonUuidLookAlikes_Accepted(string value)
        => Validation.ValidateExternalId(value, "externalId"); // must not throw
}

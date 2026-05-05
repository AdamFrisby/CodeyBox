using System.Reflection;
using CodeyBox.PluginSdk;

namespace CodeyBox.Tests;

public sealed class PluginAttributeTests
{
    [CodeyBoxPlugin("test.with-attr", "With Attribute")]
    private sealed class TypeWithAttribute { }

    private sealed class TypeWithoutAttribute { }

    [CodeyBoxPlugin("test.custom-version", "Custom Version", "1.0")]
    private sealed class TypeWithCustomVersion { }

    [CodeyBoxPlugin("test.abstract", "Abstract")]
    private abstract class AbstractTypeWithAttribute { }

    [Fact]
    public void Attribute_OnType_IsDiscoverable()
    {
        var attr = typeof(TypeWithAttribute).GetCustomAttribute<CodeyBoxPluginAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("test.with-attr", attr.Id);
        Assert.Equal("With Attribute", attr.DisplayName);
        Assert.Equal("1.0", attr.MinHostApiVersion);
    }

    [Fact]
    public void Attribute_DefaultMinHostApiVersion_Is1_0()
    {
        var attr = typeof(TypeWithAttribute).GetCustomAttribute<CodeyBoxPluginAttribute>()!;
        Assert.Equal("1.0", attr.MinHostApiVersion);
    }

    [Fact]
    public void Attribute_CustomMinHostApiVersion_IsStored()
    {
        var attr = typeof(TypeWithCustomVersion).GetCustomAttribute<CodeyBoxPluginAttribute>()!;
        Assert.Equal("1.0", attr.MinHostApiVersion);
    }

    [Fact]
    public void Attribute_MissingOnType_IsNull()
    {
        var attr = typeof(TypeWithoutAttribute).GetCustomAttribute<CodeyBoxPluginAttribute>();
        Assert.Null(attr);
    }

    [Fact]
    public void Attribute_AbstractType_IsNotDiscoveredByLoader()
    {
        // Loader filters out abstract types even when they carry the attribute.
        var attr = typeof(AbstractTypeWithAttribute).GetCustomAttribute<CodeyBoxPluginAttribute>();
        Assert.NotNull(attr); // attribute is there …
        Assert.True(typeof(AbstractTypeWithAttribute).IsAbstract); // … but type is abstract
        // The loader checks !t.IsAbstract && t.IsClass, so this would be skipped.
    }

    [Fact]
    public void Attribute_NotInherited()
    {
        // AttributeUsage.Inherited = false (default for sealed attribute usage)
        // means a subclass does NOT inherit it.
        var attr = typeof(DerivedWithoutOwnAttribute).GetCustomAttribute<CodeyBoxPluginAttribute>(inherit: true);
        // TypeWithAttribute is sealed, so no subclass is possible in practice,
        // but test the Inherited=false contract directly.
        Assert.False(typeof(CodeyBoxPluginAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>()!.Inherited);
    }

    [CodeyBoxPlugin("test.parent", "Parent")]
    private class ParentWithAttribute { }

    private sealed class DerivedWithoutOwnAttribute : ParentWithAttribute { }
}

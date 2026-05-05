using CodeyBox.Api;

namespace CodeyBox.Tests;

public sealed class OtelOptionsValidationTests
{
    [Fact]
    public void Disabled_DoesNotThrow_EvenWithNoEndpoint()
    {
        var opts = new OtelOptions { Enabled = false };
        OtelOptions.Validate(opts); // must not throw
    }

    [Fact]
    public void Disabled_DoesNotThrow_EvenWithInvalidProtocol()
    {
        var opts = new OtelOptions { Enabled = false, ExportProtocol = "totally-wrong" };
        OtelOptions.Validate(opts); // must not throw
    }

    [Fact]
    public void Enabled_NullEndpoint_Throws()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = null };
        var ex = Assert.Throws<InvalidOperationException>(() => OtelOptions.Validate(opts));
        Assert.Contains("OtlpEndpoint", ex.Message);
    }

    [Fact]
    public void Enabled_EmptyEndpoint_Throws()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "" };
        var ex = Assert.Throws<InvalidOperationException>(() => OtelOptions.Validate(opts));
        Assert.Contains("OtlpEndpoint", ex.Message);
    }

    [Fact]
    public void Enabled_WhitespaceEndpoint_Throws()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "   " };
        var ex = Assert.Throws<InvalidOperationException>(() => OtelOptions.Validate(opts));
        Assert.Contains("OtlpEndpoint", ex.Message);
    }

    [Fact]
    public void Enabled_NonUrlEndpoint_Throws()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "not-a-url" };
        var ex = Assert.Throws<InvalidOperationException>(() => OtelOptions.Validate(opts));
        Assert.Contains("not-a-url", ex.Message);
    }

    [Fact]
    public void Enabled_FileSchemeEndpoint_Throws()
    {
        // /relative/path is parsed as file:// on .NET — must reject non-http/https schemes.
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "/relative/path" };
        var ex = Assert.Throws<InvalidOperationException>(() => OtelOptions.Validate(opts));
        Assert.Contains("http/https", ex.Message);
    }

    [Fact]
    public void Enabled_NonHttpScheme_Throws()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "grpc://collector:4317" };
        var ex = Assert.Throws<InvalidOperationException>(() => OtelOptions.Validate(opts));
        Assert.Contains("http/https", ex.Message);
    }

    [Fact]
    public void Enabled_InvalidProtocol_Throws()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "http://localhost:4317", ExportProtocol = "invalid" };
        var ex = Assert.Throws<InvalidOperationException>(() => OtelOptions.Validate(opts));
        Assert.Contains("ExportProtocol", ex.Message);
        Assert.Contains("grpc", ex.Message);
    }

    [Fact]
    public void Enabled_GrpcProtocol_DoesNotThrow()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "http://localhost:4317", ExportProtocol = "grpc" };
        OtelOptions.Validate(opts); // must not throw
    }

    [Fact]
    public void Enabled_HttpProtobufProtocol_DoesNotThrow()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "http://localhost:4318", ExportProtocol = "httpprotobuf" };
        OtelOptions.Validate(opts); // must not throw
    }

    [Fact]
    public void Enabled_ValidHttpsEndpoint_DoesNotThrow()
    {
        var opts = new OtelOptions { Enabled = true, OtlpEndpoint = "https://api.honeycomb.io" };
        OtelOptions.Validate(opts); // must not throw
    }
}

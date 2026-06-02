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

    // ── Standard OTEL_* env contract ─────────────────────────────────────────

    [Fact]
    public void Enabled_NoAppsettingsEndpoint_ButEnvEndpointSet_DoesNotThrow()
    {
        const string envVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var prior = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, "http://collector:4317");
        try
        {
            // Telemetry can be enabled from the conventional env-only bootstrap
            // without duplicating the endpoint under CodeyBox:Otel.
            var opts = new OtelOptions { Enabled = true, OtlpEndpoint = null };
            OtelOptions.Validate(opts); // must not throw
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, prior);
        }
    }

    [Fact]
    public void Enabled_InvalidAppsettingsProtocol_DoesNotThrow_WhenEnvProtocolSet()
    {
        // ConfigureOtlp defers to OTEL_EXPORTER_OTLP_PROTOCOL at export time, so
        // an env-only bootstrap that ships a valid protocol via env must not be
        // blocked by a stale/invalid CodeyBox:Otel:ExportProtocol value.
        const string envVar = "OTEL_EXPORTER_OTLP_PROTOCOL";
        var prior = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, "http/protobuf");
        try
        {
            var opts = new OtelOptions
            {
                Enabled = true,
                OtlpEndpoint = "http://localhost:4318",
                ExportProtocol = "totally-bogus",
            };
            OtelOptions.Validate(opts); // must not throw
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, prior);
        }
    }

    [Fact]
    public void ParseResourceAttributesEnv_ParsesPairs_AndSkipsMalformed()
    {
        var parsed = OtelOptions.ParseResourceAttributesEnv("service.namespace=team-a, host.name=worker-1 ,bogus,=novalue,k=");
        Assert.Equal("team-a", parsed.Single(p => p.Key == "service.namespace").Value);
        Assert.Equal("worker-1", parsed.Single(p => p.Key == "host.name").Value);
        Assert.Equal("", parsed.Single(p => p.Key == "k").Value);
        Assert.DoesNotContain(parsed, p => p.Key == "bogus");
        Assert.DoesNotContain(parsed, p => p.Key.Length == 0);
    }

    [Fact]
    public void ParseResourceAttributesEnv_NullOrBlank_ReturnsEmpty()
    {
        Assert.Empty(OtelOptions.ParseResourceAttributesEnv(null));
        Assert.Empty(OtelOptions.ParseResourceAttributesEnv("   "));
    }
}

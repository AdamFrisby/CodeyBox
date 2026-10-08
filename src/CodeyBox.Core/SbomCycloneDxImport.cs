using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace CodeyBox.Core;

/// <summary>
/// Producer-neutral CycloneDX import and validation. Pure core: no filesystem,
/// network, or process access. Every byte of external output is untrusted data.
/// Bounds are enforced before buffering; malformed, unsupported, oversized,
/// truncated, duplicate-identity, or ambiguous-relationship evidence is rejected
/// with typed issues — never silently accepted.
/// </summary>
public static class SbomCycloneDxImport
{
    private const int MaxSingleFieldChars = 2048;
    private const int MaxPurlChars = 2048;
    private const int MaxBomRefChars = 1024;

    private static readonly HashSet<string> AllowedHashAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "MD5", "SHA-1", "SHA-256", "SHA-384", "SHA-512",
        "SHA3-256", "SHA3-384", "SHA3-512",
        "BLAKE2b-256", "BLAKE2b-384", "BLAKE2b-512", "BLAKE3",
    };

    private static readonly Dictionary<string, int> HexLengthByAlgorithm = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MD5"] = 32,
        ["SHA-1"] = 40,
        ["SHA-256"] = 64,
        ["SHA-384"] = 96,
        ["SHA-512"] = 128,
        ["SHA3-256"] = 64,
        ["SHA3-384"] = 96,
        ["SHA3-512"] = 128,
    };

    /// <summary>
    /// Imports and validates one SBOM document. <paramref name="format"/> is
    /// <c>json</c> or <c>xml</c> (exact match). Returns failure — never throws
    /// on malformed input — except on cancellation.
    /// </summary>
    public static SbomImportResult Import(
        byte[] content,
        string format,
        SbomCycloneDxOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        if (!SbomCycloneDxOptionsIsUsable(options, out var optionsIssue))
            return SbomImportResult.Failure(optionsIssue);
        if (content.Length == 0)
            return SbomImportResult.Failure(Issue("sbom.missing", "SBOM evidence is empty."));
        if (content.LongLength > options.MaxSbomBytes)
            return SbomImportResult.Failure(Issue("sbom.oversized",
                $"SBOM evidence is {content.LongLength} bytes, exceeding the {options.MaxSbomBytes}-byte cap."));
        var normalizedFormat = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (!options.SupportedFormats.Any(f => string.Equals(f, normalizedFormat, StringComparison.Ordinal)))
            return SbomImportResult.Failure(Issue("sbom.unsupported-format",
                $"SBOM format '{format}' is not supported (allowed: {string.Join(", ", options.SupportedFormats)})."));

        try
        {
            ct.ThrowIfCancellationRequested();
            return normalizedFormat switch
            {
                "json" => ImportJson(content, options, ct),
                "xml" => ImportXml(content, options, ct),
                _ => SbomImportResult.Failure(Issue("sbom.unsupported-format",
                    $"SBOM format '{format}' is not supported.")),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SbomImportResult.Failure(Issue("sbom.malformed",
                $"SBOM evidence is malformed: {TrimOneLine(ex.Message)}"));
        }
    }

    /// <summary>SHA-256 hex digest of exact bytes (candidate/budget binding).</summary>
    public static string DigestBytes(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private static bool SbomCycloneDxOptionsIsUsable(SbomCycloneDxOptions options, out SbomValidationIssue issue)
    {
        if (SbomCycloneDxOptions.IsValid(options))
        {
            issue = default!;
            return true;
        }
        issue = Issue("sbom.invalid-options", "SBOM options are invalid (bounds, formats, spec versions, or policy mode).");
        return false;
    }

    private static SbomImportResult ImportJson(byte[] content, SbomCycloneDxOptions options, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            var readerOptions = new JsonReaderOptions
            {
                MaxDepth = 32,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            };
            doc = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            _ = readerOptions;
        }
        catch (JsonException ex)
        {
            return SbomImportResult.Failure(Issue("sbom.malformed",
                $"SBOM JSON is malformed or truncated: {TrimOneLine(ex.Message)}"));
        }
        using (doc)
        {
            return BuildDocument(doc.RootElement, "json", options, ct);
        }
    }

    private static SbomImportResult BuildDocument(JsonElement root, string format, SbomCycloneDxOptions options, CancellationToken ct)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return SbomImportResult.Failure(Issue("sbom.malformed", "SBOM document root must be a JSON object."));

        if (!TryGetString(root, "bomFormat", out var bomFormat)
            || !string.Equals(bomFormat, "CycloneDX", StringComparison.Ordinal))
            return SbomImportResult.Failure(Issue("sbom.invalid-schema",
                "SBOM 'bomFormat' must be exactly 'CycloneDX'."));

        if (!TryGetString(root, "specVersion", out var specVersion) || string.IsNullOrWhiteSpace(specVersion))
            return SbomImportResult.Failure(Issue("sbom.invalid-schema",
                "SBOM 'specVersion' is missing."));
        specVersion = specVersion!.Trim();
        if (!options.SupportedSpecVersions.Any(v => string.Equals(v, specVersion, StringComparison.Ordinal)))
            return SbomImportResult.Failure(Issue("sbom.unsupported-spec",
                $"SBOM specVersion '{specVersion}' is not supported (allowed: {string.Join(", ", options.SupportedSpecVersions)})."));

        var producer = ReadProducer(root);
        var rootBomRef = ReadMetadataRootBomRef(root);

        if (!root.TryGetProperty("components", out var componentsEl))
            return SbomImportResult.Failure(Issue("sbom.invalid-schema",
                "SBOM 'components' array is missing (partial inventory is rejected)."));
        if (componentsEl.ValueKind != JsonValueKind.Array)
            return SbomImportResult.Failure(Issue("sbom.invalid-schema",
                "SBOM 'components' must be an array."));
        if (componentsEl.GetArrayLength() > options.MaxComponents)
            return SbomImportResult.Failure(Issue("sbom.oversized",
                $"SBOM carries {componentsEl.GetArrayLength()} components, exceeding the {options.MaxComponents} cap."));

        var components = new List<SbomComponent>(Math.Min(componentsEl.GetArrayLength(), 1024));
        var issues = new List<SbomValidationIssue>();
        var seenBomRefs = new HashSet<string>(StringComparer.Ordinal);
        var seenIdentityKeys = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in componentsEl.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var component = ReadComponent(item, index, issues);
            if (component is null)
            {
                index++;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(component.Identity.BomRef))
            {
                if (!seenBomRefs.Add(component.Identity.BomRef!.Trim()))
                    issues.Add(Issue("sbom.duplicate-identity",
                        $"Duplicate bom-ref '{component.Identity.BomRef}' at components[{index}]."));
            }
            if (!seenIdentityKeys.Add(component.Identity.IdentityKey))
                issues.Add(Issue("sbom.duplicate-identity",
                    $"Duplicate component identity '{component.Identity.IdentityKey}' at components[{index}] (purl qualifiers included; distinct components must not share an identity)."));
            components.Add(component);
            index++;
        }

        var dependencies = new List<SbomDependency>();
        if (root.TryGetProperty("dependencies", out var depsEl))
        {
            if (depsEl.ValueKind != JsonValueKind.Array)
                return SbomImportResult.Failure(Issue("sbom.invalid-schema",
                    "SBOM 'dependencies' must be an array."));
            var edgeCount = 0;
            foreach (var dep in depsEl.EnumerateArray())
                edgeCount += dep.TryGetProperty("dependsOn", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;
            if (depsEl.GetArrayLength() > options.MaxDependencies || edgeCount > options.MaxDependencies)
                return SbomImportResult.Failure(Issue("sbom.oversized",
                    "SBOM dependency graph exceeds the configured cap."));
            var seenDepRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dep in depsEl.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var edge = ReadDependency(dep, issues);
                if (edge is null)
                    continue;
                if (!seenDepRefs.Add(edge.Ref))
                    issues.Add(Issue("sbom.ambiguous-relationship",
                        $"Ambiguous dependency entry: duplicate ref '{edge.Ref}'."));
                dependencies.Add(edge);
            }
        }

        if (issues.Count > 0)
            return SbomImportResult.Failure(issues);

        var knownRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in components)
        {
            if (!string.IsNullOrWhiteSpace(c.Identity.BomRef))
                knownRefs.Add(c.Identity.BomRef!.Trim());
        }
        if (!string.IsNullOrWhiteSpace(rootBomRef))
            knownRefs.Add(rootBomRef!.Trim());

        foreach (var edge in dependencies)
        {
            if (!knownRefs.Contains(edge.Ref))
                return SbomImportResult.Failure(Issue("sbom.ambiguous-relationship",
                    $"Dependency ref '{edge.Ref}' does not match any component bom-ref."));
            foreach (var target in edge.DependsOn)
            {
                if (!knownRefs.Contains(target))
                    return SbomImportResult.Failure(Issue("sbom.ambiguous-relationship",
                        $"Dependency target '{target}' of '{edge.Ref}' does not match any component bom-ref."));
            }
        }

        var contentDigest = DigestBytes(Encoding.UTF8.GetBytes(root.GetRawText()));
        return SbomImportResult.Success(new SbomDocument
        {
            SpecVersion = specVersion,
            Format = format,
            Producer = producer,
            Components = components,
            Dependencies = dependencies,
            ContentDigest = contentDigest,
            InventoryComplete = true,
        });
    }

    private static SbomComponent? ReadComponent(JsonElement item, int index, List<SbomValidationIssue> issues)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            issues.Add(Issue("sbom.invalid-schema", $"SBOM components[{index}] must be an object."));
            return null;
        }
        TryGetString(item, "bom-ref", out var bomRef);
        TryGetString(item, "purl", out var purl);
        TryGetString(item, "type", out var type);
        TryGetString(item, "group", out var group);
        TryGetString(item, "name", out var name);
        TryGetString(item, "version", out var version);
        TryGetString(item, "scope", out var scope);
        TryGetString(item, "publisher", out var publisher);

        if (string.IsNullOrWhiteSpace(name) || name!.Trim().Length > MaxSingleFieldChars)
        {
            issues.Add(Issue("sbom.invalid-schema", $"SBOM components[{index}] has a missing or oversized 'name'."));
            return null;
        }
        if (bomRef is not null && !IsValidBomRef(bomRef))
        {
            issues.Add(Issue("sbom.invalid-identity", $"SBOM components[{index}] carries an invalid bom-ref."));
            return null;
        }
        if (purl is not null && !IsValidPurl(purl))
        {
            issues.Add(Issue("sbom.invalid-identity", $"SBOM components[{index}] carries an invalid purl."));
            return null;
        }
        if (string.IsNullOrWhiteSpace(bomRef) && string.IsNullOrWhiteSpace(purl))
        {
            issues.Add(Issue("sbom.invalid-identity",
                $"SBOM components[{index}] ('{name!.Trim()}') has neither purl nor bom-ref (partial inventory is rejected)."));
            return null;
        }

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("hashes", out var hashesEl) && hashesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var hash in hashesEl.EnumerateArray())
            {
                if (hash.ValueKind != JsonValueKind.Object)
                    continue;
                if (!TryGetString(hash, "alg", out var alg) || !TryGetString(hash, "content", out var content))
                    continue;
                if (!IsValidHash(alg!, content!))
                {
                    issues.Add(Issue("sbom.invalid-digest",
                        $"SBOM components[{index}] carries an invalid '{alg}' digest."));
                    return null;
                }
                hashes[alg!.Trim()] = content!.Trim().ToLowerInvariant();
            }
        }

        var licenses = new List<string>();
        if (item.TryGetProperty("licenses", out var licensesEl) && licensesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var lic in licensesEl.EnumerateArray())
            {
                if (lic.ValueKind != JsonValueKind.Object)
                    continue;
                if (TryGetString(lic, "expression", out var expr) && !string.IsNullOrWhiteSpace(expr))
                    licenses.Add(expr!.Trim());
                else if (lic.TryGetProperty("license", out var inner) && inner.ValueKind == JsonValueKind.Object
                    && TryGetString(inner, "id", out var id) && !string.IsNullOrWhiteSpace(id))
                    licenses.Add(id!.Trim());
                else if (lic.TryGetProperty("license", out var inner2) && inner2.ValueKind == JsonValueKind.Object
                    && TryGetString(inner2, "name", out var licenseName) && !string.IsNullOrWhiteSpace(licenseName))
                    licenses.Add(licenseName!.Trim());
            }
        }

        return new SbomComponent
        {
            Identity = new SbomComponentIdentity
            {
                BomRef = string.IsNullOrWhiteSpace(bomRef) ? null : bomRef!.Trim(),
                Purl = string.IsNullOrWhiteSpace(purl) ? null : purl!.Trim(),
                Type = string.IsNullOrWhiteSpace(type) ? "library" : type!.Trim(),
                Group = string.IsNullOrWhiteSpace(group) ? null : group!.Trim(),
                Name = name!.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? null : version!.Trim(),
            },
            Hashes = hashes,
            Licenses = licenses,
            Scope = string.IsNullOrWhiteSpace(scope) ? null : scope!.Trim(),
            Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher!.Trim(),
        };
    }

    private static SbomDependency? ReadDependency(JsonElement dep, List<SbomValidationIssue> issues)
    {
        if (dep.ValueKind != JsonValueKind.Object)
        {
            issues.Add(Issue("sbom.invalid-schema", "SBOM dependencies[] entries must be objects."));
            return null;
        }
        if (!TryGetString(dep, "ref", out var depRef) || string.IsNullOrWhiteSpace(depRef) || !IsValidBomRef(depRef!))
        {
            issues.Add(Issue("sbom.ambiguous-relationship", "SBOM dependencies[] entry has a missing or invalid 'ref'."));
            return null;
        }
        var targets = new List<string>();
        if (dep.TryGetProperty("dependsOn", out var dependsOn) && dependsOn.ValueKind == JsonValueKind.Array)
        {
            var seenTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in dependsOn.EnumerateArray())
            {
                if (target.ValueKind != JsonValueKind.String)
                {
                    issues.Add(Issue("sbom.ambiguous-relationship",
                        $"SBOM dependency '{depRef!.Trim()}' has a non-string target."));
                    return null;
                }
                var value = target.GetString()!;
                if (!IsValidBomRef(value) || !seenTargets.Add(value.Trim()))
                {
                    issues.Add(Issue("sbom.ambiguous-relationship",
                        $"SBOM dependency '{depRef!.Trim()}' has an invalid or duplicated target."));
                    return null;
                }
                targets.Add(value.Trim());
            }
        }
        return new SbomDependency { Ref = depRef!.Trim(), DependsOn = targets };
    }

    private static SbomProducerInfo ReadProducer(JsonElement root)
    {
        var producer = string.Empty;
        var toolName = string.Empty;
        var toolVersion = string.Empty;
        string? serialNumber = null;
        if (TryGetString(root, "serialNumber", out var serial) && !string.IsNullOrWhiteSpace(serial))
            serialNumber = serial!.Trim();
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("tools", out var tools))
            {
                var first = ExtractFirstTool(tools);
                if (first is not null)
                {
                    producer = first.Value.Name;
                    toolName = first.Value.Name;
                    toolVersion = first.Value.Version;
                }
            }
        }
        return new SbomProducerInfo
        {
            Producer = producer,
            ToolName = toolName,
            ToolVersion = toolVersion,
            SerialNumber = serialNumber,
        };
    }

    private static (string Name, string Version)? ExtractFirstTool(JsonElement tools)
    {
        if (tools.ValueKind == JsonValueKind.Object && tools.TryGetProperty("components", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in list.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object)
                    continue;
                TryGetString(tool, "name", out var name);
                TryGetString(tool, "version", out var version);
                if (!string.IsNullOrWhiteSpace(name))
                    return (name!.Trim(), version?.Trim() ?? string.Empty);
            }
            return null;
        }
        if (tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object)
                    continue;
                TryGetString(tool, "name", out var name);
                TryGetString(tool, "version", out var version);
                if (!string.IsNullOrWhiteSpace(name))
                    return (name!.Trim(), version?.Trim() ?? string.Empty);
            }
        }
        return null;
    }

    private static string? ReadMetadataRootBomRef(JsonElement root)
    {
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("component", out var component) && component.ValueKind == JsonValueKind.Object
            && TryGetString(component, "bom-ref", out var bomRef) && IsValidBomRef(bomRef ?? string.Empty))
            return bomRef!.Trim();
        return null;
    }

    private static SbomImportResult ImportXml(byte[] content, SbomCycloneDxOptions options, CancellationToken ct)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(content);
        }
        catch (Exception ex)
        {
            return SbomImportResult.Failure(Issue("sbom.malformed",
                $"SBOM XML is not valid UTF-8: {TrimOneLine(ex.Message)}"));
        }
        XmlDocument xml;
        try
        {
            xml = new XmlDocument();
            using var reader = XmlReader.Create(new System.IO.StringReader(text), SafeXmlSettings.Create());
            xml.Load(reader);
        }
        catch (Exception ex)
        {
            return SbomImportResult.Failure(Issue("sbom.malformed",
                $"SBOM XML is malformed or truncated: {TrimOneLine(ex.Message)}"));
        }
        var namespaceManager = new XmlNamespaceManager(xml.NameTable);
        var root = xml.DocumentElement;
        if (root is null || !string.Equals(root.LocalName, "bom", StringComparison.Ordinal))
            return SbomImportResult.Failure(Issue("sbom.invalid-schema", "SBOM XML root must be a 'bom' element."));
        var specVersion = root.GetAttribute("specVersion");
        if (string.IsNullOrWhiteSpace(specVersion))
        {
            var versionAttr = root.GetAttribute("version");
            specVersion = string.IsNullOrWhiteSpace(versionAttr) ? string.Empty : versionAttr.Trim();
        }
        specVersion = (specVersion ?? string.Empty).Trim();
        if (!options.SupportedSpecVersions.Any(v => string.Equals(v, specVersion, StringComparison.Ordinal)))
            return SbomImportResult.Failure(Issue("sbom.unsupported-spec",
                $"SBOM specVersion '{specVersion}' is not supported (allowed: {string.Join(", ", options.SupportedSpecVersions)})."));

        var componentNodes = root.SelectNodes("c:components/c:component", NamespaceFor(root, namespaceManager))
            ?? root.SelectNodes("components/component");
        var components = new List<SbomComponent>();
        var issues = new List<SbomValidationIssue>();
        var seenBomRefs = new HashSet<string>(StringComparer.Ordinal);
        var seenIdentityKeys = new HashSet<string>(StringComparer.Ordinal);
        var nodeIndex = 0;
        if (componentNodes is not null)
        {
            if (componentNodes.Count > options.MaxComponents)
                return SbomImportResult.Failure(Issue("sbom.oversized",
                    $"SBOM carries {componentNodes.Count} components, exceeding the {options.MaxComponents} cap."));
            foreach (XmlNode node in componentNodes)
            {
                ct.ThrowIfCancellationRequested();
                var component = ReadXmlComponent(node, nodeIndex, issues);
                if (component is null)
                {
                    nodeIndex++;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(component.Identity.BomRef))
                {
                    if (!seenBomRefs.Add(component.Identity.BomRef!.Trim()))
                        issues.Add(Issue("sbom.duplicate-identity",
                            $"Duplicate bom-ref '{component.Identity.BomRef}'."));
                }
                if (!seenIdentityKeys.Add(component.Identity.IdentityKey))
                    issues.Add(Issue("sbom.duplicate-identity",
                        $"Duplicate component identity '{component.Identity.IdentityKey}'."));
                components.Add(component);
                nodeIndex++;
            }
        }
        if (issues.Count > 0)
            return SbomImportResult.Failure(issues);

        var dependencies = new List<SbomDependency>();
        var depNodes = root.SelectNodes("c:dependencies/c:dependency", NamespaceFor(root, namespaceManager))
            ?? root.SelectNodes("dependencies/dependency");
        if (depNodes is not null)
        {
            var seenDepRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode node in depNodes)
            {
                ct.ThrowIfCancellationRequested();
                var depRef = node.Attributes?["ref"]?.Value;
                if (string.IsNullOrWhiteSpace(depRef) || !IsValidBomRef(depRef!))
                {
                    issues.Add(Issue("sbom.ambiguous-relationship", "SBOM XML dependency has a missing or invalid 'ref'."));
                    continue;
                }
                var targets = new List<string>();
                var seenTargets = new HashSet<string>(StringComparer.Ordinal);
                foreach (XmlNode child in node.SelectNodes("c:dependency", NamespaceFor(root, namespaceManager))
                    ?? node.SelectNodes("dependency")!)
                {
                    var childRef = child.Attributes?["ref"]?.Value;
                    if (string.IsNullOrWhiteSpace(childRef) || !IsValidBomRef(childRef!) || !seenTargets.Add(childRef!.Trim()))
                    {
                        issues.Add(Issue("sbom.ambiguous-relationship",
                            $"SBOM XML dependency '{depRef!.Trim()}' has an invalid or duplicated target."));
                        targets.Clear();
                        break;
                    }
                    targets.Add(childRef!.Trim());
                }
                if (!seenDepRefs.Add(depRef!.Trim()))
                    issues.Add(Issue("sbom.ambiguous-relationship",
                        $"Ambiguous dependency entry: duplicate ref '{depRef!.Trim()}'."));
                else
                    dependencies.Add(new SbomDependency { Ref = depRef!.Trim(), DependsOn = targets });
            }
        }
        if (issues.Count > 0)
            return SbomImportResult.Failure(issues);

        var knownRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in components)
        {
            if (!string.IsNullOrWhiteSpace(c.Identity.BomRef))
                knownRefs.Add(c.Identity.BomRef!.Trim());
        }
        foreach (var edge in dependencies)
        {
            if (!knownRefs.Contains(edge.Ref))
                return SbomImportResult.Failure(Issue("sbom.ambiguous-relationship",
                    $"Dependency ref '{edge.Ref}' does not match any component bom-ref."));
            foreach (var target in edge.DependsOn)
            {
                if (!knownRefs.Contains(target))
                    return SbomImportResult.Failure(Issue("sbom.ambiguous-relationship",
                        $"Dependency target '{target}' of '{edge.Ref}' does not match any component bom-ref."));
            }
        }

        return SbomImportResult.Success(new SbomDocument
        {
            SpecVersion = specVersion,
            Format = "xml",
            Producer = new SbomProducerInfo(),
            Components = components,
            Dependencies = dependencies,
            ContentDigest = DigestBytes(content),
            InventoryComplete = true,
        });
    }

    private static XmlNamespaceManager NamespaceFor(XmlElement root, XmlNamespaceManager manager)
    {
        var namespaceUri = root.NamespaceURI;
        if (!string.IsNullOrEmpty(namespaceUri))
            manager.AddNamespace("c", namespaceUri);
        else
            manager.AddNamespace("c", "http://cyclonedx.org/schema/bom/1.5");
        return manager;
    }

    private static SbomComponent? ReadXmlComponent(XmlNode node, int index, List<SbomValidationIssue> issues)
    {
        var bomRef = node.Attributes?["bom-ref"]?.Value;
        var type = node.Attributes?["type"]?.Value;
        string? group = null, name = null, version = null, purl = null, scope = null, publisher = null;
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element)
                continue;
            switch (child.LocalName)
            {
                case "group": group = child.InnerText; break;
                case "name": name = child.InnerText; break;
                case "version": version = child.InnerText; break;
                case "purl": purl = child.InnerText; break;
                case "scope": scope = child.InnerText; break;
                case "publisher": publisher = child.InnerText; break;
            }
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(Issue("sbom.invalid-schema", $"SBOM XML component[{index}] has a missing 'name'."));
            return null;
        }
        if (bomRef is not null && !IsValidBomRef(bomRef))
        {
            issues.Add(Issue("sbom.invalid-identity", $"SBOM XML component[{index}] carries an invalid bom-ref."));
            return null;
        }
        if (purl is not null && !string.IsNullOrWhiteSpace(purl) && !IsValidPurl(purl.Trim()))
        {
            issues.Add(Issue("sbom.invalid-identity", $"SBOM XML component[{index}] carries an invalid purl."));
            return null;
        }
        if (string.IsNullOrWhiteSpace(bomRef) && string.IsNullOrWhiteSpace(purl))
        {
            issues.Add(Issue("sbom.invalid-identity",
                $"SBOM XML component[{index}] ('{name!.Trim()}') has neither purl nor bom-ref."));
            return null;
        }
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashesNode = ChildByLocalName(node, "hashes");
        if (hashesNode is not null)
        {
            foreach (XmlNode hashNode in hashesNode.ChildNodes)
            {
                if (hashNode.NodeType != XmlNodeType.Element || !string.Equals(hashNode.LocalName, "hash", StringComparison.Ordinal))
                    continue;
                var alg = hashNode.Attributes?["alg"]?.Value;
                var content = hashNode.InnerText;
                if (string.IsNullOrWhiteSpace(alg) || string.IsNullOrWhiteSpace(content))
                    continue;
                if (!IsValidHash(alg!, content!))
                {
                    issues.Add(Issue("sbom.invalid-digest",
                        $"SBOM XML component[{index}] carries an invalid '{alg}' digest."));
                    return null;
                }
                hashes[alg!.Trim()] = content!.Trim().ToLowerInvariant();
            }
        }
        return new SbomComponent
        {
            Identity = new SbomComponentIdentity
            {
                BomRef = string.IsNullOrWhiteSpace(bomRef) ? null : bomRef!.Trim(),
                Purl = string.IsNullOrWhiteSpace(purl) ? null : purl!.Trim(),
                Type = string.IsNullOrWhiteSpace(type) ? "library" : type!.Trim(),
                Group = string.IsNullOrWhiteSpace(group) ? null : group!.Trim(),
                Name = name!.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? null : version!.Trim(),
            },
            Hashes = hashes,
            Scope = string.IsNullOrWhiteSpace(scope) ? null : scope!.Trim(),
            Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher!.Trim(),
        };
    }

    private static XmlNode? ChildByLocalName(XmlNode node, string localName)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Element && string.Equals(child.LocalName, localName, StringComparison.Ordinal))
                return child;
        }
        return null;
    }

    /// <summary>Validates a package URL structurally (never fetched).</summary>
    public static bool IsValidPurl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value!.Length > MaxPurlChars)
            return false;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("pkg:", StringComparison.Ordinal))
            return false;
        if (trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;
        var remainder = trimmed.Substring("pkg:".Length);
        return remainder.Contains('/', StringComparison.Ordinal) && remainder.Length >= 3;
    }

    /// <summary>Validates a bom-ref (document-local identifier).</summary>
    public static bool IsValidBomRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value!.Length > MaxBomRefChars)
            return false;
        return !value.Any(c => char.IsControl(c));
    }

    /// <summary>Validates a content digest (algorithm allowlist plus hex shape).</summary>
    public static bool IsValidHash(string? algorithm, string? content)
    {
        if (string.IsNullOrWhiteSpace(algorithm) || string.IsNullOrWhiteSpace(content))
            return false;
        if (!AllowedHashAlgorithms.Contains(algorithm!.Trim()))
            return false;
        var hex = content!.Trim();
        if (hex.Length == 0 || hex.Length > 256 || hex.Any(c => !Uri.IsHexDigit(c)))
            return false;
        return !HexLengthByAlgorithm.TryGetValue(algorithm!.Trim(), out var expected) || hex.Length == expected;
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(property, out var child))
            return false;
        if (child.ValueKind != JsonValueKind.String)
            return false;
        value = child.GetString();
        return value is not null;
    }

    private static SbomValidationIssue Issue(string code, string message) =>
        new() { Code = code, Message = message };

    private static string TrimOneLine(string message)
    {
        var line = (message ?? string.Empty).Split('\n', 2)[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}

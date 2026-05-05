using System.Text.Json.Serialization;
using CodeyBox.Cli.Models;

namespace CodeyBox.Cli;

[JsonSerializable(typeof(WorkItemDto))]
[JsonSerializable(typeof(List<WorkItemDto>))]
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(CreateWorkItemRequest))]
[JsonSerializable(typeof(RetryRequest))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
internal partial class CliJsonContext : JsonSerializerContext { }

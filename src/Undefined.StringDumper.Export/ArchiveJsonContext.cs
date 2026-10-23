using System.Text.Json.Serialization;
using Undefined.StringDumper.App.Services;

namespace Undefined.StringDumper.Export;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DumperArchiveManifest))]
[JsonSerializable(typeof(DumperArchiveEnvelope))]
internal partial class ArchiveJsonContext : JsonSerializerContext;

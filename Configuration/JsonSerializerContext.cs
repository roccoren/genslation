using System.Text.Json.Serialization;

namespace genslation.Configuration;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, int>))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{
}
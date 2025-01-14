using System;
using System.Text.Json.Serialization;

namespace Eryph.GenePool.Model.Responses;

[method: JsonConstructor]
public record ApiKeySecretResponse(
    [property: JsonPropertyName("organization")]
    string Organization,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("uri")] Uri Uri,
    [property: JsonPropertyName("secret")] string Secret);
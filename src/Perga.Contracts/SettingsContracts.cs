using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perga.Contracts;

public sealed record SettingsSectionDto(string Id, string Label, string? Description);

public sealed record SettingsFieldOptionDto(string Value, string Label);

public sealed record SettingsFieldDto(
    string Key,
    string Section,
    string Label,
    string? Description,
    string Control,
    string ValueType,
    JsonElement DefaultValue,
    int? Min,
    int? Max,
    string? Unit,
    IReadOnlyList<SettingsFieldOptionDto>? Options);

public sealed record SettingsSnapshotDto(
    long Rev,
    IReadOnlyList<SettingsSectionDto> Sections,
    IReadOnlyList<SettingsFieldDto> Fields,
    IReadOnlyDictionary<string, JsonElement> Values);

public sealed record PatchSettingsRequestDto(
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] long ExpectedRev,
    [property: JsonRequired] IReadOnlyDictionary<string, JsonElement> Patch);

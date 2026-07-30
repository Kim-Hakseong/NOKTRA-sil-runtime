using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sil.Core.Scenarios;

/// <summary>
/// Source-generated serialisation contracts for the scenario document. Source generation rather
/// than reflection keeps the format working under a trimmed single-file publish.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScenarioDefinition))]
internal sealed partial class ScenarioJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes scenario documents.
/// </summary>
/// <remarks>
/// Output is deterministic and diff-friendly: indented, camel-cased, LF line endings and UTF-8
/// without a BOM, so saving an unchanged scenario produces an unchanged file.
/// </remarks>
public static class ScenarioFile
{
    /// <summary>Conventional file extension.</summary>
    public const string Extension = ".silscenario.json";

    /// <summary>Serialises a scenario to JSON text.</summary>
    public static string ToJson(ScenarioDefinition scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        string json = JsonSerializer.Serialize(scenario, ScenarioJsonContext.Default.ScenarioDefinition);

        // System.Text.Json emits '\n' already; normalise defensively so the bytes never depend
        // on the writing platform.
        return json.ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Parses a scenario from JSON text.</summary>
    /// <exception cref="ScenarioFormatException">The document is not a readable scenario.</exception>
    public static ScenarioDefinition FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ScenarioDefinition? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize(json, ScenarioJsonContext.Default.ScenarioDefinition);
        }
        catch (JsonException ex)
        {
            throw new ScenarioFormatException($"The scenario is not valid JSON: {ex.Message}", ex);
        }

        if (scenario is null)
        {
            throw new ScenarioFormatException("The scenario document is empty.");
        }

        if (scenario.FormatVersion != ScenarioDefinition.CurrentFormatVersion)
        {
            throw new ScenarioFormatException(
                $"Scenario format version {scenario.FormatVersion} is not supported; " +
                $"this build reads version {ScenarioDefinition.CurrentFormatVersion}.");
        }

        return scenario;
    }

    /// <summary>Writes a scenario to disk, replacing any existing file.</summary>
    public static void Save(ScenarioDefinition scenario, string path)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        File.WriteAllText(path, ToJson(scenario), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Reads a scenario from disk.</summary>
    /// <exception cref="ScenarioFormatException">The file is missing or not a readable scenario.</exception>
    public static ScenarioDefinition Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new ScenarioFormatException($"No scenario file at '{Path.GetFullPath(path)}'.");
        }

        return FromJson(File.ReadAllText(path));
    }
}

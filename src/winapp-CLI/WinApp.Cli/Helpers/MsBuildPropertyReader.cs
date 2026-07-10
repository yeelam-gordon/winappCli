// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Parses the stdout of <c>dotnet build/msbuild --getProperty:...</c> into a property dictionary.
/// </summary>
/// <remarks>
/// The dotnet SDK returns two different shapes depending on how many properties were requested:
/// <list type="bullet">
/// <item><description>A <b>single</b> <c>--getProperty</c> returns a raw scalar (e.g. <c>WinExe</c>).</description></item>
/// <item><description><b>Multiple</b> <c>--getProperty</c> return JSON: <c>{ "Properties": { "Name": "Value", ... } }</c>.</description></item>
/// </list>
/// This helper accepts either shape. Pure and side-effect free so it can be unit tested without a build.
/// </remarks>
internal static class MsBuildPropertyReader
{
    /// <summary>
    /// Parses <paramref name="stdout"/> for the requested properties.
    /// </summary>
    /// <param name="stdout">Raw stdout captured from the dotnet invocation.</param>
    /// <param name="requestedNames">
    /// The property names that were requested. When exactly one name is requested and the output is
    /// not JSON, the whole (trimmed) output is treated as that property's value.
    /// </param>
    /// <returns>
    /// A case-insensitive map of property name to value. Values may be empty strings (a property that
    /// evaluated to empty). Missing properties are simply absent from the map.
    /// </returns>
    public static IReadOnlyDictionary<string, string> Parse(string stdout, IReadOnlyList<string> requestedNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stdout is null)
        {
            return result;
        }

        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return result;
        }

        // JSON shape (multiple properties, or a single property when the SDK still emits an object).
        // Normally stdout is clean JSON, but be tolerant of a diagnostic preamble before the object.
        var braceIndex = trimmed.IndexOf('{');
        if (braceIndex >= 0)
        {
            var jsonCandidate = trimmed[braceIndex..];
            try
            {
                using var doc = JsonDocument.Parse(jsonCandidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("Properties", out var props) &&
                    props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in props.EnumerateObject())
                    {
                        result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? string.Empty
                            : prop.Value.ToString();
                    }

                    return result;
                }
            }
            catch (JsonException)
            {
                // Fall through to scalar handling — the leading '{' was not a JSON properties object.
            }
        }

        // Scalar shape: a single requested property whose raw value is the whole output.
        if (requestedNames.Count == 1)
        {
            result[requestedNames[0]] = trimmed;
        }

        return result;
    }
}

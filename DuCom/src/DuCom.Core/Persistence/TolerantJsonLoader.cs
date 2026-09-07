using System.Reflection;
using System.Text.Json;

namespace DuCom.Core.Persistence;

/// <summary>
/// Fault-isolated JSON loading for persisted snapshots. Strict deserialization is
/// attempted first; when the whole document fails (for example one property changed type
/// between application versions), the document is re-read piece by piece so a single
/// unreadable part degrades to its default instead of discarding everything:
/// <see cref="TryLoad{T}"/> binds positional-record constructor parameters (or plain
/// properties for types with a parameterless constructor) individually,
/// <see cref="TryLoadList{T}"/> skips unreadable array elements, and
/// <see cref="TryLoadDictionary{TValue}"/> skips unreadable object entries.
/// </summary>
public static class TolerantJsonLoader
{
    /// <summary>
    /// Returns true when a snapshot could be loaded (strictly or field-by-field).
    /// <paramref name="skippedProperties"/> names the top-level properties that were
    /// present but unreadable and therefore fell back to their defaults.
    /// </summary>
    public static bool TryLoad<T>(
        string json,
        JsonSerializerOptions options,
        out T? value,
        out IReadOnlyList<string> skippedProperties)
        where T : class
    {
        value = null;
        skippedProperties = [];
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(options);

        try
        {
            value = JsonSerializer.Deserialize<T>(json, options);
            return value is not null;
        }
        catch (JsonException)
        {
            // One property poisoned the strict read; fall through to the isolated path.
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            ConstructorInfo? constructor = typeof(T).GetConstructors()
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();
            if (constructor is null)
            {
                return false;
            }

            if (constructor.GetParameters().Length == 0)
            {
                return TryBindProperties<T>(document.RootElement, constructor, options, out value, out skippedProperties);
            }

            List<string> skipped = [];
            ParameterInfo[] parameters = constructor.GetParameters();
            object?[] arguments = new object?[parameters.Length];
            for (int index = 0; index < parameters.Length; index++)
            {
                ParameterInfo parameter = parameters[index];
                if (TryGetProperty(document.RootElement, parameter.Name, out JsonElement element))
                {
                    try
                    {
                        arguments[index] = element.Deserialize(parameter.ParameterType, options);
                        continue;
                    }
                    catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
                    {
                        skipped.Add(parameter.Name!);
                    }
                }

                arguments[index] = GetFallbackValue(parameter);
            }

            try
            {
                value = (T)constructor.Invoke(arguments);
            }
            catch (Exception exception) when (exception is TargetInvocationException or MemberAccessException)
            {
                return false;
            }

            skippedProperties = skipped;
            return true;
        }
    }

    /// <summary>
    /// Returns true when the document is a JSON array (or strictly deserializable as a
    /// list). Unreadable elements are skipped and reported by index; a failed strict read
    /// of a non-array document returns false.
    /// </summary>
    public static bool TryLoadList<T>(
        string json,
        JsonSerializerOptions options,
        out List<T> values,
        out IReadOnlyList<int> skippedIndexes)
    {
        values = [];
        skippedIndexes = [];
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(options);

        try
        {
            List<T>? strict = JsonSerializer.Deserialize<List<T>>(json, options);
            if (strict is not null)
            {
                values = strict;
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            // Fall through to the element-by-element path.
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            List<T> loaded = [];
            List<int> skipped = [];
            int index = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                try
                {
                    T? item = element.Deserialize<T>(options);
                    if (item is not null)
                    {
                        loaded.Add(item);
                    }
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
                {
                    skipped.Add(index);
                }

                index++;
            }

            values = loaded;
            skippedIndexes = skipped;
            return true;
        }
    }

    /// <summary>
    /// Returns true when the document is a JSON object usable as a dictionary. Entries
    /// whose value cannot be read are skipped and reported by key.
    /// </summary>
    public static bool TryLoadDictionary<TValue>(
        string json,
        JsonSerializerOptions options,
        out Dictionary<string, TValue> values,
        out IReadOnlyList<string> skippedKeys)
    {
        values = new(StringComparer.Ordinal);
        skippedKeys = [];
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(options);

        try
        {
            Dictionary<string, TValue>? strict = JsonSerializer.Deserialize<Dictionary<string, TValue>>(json, options);
            if (strict is not null)
            {
                values = strict;
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            // Fall through to the entry-by-entry path.
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            Dictionary<string, TValue> loaded = new(StringComparer.Ordinal);
            List<string> skipped = [];
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                try
                {
                    TValue? item = property.Value.Deserialize<TValue>(options);
                    if (item is not null)
                    {
                        loaded[property.Name] = item;
                    }
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
                {
                    skipped.Add(property.Name);
                }
            }

            values = loaded;
            skippedKeys = skipped;
            return true;
        }
    }

    private static bool TryBindProperties<T>(
        JsonElement root,
        ConstructorInfo constructor,
        JsonSerializerOptions options,
        out T? value,
        out IReadOnlyList<string> skippedProperties)
        where T : class
    {
        value = null;
        skippedProperties = [];
        try
        {
            if (constructor.Invoke(null) is not T instance)
            {
                return false;
            }

            List<string> skipped = [];
            foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite)
                {
                    continue;
                }

                if (!TryGetProperty(root, property.Name, out JsonElement element))
                {
                    continue;
                }

                try
                {
                    property.SetValue(instance, element.Deserialize(property.PropertyType, options));
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
                {
                    skipped.Add(property.Name);
                }
            }

            value = instance;
            skippedProperties = skipped;
            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or MemberAccessException)
        {
            return false;
        }
    }

    private static object? GetFallbackValue(ParameterInfo parameter) =>
        parameter.HasDefaultValue
            ? parameter.DefaultValue
            : parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null;

    private static bool TryGetProperty(JsonElement root, string? name, out JsonElement element)
    {
        if (!string.IsNullOrEmpty(name))
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    element = property.Value;
                    return true;
                }
            }
        }

        element = default;
        return false;
    }
}

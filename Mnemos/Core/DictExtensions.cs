namespace Mnemos;

/// <summary>
/// Helper extensions for <see cref="Dictionary{TKey,TValue}"/> 
/// to safely extract typed values from deserialized V8 objects.
/// </summary>
public static class DictExtensions
{
    /// <summary>
    /// Returns the string value associated with the specified key,
    /// or <see cref="string.Empty"/> if the key is missing or the value is not a string.
    /// </summary>
    /// <param name="dict">The dictionary to read from.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The string value, or <c>""</c> if not found.</returns>
    public static string GetString(this Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var v) && v is string s ? s : string.Empty;
}
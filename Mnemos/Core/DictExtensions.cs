namespace Mnemos;

public static class DictExtensions
{
    public static string GetString(this Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var v) && v is string s ? s : string.Empty;
}
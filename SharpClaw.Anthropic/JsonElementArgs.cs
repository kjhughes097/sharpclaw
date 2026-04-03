using System.Text.Json;

namespace SharpClaw.Anthropic;

internal sealed class JsonElementArgs : IReadOnlyDictionary<string, object?>
{
    private readonly IReadOnlyDictionary<string, JsonElement> _inner;

    internal JsonElementArgs(IReadOnlyDictionary<string, JsonElement> inner) =>
        _inner = inner;

    public object? this[string key] => _inner[key];
    public IEnumerable<string> Keys => _inner.Keys;
    public IEnumerable<object?> Values => _inner.Values.Cast<object?>();
    public int Count => _inner.Count;

    public bool ContainsKey(string key) => _inner.ContainsKey(key);

    public bool TryGetValue(string key, out object? value)
    {
        if (_inner.TryGetValue(key, out var element))
        {
            value = element;
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        _inner.Select(kvp => KeyValuePair.Create(kvp.Key, (object?)kvp.Value)).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
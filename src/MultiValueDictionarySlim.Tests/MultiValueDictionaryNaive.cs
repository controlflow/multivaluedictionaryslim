using System.Collections;

namespace ControlFlow.Collections.Tests;

public class MultiValueDictionaryNaive<TKey, TValue> : IEnumerable<KeyValuePair<TKey, List<TValue>>>
  where TKey : notnull
{
  private readonly Dictionary<TKey, List<TValue>> _dictionary = new();

  public int Count => _dictionary.Count;
  public int ValuesCount => _dictionary.Values.Sum(x => x.Count);

  public Dictionary<TKey, List<TValue>>.KeyCollection Keys => _dictionary.Keys;

  public void Add(TKey key, TValue value)
  {
    if (!_dictionary.TryGetValue(key, out var list))
    {
      _dictionary[key] = list = new List<TValue>();
    }

    list.Add(value);
  }

  public bool Remove(TKey key)
  {
    return _dictionary.Remove(key);
  }

  public void Clear()
  {
    _dictionary.Clear();
  }

  public IEnumerator<KeyValuePair<TKey, List<TValue>>> GetEnumerator()
  {
    foreach (var pair in _dictionary)
    {
      yield return new KeyValuePair<TKey, List<TValue>>(pair.Key, pair.Value);
    }
  }

  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

  public List<TValue> this[TKey key]
  {
    get
    {
      if (_dictionary.TryGetValue(key, out var list))
      {
        return list;
      }

      return new List<TValue>();
    }
  }
}
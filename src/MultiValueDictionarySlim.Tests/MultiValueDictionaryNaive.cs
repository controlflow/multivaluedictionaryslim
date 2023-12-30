using System.Collections;
using System.Diagnostics;

namespace ControlFlow.Collections.Tests;

public class MultiValueDictionaryNaive<TKey, TValue> : IEnumerable<KeyValuePair<TKey, List<TValue>>>
  where TKey : notnull
{
  private readonly Dictionary<TKey, List<TValue>> _dictionary;

  public MultiValueDictionaryNaive()
  {
    _dictionary = new Dictionary<TKey, List<TValue>>();
  }

  public MultiValueDictionaryNaive(IEqualityComparer<TKey> comparer)
  {
    _dictionary = new Dictionary<TKey, List<TValue>>(comparer);
  }

  public MultiValueDictionaryNaive(int keyCapacity, int valueCapacity)
  {
    _dictionary = new Dictionary<TKey, List<TValue>>(capacity: keyCapacity);
    _ = valueCapacity;
  }

  public MultiValueDictionaryNaive(int keyCapacity, int valueCapacity, IEqualityComparer<TKey>? comparer)
  {
    _dictionary = new Dictionary<TKey, List<TValue>>(capacity: keyCapacity, comparer);
    _ = valueCapacity;
  }

  public int Count => _dictionary.Count;
  public int ValuesCount => _dictionary.Values.Sum(x => x.Count);
  public int ValuesCapacity => _dictionary.Values.Sum(x => x.Capacity);

  public Dictionary<TKey, List<TValue>>.KeyCollection Keys => _dictionary.Keys;
  public IEnumerable<TValue> Values => _dictionary.Values.SelectMany(x => x);

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

  public IReadOnlyList<TValue> this[TKey key]
  {
    get
    {
      if (_dictionary.TryGetValue(key, out var list))
      {
        return list;
      }

      return Array.Empty<TValue>();
    }
  }

  public void AddValueRange(TKey key, IEnumerable<TValue> values)
  {
    if (values is ICollection<TValue> collection)
    {
      if (collection.Count > 0)
      {
        if (!_dictionary.TryGetValue(key, out var list))
        {
          _dictionary[key] = list = new List<TValue>();
        }

        list.AddRange(collection);
      }
    }
    else
    {
      using var enumerator = values.GetEnumerator();

      if (enumerator.MoveNext())
      {
        if (!_dictionary.TryGetValue(key, out var list))
        {
          _dictionary[key] = list = new List<TValue>();
        }

        list.Add(enumerator.Current);

        while (enumerator.MoveNext())
        {
          list.Add(enumerator.Current);
        }
      }
    }
  }

  public void TrimExcessKeys()
  {
    #if NETCOREAPP
    _dictionary.TrimExcess();
    #endif
  }

  public void TrimExcessValues()
  {
    foreach (var list in _dictionary.Values)
    {
      list.Capacity = list.Count;
    }
  }
}
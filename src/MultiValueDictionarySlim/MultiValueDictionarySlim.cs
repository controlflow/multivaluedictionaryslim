using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
// ReSharper disable UseCollectionExpression
// ReSharper disable ConvertToAutoPropertyWithPrivateSetter
// ReSharper disable IntroduceOptionalParameters.Global
// ReSharper disable MergeConditionalExpression

namespace ControlFlow.Collections;

/// <summary>
/// Represents a dictionary to map <typeparamref name="TKey"/> keys to collections of <typeparamref name="TValue"/> values.
/// This dictionary is a implementation of `Dictionary{TKey, List{TValue}}` that is optimized:
/// 1. Not to allocate lists on the heap for each key;
/// 2. To use contiguous memory (arrays) to store both keys and values - this makes this dictionary pooling-friendly.
///
/// Trade-offs:
/// 1. Generally it consumes (and wastes) more memory. Individual `List{TValue}` instances
///    grow when needed, while this dictionary grows the whole array for values.
/// 2. This dictionary also uses 1 `int` per each `TValue` stored (for linked list).
///    Probably not a problem until you store a LOT of values for just a few keys.
/// 3. Values are stored as a linked-lists - so no list operatios support, only enumeration + count.
/// 4. It is more likely for values array to make it into LOH.
/// 5. Key removal/values clear is a O(n) operation if <typeparamref name="TValue"/> is a reference type,
///    because of the need of linked list traversal to clear the managed references.
/// </summary>
/// <typeparam name="TKey">Type of keys</typeparam>
/// <typeparam name="TValue">Type of values</typeparam>
[DebuggerTypeProxy(typeof(MultiValueDictionarySlimDebugView<,>))]
[DebuggerDisplay("Count = {Count}")]
public class MultiValueDictionarySlim<TKey, TValue>
  where TKey : notnull
{
  private int[]? _buckets;
  private Entry[]? _entries;
  private TValue[] _values;
  private int[] _indexes;

  private int _keyCount;
  private int _valuesCount;
  private int _version;

  private readonly IEqualityComparer<TKey>? _comparer;

  private int _keyFreeList;
  private int _keyFreeCount;
  private int _valueFreeList;
  private int _valueFreeCount;

  private const int StartOfFreeList = -3;
  private const int DefaultValuesListSize = 4;

  private static readonly TValue[] s_emptyValues = Array.Empty<TValue>();
  private static readonly int[] s_emptyIndexes = Array.Empty<int>();

  private struct Entry
  {
    public uint HashCode;

    /// <summary>
    /// 0-based index of next entry in chain: -1 means end of the chain (no more entries with the same hash code).
    /// Also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
    /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
    /// </summary>
    public int Next;

    public TKey Key;

    /// <summary>
    /// 0-based index in <see cref="MultiValueDictionarySlim{TKey,TValue}._values"/> list
    /// where the first value corresponding to this <see cref="Key"/> is stored.
    /// </summary>
    public int StartIndex;

    /// <summary>
    /// 0-based index in <see cref="MultiValueDictionarySlim{TKey,TValue}._values"/> list
    /// or -1 for incomplete (empty) entries.
    /// At this index the <see cref="MultiValueDictionarySlim{TKey,TValue}._indexes"/> value
    /// at this position is a <see cref="ValuesCollection.Count"/> of values.
    /// </summary>
    public int EndIndex;

    public readonly override string ToString()
    {
      return $"Entry [key={Key}, start={StartIndex}, end={EndIndex}]";
    }
  }

  public MultiValueDictionarySlim()
    : this(keyCapacity: 0, 0, comparer: null)
  {
  }

  public MultiValueDictionarySlim(int keyCapacity, int valueCapacity)
    : this(keyCapacity: keyCapacity, valueCapacity, comparer: null)
  {
  }

  public MultiValueDictionarySlim(IEqualityComparer<TKey>? comparer)
    : this(keyCapacity: 0, valueCapacity: 0, comparer)
  {
  }

  public MultiValueDictionarySlim(int keyCapacity, int valueCapacity, IEqualityComparer<TKey>? comparer)
  {
    if (keyCapacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(nameof(keyCapacity));
    if (valueCapacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(nameof(valueCapacity));

    if (keyCapacity > 0)
    {
      Initialize(keyCapacity);
    }

    _values = valueCapacity > 0 ? new TValue[valueCapacity] : s_emptyValues;
    _indexes = valueCapacity > 0 ? new int[valueCapacity] : s_emptyIndexes;

    // first check for null to avoid forcing default comparer instantiation unnecessarily
    if (comparer != null && !ReferenceEquals(comparer, EqualityComparer<TKey>.Default))
    {
      _comparer = comparer;
    }
  }

  public int Count => _keyCount - _keyFreeCount;
  public int ValuesCount => _valuesCount - _valueFreeCount;

  public int KeysCapacity => _entries == null ? 0 : _entries.Length;
  public int ValuesCapacity => _values.Length;

  public IEqualityComparer<TKey> Comparer => _comparer ?? EqualityComparer<TKey>.Default;

  public KeyCollection Keys => new(this);
  public AllValuesCollection Values => new(this);

  public void Add(TKey key, TValue value)
  {
    var entryIndex = GetOrCreateEntry(key);

    StoreEntryValue(ref _entries![entryIndex], value);

    _version++;
  }

  public void AddValueRange(TKey key, IEnumerable<TValue> values)
  {
    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    if (values == null) ThrowHelper.ThrowArgumentNullException(nameof(values));

    if (values is ICollection<TValue> collection)
    {
      var collectionCount = collection.Count;
      if (collectionCount > 0)
      {
        var entryIndex = GetOrCreateEntry(key);
        ref var entry = ref _entries![entryIndex];
        StoreEntryValuesCollection(ref entry, collection, collectionCount);
      }
    }
    else
    {
      using var enumerator = values.GetEnumerator();

      if (enumerator.MoveNext())
      {
        var entryIndex = GetOrCreateEntry(key);
        ref var entry = ref _entries![entryIndex];

        StoreEntryValue(ref entry, enumerator.Current);

        while (enumerator.MoveNext())
        {
          StoreEntryValue(ref entry, enumerator.Current);
        }
      }
    }

    _version++;
  }

  public bool Remove(TKey key)
  {
    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    if (key == null)
    {
      ThrowHelper.ThrowArgumentNullException(nameof(key));
    }

    if (_buckets == null) return false;

    Debug.Assert(_entries != null, "entries should be non-null");

    uint collisionCount = 0;
    var hashCode = (uint)(_comparer?.GetHashCode(key) ?? key.GetHashCode());

    ref var bucket = ref GetBucket(hashCode);
    var entries = _entries!;
    var last = -1;
    var index = bucket - 1; // Value in buckets is 1-based

    while (index >= 0)
    {
      ref var entry = ref entries[index];

      if (entry.HashCode == hashCode && (_comparer?.Equals(entry.Key, key) ?? EqualityComparer<TKey>.Default.Equals(entry.Key, key)))
      {
        if (last < 0)
        {
          bucket = entry.Next + 1; // Value in buckets is 1-based
        }
        else
        {
          entries[last].Next = entry.Next;
        }

        // note: order is important
        ClearSameKeyValues(entry.StartIndex, entry.EndIndex);
        RemoveKeyEntryImpl(index, ref entry);
        return true;
      }

      last = index;
      index = entry.Next;

      collisionCount++;
      if (collisionCount > (uint)entries.Length)
      {
        // The chain of entries forms a loop; which means a concurrent update has happened.
        // Break out of the loop and throw, rather than looping forever.
        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
      }
    }

    return false;
  }

  public bool ContainsKey(TKey key)
  {
    var entryIndex = FindEntryIndex(key);
    return entryIndex >= 0;
  }

  public void Clear()
  {
    var keyCount = _keyCount;
    if (keyCount <= 0) return; // no keys - no values

    Debug.Assert(_buckets != null, "_buckets should be non-null");
    Debug.Assert(_entries != null, "_entries should be non-null");

    Array.Clear(_buckets, 0, _buckets.Length);
    Array.Clear(_entries, index: 0, length: keyCount);

    _keyCount = 0;
    _keyFreeList = -1;
    _keyFreeCount = 0;

    var valuesCount = _valuesCount; // may contains gaps
    if (valuesCount > 0 && MyRuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
    {
      Array.Clear(_values, 0, valuesCount);
    }

    _valuesCount = 0;
    _valueFreeList = -1;
    _valueFreeCount = 0;
  }

  public void TrimExcessKeys()
  {
    var newCapacity = HashHelpers.GetPrime(Count);
    var oldEntries = _entries;
    var currentCapacity = oldEntries == null ? 0 : oldEntries.Length;
    if (newCapacity >= currentCapacity)
      return;

    var oldCount = _keyCount;
    _version++;
    Initialize(newCapacity);

    Debug.Assert(oldEntries is not null);

    CopyEntries(oldEntries, oldCount);
  }

  public void TrimExcessValues()
  {
    var newCapacity = ValuesCount;
    var currentCapacity = _values.Length;
    if (newCapacity >= currentCapacity)
      return;

    _version++;

    if (newCapacity == 0)
    {
      _values = s_emptyValues;
      _indexes = s_emptyIndexes;
      // previous empty array may contains gaps, reset all value list counters
      _valuesCount = 0;
      _valueFreeCount = 0;
      _valueFreeList = -1;
    }
    else if (_valueFreeCount == 0) // no gaps
    {
      var newValues = new TValue[newCapacity];
      var newIndexes = new int[newCapacity];

      if (_valuesCount > 0)
      {
        Array.Copy(_values, newValues, length: _valuesCount);
        Array.Copy(_indexes, newIndexes, length: _valuesCount);
      }

      _values = newValues;
      _indexes = newIndexes;
    }
    else // has value gaps
    {
      var newValues = new TValue[newCapacity];
      var newIndexes = new int[newCapacity];

      var entries = _entries!;
      var oldValues = _values;
      var oldIndexes = _indexes;
      var newValuesIndex = 0;

      for (var keyIndex = 0; keyIndex < _keyCount; keyIndex++)
      {
        if (entries[keyIndex].Next < -1) continue;

        ref var entry = ref entries[keyIndex];

        var endIndex = entry.EndIndex;
        var newStartIndex = newValuesIndex;

        for (var index = entry.StartIndex; index != endIndex; index = oldIndexes[index])
        {
          newValues[newValuesIndex] = oldValues[index];
          newIndexes[newValuesIndex] = newValuesIndex + 1;
          newValuesIndex++;
        }

        newValues[newValuesIndex] = oldValues[endIndex]; // last value
        newIndexes[newValuesIndex] = oldIndexes[endIndex]; // copy count

        // update offsets in key entry
        entry.StartIndex = newStartIndex;
        entry.EndIndex = newValuesIndex;

        newValuesIndex++;
      }

      _values = newValues;
      _indexes = newIndexes;
      _valuesCount = newValuesIndex;
      _valueFreeCount = 0;
      _valueFreeList = -1;
    }
  }

  public ValuesCollection this[TKey key]
  {
    get
    {
      var entryIndex = FindEntryIndex(key);

      if (entryIndex >= 0)
      {
        var entries = _entries!;
        return new ValuesCollection(this, entries[entryIndex].StartIndex, entries[entryIndex].EndIndex);
      }

      return new ValuesCollection(this, startIndex: 0, endIndex: -1);
    }
  }

  public KeyValuePairEnumerator GetEnumerator() => new(this);

  public struct KeyValuePairEnumerator
  {
    private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
    private readonly int _version;
    private int _index;
    private KeyValuePair<TKey, ValuesCollection> _current;

    [Obsolete("Do not use", error: true)]
#pragma warning disable CS8618
    public KeyValuePairEnumerator() { }
#pragma warning restore CS8618

    internal KeyValuePairEnumerator(MultiValueDictionarySlim<TKey, TValue> dictionary)
    {
      _dictionary = dictionary;
      _version = dictionary._version;
      _index = 0;
      _current = default;
    }

    public bool MoveNext()
    {
      if (_version != _dictionary._version)
      {
        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
      }

      // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
      // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
      while ((uint)_index < (uint)_dictionary._keyCount)
      {
        ref var entry = ref _dictionary._entries![_index++];

        if (entry.Next >= -1)
        {
          _current = new KeyValuePair<TKey, ValuesCollection>(
            entry.Key, new ValuesCollection(_dictionary, entry.StartIndex, entry.EndIndex));
          return true;
        }
      }

      _index = _dictionary._keyCount + 1;
      _current = default;
      return false;
    }

    public readonly KeyValuePair<TKey, ValuesCollection> Current => _current;
  }

  [DebuggerTypeProxy(typeof(MultiValueDictionarySlimValueListDebugView<,>))]
  [DebuggerDisplay("Count = {Count}")]
  public readonly struct ValuesCollection
  {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly int _version;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly int _startIndex;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly int _endIndex;

    [Obsolete("Do not use", error: true)]
#pragma warning disable CS8618
    public ValuesCollection() { }
#pragma warning restore CS8618

    internal ValuesCollection(MultiValueDictionarySlim<TKey, TValue> dictionary, int startIndex, int endIndex)
    {
      _dictionary = dictionary;
      _version = dictionary._version;
      _startIndex = startIndex;
      _endIndex = endIndex;
    }

    public int Count => _endIndex < 0 ? 0 : _dictionary._indexes[_endIndex];
    public bool IsEmpty => _endIndex < 0;

    public ValuesEnumerator GetEnumerator()
    {
      if (_version != _dictionary._version)
      {
        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
      }

      return new ValuesEnumerator(_dictionary, _startIndex, _endIndex);
    }

    public struct ValuesEnumerator
    {
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private readonly int _version;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private readonly int _endIndex;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private int _currentIndex;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private TValue? _current;

      [Obsolete("Do not use", error: true)]
#pragma warning disable CS8618
      public ValuesEnumerator() { }
#pragma warning restore CS8618

      internal ValuesEnumerator(MultiValueDictionarySlim<TKey, TValue> dictionary, int startIndex, int endIndex)
      {
        _dictionary = dictionary;
        _version = dictionary._version;
        _endIndex = endIndex;
        _currentIndex = ~startIndex;
        _current = default;
      }

      public bool MoveNext()
      {
        var dictionary = _dictionary;
        if (_version == dictionary._version && _currentIndex != _endIndex)
        {
          if (_currentIndex < 0)
          {
            _currentIndex = ~_currentIndex;
            _current = dictionary._values[_currentIndex];
          }
          else
          {
            _currentIndex = dictionary._indexes[_currentIndex];
            _current = dictionary._values[_currentIndex];
          }

          return true;
        }

        return MoveNextRare();
      }

      private bool MoveNextRare()
      {
        if (_version != _dictionary._version)
        {
          ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
        }

        _current = default;
        return false;
      }

      public readonly TValue Current => _current!;
    }

    public TValue[] ToArray()
    {
      if (_version != _dictionary._version)
      {
        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
      }

      if (_endIndex < 0)
        return Array.Empty<TValue>();

      var count = _dictionary._indexes[_endIndex];
      if (count == 0)
        return Array.Empty<TValue>();

      var array = new TValue[count];
      var arrayIndex = 0;

      var values = _dictionary._values;
      var indexes = _dictionary._indexes;
      var endIndex = _endIndex;

      for (var index = _startIndex; index != endIndex; index = indexes[index])
      {
        array[arrayIndex++] = values[index];
      }

      array[arrayIndex] = values[endIndex];

      return array;
    }
  }

  public readonly struct KeyCollection
  {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;

    internal KeyCollection(MultiValueDictionarySlim<TKey, TValue> dictionary)
    {
      _dictionary = dictionary;
    }

    public int Count => _dictionary.Count;

    public KeyEnumerator GetEnumerator() => new(_dictionary);

    public struct KeyEnumerator
    {
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private readonly int _version;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private int _index;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private TKey? _current;

      [Obsolete("Do not use", error: true)]
#pragma warning disable CS8618
      public KeyEnumerator() { }
#pragma warning restore CS8618

      internal KeyEnumerator(MultiValueDictionarySlim<TKey, TValue> dictionary)
      {
        _dictionary = dictionary;
        _version = dictionary._version;
        _index = 0;
        _current = default;
      }

      public bool MoveNext()
      {
        if (_version != _dictionary._version)
        {
          ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
        }

        // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
        // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
        while ((uint)_index < (uint)_dictionary._keyCount)
        {
          ref var entry = ref _dictionary._entries![_index++];

          if (entry.Next >= -1)
          {
            _current = entry.Key;
            return true;
          }
        }

        _index = _dictionary._keyCount + 1;
        _current = default;
        return false;
      }

      public readonly TKey Current => _current!;
    }

    public TKey[] ToArray()
    {
      var keyCount = _dictionary.Count;
      if (keyCount == 0)
        return Array.Empty<TKey>();

      var array = new TKey[keyCount];
      var arrayIndex = 0;
      var entries = _dictionary._entries!;
      var keyEntriesCount = _dictionary._keyCount;

      // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
      // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
      for (var index = 0; (uint)index < (uint)keyEntriesCount; index++)
      {
        ref var entry = ref entries[index];
        if (entry.Next < -1) continue;

        array[arrayIndex++] = entry.Key;
      }

      return array;
    }
  }

  public readonly struct AllValuesCollection
  {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;

    internal AllValuesCollection(MultiValueDictionarySlim<TKey, TValue> dictionary)
    {
      _dictionary = dictionary;
    }

    public int Count => _dictionary.ValuesCount;

    public AllValuesEnumerator GetEnumerator() => new(_dictionary);

    public struct AllValuesEnumerator
    {
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private readonly int _version;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private int _keyIndex;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private int _valueEndIndex;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private int _valueCurrentIndex;
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private TValue? _current;

      [Obsolete("Do not use", error: true)]
#pragma warning disable CS8618
      public AllValuesEnumerator() { }
#pragma warning restore CS8618

      internal AllValuesEnumerator(MultiValueDictionarySlim<TKey, TValue> dictionary)
      {
        _dictionary = dictionary;
        _version = dictionary._version;
        _current = default;
        // end and current are equal
      }

      public bool MoveNext()
      {
        var dictionary = _dictionary;
        if (_version == dictionary._version && _valueCurrentIndex != _valueEndIndex)
        {
          _valueCurrentIndex = dictionary._indexes[_valueCurrentIndex];
          _current = dictionary._values[_valueCurrentIndex];
          return true;
        }

        return MoveNextRare();
      }

      private bool MoveNextRare()
      {
        if (_version != _dictionary._version)
        {
          ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
        }

        // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
        // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
        while ((uint)_keyIndex < (uint)_dictionary._keyCount)
        {
          ref var entry = ref _dictionary._entries![_keyIndex++];

          if (entry.Next >= -1)
          {
            _valueEndIndex = entry.EndIndex;
            _valueCurrentIndex = entry.StartIndex;
            _current = _dictionary._values[entry.StartIndex];
            return true;
          }
        }

        _keyIndex = _dictionary._keyCount + 1;
        _current = default;
        return false;
      }

      public readonly TValue Current => _current!;
    }

    public TValue[] ToArray()
    {
      var valuesCount = _dictionary.ValuesCount;
      if (valuesCount == 0)
        return Array.Empty<TValue>();

      var array = new TValue[valuesCount];
      var arrayIndex = 0;

      var entries = _dictionary._entries!;
      var values = _dictionary._values;
      var indexes = _dictionary._indexes;
      var keyEntriesCount = _dictionary._keyCount;

      // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
      // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
      for (var keyIndex = 0; (uint)keyIndex < (uint)keyEntriesCount; keyIndex++)
      {
        ref var entry = ref entries[keyIndex];
        if (entry.Next < -1) continue;

        var endIndex = entry.EndIndex;

        for (var index = entry.StartIndex; index != endIndex; index = indexes[index])
        {
          array[arrayIndex++] = values[index];
        }

        array[arrayIndex++] = values[endIndex];
      }

      return array;
    }
  }

  public void ProcessEach<TState>(TState state, Action<TState, TKey, MutableValuesCollection> processKey)
  {
    for (var index = 0; (uint)index < (uint)_keyCount; index++)
    {
      ref var entry = ref _entries![index];

      if (entry.Next >= -1)
      {
        var valuesCollection = new MutableValuesCollection(this, index);
        processKey(state, entry.Key, valuesCollection);

        if (valuesCollection.Count == 0)
        {
          RemoveConcreteEntry(ref entry, index);
        }
      }
    }

    return;

    void RemoveConcreteEntry(ref Entry entryToRemove, int indexToRemove)
    {
      uint collisionCount = 0;
      var hashCode = entryToRemove.HashCode;

      ref var bucket = ref GetBucket(hashCode);
      var entries = _entries!;
      var last = -1;
      var index = bucket - 1; // Value in buckets is 1-based

      while (index >= 0)
      {
        ref var entry = ref entries[index];

        if (entry.HashCode == hashCode && index == indexToRemove)
        {
          if (last < 0)
          {
            bucket = entry.Next + 1; // Value in buckets is 1-based
          }
          else
          {
            entries[last].Next = entry.Next;
          }

          // note: we expect all the values already be removed
          RemoveKeyEntryImpl(index, ref entry);
          return;
        }

        last = index;
        index = entry.Next;

        collisionCount++;
        if (collisionCount > (uint)entries.Length)
          ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
      }
    }
  }

  [DebuggerTypeProxy(typeof(MultiValueDictionarySlimValueListDebugView<,>))]
  [DebuggerDisplay("Count = {Count}")]
  public readonly struct MutableValuesCollection
  {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly int _entryIndex;

    internal MutableValuesCollection(MultiValueDictionarySlim<TKey, TValue> dictionary, int entryIndex)
    {
      _dictionary = dictionary;
      _entryIndex = entryIndex;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private int ValuesEndIndex => _dictionary._entries![_entryIndex].EndIndex;

    public int Count
    {
      get
      {
        var endIndex = ValuesEndIndex;
        return endIndex < 0 ? 0 : _dictionary._indexes[endIndex];
      }
    }

    public bool IsEmpty => ValuesEndIndex < 0;

    public ValuesCollection.ValuesEnumerator GetEnumerator()
    {
      var entry = _dictionary._entries![_entryIndex];
      return new ValuesCollection.ValuesEnumerator(_dictionary, entry.StartIndex, entry.EndIndex);
    }

    public TValue[] ToArray()
    {
      var entry = _dictionary._entries![_entryIndex];
      var valuesCollection = new ValuesCollection(_dictionary, entry.StartIndex, entry.EndIndex);
      return valuesCollection.ToArray();
    }

    public void Clear()
    {
      ref var entry = ref _dictionary._entries![_entryIndex];
      if (entry.EndIndex < 0) return; // already empty

      _dictionary.ClearSameKeyValues(entry.StartIndex, entry.EndIndex);
      entry.StartIndex = 0; // note: important for GetEnumerator()
      entry.EndIndex = -1; // invalid state to indicate emptiness

      _dictionary._version++;
    }

    public void Add(TValue value)
    {
      ref var entry = ref _dictionary._entries![_entryIndex];
      _dictionary.StoreEntryValue(ref entry, value);
      _dictionary._version++;
    }

    public void AddRange(IEnumerable<TValue> values)
    {
      // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
      if (values == null) ThrowHelper.ThrowArgumentNullException(nameof(values));

      if (values is ICollection<TValue> collection)
      {
        var collectionCount = collection.Count;
        if (collectionCount > 0)
        {
          ref var entry = ref _dictionary._entries![_entryIndex];

          _dictionary.StoreEntryValuesCollection(ref entry, collection, collectionCount);
        }
      }
      else
      {
        using var enumerator = values.GetEnumerator();
        ref var entry = ref _dictionary._entries![_entryIndex];

        while (enumerator.MoveNext())
        {
          _dictionary.StoreEntryValue(ref entry, enumerator.Current);
        }
      }

      _dictionary._version++;
    }
  }

  //////////////////////////////////////////////////////////////

  private void RemoveKeyEntryImpl(int entryIndex, ref Entry entry)
  {
    Debug.Assert(
      StartOfFreeList - _keyFreeList < 0,
      "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = " +
      "0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");

    entry.Next = StartOfFreeList - _keyFreeList;

    if (MyRuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
    {
      entry.Key = default!;
    }

    entry.EndIndex = -1; // mark entry as incomplete

    _keyFreeList = entryIndex;
    _keyFreeCount++;
  }

  private void ClearSameKeyValues(int startIndex, int endIndex)
  {
    Debug.Assert(startIndex >= 0);
    Debug.Assert(endIndex >= 0);

    if (MyRuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
    {
      // O(n) removal, unfortunately
      for (var index = startIndex; index != endIndex; index = _indexes[index])
      {
        _values[index] = default!;
      }

      _values[endIndex] = default!;
    }

    // append whole linked list of values to freelist
    var count = _indexes[endIndex];
    _valueFreeCount += count;
    _indexes[endIndex] = _valueFreeList;
    _valueFreeList = startIndex;
  }

  private void Initialize(int capacity)
  {
    var size = HashHelpers.GetPrime(capacity);
    var buckets = new int[size];
    var entries = new Entry[size];

    // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
    _keyFreeList = -1;
    _buckets = buckets;
    _entries = entries;
  }

  private int GetOrCreateEntry(TKey key)
  {
    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    if (key == null)
    {
      ThrowHelper.ThrowArgumentNullException(nameof(key));
    }

    if (_buckets == null)
    {
      Initialize(capacity: 0);
    }

    Debug.Assert(_buckets != null);

    var entries = _entries;
    Debug.Assert(entries != null, "expected entries to be non-null");

    var comparer = _comparer;
    var hashCode = (uint)(comparer == null ? key.GetHashCode() : comparer.GetHashCode(key));

    uint collisionCount = 0;
    ref var bucket = ref GetBucket(hashCode);
    var i = bucket - 1; // Value in _buckets is 1-based

    if (comparer == null)
    {
      if (typeof(TKey).IsValueType)
      {
        // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
        while (true)
        {
          // Should be a while loop https://github.com/dotnet/runtime/issues/9422
          // Test uint in if rather than loop condition to drop range check for following array access
          if ((uint)i >= (uint)entries.Length)
          {
            break; // not found
          }

          if (entries[i].HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].Key, key))
          {
            return i;
          }

          i = entries[i].Next;

          collisionCount++;
          if (collisionCount > (uint)entries.Length)
          {
            // The chain of entries forms a loop; which means a concurrent update has happened.
            // Break out of the loop and throw, rather than looping forever.
            ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
          }
        }
      }
      else // reference type
      {
        // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
        // https://github.com/dotnet/runtime/issues/10050
        // So cache in a local rather than get EqualityComparer per loop iteration
        var defaultComparer = EqualityComparer<TKey>.Default;
        while (true)
        {
          // Should be a while loop https://github.com/dotnet/runtime/issues/9422
          // Test uint in if rather than loop condition to drop range check for following array access
          if ((uint)i >= (uint)entries.Length)
          {
            break; // not found
          }

          if (entries[i].HashCode == hashCode && defaultComparer.Equals(entries[i].Key, key))
          {
            return i;
          }

          i = entries[i].Next;

          collisionCount++;
          if (collisionCount > (uint)entries.Length)
          {
            // The chain of entries forms a loop; which means a concurrent update has happened.
            // Break out of the loop and throw, rather than looping forever.
            ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
          }
        }
      }
    }
    else // custom comparer
    {
      while (true)
      {
        // Should be a while loop https://github.com/dotnet/runtime/issues/9422
        // Test uint in if rather than loop condition to drop range check for following array access
        if ((uint)i >= (uint)entries.Length)
        {
          break;
        }

        if (entries[i].HashCode == hashCode && comparer.Equals(entries[i].Key, key))
        {
          return i;
        }

        i = entries[i].Next;

        collisionCount++;
        if (collisionCount > (uint)entries.Length)
        {
          // The chain of entries forms a loop; which means a concurrent update has happened.
          // Break out of the loop and throw, rather than looping forever.
          ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
        }
      }
    }

    int index;
    if (_keyFreeCount > 0)
    {
      index = _keyFreeList;
      Debug.Assert(StartOfFreeList - entries[_keyFreeList].Next >= -1, "shouldn't overflow because `next` cannot underflow");

      _keyFreeList = StartOfFreeList - entries[_keyFreeList].Next;
      _keyFreeCount--;
    }
    else
    {
      var count = _keyCount;
      if (count == entries.Length)
      {
        ResizeKeys();
        bucket = ref GetBucket(hashCode);
      }

      index = count;
      _keyCount = count + 1;
      entries = _entries;
    }

    ref var entry = ref entries![index];
    entry.HashCode = hashCode;
    entry.Next = bucket - 1; // Value in _buckets is 1-based
    entry.Key = key;
    entry.EndIndex = -1;
    bucket = index + 1; // Value in _buckets is 1-based

    // users of this method must do this
    // _version++;

    return index;
  }

  private int FindEntryIndex(TKey key)
  {
    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    if (key == null)
    {
      ThrowHelper.ThrowArgumentNullException(nameof(key));
    }

    if (_buckets == null) return -1;

    Debug.Assert(_entries != null, "expected entries to be != null");

    var comparer = _comparer;
    if (comparer == null)
    {
      var hashCode = (uint)key.GetHashCode();
      var i = GetBucket(hashCode);
      var entries = _entries!;
      uint collisionCount = 0;

      if (typeof(TKey).IsValueType)
      {
        // devirtualize with EqualityComparer<TValue>.Default intrinsic for value types

        i--; // Value in _buckets is 1-based; subtract 1 from `i`. We do it here, so it fuses with the following conditional
        do
        {
          // Should be a while loop https://github.com/dotnet/runtime/issues/9422
          // Test in if to drop range check for following array access
          if ((uint)i >= (uint)entries.Length)
          {
            return -1;
          }

          if (entries[i].HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].Key, key))
          {
            return i;
          }

          i = entries[i].Next;

          collisionCount++;
        } while (collisionCount <= (uint)entries.Length);
      }
      else // reference type
      {
        // cache in the local for reference types
        var defaultComparer = EqualityComparer<TKey>.Default;

        i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
        do
        {
          // Should be a while loop https://github.com/dotnet/runtime/issues/9422
          // Test in if to drop range check for following array access
          if ((uint)i >= (uint)entries.Length)
          {
            return -1;
          }

          if (entries[i].HashCode == hashCode && defaultComparer.Equals(entries[i].Key, key))
          {
            return i;
          }

          i = entries[i].Next;

          collisionCount++;
        } while (collisionCount <= (uint)entries.Length);
      }
    }
    else // custom comparer
    {
      var hashCode = (uint)comparer.GetHashCode(key);
      var i = GetBucket(hashCode);
      var entries = _entries!;
      uint collisionCount = 0;
      i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
      do
      {
        // Should be a while loop https://github.com/dotnet/runtime/issues/9422
        // Test in if to drop range check for following array access
        if ((uint)i >= (uint)entries.Length)
        {
          return -1;
        }

        if (entries[i].HashCode == hashCode && comparer.Equals(entries[i].Key, key))
        {
          return i;
        }

        i = entries[i].Next;

        collisionCount++;
      } while (collisionCount <= (uint)entries.Length);
    }

    // The chain of entries forms a loop; which means a concurrent update has happened.
    // Break out of the loop and throw, rather than looping forever.
    ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
    return -1;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private ref int GetBucket(uint hashCode)
  {
    var buckets = _buckets!;
    return ref buckets[hashCode % (uint)buckets.Length];
  }

  private void CopyEntries(Entry[] oldEntries, int count)
  {
    Debug.Assert(_entries is not null);

    var newEntries = _entries;
    var newCount = 0;
    for (var index = 0; index < count; index++)
    {
      var hashCode = oldEntries[index].HashCode;
      if (oldEntries[index].Next < -1) continue;

      ref var entry = ref newEntries[newCount];
      entry = oldEntries[index];

      ref var bucket = ref GetBucket(hashCode);
      entry.Next = bucket - 1; // Value in _buckets is 1-based
      bucket = newCount + 1;
      newCount++;
    }

    _keyCount = newCount;
    _keyFreeCount = 0;
  }

  private void StoreEntryValue(ref Entry entry, TValue value)
  {
    // allocate a place for value to store
    int valueIndex;
    if (_valueFreeCount > 0) // try freelist first
    {
      valueIndex = _valueFreeList;
      Debug.Assert(valueIndex < _values.Length);

      _valueFreeList = _indexes[valueIndex]; // next freelist index
      _valueFreeCount--;
    }
    else
    {
      valueIndex = _valuesCount;

      if (valueIndex == _values.Length) // value list is full
      {
        ResizeValues();

        Debug.Assert(valueIndex < _values.Length);
        Debug.Assert(valueIndex < _indexes.Length);
      }

      _valuesCount++;
    }

    if (entry.EndIndex == -1) // new key added
    {
      entry.StartIndex = valueIndex;
      entry.EndIndex = valueIndex;

      _values[valueIndex] = value;
      _indexes[valueIndex] = 1; // store count
    }
    else // key has values associated
    {
      var oldCount = _indexes[entry.EndIndex];
      _indexes[entry.EndIndex] = valueIndex;

      entry.EndIndex = valueIndex; // append new item
      _values[valueIndex] = value;
      _indexes[valueIndex] = oldCount + 1; // new count
    }
  }

  private void StoreEntryValuesCollection(ref Entry entry, ICollection<TValue> collection, int collectionCount)
  {
    Debug.Assert(collectionCount > 0);

    if (_valuesCount + collectionCount <= _values.Length) // has tail space
    {
      // can copy to the tail of the list
    }
    // fill the gaps
    else if (_valuesCount - _valueFreeCount + collectionCount < _values.Length)
    {
      // todo: specializations

      using var enumerator = collection.GetEnumerator(); // alloc :(

      while (enumerator.MoveNext())
      {
        StoreEntryValue(ref entry, enumerator.Current);
      }

      return;
    }
    // resize and copy to the tail
    else
    {
      ResizeValues(extraCapacity: collectionCount);

      Debug.Assert(_valuesCount + collectionCount <= _values.Length);
    }

    // copy to the list tail
    collection.CopyTo(_values, _valuesCount);

    var endIndex = _valuesCount + collectionCount - 1;
    var indexes = _indexes;
    int countBefore;

    if (entry.EndIndex < 0) // empty entry
    {
      countBefore = 0;
      entry.StartIndex = _valuesCount;
      entry.EndIndex = endIndex;
    }
    else
    {
      countBefore = indexes[entry.EndIndex];
      indexes[entry.EndIndex] = _valuesCount; // link from last value to head of the copied list
      entry.EndIndex = endIndex;
    }

    for (var index = _valuesCount; index < endIndex; index++)
    {
      indexes[index] = index + 1;
    }

    indexes[endIndex] = countBefore + collectionCount;

    _valuesCount += collectionCount;
  }

  private void ResizeKeys()
  {
    Debug.Assert(_keyFreeCount == 0); // only resize when freelist is empty
    ResizeKeys(HashHelpers.ExpandPrime(_keyCount));
  }

  private void ResizeKeys(int newSize)
  {
    Debug.Assert(_entries != null, "_entries should be non-null");
    Debug.Assert(newSize >= _entries.Length);

    var entries = new Entry[newSize];

    var count = _keyCount;
    Array.Copy(_entries, entries, count);

    // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
    _buckets = new int[newSize];

    for (var i = 0; i < count; i++)
    {
      if (entries[i].Next >= -1)
      {
        ref var bucket = ref GetBucket(entries[i].HashCode);
        entries[i].Next = bucket - 1; // Value in _buckets is 1-based
        bucket = i + 1;
      }
    }

    _entries = entries;
  }

  private void ResizeValues(int extraCapacity = 1)
  {
    Debug.Assert(extraCapacity >= 1);
    Debug.Assert(_valuesCount <= _values.Length);

    if (_values.Length == 0)
    {
      var capacity = Math.Max(extraCapacity, DefaultValuesListSize);
      _values = new TValue[capacity];
      _indexes = new int[capacity];
    }
    else
    {
      var capacity = Math.Max(
        Math.Max(_valuesCount - _valueFreeCount + extraCapacity, _values.Length * 2),
        DefaultValuesListSize);

      var newValues = new TValue[capacity];
      var newIndexes = new int[capacity];
      var newValuesIndex = 0;

      var keyIndex = 0;
      var keyCount = (uint)_keyCount;
      var entries = _entries!;

      while ((uint)keyIndex < keyCount)
      {
        ref var entry = ref entries[keyIndex++];
        if (entry.Next < -1) continue;

        var newStartIndex = newValuesIndex;
        var endIndex = entry.EndIndex;
        if (endIndex < 0) continue; // allocated but empty entry

        for (var index = entry.StartIndex; index != endIndex; index = _indexes[index])
        {
          newValues[newValuesIndex] = _values[index];
          newIndexes[newValuesIndex] = newValuesIndex + 1;
          newValuesIndex++;
        }

        newValues[newValuesIndex] = _values[endIndex];
        newIndexes[newValuesIndex] = _indexes[endIndex]; // copy count

        entry.StartIndex = newStartIndex;
        entry.EndIndex = newValuesIndex;
        newValuesIndex++;
      }

      _values = newValues;
      _indexes = newIndexes;
      _valuesCount = newValuesIndex;
      _valueFreeCount = 0;
      _valueFreeList = -1;

      Debug.Assert(_valuesCount < _values.Length);
    }
  }

  [Conditional("DEBUG")]
  public void VerifyConsistency()
  {
    var usedValuesIndexes = new HashSet<int>();

    var entries = _entries;
    if (entries != null)
    {
      var keyIsOfReferenceType = !typeof(TKey).IsValueType;
      var freelistIndexes = new HashSet<int>();
      var occupiedKeysCount = 0;

      for (var index = 0; index < entries.Length; index++)
      {
        var entry = entries[index];

        if (index < _keyCount)
        {
          if (entry.Next >= -1) // occupied entry
          {
            if (keyIsOfReferenceType) Assert(!ReferenceEquals(entry.Key, null));

            occupiedKeysCount++;

            Assert(entry.StartIndex >= 0);
            Assert(entry.EndIndex >= 0);

            for (int valueIndex = entry.StartIndex, valuesCount = 0; ; valueIndex = _indexes[valueIndex])
            {
              Assert(usedValuesIndexes.Add(valueIndex));
              valuesCount++;

              if (valueIndex == entry.EndIndex)
              {
                Assert(_indexes[valueIndex] == valuesCount);
                break;
              }
            }
          }
          else // freelist entry
          {
            Assert(entry.EndIndex == -1);

            if (keyIsOfReferenceType) Assert(ReferenceEquals(entry.Key, null));

            freelistIndexes.Add(index);
          }
        }
        else // not allocated entry
        {
          Assert(entry.Next == 0);

          if (keyIsOfReferenceType) Assert(ReferenceEquals(entry.Key, null));
        }
      }

      Assert(occupiedKeysCount == Count);
      Assert(freelistIndexes.Count == _keyFreeCount);

      if (freelistIndexes.Count > 0)
      {
        var freeIndex = _keyFreeList;
        do
        {
          Assert(freelistIndexes.Remove(freeIndex));
          freeIndex = StartOfFreeList - entries[freeIndex].Next;
        }
        while (freeIndex >= 0);

        Assert(freeIndex == -1);
        Assert(freelistIndexes.Count == 0);
      }
      else
      {
        Assert(_keyFreeList == -1);
        Assert(_keyFreeCount == 0);
      }
    }
    else
    {
      Assert(_keyFreeList == 0); // not initialized
      Assert(_keyFreeCount == 0);
    }

    var values = _values;
    var valueIsOfReferenceType = !typeof(TValue).IsValueType;

    Assert(ValuesCount == usedValuesIndexes.Count);

    for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
    {
      if (usedValuesIndexes.Contains(valueIndex))
      {
        // occupied value slot
      }
      else
      {
        if (valueIsOfReferenceType) Assert(ReferenceEquals(values[valueIndex], null));
      }
    }

    return;

    void Assert(bool condition)
    {
      if (!condition) throw new InvalidOperationException();
    }
  }

  [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
  internal readonly struct DebugPair(TKey key, TValue[] values)
  {
    public readonly TKey Key = key;

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public readonly TValue[] Values = values;

    public override string ToString()
    {
      return Values.Length == 1
        ? $"[{Key}, {Values[0]}]"
        : $"[{Key}, <Count = {Values.Length}>]";
    }
  }
}

[SuppressMessage("ReSharper", "UnusedMember.Local")]
file sealed class MultiValueDictionarySlimDebugView<TKey, TValue>(MultiValueDictionarySlim<TKey, TValue> dictionary)
  where TKey : notnull
{
  [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
  public MultiValueDictionarySlim<TKey, TValue>.DebugPair[] Entries
  {
    get
    {
      var entries = new MultiValueDictionarySlim<TKey, TValue>.DebugPair[dictionary.Count];
      var index = 0;

      foreach (var pair in dictionary)
      {
        entries[index++] = new MultiValueDictionarySlim<TKey, TValue>.DebugPair(pair.Key, pair.Value.ToArray());
      }

      return entries;
    }
  }
}

[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
file class MultiValueDictionarySlimValueListDebugView<TKey, TValue>
  where TKey : notnull
{
  public MultiValueDictionarySlimValueListDebugView(
    MultiValueDictionarySlim<TKey, TValue>.ValuesCollection valuesCollection)
  {
    Entries = valuesCollection.ToArray();
  }

  public MultiValueDictionarySlimValueListDebugView(
    MultiValueDictionarySlim<TKey, TValue>.MutableValuesCollection mutableValuesCollection)
  {
    Entries = mutableValuesCollection.ToArray();
  }

  [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
  public TValue[] Entries { get; }
}
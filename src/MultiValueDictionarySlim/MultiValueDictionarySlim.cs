using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

// ReSharper disable IntroduceOptionalParameters.Global
// ReSharper disable UseArrayEmptyMethod
// ReSharper disable MemberHidesStaticFromOuterClass

// ReSharper disable MergeConditionalExpression
// ReSharper disable ConditionIsAlwaysTrueOrFalse

namespace System.Collections.Generic;

// todo: keys collection
// todo: all values collection
// todo: do we need separate capacity for values? can it be lower?
// todo: count + start index = int
// todo: one item price must be low
// todo: sequential addition price must be low
// todo: must be poolable, big sequential chunks
// todo: ushort start + ushort count = can't address much items
// todo: [k1, 111], [k2, 111], [k1, 222], [k2, 222] problem
// todo: [1][2][1,1,1,1][3][2,2,2] - 11223
// todo: 1 int per item?
// todo: store count somewhere?

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

  private const int StartOfFreeList = -3;

  private int _keyFreeList;
  private int _keyFreeCount;
  private int _valueFreeList;
  private int _valueFreeCount;

  private const int DefaultValuesListSize = 4;

  private static readonly TValue[] s_emptyValues = new TValue[0];
  private static readonly int[] s_emptyIndexes = new int[0];

  private struct Entry
  {
    public uint HashCode;

    /// <summary>
    /// 0-based index of next entry in chain: -1 means end of chain
    /// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
    /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
    /// </summary>
    public int Next;

    public TKey Key;

    // can be 0
    public int StartIndex;

    // can be 0, can be the same as StartIndex
    // index at EndIndex is a Count or collection
    // can be -1 for allocated empty entries
    public int EndIndex;

    public override string ToString()
    {
      return $"Entry [key={Key}, start={StartIndex}, end={EndIndex}]";
    }
  }

  public MultiValueDictionarySlim()
    : this(keyCapacity: 0, 0, comparer: null) { }

  public MultiValueDictionarySlim(int keyCapacity, int valueCapacity)
    : this(keyCapacity: keyCapacity, valueCapacity, comparer: null) { }

  public MultiValueDictionarySlim(IEqualityComparer<TKey>? comparer)
    : this(keyCapacity: 0, valueCapacity: 0, comparer) { }

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

  public IEqualityComparer<TKey> Comparer => _comparer ?? EqualityComparer<TKey>.Default;

  public int KeysCapacity => _entries == null ? 0 : _entries.Length;
  public int ValuesCapacity => _values.Length;

  public void Add(TKey key, TValue value)
  {
    var entryIndex = GetOrCreateKeyEntry(key);

    ref var entry = ref _entries![entryIndex];

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

    _version++;
  }

  public bool Remove(TKey key)
  {
    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    if (key == null)
    {
      ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
    }

    if (_buckets != null)
    {
      Debug.Assert(_entries != null, "entries should be non-null");

      uint collisionCount = 0;
      var hashCode = (uint)(_comparer?.GetHashCode(key) ?? key.GetHashCode());

      ref var bucket = ref GetBucket(hashCode);
      var entries = _entries;
      var last = -1;
      var i = bucket - 1; // Value in buckets is 1-based

      while (i >= 0)
      {
        ref var entry = ref entries[i];

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

          Debug.Assert(
            StartOfFreeList - _keyFreeList < 0,
            "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
          entry.Next = StartOfFreeList - _keyFreeList;

          if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
          {
            entry.Key = default!;
          }

          if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
          {
            // O(n) removal, unfortunately
            var endIndex = entry.EndIndex;
            for (var index = entry.StartIndex; index != endIndex; index = _indexes[index])
            {
              _values[index] = default!;
            }

            _values[endIndex] = default!;
          }

          // append linked list of values to freelist
          var count = _indexes[entry.EndIndex];
          _valueFreeCount += count;
          _indexes[entry.EndIndex] = _valueFreeList;
          _valueFreeList = entry.StartIndex;

          // todo: do we need this?
          //entry.StartIndex = -1;
          entry.EndIndex = -1;

          _keyFreeList = i;
          _keyFreeCount++;
          return true;
        }

        last = i;
        i = entry.Next;

        collisionCount++;
        if (collisionCount > (uint)entries.Length)
        {
          // The chain of entries forms a loop; which means a concurrent update has happened.
          // Break out of the loop and throw, rather than looping forever.
          ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
        }
      }
    }

    return false;
  }

  public bool ContainsKey(TKey key)
  {
    return !Unsafe.IsNullRef(ref FindEntry(key));
  }

  public void Clear()
  {
    var count = _keyCount;
    if (count <= 0) return;

    Debug.Assert(_buckets != null, "_buckets should be non-null");
    Debug.Assert(_entries != null, "_entries should be non-null");

    Array.Clear(_buckets, 0, _buckets.Length);

    _keyCount = 0;
    _keyFreeList = -1;
    _keyFreeCount = 0;

    _valuesCount = 0;
    _valueFreeList = -1;
    _valueFreeCount = 0;

    Array.Clear(_entries, 0, count);

    if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
    {
      Array.Clear(_values, 0, _valuesCount);
    }
  }

  private void Initialize(int capacity)
  {
    var size = HashHelpers.GetPrime(capacity);
    var buckets = new int[size];
    var entries = new Entry[size];

    // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
    _keyFreeList = -1;
#if TARGET_64BIT
    _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)size);
#endif
    _buckets = buckets;
    _entries = entries;
  }

  private int GetOrCreateKeyEntry(TKey key)
  {
    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    if (key == null)
    {
      ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
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
            break;
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
      else
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
            break;
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
    else
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
      Debug.Assert((StartOfFreeList - entries[_keyFreeList].Next) >= -1, "shouldn't overflow because `next` cannot underflow");
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

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private ref int GetBucket(uint hashCode)
  {
    var buckets = _buckets!;
#if TARGET_64BIT
    return ref buckets[HashHelpers.FastMod(hashCode, (uint)buckets.Length, _fastModMultiplier)];
#else
    return ref buckets[hashCode % (uint) buckets.Length];
#endif
  }

  private void ResizeKeys()
  {
    // todo: count?
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
#if TARGET_64BIT
    _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)newSize);
#endif

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
      var keyCount = (uint) _keyCount;
      var entries = _entries!;

      while ((uint) keyIndex < keyCount)
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

  private ref Entry FindEntry(TKey key)
  {
    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
    if (key == null)
    {
      ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
    }

    ref var entry = ref Unsafe.NullRef<Entry>();
    if (_buckets != null)
    {
      Debug.Assert(_entries != null, "expected entries to be != null");
      var comparer = _comparer;
      if (comparer == null)
      {
        var hashCode = (uint)key.GetHashCode();
        var i = GetBucket(hashCode);
        var entries = _entries;
        uint collisionCount = 0;
        if (typeof(TKey).IsValueType)
        {
          // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic

          i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
          do
          {
            // Should be a while loop https://github.com/dotnet/runtime/issues/9422
            // Test in if to drop range check for following array access
            if ((uint)i >= (uint)entries.Length)
            {
              goto ReturnNotFound;
            }

            entry = ref entries[i];
            if (entry.HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entry.Key, key))
            {
              goto ReturnFound;
            }

            i = entry.Next;

            collisionCount++;
          } while (collisionCount <= (uint)entries.Length);

          // The chain of entries forms a loop; which means a concurrent update has happened.
          // Break out of the loop and throw, rather than looping forever.
          goto ConcurrentOperation;
        }
        else
        {
          // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
          // https://github.com/dotnet/runtime/issues/10050
          // So cache in a local rather than get EqualityComparer per loop iteration
          var defaultComparer = EqualityComparer<TKey>.Default;

          i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
          do
          {
            // Should be a while loop https://github.com/dotnet/runtime/issues/9422
            // Test in if to drop range check for following array access
            if ((uint)i >= (uint)entries.Length)
            {
              goto ReturnNotFound;
            }

            entry = ref entries[i];
            if (entry.HashCode == hashCode && defaultComparer.Equals(entry.Key, key))
            {
              goto ReturnFound;
            }

            i = entry.Next;

            collisionCount++;
          }
          while (collisionCount <= (uint)entries.Length);

          // The chain of entries forms a loop; which means a concurrent update has happened.
          // Break out of the loop and throw, rather than looping forever.
          goto ConcurrentOperation;
        }
      }
      else
      {
        var hashCode = (uint)comparer.GetHashCode(key);
        var i = GetBucket(hashCode);
        var entries = _entries;
        uint collisionCount = 0;
        i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
        do
        {
          // Should be a while loop https://github.com/dotnet/runtime/issues/9422
          // Test in if to drop range check for following array access
          if ((uint)i >= (uint)entries.Length)
          {
            goto ReturnNotFound;
          }

          entry = ref entries[i];
          if (entry.HashCode == hashCode && comparer.Equals(entry.Key, key))
          {
            goto ReturnFound;
          }

          i = entry.Next;

          collisionCount++;
        } while (collisionCount <= (uint)entries.Length);

        // The chain of entries forms a loop; which means a concurrent update has happened.
        // Break out of the loop and throw, rather than looping forever.
        goto ConcurrentOperation;
      }
    }

    goto ReturnNotFound;

    ConcurrentOperation:
    ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();

    ReturnFound:
    ref var value = ref entry;

    Return:
    return ref value;

    ReturnNotFound:
    value = ref Unsafe.NullRef<Entry>();
    goto Return;
  }

  public ValuesList this[TKey key]
  {
    get
    {
      ref var entry = ref FindEntry(key);

      if (!Unsafe.IsNullRef(ref entry))
      {
        return new ValuesList(this, entry.StartIndex, entry.EndIndex);
      }

      return new ValuesList(this);
    }
  }

  public Enumerator GetEnumerator() => new(this);

  public struct Enumerator
  {
    private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
    private readonly int _version;
    private int _index;
    private KeyValuePair<TKey, ValuesList> _current;

    public Enumerator(MultiValueDictionarySlim<TKey, TValue> dictionary)
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
      while ((uint) _index < (uint) _dictionary._keyCount)
      {
        ref var entry = ref _dictionary._entries![_index++];

        if (entry.Next >= -1)
        {
          _current = new KeyValuePair<TKey, ValuesList>(
            entry.Key, new ValuesList(_dictionary, entry.StartIndex, entry.EndIndex));
          return true;
        }
      }

      _index = _dictionary._keyCount + 1;
      _current = default;
      return false;
    }

    public KeyValuePair<TKey, ValuesList> Current => _current;
  }

  // todo: debug view
  [DebuggerTypeProxy(typeof(MultiValueDictionarySlimValueListDebugView<,>))]
  [DebuggerDisplay("Count = {Count}")]
  public readonly struct ValuesList
  {
    private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
    private readonly int _version;
    private readonly int _startIndex;
    private readonly int _endIndex;
    private readonly int _count;

    public ValuesList(MultiValueDictionarySlim<TKey, TValue> dictionary)
    {
      _dictionary = dictionary;
      _version = dictionary._version;
      _startIndex = 0;
      _endIndex = 0;
      _count = 0;
    }

    internal ValuesList(MultiValueDictionarySlim<TKey, TValue> dictionary, int startIndex, int endIndex)
    {
      _dictionary = dictionary;
      _version = dictionary._version;
      _startIndex = startIndex;
      _endIndex = endIndex;
      _count = dictionary._indexes[endIndex];
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public Enumerator GetEnumerator()
    {
      if (_version != _dictionary._version)
      {
        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
      }

      return new Enumerator(_dictionary, _version, _startIndex, _count);
    }

    public TValue this[int index]
    {
      get
      {
        if ((uint) index >= (uint) _count)
          ThrowHelper.ThrowArgumentOutOfRangeException(nameof(index));

        return _dictionary._values[_startIndex + index];
      }
      // todo: implement set?
    }

    public struct Enumerator
    {
      private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
      private readonly int _version;
      private readonly int _finalIndex;
      private int _indexInList;
      private TValue? _current;

      public Enumerator(MultiValueDictionarySlim<TKey, TValue> dictionary, int version, int startIndex, int count)
      {
        _dictionary = dictionary;
        _version = version;
        _indexInList = startIndex;
        _finalIndex = startIndex + count;
        _current = default;
      }

      public bool MoveNext()
      {
        var dictionary = _dictionary;
        if (_version == dictionary._version && (uint)_indexInList < (uint)_finalIndex)
        {
          _current = dictionary._values[_indexInList];
          _indexInList++;
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

      public TValue Current => _current!;
    }

    public TValue[] ToArray()
    {
      if (_count == 0)
        return Array.Empty<TValue>();

      var array = new TValue[_count];

      // todo: for loop

      Array.Copy(_dictionary._values, _startIndex, array, destinationIndex: 0, _count);
      return array;
    }
  }

  internal class Pair
  {
    public readonly TKey Key;
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public readonly TValue[] Values;

    public Pair(TKey key, TValue[] values)
    {
      Key = key;
      Values = values;
    }

    public override string ToString()
    {
      return Values.Length == 1
        ? $"[{Key}, {Values[0]}]"
        : $"[{Key}, <Count = {Values.Length}>]";
    }
  }
}

internal sealed class MultiValueDictionarySlimDebugView<TKey, TValue>
  where TKey : notnull
{
  private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;

  public MultiValueDictionarySlimDebugView(MultiValueDictionarySlim<TKey, TValue> dictionary)
  {
    _dictionary = dictionary;
  }

  [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
  private MultiValueDictionarySlim<TKey, TValue>.Pair[] Entries
  {
    get
    {
      var entries = new MultiValueDictionarySlim<TKey, TValue>.Pair[_dictionary.Count];
      var index = 0;

      foreach (var (key, values) in _dictionary)
      {
        entries[index++] = new MultiValueDictionarySlim<TKey, TValue>.Pair(key, values.ToArray());
      }

      return entries;
    }
  }
}

internal sealed class MultiValueDictionarySlimValueListDebugView<TKey, TValue>
  where TKey : notnull
{
  private readonly MultiValueDictionarySlim<TKey, TValue>.ValuesList _valuesList;

  public MultiValueDictionarySlimValueListDebugView(MultiValueDictionarySlim<TKey, TValue>.ValuesList valuesList)
  {
    _valuesList = valuesList;
  }

  [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
  private TValue[] Entries => _valuesList.ToArray();
}
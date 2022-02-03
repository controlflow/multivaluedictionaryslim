using System.Diagnostics;
using System.Runtime.CompilerServices;

// ReSharper disable MergeConditionalExpression
// ReSharper disable ConditionIsAlwaysTrueOrFalse

namespace System.Collections.Generic;

// todo: keys collection
// todo: all values collection 

[DebuggerTypeProxy(typeof(MultiValueDictionarySlimDebugView<,>))]
[DebuggerDisplay("Count = {Count}")]
public class MultiValueDictionarySlim<TKey, TValue>
  where TKey : notnull
{
  private int[]? _buckets;
  private Entry[]? _entries;

  private int _count;
  private int _freeList;
  private int _freeCount;
  private int _version;
  
  private IEqualityComparer<TKey>? _comparer;
  
  
  
  private const int StartOfFreeList = -3;

  private TValue[]? _values = s_emptyValues;
  private int _valuesFreeStartIndex;
  private int _valuesStoredCount;
  
  private const int DefaultValuesListSize = 4;

  private static readonly TValue[] s_emptyValues = Array.Empty<TValue>();

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

    public int StartIndex;
    public int Capacity;
    public int Count;

    public override string ToString()
    {
      return $"Entry [key={Key}, used={Count} of {Capacity}, range={StartIndex}-{StartIndex + Capacity}]";
    }
  }
  
  // TODO: constructors, remove field initializers?
  
  public int Count => _count - _freeCount;

  public void Add(TKey key, TValue value)
  {
    ref var entry = ref GetOrCreateEntry(key);

    if (entry.Capacity == 0)
    {
      // entry was not allocated in the _values array
      // this is the most likely case - fresh key w/o any values added

      if (_valuesFreeStartIndex == _values.Length)
      {
        ExpandValuesListOrCompact();
      }
        
      // allocate single item list first
      // we expect { 1 key - 1 value } to be the common scenario
      entry.StartIndex = _valuesFreeStartIndex;
      entry.Capacity = 1;
      _values[_valuesFreeStartIndex] = value;
      entry.Count = 1;
      _valuesFreeStartIndex++;
    }
    else
    {
      Debug.Assert(_values != null);

      // entry has values associated
      if (entry.Count < entry.Capacity)
      {
        // there is a free space inside a list, simply put the value and increase the entry count
        _values[entry.StartIndex + entry.Count] = value;
        entry.Count++;
      }
      else
      {
        ExpandValueListToAdd:

        // values list is full, must increase increase it's size
        var newCapacity = Math.Max(entry.Capacity * 2, DefaultValuesListSize);

        if (entry.StartIndex + entry.Capacity == _valuesFreeStartIndex
            && _valuesFreeStartIndex + newCapacity < _values.Length)
        {
          // value list is the last list before free space
          // and we can increase it's capacity w/o moving the data

          entry.Capacity = newCapacity;
          _valuesFreeStartIndex += newCapacity;
        }
        else if (_valuesFreeStartIndex + newCapacity < _values.Length)
        {
          // resized value list can be fitted in the free space
          Array.Copy(_values, entry.StartIndex, _values, _valuesFreeStartIndex, entry.Capacity);

          if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
          {
            Array.Clear(_values, entry.StartIndex, entry.Capacity); 
          }

          entry.StartIndex = _valuesFreeStartIndex;
          entry.Capacity = newCapacity;
          _valuesFreeStartIndex += newCapacity;
        }
        else
        {
          // not enough space to put the resized value list
          ExpandValuesListOrCompact();
          goto ExpandValueListToAdd;
        }

        Debug.Assert(entry.Count < entry.Capacity);

        _values[entry.StartIndex + entry.Count] = value;
        entry.Count++;
      }
    }

    _valuesStoredCount++;
    _version++;
  }
  
  private int Initialize(int capacity)
  {
    var size = HashHelpers.GetPrime(capacity);
    var buckets = new int[size];
    var entries = new Entry[size];

    // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
    _freeList = -1;
#if TARGET_64BIT
    _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)size);
#endif
    _buckets = buckets;
    _entries = entries;

    return size;
  }

  private ref Entry GetOrCreateEntry(TKey key)
  {
    if (key == null)
    {
      ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
    }

    if (_buckets == null)
    {
      Initialize(0);
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
            return ref entries[i];
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
            return ref entries[i];
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
          return ref entries[i];
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
    if (_freeCount > 0)
    {
      index = _freeList;
      Debug.Assert((StartOfFreeList - entries[_freeList].Next) >= -1, "shouldn't overflow because `next` cannot underflow");
      _freeList = StartOfFreeList - entries[_freeList].Next;
      _freeCount--;
    }
    else
    {
      var count = _count;
      if (count == entries.Length)
      {
        Resize();
        bucket = ref GetBucket(hashCode);
      }

      index = count;
      _count = count + 1;
      entries = _entries;
    }

    ref var entry = ref entries![index];
    entry.HashCode = hashCode;
    entry.Next = bucket - 1; // Value in _buckets is 1-based
    entry.Key = key;
    bucket = index + 1; // Value in _buckets is 1-based

    // users must do this
    // _version++;

    return ref entry;
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

  private void Resize() => Resize(HashHelpers.ExpandPrime(_count));

  private void Resize(int newSize)
  {
    Debug.Assert(_entries != null, "_entries should be non-null");
    Debug.Assert(newSize >= _entries.Length);

    var entries = new Entry[newSize];

    var count = _count;
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

  // todo: when copying and compacting we should skip the "current" entry and put it last, so we can extend it last
  private void ExpandValuesListOrCompact()
  {
    Debug.Assert(_values != null);

    var newCapacity = Math.Max(_values.Length * 2, DefaultValuesListSize);
    
    if (_valuesStoredCount > _values.Length / 2)
    {
      
      //var newArray = new TValue[newCapacity];

      // copy and compact

      Array.Resize(ref _values, _values!.Length * 2);
    }
    else
    {
      unsafe
      {
        var bitsPerNativeInt = sizeof(nint) * 8;
        var itemsCount = _values.Length / bitsPerNativeInt;
        if (itemsCount < 10)
        {
          // stackalloc
        }


        
        // we can compact the list in-place
      
        Array.Resize(ref _values, newCapacity);
      }
    }
  }

  private ref Entry FindEntry(TKey key)
  {
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
        return new ValuesList(this, entry.StartIndex, entry.Count);
      }
      
      return new ValuesList(this);
    }
  }

  public Enumerator GetEnumerator() => new Enumerator(this);

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
      while ((uint) _index < (uint) _dictionary._count)
      {
        ref var entry = ref _dictionary._entries![_index++];

        if (entry.Next >= -1)
        {
          _current = new KeyValuePair<TKey, ValuesList>(
            entry.Key, new ValuesList(_dictionary, entry.StartIndex, entry.Count));
          return true;
        }
      }
      
      _index = _dictionary._count + 1;
      _current = default;
      return false;
    }
    
    public KeyValuePair<TKey, ValuesList> Current => _current;
  }

  // todo: debug view
  public struct ValuesList
  {
    private readonly MultiValueDictionarySlim<TKey, TValue> _dictionary;
    private readonly int _version;
    private readonly int _startIndex;
    private readonly int _count;

    public ValuesList(MultiValueDictionarySlim<TKey, TValue> dictionary)
    {
      _dictionary = dictionary;
      _version = dictionary._version;
      _startIndex = 0;
      _count = 0;
    }

    internal ValuesList(MultiValueDictionarySlim<TKey, TValue> dictionary, int startIndex, int count)
    {
      _dictionary = dictionary;
      _version = dictionary._version;
      _startIndex = startIndex;
      _count = count;
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

        return _dictionary._values![_startIndex + index];
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
          _current = dictionary._values![_indexInList];
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
      Array.Copy(_dictionary._values!, _startIndex, array, destinationIndex: 0, _count);
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
        entries[index++] = new MultiValueDictionarySlim<TKey, TValue>.Pair(key, values.ToArray());

      return entries;
    }
  }
}
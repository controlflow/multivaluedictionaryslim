using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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

  // [ [1, 2, 3, _], [4], _, _, [5, 6], _, _]
  private TValue[] _values;
  private int _valuesFreeStartIndex;
  private int _valuesCapacityUsed;
  private int _valuesStoredCount;

  private List<(int, int)> gaps = new List<(int, int)>();

  private int _valuesGapStartOffset;
  private int _valuesGapCapacity;

  private const int DefaultValuesListSize = 4;

  private static readonly TValue[] s_emptyValues = new TValue[0];



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

    public int StartIndex; // can be 0
    public int Capacity; // only 0 for newly-created and not initialized entries
    public int Count; // only 0 for newly-created and not-initialized entries

    public override string ToString()
    {
      if (StartIndex == 0 && Capacity == 0)
        return "<empty>";

      return $"Entry [key={Key}, used={Count} of {Capacity}, range={StartIndex}-{StartIndex + Capacity}]";
    }
  }

  public MultiValueDictionarySlim()
    : this(keyCapacity: 0, 0, null) { }

  public MultiValueDictionarySlim(int keyCapacity, int valueCapacity)
    : this(keyCapacity: keyCapacity, valueCapacity, null) { }

  public MultiValueDictionarySlim(IEqualityComparer<TKey>? comparer)
    : this(keyCapacity: 0, 0, comparer) { }

  public MultiValueDictionarySlim(int keyCapacity, int valueCapacity, IEqualityComparer<TKey>? comparer)
  {
    if (keyCapacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(nameof(keyCapacity));
    if (valueCapacity < 0) ThrowHelper.ThrowArgumentOutOfRangeException(nameof(valueCapacity));

    if (keyCapacity > 0)
    {
      Initialize(keyCapacity);
    }

    _values = valueCapacity > 0 ? new TValue[valueCapacity] : s_emptyValues;

    // first check for null to avoid forcing default comparer instantiation unnecessarily
    if (comparer != null && comparer != EqualityComparer<TKey>.Default)
    {
      _comparer = comparer;
    }
  }

  // TODO: constructors, remove field initializers?

  public int Count => _count - _freeCount;
  public int ValuesCount => _valuesStoredCount;
  public int ValuesUsedCapacity => _valuesCapacityUsed;
  public bool ValuesListHasGaps => _valuesCapacityUsed != _valuesFreeStartIndex;

  public IEqualityComparer<TKey> Comparer => _comparer ?? EqualityComparer<TKey>.Default;

  public int KeysCapacity => _entries == null ? 0 : _entries.Length;
  public int ValuesCapacity => _values.Length;

  public void Add(TKey key, TValue value)
  {
    var entryIndex = GetOrCreateEntry(key);

    ref var entry = ref _entries![entryIndex];
    if (entry.Capacity == 0)
    {
      // entry was not allocated in the _values array
      // this is the most likely case - fresh key w/o any values added

      if (_valuesFreeStartIndex == _values.Length)
      {
        ExpandValuesListOrCompact(entryIndex);
        Debug.Assert(_valuesFreeStartIndex < _values.Length);
      }

      // allocate single item list first
      // we expect { 1 key - 1 value } to be the common scenario
      entry.StartIndex = _valuesFreeStartIndex;
      entry.Capacity = 1;
      _values[_valuesFreeStartIndex] = value;
      entry.Count = 1;
      _valuesFreeStartIndex++;
      _valuesCapacityUsed++;
    }
    else
    {
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
        var newCapacity = entry.Capacity * 2;
        Debug.Assert(newCapacity > 0);

        if (entry.StartIndex + entry.Capacity == _valuesFreeStartIndex
            && entry.StartIndex + newCapacity <= _values.Length)
        {
          // value list is the last list before free space
          // and we can increase it's capacity w/o moving the data

          _valuesCapacityUsed += newCapacity - entry.Capacity;
          _valuesFreeStartIndex += newCapacity - entry.Capacity;
          entry.Capacity = newCapacity;
        }
        else if (newCapacity <= _valuesGapCapacity)
        {
          // value list can be fitted in the gap of free space

          var gapStartOffset = _valuesGapStartOffset;
          Array.Copy(_values, entry.StartIndex, _values, gapStartOffset, entry.Count);

          if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
          {
            Array.Clear(_values, entry.StartIndex, entry.Count);
          }

          // update gap size
          _valuesGapStartOffset += newCapacity;
          _valuesGapCapacity -= newCapacity;

          // store gap location if freed space is bigger then left in the gap
          if (entry.Capacity > _valuesGapCapacity)
          {
            _valuesGapStartOffset = entry.StartIndex;
            _valuesGapCapacity = entry.Capacity;
          }

          entry.StartIndex = gapStartOffset;
          _valuesCapacityUsed += newCapacity - entry.Capacity;
          entry.Capacity = newCapacity;
        }
        else if (_valuesFreeStartIndex + newCapacity < _values.Length)
        {
          // value list can be relocated in the tail free space and expanded
          Array.Copy(_values, entry.StartIndex, _values, _valuesFreeStartIndex, entry.Count);

          if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
          {
            Array.Clear(_values, entry.StartIndex, entry.Count);
          }

          // store gap location
          if (entry.Capacity > _valuesGapCapacity)
          {
            _valuesGapCapacity = entry.Capacity;
            _valuesGapStartOffset = entry.StartIndex;
          }

          entry.StartIndex = _valuesFreeStartIndex;
          _valuesCapacityUsed += newCapacity - entry.Capacity;
          entry.Capacity = newCapacity;
          _valuesFreeStartIndex += newCapacity;
        }
        else
        {
          // not enough space to put the resized value list
          ExpandValuesListOrCompact(entryIndex, extraCapacity: 1);
          goto ExpandValueListToAdd;
        }

        Debug.Assert(entry.Count < entry.Capacity);

        _values[entry.StartIndex + entry.Count] = value;
        entry.Count++;
      }
    }

    _valuesStoredCount++;
    _version++;

    Debug.Assert(_valuesStoredCount <= _valuesCapacityUsed);
    Debug.Assert(_valuesCapacityUsed <= _values.Length);
  }

  public bool Remove(TKey key)
  {
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
            (StartOfFreeList - _freeList) < 0,
            "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
          entry.Next = StartOfFreeList - _freeList;

          if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
          {
            entry.Key = default!;
          }

          if (entry.Count > 0)
          {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            {
              Array.Clear(_values, entry.StartIndex, entry.Count);
            }

            _valuesStoredCount -= entry.Count;
          }

          _valuesCapacityUsed -= entry.Capacity;

          // important!
          entry.Count = 0;
          entry.Capacity = 0;
          entry.StartIndex = 0;

          _freeList = i;
          _freeCount++;
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
    var count = _count;
    if (count > 0)
    {
      Debug.Assert(_buckets != null, "_buckets should be non-null");
      Debug.Assert(_entries != null, "_entries should be non-null");

      Array.Clear(_buckets, 0, _buckets.Length);

      _count = 0;
      _freeList = -1;
      _freeCount = 0;
      Array.Clear(_entries, 0, count);

      if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
      {
        Array.Clear(_values, 0, _valuesFreeStartIndex);
      }

      _valuesStoredCount = 0;
      _valuesCapacityUsed = 0;
      _valuesFreeStartIndex = 0;
    }
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

  private int GetOrCreateEntry(TKey key)
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

    //entry.Capacity = ;

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

  // todo:
  // todo: when copying and compacting we should skip the "current" entry and put it last, so we can extend it last
  private void ExpandValuesListOrCompact(int entryIndex, int extraCapacity = 1)
  {
    Debug.Assert(_valuesStoredCount <= _valuesCapacityUsed);
    Debug.Assert(_valuesCapacityUsed <= _values.Length);

    if (_values.Length == 0)
    {
      _values = new TValue[Math.Max(extraCapacity, DefaultValuesListSize)];
      return;
    }

    if (_valuesCapacityUsed < _values.Length / 2)
    {
      // todo: this is not optimized, must be done in-place
      ResizeValuesAndCompactWhileCopying(entryIndex, _values.Length, extraCapacity);
    }
    else
    {
      var newCapacity = Math.Max(
        Math.Max(_valuesCapacityUsed + extraCapacity, _values.Length * 2),
        DefaultValuesListSize);

      ResizeValuesAndCompactWhileCopying(entryIndex, newCapacity, extraCapacity);
    }

    // 1, 2, 4, 8, 16

    return;

    var map = this.ValueListMapView;

    if (_valuesCapacityUsed > _values.Length / 2)
    {
      // if fill ratio is high, copy to new array and compactify


    }
    else
    {


      // capacity is low, but we need more space then available



      var index = 0;
      var count = (uint) _count;
      var entries = _entries!;

      while ((uint)index < count)
      {
        ref var entry = ref entries[index++];

        if (entry.Next >= -1)
        {
          //var maskIndex = entry.StartIndex / 64;
          //var maskShift = entry.StartIndex & 63;

        }
      }

      /*
      ranges.Sort(); // todo: special comparer
      */

      // holes


      // fill ratio is low, can we compact in-place?

      //var newCapacity = Math.Max(_values.Length * 2, DefaultValuesListSize);

      //Array.Resize(ref _values, newCapacity);
    }
  }

  private void ResizeValuesAndCompactWhileCopying(int entryIndex, int newCapacity, int extraCapacity)
  {
    var newArray = new TValue[newCapacity];
    var newArrayIndex = 0;

    var index = 0;
    var count = (uint) _count;
    var entries = _entries!;

    while ((uint) index < count)
    {
      // skip currently modified list
      /*
      if (index == entryIndex)
      {
        index++;
        continue;
      }*/

      ref var entry = ref entries[index++];

      if (entry.Next >= -1)
      {
        Array.Copy(_values, entry.StartIndex, newArray, newArrayIndex, entry.Count);

        // if less than half of value list capacity used - reduce the capacity to twice the used count
        entry.Capacity = (entry.Count < entry.Capacity / 2) ? Math.Max(entry.Count * 2, 1) : entry.Capacity;
        entry.StartIndex = newArrayIndex;

        newArrayIndex += entry.Capacity;
      }
    }

    // copy last
    /*
    {
      ref var lastEntry = ref entries[entryIndex];

      if (lastEntry.Capacity == 0)
      {
        lastEntry.StartIndex = newArrayIndex;
      }
      else
      {
        Array.Copy(_values, lastEntry.StartIndex, newArray, newArrayIndex, lastEntry.Count);

        lastEntry.StartIndex = newArrayIndex;
        newArrayIndex += lastEntry.Capacity;
      }
    }
    */

    _values = newArray;
    _valuesFreeStartIndex = newArrayIndex;
    _valuesCapacityUsed = newArrayIndex;
    _valuesGapCapacity = 0;
    _valuesGapStartOffset = 0;

    Debug.Assert(_valuesStoredCount <= _valuesCapacityUsed);
    Debug.Assert(_valuesCapacityUsed <= _values.Length);
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
  [DebuggerTypeProxy(typeof(MultiValueDictionarySlimValueListDebugView<,>))]
  [DebuggerDisplay("Count = {Count}")]
  public readonly struct ValuesList
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

  public void TrimExcess()
  {
    var newKeyCapacity = HashHelpers.GetPrime(_count);
    var oldEntries = _entries;
    var currentCapacity = oldEntries == null ? 0 : oldEntries.Length;
    if (newKeyCapacity < currentCapacity)
    {
      var oldCount = _count;
      _version++;
      Initialize(newKeyCapacity);

      Debug.Assert(oldEntries != null);

      CopyEntries(oldEntries, oldCount);
    }

    if (_valuesStoredCount < _values.Length)
    {
      CompactValues();
    }

    void CopyEntries(Entry[] entries, int count)
    {
      Debug.Assert(_entries != null);

      var newEntries = _entries;
      var newCount = 0;
      for (var i = 0; i < count; i++)
      {
        var hashCode = entries[i].HashCode;
        if (entries[i].Next >= -1)
        {
          ref var entry = ref newEntries[newCount];
          entry = entries[i];
          ref var bucket = ref GetBucket(hashCode);
          entry.Next = bucket - 1; // Value in _buckets is 1-based
          bucket = newCount + 1;
          newCount++;
        }
      }

      _count = newCount;
      _freeCount = 0;
    }

    void CompactValues()
    {
      var keyCount = (uint) _count;
      if (keyCount == 0)
      {
        _values = s_emptyValues;
        _valuesFreeStartIndex = 0;
        return;
      }

      var newArray = new TValue[_valuesStoredCount];
      var newArrayIndex = 0;
      var keyIndex = 0;

      Debug.Assert(_entries != null);
      var entries = _entries;

      while ((uint) keyIndex < keyCount)
      {
        ref var entry = ref entries[keyIndex++];

        if (entry.Next >= -1)
        {
          Array.Copy(_values, entry.StartIndex, newArray, newArrayIndex, entry.Count);

          entry.StartIndex = newArrayIndex;
          entry.Capacity = entry.Count;

          newArrayIndex += entry.Count;
        }
      }

      _values = newArray;
      _valuesFreeStartIndex = newArrayIndex;
      _valuesCapacityUsed = newArrayIndex;
    }
  }

  public string ValueListMapView
  {
    get
    {
      var sb = new StringBuilder(_values.Length);
      sb.Append('_', _values.Length);

      var index = 0;
      var count = (uint)_count;
      var entries = _entries!;
      var list = new List<(int start, int end)>();

      while ((uint)index < count)
      {
        ref var entry = ref entries[index++];

        if (entry.Next >= -1 && entry.Capacity > 0)
        {
          for (var j = 0; j < entry.Capacity; j++)
          {
            sb[entry.StartIndex + j] = j < entry.Count ? '#' : '+';
          }

          list.Add((entry.StartIndex, entry.StartIndex + entry.Capacity));
        }
      }

      list.Sort();

      for (var i = list.Count - 1; i >= 0; i--)
      {
        var (start, end) = list[i];
        sb.Insert(end, ']');
        sb.Insert(start, '[');
      }

      return sb.ToString();
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
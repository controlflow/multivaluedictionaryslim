using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
// ReSharper disable IntroduceOptionalParameters.Global
// ReSharper disable MergeConditionalExpression
// ReSharper disable ConditionIsAlwaysTrueOrFalse
// ReSharper disable MemberHidesStaticFromOuterClass

namespace System.Collections.Generic
{
  [DebuggerDisplay("Count = {Count}")]
  [Serializable]
  public class StdDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
  {
    private int[]? _buckets;
    private Entry[]? _entries;
#if TARGET_64BIT
    private ulong _fastModMultiplier;
#endif
    private int _count;
    private int _freeList;
    private int _freeCount;
    private int _version;
    private IEqualityComparer<TKey>? _comparer;
    private KeyCollection? _keys;
    private ValueCollection? _values;

    private const int StartOfFreeList = -3;

    public StdDictionary()
      : this(0, null) { }

    public StdDictionary(int capacity)
      : this(capacity, null) { }

    public StdDictionary(IEqualityComparer<TKey>? comparer)
      : this(0, comparer) { }

    public StdDictionary(int capacity, IEqualityComparer<TKey>? comparer)
    {
      if (capacity < 0)
      {
        ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
      }

      if (capacity > 0)
      {
        Initialize(capacity);
      }

      // first check for null to avoid forcing default comparer instantiation unnecessarily
      if (comparer != null && comparer != EqualityComparer<TKey>.Default)
      {
        _comparer = comparer;
      }
    }

    public StdDictionary(IDictionary<TKey, TValue> dictionary)
      : this(dictionary, comparer: null) { }

    public StdDictionary(IDictionary<TKey, TValue>? dictionary, IEqualityComparer<TKey>? comparer)
      : this(dictionary != null ? dictionary.Count : 0, comparer)
    {
      if (dictionary == null)
      {
        ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
      }

      AddRange(dictionary);
    }

    public StdDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
      : this(collection, null) { }

    public StdDictionary(IEnumerable<KeyValuePair<TKey, TValue>>? collection, IEqualityComparer<TKey>? comparer)
      : this((collection as ICollection<KeyValuePair<TKey, TValue>>)?.Count ?? 0, comparer)
    {
      if (collection == null)
      {
        ThrowHelper.ThrowArgumentNullException(ExceptionArgument.collection);
      }

      AddRange(collection);
    }

    private void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> collection)
    {
      // It is likely that the passed-in dictionary is Dictionary<TKey,TValue>. When this is the case,
      // avoid the enumerator allocation and overhead by looping through the entries array directly.
      // We only do this when dictionary is Dictionary<TKey,TValue> and not a subclass, to maintain
      // back-compat with subclasses that may have overridden the enumerator behavior.
      if (collection.GetType() == typeof(StdDictionary<TKey, TValue>))
      {
        var source = (StdDictionary<TKey, TValue>) collection;

        if (source.Count == 0)
        {
          // Nothing to copy, all done
          return;
        }

        // This is not currently a true .AddRange as it needs to be an initalized dictionary
        // of the correct size, and also an empty dictionary with no current entities (and no argument checks).
        Debug.Assert(source._entries is not null);
        Debug.Assert(_entries is not null);
        Debug.Assert(_entries.Length >= source.Count);
        Debug.Assert(_count == 0);

        var oldEntries = source._entries;
        if (source._comparer == _comparer)
        {
          // If comparers are the same, we can copy _entries without rehashing.
          CopyEntries(oldEntries, source._count);
          return;
        }

        // Comparers differ need to rehash all the entires via Add
        var count = source._count;
        for (var i = 0; i < count; i++)
        {
          // Only copy if an entry
          if (oldEntries[i].Next >= -1)
          {
            Add(oldEntries[i].Key, oldEntries[i].Value);
          }
        }

        return;
      }

      // Fallback path for IEnumerable that isn't a non-subclassed Dictionary<TKey,TValue>.
      foreach (var pair in collection)
      {
        Add(pair.Key, pair.Value);
      }
    }

    public IEqualityComparer<TKey> Comparer => _comparer ?? EqualityComparer<TKey>.Default;

    public int Count => _count - _freeCount;

    public KeyCollection Keys => _keys ??= new KeyCollection(this);

    ICollection<TKey> IDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    public ValueCollection Values => _values ??= new ValueCollection(this);

    ICollection<TValue> IDictionary<TKey, TValue>.Values => Values;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    public TValue this[TKey key]
    {
      get
      {
        ref var value = ref FindValue(key);
        if (!Unsafe.IsNullRef(ref value))
        {
          return value;
        }

        ThrowHelper.ThrowKeyNotFoundException(key);
        return default;
      }
      set
      {
        var modified = TryInsert(key, value, InsertionBehavior.OverwriteExisting);
        Debug.Assert(modified);
      }
    }

    public void Add(TKey key, TValue value)
    {
      var modified = TryInsert(key, value, InsertionBehavior.ThrowOnExisting);
      Debug.Assert(modified); // If there was an existing key and the Add failed, an exception will already have been thrown.
    }

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> keyValuePair)
    {
      Add(keyValuePair.Key, keyValuePair.Value);
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> keyValuePair)
    {
      ref var value = ref FindValue(keyValuePair.Key);
      if (!Unsafe.IsNullRef(ref value)
          && EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value))
      {
        return true;
      }

      return false;
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> keyValuePair)
    {
      ref var value = ref FindValue(keyValuePair.Key);
      if (!Unsafe.IsNullRef(ref value)
          && EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value))
      {
        Remove(keyValuePair.Key);
        return true;
      }

      return false;
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
      }
    }

    public bool ContainsKey(TKey key)
    {
      return !Unsafe.IsNullRef(ref FindValue(key));
    }

    public bool ContainsValue(TValue value)
    {
      var entries = _entries;
      if (value == null)
      {
        for (var i = 0; i < _count; i++)
        {
          if (entries![i].Next >= -1 && entries[i].Value == null)
          {
            return true;
          }
        }
      }
      else if (typeof(TValue).IsValueType)
      {
        // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
        for (var i = 0; i < _count; i++)
        {
          if (entries![i].Next >= -1
              && EqualityComparer<TValue>.Default.Equals(entries[i].Value, value))
          {
            return true;
          }
        }
      }
      else
      {
        // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
        // https://github.com/dotnet/runtime/issues/10050
        // So cache in a local rather than get EqualityComparer per loop iteration
        var defaultComparer = EqualityComparer<TValue>.Default;
        for (var i = 0; i < _count; i++)
        {
          if (entries![i].Next >= -1 && defaultComparer.Equals(entries[i].Value, value))
          {
            return true;
          }
        }
      }

      return false;
    }

    private void CopyTo(KeyValuePair<TKey, TValue>[]? array, int index)
    {
      if (array == null)
      {
        ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
      }

      if ((uint) index > (uint) array.Length)
      {
        ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
      }

      if (array.Length - index < Count)
      {
        ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
      }

      var count = _count;
      var entries = _entries;
      for (var i = 0; i < count; i++)
      {
        if (entries![i].Next >= -1)
        {
          array[index++] = new KeyValuePair<TKey, TValue>(entries[i].Key, entries[i].Value);
        }
      }
    }

    public Enumerator GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
    {
      return new Enumerator(this, Enumerator.KeyValuePair);
    }

    private ref TValue FindValue(TKey key)
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
          var hashCode = (uint) key.GetHashCode();
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
              if ((uint) i >= (uint) entries.Length)
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
            } while (collisionCount <= (uint) entries.Length);

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
              if ((uint) i >= (uint) entries.Length)
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
            while (collisionCount <= (uint) entries.Length);

            // The chain of entries forms a loop; which means a concurrent update has happened.
            // Break out of the loop and throw, rather than looping forever.
            goto ConcurrentOperation;
          }
        }
        else
        {
          var hashCode = (uint) comparer.GetHashCode(key);
          var i = GetBucket(hashCode);
          var entries = _entries;
          uint collisionCount = 0;
          i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
          do
          {
            // Should be a while loop https://github.com/dotnet/runtime/issues/9422
            // Test in if to drop range check for following array access
            if ((uint) i >= (uint) entries.Length)
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
          }
          while (collisionCount <= (uint) entries.Length);

          // The chain of entries forms a loop; which means a concurrent update has happened.
          // Break out of the loop and throw, rather than looping forever.
          goto ConcurrentOperation;
        }
      }

      goto ReturnNotFound;

ConcurrentOperation:
      ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();

ReturnFound:
      ref var value = ref entry.Value;

Return:
      return ref value;

ReturnNotFound:
      value = ref Unsafe.NullRef<TValue>();
      goto Return;
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

    private bool TryInsert(TKey key, TValue value, InsertionBehavior behavior)
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
      var hashCode = (uint) ((comparer == null) ? key.GetHashCode() : comparer.GetHashCode(key));

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
            if ((uint) i >= (uint) entries.Length)
            {
              break;
            }

            if (entries[i].HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].Key, key))
            {
              if (behavior == InsertionBehavior.OverwriteExisting)
              {
                entries[i].Value = value;
                return true;
              }

              if (behavior == InsertionBehavior.ThrowOnExisting)
              {
                ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
              }

              return false;
            }

            i = entries[i].Next;

            collisionCount++;
            if (collisionCount > (uint) entries.Length)
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
            if ((uint) i >= (uint) entries.Length)
            {
              break;
            }

            if (entries[i].HashCode == hashCode && defaultComparer.Equals(entries[i].Key, key))
            {
              if (behavior == InsertionBehavior.OverwriteExisting)
              {
                entries[i].Value = value;
                return true;
              }

              if (behavior == InsertionBehavior.ThrowOnExisting)
              {
                ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
              }

              return false;
            }

            i = entries[i].Next;

            collisionCount++;
            if (collisionCount > (uint) entries.Length)
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
          if ((uint) i >= (uint) entries.Length)
          {
            break;
          }

          if (entries[i].HashCode == hashCode && comparer.Equals(entries[i].Key, key))
          {
            if (behavior == InsertionBehavior.OverwriteExisting)
            {
              entries[i].Value = value;
              return true;
            }

            if (behavior == InsertionBehavior.ThrowOnExisting)
            {
              ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
            }

            return false;
          }

          i = entries[i].Next;

          collisionCount++;
          if (collisionCount > (uint) entries.Length)
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
      entry.Value = value;
      bucket = index + 1; // Value in _buckets is 1-based
      _version++;

      return true;
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

    public bool Remove(TKey key)
    {
      // The overload Remove(TKey key, out TValue value) is a copy of this method with one additional
      // statement to copy the value for entry being removed into the output parameter.
      // Code has been intentionally duplicated for performance reasons.

      if (key == null)
      {
        ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
      }

      if (_buckets != null)
      {
        Debug.Assert(_entries != null, "entries should be non-null");
        uint collisionCount = 0;
        var hashCode = (uint) (_comparer?.GetHashCode(key) ?? key.GetHashCode());
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

            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            {
              entry.Value = default!;
            }

            _freeList = i;
            _freeCount++;
            return true;
          }

          last = i;
          i = entry.Next;

          collisionCount++;
          if (collisionCount > (uint) entries.Length)
          {
            // The chain of entries forms a loop; which means a concurrent update has happened.
            // Break out of the loop and throw, rather than looping forever.
            ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
          }
        }
      }

      return false;
    }

    public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
      // This overload is a copy of the overload Remove(TKey key) with one additional
      // statement to copy the value for entry being removed into the output parameter.
      // Code has been intentionally duplicated for performance reasons.

      if (key == null)
      {
        ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
      }

      if (_buckets != null)
      {
        Debug.Assert(_entries != null, "entries should be non-null");
        uint collisionCount = 0;
        var hashCode = (uint) (_comparer?.GetHashCode(key) ?? key.GetHashCode());
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

            value = entry.Value;

            Debug.Assert((StartOfFreeList - _freeList) < 0,
              "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
            entry.Next = StartOfFreeList - _freeList;

            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
            {
              entry.Key = default!;
            }

            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
            {
              entry.Value = default!;
            }

            _freeList = i;
            _freeCount++;
            return true;
          }

          last = i;
          i = entry.Next;

          collisionCount++;
          if (collisionCount > (uint) entries.Length)
          {
            // The chain of entries forms a loop; which means a concurrent update has happened.
            // Break out of the loop and throw, rather than looping forever.
            ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
          }
        }
      }

      value = default;
      return false;
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
      ref var valRef = ref FindValue(key);
      if (!Unsafe.IsNullRef(ref valRef))
      {
        value = valRef;
        return true;
      }

      value = default;
      return false;
    }

    public bool TryAdd(TKey key, TValue value)
    {
      return TryInsert(key, value, InsertionBehavior.None);
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
    {
      CopyTo(array, index);
    }



    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);

    /// <summary>
    /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
    /// </summary>
    public int EnsureCapacity(int capacity)
    {
      if (capacity < 0)
      {
        ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
      }

      var currentCapacity = _entries == null ? 0 : _entries.Length;
      if (currentCapacity >= capacity)
      {
        return currentCapacity;
      }

      _version++;

      if (_buckets == null)
      {
        return Initialize(capacity);
      }

      var newSize = HashHelpers.GetPrime(capacity);
      Resize(newSize);
      return newSize;
    }

    /// <summary>
    /// Sets the capacity of this dictionary to what it would be if it had been originally initialized with all its entries
    /// </summary>
    /// <remarks>
    /// This method can be used to minimize the memory overhead
    /// once it is known that no new elements will be added.
    ///
    /// To allocate minimum size storage array, execute the following statements:
    ///
    /// dictionary.Clear();
    /// dictionary.TrimExcess();
    /// </remarks>
    public void TrimExcess() => TrimExcess(Count);

    /// <summary>
    /// Sets the capacity of this dictionary to hold up 'capacity' entries without any further expansion of its backing storage
    /// </summary>
    /// <remarks>
    /// This method can be used to minimize the memory overhead
    /// once it is known that no new elements will be added.
    /// </remarks>
    public void TrimExcess(int capacity)
    {
      if (capacity < Count)
      {
        ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
      }

      var newSize = HashHelpers.GetPrime(capacity);
      var oldEntries = _entries;
      var currentCapacity = oldEntries == null ? 0 : oldEntries.Length;
      if (newSize >= currentCapacity)
      {
        return;
      }

      var oldCount = _count;
      _version++;
      Initialize(newSize);

      Debug.Assert(oldEntries is not null);

      CopyEntries(oldEntries, oldCount);
    }

    private void CopyEntries(Entry[] entries, int count)
    {
      Debug.Assert(_entries is not null);

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

    private static bool IsCompatibleKey(object? key)
    {
      if (key == null)
      {
        ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
      }

      return key is TKey;
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

    private struct Entry
    {
      public uint HashCode;

      /// <summary>
      /// 0-based index of next entry in chain: -1 means end of chain
      /// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
      /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
      /// </summary>
      public int Next;

      public TKey Key; // Key of entry
      public TValue Value; // Value of entry
    }

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
    {
      private readonly StdDictionary<TKey, TValue> _dictionary;
      private readonly int _version;
      private int _index;
      private KeyValuePair<TKey, TValue> _current;
      private readonly int _getEnumeratorRetType; // What should Enumerator.Current return?

      internal const int DictEntry = 1;
      internal const int KeyValuePair = 2;

      internal Enumerator(StdDictionary<TKey, TValue> dictionary, int getEnumeratorRetType)
      {
        _dictionary = dictionary;
        _version = dictionary._version;
        _index = 0;
        _getEnumeratorRetType = getEnumeratorRetType;
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
            _current = new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
            return true;
          }
        }

        _index = _dictionary._count + 1;
        _current = default;
        return false;
      }

      public KeyValuePair<TKey, TValue> Current => _current;

      public void Dispose()
      {
      }

      object IEnumerator.Current
      {
        get
        {
          if (_index == 0 || (_index == _dictionary._count + 1))
          {
            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
          }

          if (_getEnumeratorRetType == DictEntry)
          {
            return new DictionaryEntry(_current.Key, _current.Value);
          }

          return new KeyValuePair<TKey, TValue>(_current.Key, _current.Value);
        }
      }

      void IEnumerator.Reset()
      {
        if (_version != _dictionary._version)
        {
          ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
        }

        _index = 0;
        _current = default;
      }
    }

    [DebuggerDisplay("Count = {" + nameof(Count) + "}")]
    public sealed class KeyCollection : ICollection<TKey>, IReadOnlyCollection<TKey>
    {
      private readonly StdDictionary<TKey, TValue> _dictionary;

      public KeyCollection(StdDictionary<TKey, TValue>? dictionary)
      {
        if (dictionary == null)
        {
          ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
        }

        _dictionary = dictionary;
      }

      public Enumerator GetEnumerator() => new Enumerator(_dictionary);

      public void CopyTo(TKey[] array, int index)
      {
        if (array == null)
        {
          ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
        }

        if (index < 0 || index > array.Length)
        {
          ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
        }

        if (array.Length - index < _dictionary.Count)
        {
          ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
        }

        var count = _dictionary._count;
        var entries = _dictionary._entries;
        for (var i = 0; i < count; i++)
        {
          if (entries![i].Next >= -1) array[index++] = entries[i].Key;
        }
      }

      public int Count => _dictionary.Count;

      bool ICollection<TKey>.IsReadOnly => true;

      void ICollection<TKey>.Add(TKey item)
      {
        ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);
      }

      void ICollection<TKey>.Clear()
      {
        ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);
      }

      bool ICollection<TKey>.Contains(TKey item)
      {
        return _dictionary.ContainsKey(item);
      }

      bool ICollection<TKey>.Remove(TKey item)
      {
        ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);
        return false;
      }

      IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => new Enumerator(_dictionary);

      IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_dictionary);

      public struct Enumerator : IEnumerator<TKey>
      {
        private readonly StdDictionary<TKey, TValue> _dictionary;
        private int _index;
        private readonly int _version;
        private TKey? _currentKey;

        internal Enumerator(StdDictionary<TKey, TValue> dictionary)
        {
          _dictionary = dictionary;
          _version = dictionary._version;
          _index = 0;
          _currentKey = default;
        }

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
          if (_version != _dictionary._version)
          {
            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
          }

          while ((uint) _index < (uint) _dictionary._count)
          {
            ref var entry = ref _dictionary._entries![_index++];

            if (entry.Next >= -1)
            {
              _currentKey = entry.Key;
              return true;
            }
          }

          _index = _dictionary._count + 1;
          _currentKey = default;
          return false;
        }

        public TKey Current => _currentKey!;

        object? IEnumerator.Current
        {
          get
          {
            if (_index == 0 || (_index == _dictionary._count + 1))
            {
              ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
            }

            return _currentKey;
          }
        }

        void IEnumerator.Reset()
        {
          if (_version != _dictionary._version)
          {
            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
          }

          _index = 0;
          _currentKey = default;
        }
      }
    }

    [DebuggerDisplay("Count = {" + nameof(Count) + "}")]
    public sealed class ValueCollection : ICollection<TValue>, IReadOnlyCollection<TValue>
    {
      private readonly StdDictionary<TKey, TValue> _dictionary;

      public ValueCollection(StdDictionary<TKey, TValue>? dictionary)
      {
        if (dictionary == null)
        {
          ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
        }

        _dictionary = dictionary;
      }

      public Enumerator GetEnumerator() => new Enumerator(_dictionary);

      public void CopyTo(TValue[]? array, int index)
      {
        if (array == null)
        {
          ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
        }

        if ((uint) index > array.Length)
        {
          ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
        }

        if (array.Length - index < _dictionary.Count)
        {
          ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
        }

        var count = _dictionary._count;
        var entries = _dictionary._entries;
        for (var i = 0; i < count; i++)
        {
          if (entries![i].Next >= -1) array[index++] = entries[i].Value;
        }
      }

      public int Count => _dictionary.Count;

      bool ICollection<TValue>.IsReadOnly => true;

      void ICollection<TValue>.Add(TValue item)
      {
        ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);
      }

      bool ICollection<TValue>.Remove(TValue item)
      {
        ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);
        return false;
      }

      void ICollection<TValue>.Clear()
      {
        ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);
      }

      bool ICollection<TValue>.Contains(TValue item) => _dictionary.ContainsValue(item);

      IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => new Enumerator(_dictionary);

      IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_dictionary);

      public struct Enumerator : IEnumerator<TValue>
      {
        private readonly StdDictionary<TKey, TValue> _dictionary;
        private int _index;
        private readonly int _version;
        private TValue? _currentValue;

        internal Enumerator(StdDictionary<TKey, TValue> dictionary)
        {
          _dictionary = dictionary;
          _version = dictionary._version;
          _index = 0;
          _currentValue = default;
        }

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
          if (_version != _dictionary._version)
          {
            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
          }

          while ((uint) _index < (uint) _dictionary._count)
          {
            ref var entry = ref _dictionary._entries![_index++];

            if (entry.Next >= -1)
            {
              _currentValue = entry.Value;
              return true;
            }
          }

          _index = _dictionary._count + 1;
          _currentValue = default;
          return false;
        }

        public TValue Current => _currentValue!;

        object? IEnumerator.Current
        {
          get
          {
            if (_index == 0 || (_index == _dictionary._count + 1))
            {
              ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
            }

            return _currentValue;
          }
        }

        void IEnumerator.Reset()
        {
          if (_version != _dictionary._version)
          {
            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
          }

          _index = 0;
          _currentValue = default;
        }
      }
    }
  }
}
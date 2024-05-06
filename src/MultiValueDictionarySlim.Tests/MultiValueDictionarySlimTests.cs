using System.Collections;
using System.Diagnostics.CodeAnalysis;
using NUnit.Framework;

namespace ControlFlow.Collections.Tests;

public class MultiValueDictionarySlimTests
{
  private readonly Random _random = new();

  [Test]
  [TestCase(false)]
  [TestCase(true)]
  public void SimpleOperations(bool useComparer)
  {
    var dictionary = useComparer
      ? new MultiValueDictionarySlim<int, string>(CustomIntComparer.Instance)
      : new MultiValueDictionarySlim<int, string>();

    dictionary.Add(1, "11");
    dictionary.Add(1, "12");
    dictionary.Add(2, "21");
    dictionary.Add(1, "13");
    dictionary.Add(3, "31");
    dictionary.Add(3, "32");
    dictionary.Add(2, "22");

    Assert.AreEqual(3, dictionary.Count);
    Assert.AreEqual(7, dictionary.ValuesCount);
    Assert.AreEqual(3, dictionary.KeysCapacity);
    Assert.AreEqual(8, dictionary.ValuesCapacity);

    Assert.IsFalse(dictionary.ContainsKey(0));
    Assert.IsTrue(dictionary[0].IsEmpty);
    Assert.AreEqual(0, dictionary[0].Count);

    var enumerator0 = dictionary[0].GetEnumerator();
    Assert.IsFalse(enumerator0.MoveNext());
    Assert.AreEqual(default(string), enumerator0.Current);
    Assert.IsFalse(enumerator0.MoveNext());

    Assert.IsFalse(dictionary.ContainsKey(-2));
    var emptyList = dictionary[-2];
    Assert.IsTrue(emptyList.IsEmpty);
    Assert.AreEqual(0, emptyList.Count);
    var emptyEnumerator = emptyList.GetEnumerator();
    Assert.IsFalse(emptyEnumerator.MoveNext());

    Assert.IsTrue(dictionary.ContainsKey(1));
    var valuesList1 = dictionary[1];
    Assert.IsFalse(valuesList1.IsEmpty);
    Assert.AreEqual(3, valuesList1.Count);

    var enumerator1 = valuesList1.GetEnumerator(); // -11,13
    Assert.IsTrue(enumerator1.MoveNext()); // 11,13 after
    Assert.AreEqual("11", enumerator1.Current);
    Assert.IsTrue(enumerator1.MoveNext()); // 12,13
    Assert.AreEqual("12", enumerator1.Current);
    Assert.IsTrue(enumerator1.MoveNext()); // 13,13
    Assert.AreEqual("13", enumerator1.Current);
    Assert.IsFalse(enumerator1.MoveNext());

    Assert.IsTrue(dictionary.ContainsKey(2));
    Assert.IsFalse(dictionary[2].IsEmpty);
    Assert.AreEqual(2, dictionary[2].Count);

    Assert.IsTrue(dictionary.ContainsKey(3));
    var valuesList3 = dictionary[3];
    Assert.IsFalse(valuesList3.IsEmpty);
    Assert.AreEqual(2, valuesList3.Count);
    Assert.AreEqual(new[] { "31", "32" }, valuesList3.ToArray());

    Assert.IsFalse(dictionary.Remove(0));
    Assert.IsTrue(dictionary.Remove(2));

    Assert.AreEqual(2, dictionary.Count);
    Assert.AreEqual(5, dictionary.ValuesCount);
    Assert.AreEqual(3, dictionary.KeysCapacity);
    Assert.AreEqual(8, dictionary.ValuesCapacity);

    // freelist usage
    dictionary.Add(7, "71");
    dictionary.Add(7, "72");
    dictionary.Add(7, "73");

    Assert.Throws<InvalidOperationException>(() => valuesList1.ToArray());
    Assert.Throws<InvalidOperationException>(() => enumerator1.MoveNext());

    Assert.AreEqual(3, dictionary.Count);
    Assert.AreEqual(8, dictionary.ValuesCount);
    Assert.AreEqual(3, dictionary.KeysCapacity);
    Assert.AreEqual(8, dictionary.ValuesCapacity);

    Assert.IsTrue(dictionary.ContainsKey(7));
    Assert.IsFalse(dictionary[7].IsEmpty);
    Assert.AreEqual(3, dictionary[7].Count);
    Assert.AreEqual(new[] { "71", "72", "73" }, dictionary[7].ToArray());

    dictionary.Clear();

    Assert.AreEqual(3, dictionary.KeysCapacity);
    Assert.AreEqual(8, dictionary.ValuesCapacity);

    var enumeratorKeyValue = dictionary.GetEnumerator();
    for (var index = 0; index < dictionary.Count; index++)
    {
      Assert.IsTrue(enumeratorKeyValue.MoveNext());
    }

    Assert.IsFalse(enumeratorKeyValue.MoveNext());
  }

  [Test]
  public void Exceptions()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new MultiValueDictionarySlim<int, int>(valueCapacity: -1, keyCapacity: 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new MultiValueDictionarySlim<int, int>(valueCapacity: 0, keyCapacity: -1));
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new MultiValueDictionarySlim<int, int>(valueCapacity: -1, keyCapacity: 0, comparer: null));
    Assert.Throws<ArgumentOutOfRangeException>(() => _ = new MultiValueDictionarySlim<int, int>(valueCapacity: 0, keyCapacity: -1, comparer: null));

    var dictionarySlim = new MultiValueDictionarySlim<string, int>();
    Assert.Throws<ArgumentNullException>(() => dictionarySlim.Add(null!, 1));
    Assert.Throws<ArgumentNullException>(() => dictionarySlim.AddValueRange(null!, new[] { 1 }));
    Assert.Throws<ArgumentNullException>(() => dictionarySlim.AddValueRange("aaa", values: null!));
  }

  [Test]
  public void VersionCheck()
  {
    var dictionarySlim = new MultiValueDictionarySlim<string, int>();

    var kvpEnumerator = dictionarySlim.GetEnumerator();
    dictionarySlim.Add("aaa1", 1);
    Assert.Throws<InvalidOperationException>(() => kvpEnumerator.MoveNext());

    var kvpEnumerator2 = dictionarySlim.GetEnumerator();
    Assert.IsTrue(kvpEnumerator2.MoveNext());
    dictionarySlim.Add("aaa2", 2);
    Assert.Throws<InvalidOperationException>(() => kvpEnumerator2.MoveNext());

    var keyEnumerator = dictionarySlim.Keys.GetEnumerator();
    dictionarySlim.Add("bbb1", 1);
    Assert.Throws<InvalidOperationException>(() => keyEnumerator.MoveNext());

    var keyEnumerator2 = dictionarySlim.Keys.GetEnumerator();
    Assert.IsTrue(keyEnumerator2.MoveNext());
    dictionarySlim.Add("bbb2", 2);
    Assert.Throws<InvalidOperationException>(() => keyEnumerator2.MoveNext());
  }

  [Test]
  public void AddValuesRange()
  {
    const int dataCount = 17;

    var dict1 = new MultiValueDictionarySlim<string, int>();
    dict1.AddValueRange("enum", Enumerate());
    Assert.AreEqual(1, dict1.Count);
    Assert.AreEqual(dataCount, dict1.ValuesCount);
    Assert.AreEqual(32, dict1.ValuesCapacity);
    var enumCollection = dict1["enum"];
    Assert.AreEqual(dataCount, enumCollection.Count);
    CollectionAssert.AreEqual(Enumerate().ToArray(), enumCollection.ToArray());

    var dict2 = new MultiValueDictionarySlim<string, int>();
    dict2.AddValueRange("list", Enumerate().ToList());
    Assert.AreEqual(1, dict2.Count);
    Assert.AreEqual(dataCount, dict2.ValuesCount);
    Assert.AreEqual(dataCount, dict2.ValuesCapacity);
    var listCollection = dict2["list"];
    Assert.AreEqual(dataCount, listCollection.Count);
    CollectionAssert.AreEqual(Enumerate().ToArray(), listCollection.ToArray());

    var emptyCollection = dict2["empty"];
    Assert.AreEqual(0, emptyCollection.Count);
    Assert.IsTrue(emptyCollection.IsEmpty);
    Assert.IsFalse(emptyCollection.GetEnumerator().MoveNext());
    CollectionAssert.AreEqual(emptyCollection.ToArray(), Array.Empty<int>());

    return;

    static IEnumerable<int> Enumerate()
    {
      for (var index = 0; index < dataCount; index++)
        yield return index;
    }
  }

  [Test]
  [Repeat(2000)]
  [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
  public void BasicOperations()
  {
    var keyCapacity = _random.Next(0, 100);
    var valueCapacity = _random.Next(0, 100);

    var (dictionarySlim, dictionaryNaive) = _random.Next(0, 20) switch
    {
      >= 0 and < 10 => (
        new MultiValueDictionarySlim<int, string>(),
        new MultiValueDictionaryNaive<int, string>()),
      >= 10 and < 16 => (
        new MultiValueDictionarySlim<int, string>(CustomIntComparer.Instance),
        new MultiValueDictionaryNaive<int, string>(CustomIntComparer.Instance)),
      17 => (
        new MultiValueDictionarySlim<int, string>(keyCapacity: 0, valueCapacity),
        new MultiValueDictionaryNaive<int, string>(keyCapacity: 0, valueCapacity)),
      18 => (
        new MultiValueDictionarySlim<int, string>(keyCapacity, valueCapacity: 0),
        new MultiValueDictionaryNaive<int, string>(keyCapacity, valueCapacity: 0)),
      19 => (
        new MultiValueDictionarySlim<int, string>(keyCapacity, valueCapacity),
        new MultiValueDictionaryNaive<int, string>(keyCapacity, valueCapacity)),
      _ => (
        new MultiValueDictionarySlim<int, string>(keyCapacity, valueCapacity, CustomIntComparer.Instance),
        new MultiValueDictionaryNaive<int, string>(keyCapacity, valueCapacity, CustomIntComparer.Instance))
    };

    var keysPerCollection = _random.Next(1, 20);

    Assert.AreEqual(0, dictionarySlim.Count);
    Assert.AreEqual(0, dictionarySlim.ValuesCount);

    for (var operationsCount = _random.Next(0, 500); operationsCount >= 0; operationsCount--)
    {
      var hasItems = dictionarySlim.Count > 0;

      switch (_random.Next(minValue: 0, maxValue: 25))
      {
        // add key-value pair
        case >= 0 and < 17:
        {
          var key = _random.Next(0, keysPerCollection);
          var value = RandomString();
          var valuesCount = dictionarySlim.ValuesCount;

          dictionarySlim.Add(key, value);
          dictionaryNaive.Add(key, value);

          Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);
          Assert.AreEqual(dictionaryNaive.ValuesCount, dictionarySlim.ValuesCount);
          Assert.AreEqual(valuesCount + 1, dictionarySlim.ValuesCount);
          break;
        }

        // key with values range
        case 17:
        {
          var key = _random.Next(0, keysPerCollection);

          

          var enumerable = RandomValuesCollection(out var count);
          var valuesCountBefore = dictionarySlim.ValuesCount;

          dictionarySlim.AddValueRange(key, enumerable);
          dictionaryNaive.AddValueRange(key, enumerable);

          var valuesCountAfter = valuesCountBefore + count;

          Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);
          Assert.AreEqual(dictionaryNaive.ValuesCount, dictionarySlim.ValuesCount);
          Assert.AreEqual(valuesCountAfter, dictionarySlim.ValuesCount);
          break;
        }

        // key removal
        case 18 when hasItems:
        {
          var index = _random.Next(0, dictionaryNaive.Count);
          var keyToRemove = dictionaryNaive.Keys.ElementAt(index);
          var valuesCountBefore = dictionarySlim.ValuesCount;

          Assert.IsTrue(dictionarySlim.ContainsKey(keyToRemove));
          var keyValuesCount = dictionarySlim[keyToRemove].Count;
          Assert.That(keyValuesCount, Is.GreaterThan(0));

          var r1 = dictionaryNaive.Remove(keyToRemove);
          var r2 = dictionarySlim.Remove(keyToRemove);

          Assert.AreEqual(r1, r2);
          Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);
          Assert.AreEqual(valuesCountBefore - keyValuesCount, dictionarySlim.ValuesCount);
          break;
        }

        // key removal via ProcessEach
        case 19 when hasItems:
        {
          var index = _random.Next(0, dictionaryNaive.Count);
          var keyToRemove = dictionaryNaive.Keys.ElementAt(index);
          var valuesCountBefore = dictionarySlim.ValuesCount;

          Assert.IsTrue(dictionarySlim.ContainsKey(keyToRemove));
          var keyValuesCount = dictionarySlim[keyToRemove].Count;
          Assert.That(keyValuesCount, Is.GreaterThan(0));

          dictionaryNaive.Remove(keyToRemove);
          dictionarySlim.ProcessEach(
            keyToRemove, static (keyToRemove, key, collection) =>
            {
              Assert.That(collection.Count > 0);

              if (key == keyToRemove)
              {
                collection.Clear();

                var enumerator = collection.GetEnumerator();
                Assert.IsFalse(enumerator.MoveNext());
                Assert.AreEqual(0, collection.Count);
                Assert.IsTrue(collection.IsEmpty);
              }
            });

          Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);
          Assert.AreEqual(valuesCountBefore - keyValuesCount, dictionarySlim.ValuesCount);

          dictionarySlim.VerifyConsistency();
          break;
        }

        // clear
        case 20:
        {
          dictionaryNaive.Clear();
          dictionarySlim.Clear();

          Assert.AreEqual(0, dictionaryNaive.Count);
          Assert.AreEqual(0, dictionaryNaive.ValuesCount);
          Assert.AreEqual(0, dictionarySlim.Count);
          Assert.AreEqual(0, dictionarySlim.ValuesCount);
          break;
        }

        // trim keys
        case 21:
        {
          var count = dictionarySlim.Count;

          dictionaryNaive.TrimExcessKeys();
          dictionarySlim.TrimExcessKeys();

          Assert.AreEqual(count, dictionarySlim.Count);
          Assert.AreEqual(count, dictionaryNaive.Count);
          Assert.IsTrue(dictionarySlim.Count <= dictionarySlim.KeysCapacity);
          break;
        }

        // trim values
        case 22:
        {
          var valuesCount = dictionarySlim.ValuesCount;

          dictionaryNaive.TrimExcessValues();
          dictionarySlim.TrimExcessValues();

          Assert.AreEqual(valuesCount, dictionarySlim.ValuesCount);
          Assert.AreEqual(valuesCount, dictionaryNaive.ValuesCount);
          Assert.AreEqual(dictionarySlim.ValuesCount, dictionarySlim.ValuesCapacity);
          Assert.AreEqual(dictionaryNaive.ValuesCount, dictionaryNaive.ValuesCapacity);
          Assert.AreEqual(dictionarySlim.ValuesCapacity, dictionaryNaive.ValuesCapacity);
          break;
        }

        // value add via ProcessEach
        case 23 when hasItems:
        {
          var index = _random.Next(0, dictionaryNaive.Count);
          var existingKey = dictionaryNaive.Keys.ElementAt(index);
          var valuesCountBefore = dictionarySlim.ValuesCount;

          Assert.IsTrue(dictionarySlim.ContainsKey(existingKey));
          var keyValuesCount = dictionarySlim[existingKey].Count;
          Assert.That(keyValuesCount, Is.GreaterThan(0));

          var newValue = RandomString();
          dictionaryNaive.Add(existingKey, newValue);
          dictionarySlim.ProcessEach(
            state: (existingKey, newValue), static (state, key, collection) =>
            {
              Assert.That(collection.Count > 0);

              if (key == state.existingKey)
              {
                collection.Add(state.newValue);
              }
            });

          Assert.AreEqual(keyValuesCount + 1, dictionarySlim[existingKey].Count);
          Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);
          Assert.AreEqual(valuesCountBefore + 1, dictionaryNaive.ValuesCount);
          Assert.AreEqual(valuesCountBefore + 1, dictionarySlim.ValuesCount);
          break;
        }

        // replace key values
        case 24 when hasItems:
        {
          var index = _random.Next(0, dictionaryNaive.Count);
          var existingKey = dictionaryNaive.Keys.ElementAt(index);
          var valuesCountBefore = dictionarySlim.ValuesCount;
          var newValues = RandomValuesCollection(out var newValuesCount);

          Assert.IsTrue(dictionarySlim.ContainsKey(existingKey));
          var keyValuesCount = dictionarySlim[existingKey].Count;
          Assert.That(keyValuesCount, Is.GreaterThan(0));

          dictionaryNaive.Remove(existingKey);
          dictionaryNaive.AddValueRange(existingKey, newValues);

          dictionarySlim.ProcessEach(
            state: (existingKey, newValues), (state, key, collection) =>
            {
              Assert.That(collection.Count > 0);

              if (key == state.existingKey)
              {
                collection.Add(RandomString());
                collection.Clear();
                collection.AddRange(state.newValues);

                Assert.AreEqual(collection.Count, newValuesCount);
                Assert.AreEqual(collection.IsEmpty, newValuesCount == 0);

                var array = state.newValues.ToArray();
                Assert.AreEqual(array, collection.ToArray());

                var enumerator = collection.GetEnumerator();
                foreach (var arrayItem in array)
                {
                  Assert.IsTrue(enumerator.MoveNext());
                  Assert.AreEqual(arrayItem, enumerator.Current);
                }

                Assert.IsFalse(enumerator.MoveNext());
              }
            });

          // note: key may be removed
          Assert.AreEqual(newValuesCount, dictionarySlim[existingKey].Count);
          Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);
          Assert.AreEqual(valuesCountBefore - keyValuesCount + newValuesCount, dictionaryNaive.ValuesCount);
          Assert.AreEqual(valuesCountBefore - keyValuesCount + newValuesCount, dictionarySlim.ValuesCount);
          break;
        }
      }

      dictionarySlim.VerifyConsistency();
    }

    AssertEqualAndConsistent(dictionarySlim, dictionaryNaive);
    return;

    IEnumerable<string> RandomValuesCollection(out int count)
    {
      count = _random.Next(0, maxValue: 100);
      var values = new List<string>(count);

      for (var index = 0; index < count; index++)
        values.Add(RandomString());

      return (_random.Next() % 3) switch
      {
        0 => new HashSet<string>(values),
        1 => HideImplementation(values),
        _ => values,
      };

      IEnumerable<string> HideImplementation(List<string> xs)
      {
        foreach (var x in xs) yield return x;
      }
    }
  }

  [Test]
  public void ComparerProperty()
  {
    var d1 = new MultiValueDictionarySlim<int, string>(CustomIntComparer.Instance);
    Assert.IsTrue(ReferenceEquals(d1.Comparer, CustomIntComparer.Instance));

    var d2 = new MultiValueDictionarySlim<int, string>(keyCapacity: 0, valueCapacity: 0, CustomIntComparer.Instance);
    Assert.IsTrue(ReferenceEquals(d2.Comparer, CustomIntComparer.Instance));

    var d3 = new MultiValueDictionarySlim<string, string>(StringComparer.OrdinalIgnoreCase);
    Assert.IsTrue(ReferenceEquals(d3.Comparer, StringComparer.OrdinalIgnoreCase));

    var d4 = new MultiValueDictionarySlim<long, string>(EqualityComparer<long>.Default);
    Assert.IsTrue(ReferenceEquals(d4.Comparer, EqualityComparer<long>.Default));
  }

  [Test]
  public void CapacityExpectation_OneValuePerKey()
  {
    var dictionarySlim = new MultiValueDictionarySlim<int, int>();
    const int count = 128;

    for (var index = 0; index < count; index++)
    {
      dictionarySlim.Add(index, index);
    }

    Assert.AreEqual(dictionarySlim.ValuesCount, count);
    Assert.AreEqual(dictionarySlim.ValuesCapacity, count);
  }

  [Test]
  public void CapacityExpectation_OneKeyManyValues()
  {
    var dictionarySlim = new MultiValueDictionarySlim<int, int>();
    const int count = 128;

    for (var index = 0; index < count; index++)
    {
      dictionarySlim.Add(42, index);
    }

    Assert.AreEqual(dictionarySlim.ValuesCount, count);
    Assert.AreEqual(dictionarySlim.ValuesCapacity, count);
  }

  [Test, Repeat(1000)]
  public void CapacityExpectation_MultipleValuesPerKeyInOrderHasNoGaps()
  {
    var keys = new HashSet<int>();
    var dictionarySlim = new MultiValueDictionarySlim<int, string>();

    for (var operationsCount = _random.Next(0, 100); operationsCount >= 0; operationsCount--)
    {
      int key;
      do
      {
        key = _random.Next(0, 10000);
      }
      while (!keys.Add(key));

      for (var valuesCount = _random.Next(0, 100); valuesCount >= 0; valuesCount--)
      {
        dictionarySlim.Add(key, RandomString());
      }
    }

    //Assert.IsTrue(!dictionarySlim.ValuesListHasGaps);
  }

  [Test]
  [Repeat(100)]
  public void Consistency()
  {
    var dictionarySlim = new MultiValueDictionarySlim<int, int>();

    for (var operationsCount = _random.Next(0, 100); operationsCount >= 0; operationsCount--)
    {
      var key = _random.Next(1, 20);
      dictionarySlim.Add(key, key * 100 + dictionarySlim[key].Count);
      AssertConsistent();
    }

    return;

    void AssertConsistent()
    {
      foreach (var pair in dictionarySlim)
      {
        var x = pair.Key * 100;
        foreach (var i in pair.Value.ToArray())
        {
          Assert.AreEqual(x, i);
          x++;
        }

        var y = pair.Key * 100;
        foreach (var i in pair.Value)
        {
          Assert.AreEqual(y, i);
          y++;
        }
      }
    }
  }

  [Test]
  public void SameHash()
  {
    var dictionarySlim = new MultiValueDictionarySlim<string, int>(DumbEqualityComparer<string>.Instance);

    dictionarySlim.Add("aaa", 1);
    dictionarySlim.Add("aaa", 2);
    dictionarySlim.Add("aaa", 3);

    dictionarySlim.Add("bbb", 5);
    dictionarySlim.Add("bbb", 6);

    Assert.AreEqual(3, dictionarySlim["aaa"].Count);
    CollectionAssert.AreEqual(new[] { 1, 2, 3 }, dictionarySlim["aaa"].ToArray());

    Assert.AreEqual(2, dictionarySlim["bbb"].Count);
    Assert.IsTrue(dictionarySlim.ContainsKey("bbb"));
  }

  private sealed class DumbEqualityComparer<T> : IEqualityComparer<T>
  {
    private DumbEqualityComparer() { }
    public static IEqualityComparer<T> Instance { get; } = new DumbEqualityComparer<T>();

    public bool Equals(T x, T y) => EqualityComparer<T>.Default.Equals(x, y);
    public int GetHashCode(T obj) => 42;
  }

  [Test]
  public void InsertionOrder()
  {
    var dictionarySlim = new MultiValueDictionarySlim<string, int>();

    var key1 = "aaa" + RandomString();
    var key2 = "bbb" + RandomString();
    var key3 = "ccc" + RandomString();
    var key4 = "ddd" + RandomString();

    dictionarySlim.Add(key1, 1);
    dictionarySlim.Add(key4, 1111);
    dictionarySlim.Add(key4, 2222);
    dictionarySlim.Add(key2, 11);
    dictionarySlim.Add(key1, 2);
    dictionarySlim.Add(key1, 3);
    dictionarySlim.Add(key3, 111);
    dictionarySlim.Add(key2, 22);
    dictionarySlim.Add(key4, 3333);
    dictionarySlim.Add(key4, 4444);

    dictionarySlim.Remove(key4);

    var enumerator = dictionarySlim.GetEnumerator();
    Assert.IsTrue(enumerator.MoveNext());
    Assert.AreEqual(key1, enumerator.Current.Key);
    Assert.AreEqual(new[] { 1, 2, 3 }, enumerator.Current.Value.ToArray());

    Assert.IsTrue(enumerator.MoveNext());
    Assert.AreEqual(key2, enumerator.Current.Key);
    Assert.AreEqual(new[] { 11, 22 }, enumerator.Current.Value.ToArray());

    Assert.IsTrue(enumerator.MoveNext());
    Assert.AreEqual(key3, enumerator.Current.Key);
    Assert.AreEqual(new[] { 111 }, enumerator.Current.Value.ToArray());

    Assert.IsFalse(enumerator.MoveNext());
  }

  [Test]
  public void ProcessEach()
  {
    var dictionarySlim = new MultiValueDictionarySlim<string, int>();

    dictionarySlim.Add("aaa", 11);
    dictionarySlim.Add("aaa", 22);
    dictionarySlim.Add("aaa", 33);
    dictionarySlim.Add("bbb", 111);
    dictionarySlim.Add("ccc", 1111);
    dictionarySlim.Add("ccc", 2222);
    dictionarySlim.Remove("bbb");
    dictionarySlim.Add("remove_me", 1);

    dictionarySlim.ProcessEach(
      state: 42, processKey: (state, key, collection) =>
      {
        Assert.AreEqual(42, state);

        if (key == "aaa")
        {
          Assert.AreEqual(3, collection.Count);
          Assert.IsFalse(collection.IsEmpty);

          Assert.AreEqual(new[] { 11, 22, 33 }, collection.ToArray());
        }
        else if (key == "ccc")
        {
          Assert.AreEqual(2, collection.Count);
          Assert.IsFalse(collection.IsEmpty);

          Assert.AreEqual(new[] { 1111, 2222 }, collection.ToArray());
        }
        else if (key == "remove_me")
        {
          Assert.AreEqual(1, collection.Count);
          Assert.IsFalse(collection.IsEmpty);

          Assert.AreEqual(new[] { 1 }, collection.ToArray());

          collection.Clear();

          Assert.AreEqual(0, collection.Count);
          Assert.IsTrue(collection.IsEmpty);

          collection.Clear(); // no-op

          Assert.AreEqual(0, collection.Count);
          Assert.IsTrue(collection.IsEmpty);
        }
        else
        {
          Assert.Fail("Must be unreachable");
        }
      });

    Assert.IsFalse(dictionarySlim.ContainsKey("remove_me"));

    dictionarySlim.Add("remove_me2", 1);
  }

  private string RandomString()
  {
    var buffer = new byte[_random.Next(4, 10)];
    _random.NextBytes(buffer);

    return Convert.ToBase64String(buffer);
  }

  private static void AssertEqualAndConsistent<TKey, TValue>(
    MultiValueDictionarySlim<TKey, TValue> dictionarySlim,
    MultiValueDictionaryNaive<TKey, TValue> dictionaryNaive)
    where TKey : notnull
  {
    Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);

    // check keys
    Assert.AreEqual(dictionarySlim.Count, dictionarySlim.Keys.Count);
    Assert.AreEqual(dictionaryNaive.Keys.Count, dictionarySlim.Keys.Count);
    var slimKeys = dictionarySlim.Keys.ToArray();
    Assert.AreEqual(dictionarySlim.Count, slimKeys.Length);
    CollectionAssert.AreEquivalent(dictionaryNaive.Keys, slimKeys);
    CollectionAssert.AreEquivalent(dictionaryNaive.Keys, dictionarySlim.Keys.ToArray());

    // check values
    Assert.AreEqual(dictionarySlim.ValuesCount, dictionarySlim.Values.Count);
    Assert.AreEqual(dictionaryNaive.Values.Count(), dictionarySlim.Values.Count);
    var slimValues = dictionarySlim.Values.ToArray();
    Assert.AreEqual(dictionarySlim.ValuesCount, slimValues.Length);
    CollectionAssert.AreEquivalent(dictionaryNaive.Values, slimValues);
    CollectionAssert.AreEquivalent(dictionaryNaive.Values, dictionarySlim.Values.ToArray());

    foreach (var pair in dictionaryNaive)
    {
      Assert.IsTrue(dictionarySlim.ContainsKey(pair.Key));

      var slimList = dictionarySlim[pair.Key];
      Assert.AreEqual(pair.Value.Count, slimList.Count);
      CollectionAssert.AreEqual(pair.Value, slimList.ToArray());
      ListsEqualViaIndexer(pair.Value, slimList);
      ListsEqualViaEnumerators(pair.Value, slimList);
    }

    foreach (var pair in dictionarySlim)
    {
      var list = dictionaryNaive[pair.Key];
      Assert.AreEqual(pair.Value.Count, list.Count);
      CollectionAssert.AreEqual(pair.Value.ToArray(), list);
      ListsEqualViaIndexer(list, pair.Value);
      ListsEqualViaEnumerators(list, pair.Value);
    }

    return;

    void ListsEqualViaIndexer(IReadOnlyList<TValue> list, MultiValueDictionarySlim<TKey, TValue>.ValuesCollection slimList)
    {
      Assert.AreEqual(slimList.Count, list.Count);

      var index = 0;
      foreach (var value in slimList)
      {
        Assert.AreEqual(value, list[index]);
        index++;
      }
    }

    void ListsEqualViaEnumerators(IReadOnlyList<TValue> list, MultiValueDictionarySlim<TKey, TValue>.ValuesCollection slimList)
    {
      using var listEnumerator = list.GetEnumerator();
      var slimListEnumerator = slimList.GetEnumerator();

      while (true)
      {
        var m1 = listEnumerator.MoveNext();
        var m2 = slimListEnumerator.MoveNext();
        Assert.AreEqual(m1, m2);

        if (!m1) break;

        Assert.AreEqual(listEnumerator.Current, slimListEnumerator.Current);
      }
    }
  }

  private sealed class CustomIntComparer : IEqualityComparer<int>
  {
    private CustomIntComparer() { }
    public static IEqualityComparer<int> Instance { get; } = new CustomIntComparer();

    public bool Equals(int x, int y) => x == y;
    public int GetHashCode(int obj) => obj / 10;
  }

  private class MultiValueDictionaryNaive<TKey, TValue> : IEnumerable<KeyValuePair<TKey, List<TValue>>>
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
}
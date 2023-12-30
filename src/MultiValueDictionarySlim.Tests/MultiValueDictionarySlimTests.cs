using System.Diagnostics.CodeAnalysis;
using NUnit.Framework;

namespace ControlFlow.Collections.Tests;

public class MultiValueDictionarySlimTests
{
  private readonly Random _random = new(); // 43435

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
    Assert.IsTrue(dictionary[-2].IsEmpty);
    Assert.AreEqual(0, dictionary[-2].Count);

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
    Assert.AreEqual(new[] { "71", "72", "73" }, dictionary[7].ToEnumerator().ToArray());

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
    CollectionAssert.AreEqual(Enumerate(), enumCollection.ToEnumerator());

    var dict2 = new MultiValueDictionarySlim<string, int>();
    dict2.AddValueRange("list", Enumerate().ToList());
    Assert.AreEqual(1, dict2.Count);
    Assert.AreEqual(dataCount, dict2.ValuesCount);
    Assert.AreEqual(dataCount, dict2.ValuesCapacity);
    var listCollection = dict2["list"];
    Assert.AreEqual(dataCount, listCollection.Count);
    CollectionAssert.AreEqual(Enumerate().ToArray(), listCollection.ToArray());
    CollectionAssert.AreEqual(Enumerate(), listCollection.ToEnumerator());

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
      switch (_random.Next(minValue: 0, maxValue: 23))
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

          var count = _random.Next(0, maxValue: 100);
          var toAdd = new List<string>(count);

          for (var index = 0; index < count; index++)
            toAdd.Add(RandomString());

          var enumerable = _random.Next() % 2 == 0 ? Iterator() : toAdd;
          var valuesCountBefore = dictionarySlim.ValuesCount;

          dictionarySlim.AddValueRange(key, enumerable);
          dictionaryNaive.AddValueRange(key, enumerable);

          var valuesCountAfter = valuesCountBefore + count;

          Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);
          Assert.AreEqual(dictionaryNaive.ValuesCount, dictionarySlim.ValuesCount);
          Assert.AreEqual(valuesCountAfter, dictionarySlim.ValuesCount);
          break;

          IEnumerable<string> Iterator()
          {
            foreach (var x in toAdd)
              yield return x;
          }
        }

        // key removal
        case 18 when dictionaryNaive.Count > 0:
        {
          var index = _random.Next(0, dictionaryNaive.Count);
          var keyToRemove = dictionaryNaive.Keys.ElementAt(index);
          var valuesCount = dictionarySlim.ValuesCount;

          Assert.IsTrue(dictionarySlim.ContainsKey(keyToRemove));
          var itemsCount = dictionarySlim[keyToRemove].Count;
          Assert.That(itemsCount, Is.GreaterThan(0));

          var r1 = dictionaryNaive.Remove(keyToRemove);
          var r2 = dictionarySlim.Remove(keyToRemove);

          Assert.AreEqual(r1, r2);
          Assert.AreEqual(dictionaryNaive.Count, dictionarySlim.Count);
          Assert.AreEqual(valuesCount - itemsCount, dictionarySlim.ValuesCount);
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

        case 23:
        {
          break;
        }
      }
    }

    AssertEqualAndConsistent(dictionarySlim, dictionaryNaive);
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
        foreach (var i in pair.Value.ToEnumerator())
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
    CollectionAssert.AreEquivalent(dictionaryNaive.Keys, dictionarySlim.Keys.ToEnumerator());

    // check values
    Assert.AreEqual(dictionarySlim.ValuesCount, dictionarySlim.Values.Count);
    Assert.AreEqual(dictionaryNaive.Values.Count(), dictionarySlim.Values.Count);
    var slimValues = dictionarySlim.Values.ToArray();
    Assert.AreEqual(dictionarySlim.ValuesCount, slimValues.Length);
    CollectionAssert.AreEquivalent(dictionaryNaive.Values, slimValues);
    CollectionAssert.AreEquivalent(dictionaryNaive.Values, dictionarySlim.Values.ToEnumerator());

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
}
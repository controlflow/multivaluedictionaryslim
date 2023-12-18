using NUnit.Framework;

namespace OneToListMap.Tests;

public class MultiValueDictionarySlimTests
{
  private readonly Random _random = new(43431); // 43435

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
    Assert.IsFalse(dictionary[3].IsEmpty);
    Assert.AreEqual(2, dictionary[3].Count);
    Assert.AreEqual(new[] { "31", "32" }, dictionary[3].ToArray());

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
  }

  [Test]
  [Repeat(1000)]
  public void BasicOperations()
  {
    var dictionary = new MultiValueDictionaryNaive<int, string>();
    var dictionarySlim = _random.Next(0, 20) switch
    {
      >= 0 and < 10
        => new MultiValueDictionarySlim<int, string>(),
      >= 10 and < 16
        => new MultiValueDictionarySlim<int, string>(CustomIntComparer.Instance),
      17
        => new MultiValueDictionarySlim<int, string>(keyCapacity: 0, valueCapacity: _random.Next(0, 100)),
      18
        => new MultiValueDictionarySlim<int, string>(keyCapacity: _random.Next(0, 100), valueCapacity: 0),
      19
        => new MultiValueDictionarySlim<int, string>(keyCapacity: _random.Next(0, 100), valueCapacity: _random.Next(0, 100)),
      _
        => new MultiValueDictionarySlim<int, string>(keyCapacity: _random.Next(0, 100), valueCapacity: _random.Next(0, 100), CustomIntComparer.Instance)
    };

    var keysPerCollection = _random.Next(1, 20);

    Assert.AreEqual(0, dictionarySlim.Count);
    Assert.AreEqual(0, dictionarySlim.ValuesCount);

    for (var operationsCount = _random.Next(0, 500); operationsCount >= 0; operationsCount--)
    {
      switch (_random.Next(0, 20))
      {
        case >= 0 and < 18:
        {
          var key = _random.Next(0, keysPerCollection);
          var value = RandomString();
          var valuesCount = dictionarySlim.ValuesCount;

          dictionarySlim.Add(key, value);
          dictionary.Add(key, value);

          Assert.AreEqual(dictionary.Count, dictionarySlim.Count);
          Assert.AreEqual(dictionary.ValuesCount, dictionarySlim.ValuesCount);
          Assert.AreEqual(valuesCount + 1, dictionarySlim.ValuesCount);
          break;
        }

        case 18 when dictionary.Count > 0:
        {
          var index = _random.Next(0, dictionary.Count);
          var keyToRemove = dictionary.Keys.ElementAt(index);
          var valuesCount = dictionarySlim.ValuesCount;

          Assert.IsTrue(dictionarySlim.ContainsKey(keyToRemove));
          var itemsCount = dictionarySlim[keyToRemove].Count;
          Assert.That(itemsCount, Is.GreaterThan(0));

          var r1 = dictionary.Remove(keyToRemove);
          var r2 = dictionarySlim.Remove(keyToRemove);

          Assert.AreEqual(r1, r2);
          Assert.AreEqual(dictionary.Count, dictionarySlim.Count);
          Assert.AreEqual(valuesCount - itemsCount, dictionarySlim.ValuesCount);
          break;
        }

        case 19:
        {
          dictionary.Clear();
          dictionarySlim.Clear();

          Assert.AreEqual(0, dictionary.Count);
          Assert.AreEqual(0, dictionarySlim.Count);
          Assert.AreEqual(0, dictionarySlim.ValuesCount);
          break;
        }

        case 20:
        {
          break;
        }

        case 21:
        {
          var count = dictionary.Count;

          //dictionary.TrimExcess();
          //dictionarySlim.TrimExcess();

          Assert.AreEqual(count, dictionary.Count);
          Assert.AreEqual(count, dictionarySlim.Count);
          Assert.IsTrue(dictionarySlim.Count <= dictionarySlim.KeysCapacity);
          Assert.IsTrue(dictionarySlim.ValuesCount <= dictionarySlim.ValuesCapacity);
          break;
        }
      }
    }

    if (dictionarySlim.ValuesCapacity > 0)
    {

    }

    AssertEqual(dictionarySlim, dictionary);
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

  [Test, Repeat(1000)]
  public void CapacityExpectation_OneValuePerKey()
  {
    var keys = new HashSet<int>();
    var dictionarySlim = new MultiValueDictionarySlim<int, string>();

    for (var operationsCount = _random.Next(0, 1000); operationsCount >= 0; operationsCount--)
    {
      int key;
      do
      {
        key = _random.Next(0, 10000);
      } while (!keys.Add(key));

      dictionarySlim.Add(key, RandomString());
    }

    //Assert.AreEqual(dictionarySlim.ValuesUsedCapacity, dictionarySlim.ValuesCount);
  }

  [Test, Repeat(1000)]
  public void CapacityExpectation_OneKeyManyValues()
  {
    var dictionarySlim = new MultiValueDictionarySlim<int, string>();
    var key = _random.Next(0, 10000);

    var valuesCount = _random.Next(0, 1000);
    for (var x = valuesCount; x > 0; x--)
    {
      dictionarySlim.Add(key, RandomString());
    }

    Assert.AreEqual(valuesCount, dictionarySlim.ValuesCount);
    Assert.IsTrue(Math.Max(valuesCount * 2, 4) >= dictionarySlim.ValuesCapacity);
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
  [Repeat(1000)]
  public void Consistency()
  {
    var dictionarySlim = new MultiValueDictionarySlim<int, int>();

    for (var operationsCount = _random.Next(0, 100); operationsCount >= 0; operationsCount--)
    {
      var key = _random.Next(1, 20);
      dictionarySlim.Add(key, key * 100 + dictionarySlim[key].Count);
      AssertConsistent();
    }

    void AssertConsistent()
    {
      foreach (var (key, values) in dictionarySlim)
      {
        int x = key * 100;
        foreach (var i in values.ToArray())
        {
          Assert.AreEqual(x, i);
          x++;
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
    dictionarySlim.Add("bbb", 5);
    dictionarySlim.Add("bbb", 6);
  }

  private sealed class DumbEqualityComparer<T> : IEqualityComparer<T>
  {
    private DumbEqualityComparer() { }
    public static IEqualityComparer<T> Instance { get; } = new DumbEqualityComparer<T>();

    public bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x, y);
    public int GetHashCode(T obj) => 42;
  }

  private string RandomString()
  {
    Span<byte> buffer = stackalloc byte[_random.Next(4, 10)];
    _random.NextBytes(buffer);

    return Convert.ToBase64String(buffer);
  }

  private static void AssertEqual<TKey, TValue>(
    MultiValueDictionarySlim<TKey, TValue> dictionarySlim,
    MultiValueDictionaryNaive<TKey, TValue> dictionary)
    where TKey : notnull
  {
    Assert.AreEqual(dictionary.Count, dictionarySlim.Count);

    foreach (var (key, list) in dictionary)
    {
      Assert.IsTrue(dictionarySlim.ContainsKey(key));

      var slimList = dictionarySlim[key];
      Assert.AreEqual(list.Count, slimList.Count);
      CollectionAssert.AreEqual(list, slimList.ToArray());
      ListsEqualViaIndexer(list, slimList);
      ListsEqualViaEnumerators(list, slimList);
    }

    foreach (var (key, slimList) in dictionarySlim)
    {
      var list = dictionary[key];
      Assert.AreEqual(slimList.Count, list.Count);
      CollectionAssert.AreEqual(slimList.ToArray(), list);
      ListsEqualViaIndexer(list, slimList);
      ListsEqualViaEnumerators(list, slimList);
    }

    return;

    void ListsEqualViaIndexer(List<TValue> list, MultiValueDictionarySlim<TKey, TValue>.ValuesList slimList)
    {
      Assert.AreEqual(slimList.Count, list.Count);

      var index = 0;
      foreach (var value in slimList)
      {
        Assert.AreEqual(value, list[index]);
        index++;
      }
    }

    void ListsEqualViaEnumerators(List<TValue> list, MultiValueDictionarySlim<TKey, TValue>.ValuesList slimList)
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
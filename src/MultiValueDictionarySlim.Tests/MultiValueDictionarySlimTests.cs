using NUnit.Framework;

namespace OneToListMap.Tests;

public class MultiValueDictionarySlimTests
{
  private readonly Random _random = new();

  [Test]
  [Repeat(5000)]
  public void BasicOperations()
  {
    var dictionary = new Dictionary<int, List<string>>();
    var dictionarySlim = _random.Next(0, 7) switch
    {
      0 => new MultiValueDictionarySlim<int, string>(),
      1 => new MultiValueDictionarySlim<int, string>(CustomIntComparer.Instance),
      3 => new MultiValueDictionarySlim<int, string>(
        keyCapacity: 0, valueCapacity: _random.Next(0, 100)),
      4 => new MultiValueDictionarySlim<int, string>(
        keyCapacity: _random.Next(0, 100), valueCapacity: 0),
      5 => new MultiValueDictionarySlim<int, string>(
        keyCapacity: _random.Next(0, 100), valueCapacity: _random.Next(0, 100)),
      _ => new MultiValueDictionarySlim<int, string>(
        keyCapacity: _random.Next(0, 100), valueCapacity: _random.Next(0, 100), CustomIntComparer.Instance)
    };

    Assert.AreEqual(0, dictionarySlim.Count);
    Assert.AreEqual(0, dictionarySlim.ValuesCount);

    for (var operationsCount = _random.Next(0, 100); operationsCount >= 0; operationsCount--)
    {
      switch (_random.Next(0, 10))
      {
        case >= 0 and <= 7:
        {
          var key = _random.Next(0, 10);
          var value = RandomString();
          var valuesCount = dictionarySlim.ValuesCount;

          dictionarySlim.Add(key, value);

          if (!dictionary.TryGetValue(key, out var list))
            dictionary[key] = list = new List<string>();
          list.Add(value);

          Assert.AreEqual(dictionary.Count, dictionarySlim.Count);
          Assert.AreEqual(valuesCount + 1, dictionarySlim.ValuesCount);
          break;
        }

        case 8 when dictionary.Count > 0:
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

        case 9:
        {
          dictionary.Clear();
          dictionarySlim.Clear();

          Assert.AreEqual(0, dictionary.Count);
          Assert.AreEqual(0, dictionarySlim.Count);
          Assert.AreEqual(0, dictionarySlim.ValuesCount);
          break;
        }
      }
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

  private string RandomString()
  {
    Span<byte> buffer = stackalloc byte[_random.Next(4, 10)];
    _random.NextBytes(buffer);

    return Convert.ToBase64String(buffer);
  }

  private static void AssertEqual<TKey, TValue>(
    MultiValueDictionarySlim<TKey, TValue> dictionarySlim, Dictionary<TKey, List<TValue>> dictionary)
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

    void ListsEqualViaIndexer(List<TValue> list, MultiValueDictionarySlim<TKey, TValue>.ValuesList slimList)
    {
      Assert.AreEqual(slimList.Count, list.Count);

      for (int index = 0, count = list.Count; index < count; index++)
      {
        Assert.AreEqual(slimList[index], list[index]);
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
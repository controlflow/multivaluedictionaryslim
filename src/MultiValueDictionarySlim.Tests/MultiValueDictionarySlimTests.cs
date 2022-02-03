using NUnit.Framework;

namespace OneToListMap.Tests;

public class MultiValueDictionarySlimTests
{
  private readonly Random _random = new();

  [Test]
  public void Basics()
  {
    var dictionary = new Dictionary<int, List<string>>();
    var dictionarySlim = new MultiValueDictionarySlim<int, string>();
    
    for (var operationsCount = _random.Next(0, 100); operationsCount >= 0; operationsCount--)
    {
      switch (_random.Next(0, 10))
      {
        case >= 0 and <= 7:
        {
          var key = _random.Next(0, 10);
          var value = RandomString();

          dictionarySlim.Add(key, value);

          if (!dictionary.TryGetValue(key, out var list))
            dictionary[key] = list = new List<string>();
          list.Add(value);
          break;
        }
      }
    }
    
    AssertEqual(dictionarySlim, dictionary);
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
      var slimList = dictionarySlim[key];
      Assert.AreEqual(list.Count, slimList.Count);
      CollectionAssert.AreEqual(list, slimList.ToArray());
      ListEqualViaIndexer(list, slimList);
    }
    
    foreach (var (key, slimList) in dictionarySlim)
    {
      var list = dictionary[key];
      Assert.AreEqual(slimList.Count, list.Count);
      CollectionAssert.AreEqual(slimList.ToArray(), list);
      ListEqualViaIndexer(list, slimList);
    }

    void ListEqualViaIndexer(List<TValue> list, MultiValueDictionarySlim<TKey, TValue>.ValuesList slimList)
    {
      Assert.AreEqual(slimList.Count, list.Count);

      for (int index = 0, count = list.Count; index < count; index++)
      {
        Assert.AreEqual(slimList[index], list[index]);
      }
    }
  }
}
using NUnit.Framework;

namespace OneToListMap.Tests;

public class MultiValueDictionarySlimTests
{
  [Test]
  public void Basics()
  {
    var map = new MultiValueDictionarySlim<int, string>();
    map.Add(1, "aaaa");
    map.Add(2, "bbbb");
    map.Add(1, "AAA");

    foreach (var item in map[1])
    {
      _ = item;
    }

    var dd = new Dictionary<int, string>();
    dd.Add(1, "aaaa");
    dd.Add(2, "bbbb");

    ;
  }
}
using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ControlFlow.Collections;

BenchmarkRunner.Run<AdditionBenchmarkNoPooling>();
//BenchmarkRunner.Run<AdditionBenchmarkNoPoolingFewKeys>();
//BenchmarkRunner.Run<AdditionBenchmarkNoPoolingOneToOneKeys>();
//BenchmarkRunner.Run<AdditionBenchmarkPooling>();

[MemoryDiagnoser]
//[SimpleJob(RuntimeMoniker.Net80)]
//[SimpleJob(RuntimeMoniker.Net472)]
//[HideColumns("Job")]
public class AdditionBenchmarkBase
{
  protected (int Key, string Value)[] ItemsToAdd = [];

  [Params(10, 100, 1000, 10_000)]
  public int ItemsCount { get; set; }

  protected virtual Dictionary<int, List<string>> CreateOrdinaryDictionary() => new();
  protected virtual MultiValueDictionarySlim<int, string> CreateSlimDictionary() => new();

  [Benchmark]
  public object Slim()
  {
    var dictionary = CreateSlimDictionary();

    foreach (var (key, value) in ItemsToAdd)
    {
      dictionary.Add(key, value);
    }

    foreach (var pair in dictionary)
    {
      foreach (var value in pair.Value)
      {
        GC.KeepAlive(value);
      }
    }

    return dictionary;
  }

  [Benchmark]
  public object Ordinary()
  {
    var dictionary = CreateOrdinaryDictionary();

    foreach (var (key, value) in ItemsToAdd)
    {
      if (!dictionary.TryGetValue(key, out var values))
      {
        dictionary[key] = values = new List<string>();
      }

      values.Add(value);
    }

    foreach (var pair in dictionary)
    {
      foreach (var value in pair.Value)
      {
        GC.KeepAlive(value);
      }
    }

    return dictionary;
  }
}

public class AdditionBenchmarkNoPooling : AdditionBenchmarkBase
{
  [GlobalSetup]
  public void Setup()
  {
    var random = new Random(42);
    ItemsToAdd = Enumerable.Range(0, ItemsCount)
      .Select(x => (random.Next(0, 20), x.ToString()))
      .ToArray();
  }
}

public class AdditionBenchmarkNoPoolingFewKeys : AdditionBenchmarkBase
{
  [GlobalSetup]
  public void Setup()
  {
    var random = new Random(42);
    ItemsToAdd = Enumerable.Range(0, ItemsCount)
      .Select(x => (random.Next(0, 2), x.ToString()))
      .ToArray();
  }
}

public class AdditionBenchmarkNoPoolingOneToOneKeys : AdditionBenchmarkBase
{
  [GlobalSetup]
  public void Setup()
  {
    var random = new Random(42);
    ItemsToAdd = Enumerable.Range(0, ItemsCount)
      .Select(x => (random.Next(0, int.MaxValue), x.ToString()))
      .ToArray();
  }
}

public class AdditionBenchmarkPooling : AdditionBenchmarkBase
{
  [GlobalSetup]
  public void Setup()
  {
    var random = new Random(42);
    ItemsToAdd = Enumerable.Range(0, ItemsCount)
      .Select(x => (random.Next(0, 20), x.ToString()))
      .ToArray();
  }

  private readonly Dictionary<int,List<string>> _ordinaryDictionary = new();

  protected override Dictionary<int, List<string>> CreateOrdinaryDictionary()
  {
    _ordinaryDictionary.Clear();
    return _ordinaryDictionary;
  }

  private readonly MultiValueDictionarySlim<int,string> _slimDictionary = new();

  protected override MultiValueDictionarySlim<int, string> CreateSlimDictionary()
  {
    _slimDictionary.Clear();
    return _slimDictionary;
  }
}
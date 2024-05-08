using System.Collections.Generic;
using System.Collections.ObjectModel;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ControlFlow.Collections;
using Microsoft.VisualBasic;

BenchmarkRunner.Run<Benchmarks>();

class Benchmarks
{
  public Benchmarks()
  {
    Collection<int> xs;
    xs.Add();
  }

  [Benchmark]
  public void Slim()
  {
    var dictionary = new MultiValueDictionarySlim<int, string>();
  }
  
  [Benchmark]
  public void Ordinary()
  {
    var dictionary = new Dictionary<int, List<string>>();
  }
}
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ControlFlow.Collections;

BenchmarkRunner.Run<Benchmarks>();

class Benchmarks
{
  public Benchmarks()
  {
    
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
using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ControlFlow.Collections;

BenchmarkRunner.Run<AdditionNoPooling>();

public class AdditionNoPooling
{
  private readonly (int, string)[] myItems;

  public AdditionNoPooling()
  {
    var random = new Random(42);

    
    
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
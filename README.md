# `MultiValueDictionarySlim` collection

This repo consists the implementation of the multi-map data structure - a dictionary that maps a single key to multiple values. In .NET there is no such data structure available in the .NET base class library. Usually developers use `Dictionary<TKey, List<TValue>>` collection to implement multi-maps, but this comes with usability problems and associated performance costs.

LINQ APIs also comes with a `ILookup<TKey, TValue>` interface (and `.ToLookup()` extension method) that represents multi-map like data structure, but it does not allow the general multi-map operations to be performed like we can do with the ordinary collection classes.

Back in the days Microsoft shipped the [`MultiValueDictionary` package](https://devblogs.microsoft.com/dotnet/multidictionary-becomes-multivaluedictionary/) with a multi-map data structure, but it's deprecated now. The implementation generally was pretty much similar to `Dictionary<TKey, List<TValue>>`, except it allows to change the underlying data structure for values collections (for example, using `HashSet<TValue>` instead).

The `MultiValueDictionarySlim<TKey, TValue>` data structure from this repo is a specialized multi-map implementation that is intended to replace `Dictionary<TKey, List<TValue>>`-like multi-maps in scenarios where memory allocation patterns are important.

## Memory layout

Let's compare the data structure memory layout using the `Dictionary<TKey, List<TValue>>` for comparison:

![](docs/slide2.png)

The biggest advantage of the `MultiValueDictionarySlim<TKey, TValue>` memory layout is that the amount of retained heap objects count is not dependent on the amount of keys and values stored in the data structure, it always represented by just 5 heap objects:

![](docs/slide1.png)

## Trade-offs

* The values corresponding to key are stored as a linked list (inside an array), so unlike `List<TValue>` there is no `O(1)` by index access available. You can only enumerate the values, get their count, add more to the tail of the list and clear all the values (by removing the key). So `MultiValueDictionarySlim<TKey, TValue>` is more comparable to `Dictionary<TKey, Collection<TValue>>`.

* Removal of the key is a relatively slow `O(N)` operation (where `N` is the amount of values corresponding to this key) if `TValue` is of reference type. In this case we have to traverse the linked list of values and assign `null` to values to release the objects referenced. In `Dictionary<TKey, List<TValue>>` the key removal just releases the reference to corresponding `List<TValue>` instance and the rest is done by the GC, no need to assign `null`s.

* Generally `MultiValueDictionarySlim` consumes more memory to store the same data represented as a `Dictionary<TKey, List<TValue>>`. One source of increased consumption - `Int32` value (to implement linked list) stored per each `TValue` added to multi-map. This overhead can become noticeable if you use `MultiValueDictionarySlim` to store small amount of keys corresponding to very big amount of values.

* Another source of increased memory consumption - faster growth of the internal arrays, comparing to separate growth of each `List<TValue>` collections in `Dictionary<TKey, List<TValue>>`. Often `MultiValueDictionarySlim` wins over the `Dictionary<TKey, List<TValue>>`, because it stores all the values in the same array and fills all the available gaps there. But when there is no space left to insert the new value - it doubles the capacity of the internal array that store _all_ the values of `MultiValueDictionarySlim`, instead of growing the individual (and usually much smaller) `List<TValue>` instances associated with the key in `Dictionary<TKey, List<TValue>>`. In random test scenarios the design of `MultiValueDictionarySlim` results in 15-25% increase of the memory wasted on the unused tail parts of the internal arrays (bigger the array capacity - the more space is wasted if count is less than capacity).

* Another implication of using the relatively big internal arrays to implement the `MultiValueDictionarySlim` - it is more likely that those arrays eventually make it into Large Object Heap (LOH). This is generally undesirable and may result in increased heap fragmentation.

* The memory allocated to store both key and the corresponding values are not released when key is removed from the `MultiValueDictionarySlim`. This memory will be reused for next inserts of the keys/values, but in the case there will be no more inserts - this memory will remain wasted. Note that this is also the case for ordinary `Dictionary<TKey, TValue>` collection (key-value entry remains allocated for future inserts), but the situation with `Dictionary<TKey, List<TValue>>` is somewhat better - key removal releases the reference to corresponding `List<TValue>` so that the GC actually reclaims the memory occupied by the values stored in a separate heap object. However, this memory usage pattern of `MultiValueDictionarySlim` allows it to be used in object pooling scenarios. If you plan to retain the instance of `MultiValueDictionarySlim` after series of inserts and removals - you can use `TrimExcessKeys` and `TrimExcessValues` methods to compact the data.

* Unlike with `Dictionary<TKey, List<TValue>>` there is no way to get a reference to the collection values for some specific key and use it separately - as a standalone collection object. This can be quite useful sometimes, but with this design it's hard for the owner multi-map to enforce any kind of logical/performance guarantees (especially if exposed `List<TValue>` is mutable).  

* The `MultiValueDictionarySlim` data structure do not support keys without the corresponding values. This is possible in `Dictionary<TKey, List<TValue>>` by having a key corresponding to empty `List<TValue>` instance - this may be useful sometimes, but generally it's not a good state of multi-map to support.

* The `MultiValueDictionarySlim` data structure is intended to solve GC/memory traffics of using the `Dictionary<TKey, List<TValue>>` - to avoid any other possible memory-related mistakes it intentionally lacks functionality like `IEnumerable<T>` implementations for collections of values/keys/all values. There are no APIs like `Remove(key, value)` to avoid `O(N)` value lookups.

## Other notes

* Just like with `Dictionary`, the `MultiValueDictionarySlim` data structure preserves the order of keys on enumeration, but the order is guaranteed if multi-map is only used to add key-value pairs and optionally to remove some keys after all the additions (without more additions). So for:

```c#
var dictionary = new MultiValueDictionarySlim<int, string>();

dictionary.Add(11, "11");
dictionary.Add(22, "AAA");
dictionary.Add(11, "22");
dictionary.Add(33, "XYZ");
dictionary.Add(44, "---");

dictionary.Remove(22);

foreach (var (key, values) in dictionary)
{
  // order is preserved: 11, 33, 44
}
```

* Hash map implementation is based on the latest .NET Core's `Dictionary<TKey, TValue>` implementation, the code may be suboptimal for .NET Framework targets due to JIT differences.

## Benchmark

Additions of different number of key-value pairs, with up to 20 unique keys, followed by enumeration of the grouped data. `MultiValueDictionarySlim` generally is a bit slower comparing to `Dictionary<TKey, List<TValue>` and induces generally the same memory pressure. Except when the amount of items gets much bigger, big internal arrays of `MultiValueDictionarySlim` are starting to degrade the performance and affect memory traffic:

| Method   | ItemsCount |         Mean |    Gen0 |    Gen1 |    Gen2 | Allocated |
|----------|------------|-------------:|--------:|--------:|--------:|----------:|
| Slim     | 10         |     266.8 ns |  0.1097 |       - |       - |     920 B |
| Ordinary | 10         |     281.6 ns |  0.1287 |  0.0005 |       - |    1080 B |
| Slim     | 100        |   2,128.9 ns |  0.6142 |  0.0076 |       - |    5160 B |
| Ordinary | 100        |   1,700.6 ns |  0.5989 |  0.0076 |       - |    5024 B |
| Slim     | 1000       |  16,592.5 ns |  3.2043 |  0.2136 |       - |   26808 B |
| Ordinary | 1000       |  10,829.4 ns |  3.0975 |  0.1678 |       - |   26008 B |
| Slim     | 10000      | 225,620.1 ns | 41.5039 | 41.5039 | 41.5039 |  395654 B |
| Ordinary | 10000      |  87,551.7 ns | 27.0996 |  8.9111 |       - |  227272 B |

The same benchmark when instances of dictionaries are pooled (shared between iterations, cleared before each benchmark) shows the general advantage of `MultiValueDictionarySlim` - when pooled properly it shows comparable performance while completely avoiding memory traffic:

| Method   | ItemsCount |         Mean |    Gen0 |   Gen1 | Allocated |
|----------|------------|-------------:|--------:|-------:|----------:|
| Slim     | 10         |     143.0 ns |       - |      - |         - |
| Ordinary | 10         |     268.8 ns |  0.0734 |      - |     616 B |
| Slim     | 100        |   1,227.5 ns |       - |      - |         - |
| Ordinary | 100        |   1,491.8 ns |  0.3510 | 0.0019 |    2944 B |
| Slim     | 1000       |  11,728.5 ns |       - |      - |         - |
| Ordinary | 1000       |  11,021.1 ns |  2.8534 | 0.1373 |   23928 B |
| Slim     | 10000      | 134,873.8 ns |       - |      - |         - |
| Ordinary | 10000      |  87,601.1 ns | 26.8555 | 6.7139 |  225192 B |

If your keys almost always have one value associated, then `MultiValueDictionarySlim` is also better since it do not allocate `List<TValue>` instance for each key to store a single value:

| Method   | ItemsCount |         Mean |     Gen0 |     Gen1 |     Gen2 |  Allocated |
|----------|------------|-------------:|---------:|---------:|---------:|-----------:|
| Slim     | 10         |     373.1 ns |   0.1655 |   0.0005 |        - |    1.35 KB |
| Ordinary | 10         |     425.1 ns |   0.2232 |   0.0010 |        - |    1.83 KB |
| Slim     | 100        |   3,557.1 ns |   1.4496 |   0.0420 |        - |   11.85 KB |
| Ordinary | 100        |   4,070.3 ns |   2.2659 |   0.1373 |        - |   18.55 KB |
| Slim     | 1000       |  36,373.4 ns |  13.4277 |   2.9907 |        - |  110.05 KB |
| Ordinary | 1000       |  44,076.4 ns |  22.7051 |  11.2915 |        - |  185.76 KB |
| Slim     | 10000      | 598,848.6 ns | 199.2188 | 199.2188 | 199.2188 | 1173.29 KB |
| Ordinary | 10000      | 752,794.6 ns | 221.6797 | 221.6797 | 221.6797 | 1779.38 KB |

If you instead use a very few amount of keys with a lot of values associated, the linked list nature of `MultiValueDictionarySlim` performs progressively worse and allocates more memory comparing to `Dictionary<TKey, List<TValue>`:

| Method   | ItemsCount |         Mean |    Gen0 |    Gen1 |    Gen2 | Allocated |
|----------|------------|-------------:|--------:|--------:|--------:|----------:|
| Slim     | 10         |     209.4 ns |  0.0832 |       - |       - |     696 B |
| Ordinary | 10         |     191.8 ns |  0.0677 |       - |       - |     568 B |
| Slim     | 100        |   1,752.2 ns |  0.4215 |  0.0038 |       - |    3528 B |
| Ordinary | 100        |   1,120.3 ns |  0.2975 |       - |       - |    2504 B |
| Slim     | 1000       |  16,260.8 ns |  2.9907 |  0.1831 |       - |   25176 B |
| Ordinary | 1000       |   8,270.5 ns |  2.0294 |  0.0458 |       - |   16984 B |
| Slim     | 10000      | 229,566.9 ns | 41.5039 | 41.5039 | 41.5039 |  394022 B |
| Ordinary | 10000      |  83,991.1 ns | 31.2500 |       - |       - |  262936 B |

# `MultiValueDictionarySlim` collection

This repo consists the implementation of the multi-map data structure - a dictionary that maps a single key to multiple values. In .NET there is no such data structure available in the BCL.

, except there is a `ILookup` interface and `.ToLookup()` that represents the same concept, but do not expose the multi-map data structure.

now obsolete `MultiValueDictionary`.

`ILookup`

Best-used when most pairs are 1:1, but some keys have N values.
The more N gets - the more memory consumes.
Integers instead of 64bit pointers.

* integers rather than pointers are 1) smaller and 2) nicer for GC

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
# `MultiValueDictionarySlim` collection

This repo consists the implementation of the multi-map data structure - a dictionary that maps a single key to multiple values. In .NET there is no such data structure available in the BCL, except there is a `ILookup` interface and `.ToLookup()` that represents the same concept, but do not expose the multi-map data structure.

now obsolete `MultiValueDictionary`.

`ILookup`

Best-used when most pairs are 1:1, but some keys have N values.
The more N gets - the more memory consumes.
Integers instead of 64bit pointers.

## Memory layout

Let's compare the data structure memory layout using the `Dictionary<TKey, List<TValue>>` for comparison:

![](docs/slide2.png)

The biggest advantage of the `MultiValueDictionarySlim<TKey, TValue>` memory layout is that the amount of retained heap objects count is not dependent on the amount of keys and values stored in the data structure:

![](docs/slide1.png)



## Trade-offs

* The values corresponding to key are stored as a linked list (inside an array), so unlike `List<TValue>` there is no `O(1)` by index access available. You can only enumerate the values, get their count, add more to the tail of the list and clear all the values (by removing the key). So `MultiValueDictionarySlim<TKey, TValue>` is more comparable to `Dictionary<TKey, Collection<TValue>>`.

* Removal of the key is a relatively slow `O(N)` operation (where `N` is the amount of values corresponding to this key) if `TValue` is of reference type. In this case we have to traverse the linked list of values and assign `null` to values to release the objects referenced. In `Dictionary<TKey, List<TValue>>` the key removal just releases the reference to corresponding `List<TValue>` instance and the rest is done by the GC, no need to assign `null`s.

* Generally `MultiValueDictionarySlim` consumes more memory to store the same data represented as a `Dictionary<TKey, List<TValue>>`.

* The memory allocated to store both key and the corresponding values are not released when key is removed from the `MultiValueDictionarySlim`. This memory will be reused for next inserts of the keys/values, but in the case there will be no more inserts - this memory will remain wasted. Note that this is also the case for ordinary `Dictionary<TKey, TValue>` (key-value entry remains allocated for future inserts), but the situation with `Dictionary<TKey, List<TValue>>` is better - key-value pair removal releases the reference to `List<TValue>` and GC actually reclaims the memory occupied by the values.




`TrimExcessKeys` and 

* Unlike with `Dictionary<TKey, List<TValue>>` there is no way to get a reference to the collection values for some specific key and use it separately - as a standalone object. This can be quite useful sometimes, but with this design it's hard for the owner multi-map to enforce any kind of logical/performance guarantees (especially if values `List<TValue>` is mutable).  

* The `MultiValueDictionarySlim` data structure do not support keys without the corresponding values. This is possible in `Dictionary<TKey, List<TValue>>` by having a key corresponding to empty `List<TValue>` instance - this may be useful sometimes, but generally it's not a good state of multi-map to support.





* value capacity can be bigger - list<T> references are just removed

* size can be bigger due to indexes array
* no by index access
* no keys w/o items
* no api for value removal (to avoid O(n) search)
* sometimes can take more memory since it expands based on the amount of ALL values, not values for some keys
* enumeration order is preserved when you only add (and possibly removes)
* implementation is based on .NET Core's Dictionary<,> implementation, may be suboptimal for .NET Framework
* integers rather than pointers are 1) smaller and 2) nicer for GC
* memory is not fried for removed keys
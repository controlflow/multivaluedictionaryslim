* value capacity can be bigger - list<T> references are just removed
* removal can be slow - O(n)
* size can be bigger due to indexes array
* no by index access
* no keys w/o items
* no api for value removal (to avoid O(n) search)
* sometimes can take more memory since it expands based on the amount of ALL values, not values for some keys
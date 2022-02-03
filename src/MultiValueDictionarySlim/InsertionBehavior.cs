namespace System.Collections.Generic;

/// <summary>
/// Used internally to control behavior of insertion into a <see cref="StdDictionary{TKey,TValue}"/> or <see cref="HashSet{T}"/>.
/// </summary>
internal enum InsertionBehavior : byte
{
  /// <summary>
  /// The default insertion behavior.
  /// </summary>
  None = 0,
 
  /// <summary>
  /// Specifies that an existing entry with the same key should be overwritten if encountered.
  /// </summary>
  OverwriteExisting = 1,
 
  /// <summary>
  /// Specifies that if an existing entry with the same key is encountered, an exception should be thrown.
  /// </summary>
  ThrowOnExisting = 2
}
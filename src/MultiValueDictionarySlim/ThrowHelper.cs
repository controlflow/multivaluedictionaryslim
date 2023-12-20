using System.Diagnostics.CodeAnalysis;

namespace System.Collections.Generic
{
  public class ThrowHelper
  {
    [DoesNotReturn]
    public static void ThrowArgumentOutOfRangeException(string parameterName)
    {
      throw new ArgumentOutOfRangeException(parameterName);
    }

    [DoesNotReturn]

    public static void ThrowArgumentNullException(string parameterName)
    {
      throw new ArgumentNullException();
    }

    [DoesNotReturn]
    public static void ThrowKeyNotFoundException<TKey>(TKey key) where TKey : notnull
    {
      throw new KeyNotFoundException(key.ToString());
    }

    [DoesNotReturn]
    public static void ThrowIndexArgumentOutOfRange_NeedNonNegNumException()
    {
      throw new IndexOutOfRangeException();
    }

    [DoesNotReturn]
    public static void ThrowInvalidOperationException_ConcurrentOperationsNotSupported()
    {
      throw new ArgumentException();
    }

    [DoesNotReturn]
    public static void ThrowArgumentException(string k)
    {
      throw new ArgumentNullException();
    }

    [DoesNotReturn]
    public static void ThrowArgumentException_Argument_InvalidArrayType()
    {
      throw new ArgumentException();
    }


    public static void IfNullAndNullsAreIllegalThenThrow<TValue>(object? value, string k)
    {
      //throw new NotImplementedException();
    }

    [DoesNotReturn]
    public static void ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion()
    {
      throw new InvalidOperationException();
    }

    [DoesNotReturn]
    public static void ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen()
    {
      throw new NotImplementedException();
    }

    [DoesNotReturn]
    public static void ThrowNotSupportedException(string k)
    {
      throw new NotImplementedException();
    }

    [DoesNotReturn]
    public static void ThrowWrongKeyTypeArgumentException(object key, Type type)
    {
      throw new NotImplementedException();
    }

    [DoesNotReturn]
    public static void ThrowWrongValueTypeArgumentException(object? value, Type type)
    {
      throw new NotImplementedException();
    }

    [DoesNotReturn]
    public static void ThrowAddingDuplicateWithKeyArgumentException<TKey>(TKey key)
      where TKey : notnull
    {
      throw new NotImplementedException();
    }
  }

  public class ExceptionArgument
  {
    public const string array = nameof(array);
    public const string key = nameof(key);
    public const string value = nameof(value);
    public const string capacity = nameof(capacity);
    public const string dictionary = nameof(dictionary);
    public const string collection = nameof(collection);
  }

  public class ExceptionResource
  {
    public const string Arg_RankMultiDimNotSupported = "Arg_RankMultiDimNotSupported";
    public const string NotSupported_KeyCollectionSet = "NotSupported_KeyCollectionSet";
    public const string NotSupported_ValueCollectionSet = "NotSupported_ValueCollectionSet";
    public const string Arg_ArrayPlusOffTooSmall = "Arg_ArrayPlusOffTooSmall";
    public const string Arg_NonZeroLowerBound = "Arg_NonZeroLowerBound";
  }
}

#if NETFRAMEWORK

namespace System.Diagnostics.CodeAnalysis
{
  /// <summary>Specifies that a method that will never return under any circumstance.</summary>
  [AttributeUsage(AttributeTargets.Method, Inherited = false)]
  public class DoesNotReturnAttribute : Attribute;
}

#endif

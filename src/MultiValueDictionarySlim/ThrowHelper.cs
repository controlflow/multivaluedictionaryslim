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
      throw new ArgumentNullException(parameterName);
    }

    [DoesNotReturn]
    public static void ThrowInvalidOperationException_ConcurrentOperationsNotSupported()
    {
      throw new ArgumentException();
    }

    [DoesNotReturn]
    public static void ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion()
    {
      throw new InvalidOperationException();
    }
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

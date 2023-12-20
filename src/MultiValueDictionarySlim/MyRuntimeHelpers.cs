#if NETCOREAPP
using System.Runtime.CompilerServices;
#else
using System;
using System.Reflection;
#endif

namespace ControlFlow.Collections;

internal static class MyRuntimeHelpers
{
#if NETCOREAPP

  public static bool IsReferenceOrContainsReferences<T>()
  {
    return RuntimeHelpers.IsReferenceOrContainsReferences<T>();
  }

#else

  public static bool IsReferenceOrContainsReferences<T>()
  {
    return ReflectionCache<T>.IsReferenceOrContainsReferences;
  }

  private static class ReflectionCache<T>
  {
    public static readonly bool IsReferenceOrContainsReferences = ContainsReference(typeof(T));

    private static bool ContainsReference(Type type)
    {
      if (type.IsClass || type.IsInterface || type.IsArray)
        return true;

      if (type.IsEnum || type.IsPrimitive)
        return false;

      foreach (var fieldInfo in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
      {
        if (ContainsReference(fieldInfo.FieldType))
          return true;
      }

      return false;
    }
  }

#endif
}
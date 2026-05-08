using System;
using System.Collections.Generic;
using System.Reflection;

namespace SolastaUnfinishedBusiness.Api.ModKit;

internal static class AssemblyTypeCache
{
    private static readonly Dictionary<Assembly, Type[]> TypesByAssembly = new();

    internal static Type[] GetTypes(Assembly assembly)
    {
        lock (TypesByAssembly)
        {
            if (TypesByAssembly.TryGetValue(assembly, out var types))
            {
                return types;
            }

            types = assembly.GetTypes();
            TypesByAssembly[assembly] = types;

            return types;
        }
    }
}

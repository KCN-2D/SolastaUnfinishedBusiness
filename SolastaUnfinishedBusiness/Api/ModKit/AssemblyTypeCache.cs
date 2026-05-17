using System;
using System.Collections.Generic;
using System.Linq;
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

            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type != null).ToArray();
            }

            TypesByAssembly[assembly] = types;

            return types;
        }
    }
}

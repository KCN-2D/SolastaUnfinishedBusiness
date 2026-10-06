using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class UserContentPatcher
{
    [HarmonyPatch]
    [UsedImplicitly]
    public static class DatabaseLookups_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var gameTypes = typeof(UserContent).Assembly.GetTypes();
            var availabilityChecks = gameTypes
                .Where(type => typeof(UserContent).IsAssignableFrom(type) && !type.ContainsGenericParameters)
                .Select(type => type.GetMethod(nameof(UserContent.IsValidForContentPacks),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, [typeof(string).MakeByRefType(), typeof(ContentPackDefinition).MakeByRefType()], null))
                .Where(method => method != null && !method.IsAbstract && method.GetMethodBody() != null);

            // Payload containers such as rooms and props do not inherit UserContent.
            // Their common serialized-load contract is PostLoadJson, not a list of types.
            var ownedLoadMethods = typeof(UserContentPatcher).Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(UserContentPatcher).Namespace)
                .SelectMany(type => type.GetCustomAttributes<HarmonyPatch>(false))
                .Where(attribute => attribute.info.declaringType != null &&
                                    attribute.info.methodName == nameof(UserLocation.PostLoadJson))
                .Select(attribute => AccessTools.Method(attribute.info.declaringType, attribute.info.methodName,
                    attribute.info.argumentTypes))
                .ToHashSet();
            var payloadChecks = gameTypes
                .Where(type => type.IsSerializable && !type.ContainsGenericParameters)
                .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                                   BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(method => (method.Name == nameof(UserLocation.PostLoadJson) ||
                                  method.Name == nameof(UserLocation.CheckGadgetReferences)) &&
                                 !method.IsAbstract && method.GetMethodBody() != null &&
                                 !ownedLoadMethods.Contains(method));

            return availabilityChecks.Concat(payloadChecks);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return instructions.ReplaceDatabaseLookups();
        }
    }
}

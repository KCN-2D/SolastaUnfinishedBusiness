using System;
using System.Collections.Generic;
using System.Threading;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;

// ReSharper disable once CheckNamespace
internal class RulesetEffectSpellWithOrigin : RulesetEffectSpell
{
    [ThreadStatic]
    private static PendingOrigin _pendingOrigin;

    private static readonly Dictionary<ulong, List<VocalOrigin>> VocalOrigins = [];
    private static readonly object VocalOriginsLock = new();
    private static long _nextVocalOriginToken;

    [UsedImplicitly]
    public RulesetEffectSpellWithOrigin()
    {
    }

    private RulesetEffectSpellWithOrigin(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        int effectSlotLevel,
        PendingOrigin origin)
        : base(caster, repertoire, spellDefinition, effectSlotLevel)
    {
        SetOrigin(origin);
    }

    private RulesetEffectSpellWithOrigin(
        RulesetCharacter caster,
        RulesetItemDevice originItem,
        SpellDefinition spellDefinition,
        int effectSlotLevel,
        PendingOrigin origin)
        : base(caster, originItem, spellDefinition, effectSlotLevel - spellDefinition.SpellLevel)
    {
        SetOrigin(origin);
    }

    private void SetOrigin(PendingOrigin origin)
    {
        OriginatingSpell = origin.OriginatingSpell;
        ResourceSlotLevel = origin.ResourceSlotLevel;
        BypassComponentsAndCastingTime = origin.BypassComponentsAndCastingTime;
        Mode = origin.Mode;
    }

    internal SpellDefinition OriginatingSpell { get; private set; }

    internal int ResourceSlotLevel { get; private set; }

    internal bool BypassComponentsAndCastingTime { get; private set; }

    internal OriginMode Mode { get; private set; }

    internal static IDisposable UseOrigin(
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition selectedSpell,
        int effectSlotLevel,
        SpellDefinition originatingSpell,
        int resourceSlotLevel,
        bool bypassComponentsAndCastingTime,
        OriginMode mode)
    {
        var previous = _pendingOrigin;

        _pendingOrigin = new PendingOrigin(
            caster.Guid,
            repertoire,
            selectedSpell,
            effectSlotLevel,
            originatingSpell,
            resourceSlotLevel,
            bypassComponentsAndCastingTime,
            mode);

        return new PendingOriginScope(previous);
    }

    internal static IDisposable UseDeviceOrigin(
        RulesetItemDevice originItem,
        RulesetDeviceFunction deviceFunction,
        int addedCharges,
        int subSpellIndex)
    {
        var pending = _pendingOrigin;

        if (pending == null)
        {
            return EmptyScope.Instance;
        }

        var scope = new PendingDeviceOriginScope(pending);

        pending.OriginItem = originItem;
        pending.DeviceFunction = deviceFunction;
        pending.AddedCharges = addedCharges;
        pending.SubSpellIndex = subSpellIndex;

        return scope;
    }

    internal static bool TryInstantiate(
        RulesetImplementationManagerLocation manager,
        ref RulesetEffectSpell result,
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spellDefinition,
        int slotLevel,
        bool delayRegistration)
    {
        var pending = _pendingOrigin;

        if (pending == null ||
            pending.CasterGuid != caster.Guid ||
            pending.Repertoire != repertoire ||
            pending.SelectedSpell != spellDefinition ||
            pending.EffectSlotLevel != slotLevel)
        {
            return true;
        }

        _pendingOrigin = null;
        result = new RulesetEffectSpellWithOrigin(caster, repertoire, spellDefinition, slotLevel, pending);
        manager.HandleEffectRegistration(result, delayRegistration);

        return false;
    }

    internal static bool TryInstantiateDevice(
        RulesetImplementationManagerLocation manager,
        ref RulesetEffect result,
        RulesetCharacter caster,
        RulesetItemDevice originItem,
        RulesetDeviceFunction deviceFunction,
        int addedCharges,
        int subSpellIndex,
        bool delayRegistration)
    {
        var pending = _pendingOrigin;
        var originatingSpell = deviceFunction?.DeviceFunctionDescription?.SpellDefinition;

        if (caster == null ||
            pending == null ||
            pending.CasterGuid != caster.Guid ||
            pending.Repertoire != null ||
            pending.OriginatingSpell != originatingSpell ||
            pending.Mode == OriginMode.None ||
            originItem == null ||
            !ReferenceEquals(pending.OriginItem, originItem) ||
            !ReferenceEquals(pending.DeviceFunction, deviceFunction) ||
            pending.AddedCharges != addedCharges ||
            pending.SubSpellIndex != subSpellIndex ||
            !pending.SelectedSpell ||
            pending.EffectSlotLevel < pending.SelectedSpell.SpellLevel)
        {
            return true;
        }

        _pendingOrigin = null;
        result = new RulesetEffectSpellWithOrigin(
            caster,
            originItem,
            pending.SelectedSpell,
            pending.EffectSlotLevel,
            pending);
        manager.HandleEffectRegistration(result, delayRegistration);

        return false;
    }

    internal static bool IsPendingOrigin(
        RulesetCharacter caster,
        SpellDefinition spellDefinition,
        bool requireBypass = true)
    {
        var pending = _pendingOrigin;

        return pending != null &&
               pending.CasterGuid == caster.Guid &&
               pending.SelectedSpell == spellDefinition &&
               (!requireBypass || pending.BypassComponentsAndCastingTime);
    }

    internal static bool TryGetPendingOrigin(
        RulesetCharacter caster,
        SpellDefinition spellDefinition,
        out SpellDefinition originatingSpell,
        out OriginMode mode)
    {
        var pending = _pendingOrigin;

        if (pending == null ||
            caster == null ||
            pending.CasterGuid != caster.Guid ||
            pending.SelectedSpell != spellDefinition)
        {
            originatingSpell = null;
            mode = OriginMode.None;

            return false;
        }

        originatingSpell = pending.OriginatingSpell;
        mode = pending.Mode;

        return originatingSpell != null;
    }

    internal static RuleDefinitions.TargetType GetPendingDeviceTargetType(
        EffectDescription effectDescription)
    {
        var targetType = effectDescription.TargetType;
        var pending = _pendingOrigin;
        var deviceSpell = pending?.DeviceFunction?.DeviceFunctionDescription?.SpellDefinition;

        if (pending?.OriginItem == null ||
            pending.OriginatingSpell != deviceSpell ||
            !pending.SelectedSpell)
        {
            return targetType;
        }

        targetType = pending.SelectedSpell.EffectDescription.TargetType;

        // ItemMenuModal treats Self as "no targeting action" and returns before
        // DeviceFunctionEngaged. The instantiated selected spell still supplies
        // the real Self targeting data to CharacterActionPanel.
        return targetType == RuleDefinitions.TargetType.Self
            ? RuleDefinitions.TargetType.Individuals
            : targetType;
    }

    internal static IDisposable TrackVocalOrigin(
        RulesetCharacter caster,
        RulesetEffectSpell activeSpell)
    {
        if (caster == null)
        {
            return EmptyScope.Instance;
        }

        var token = Interlocked.Increment(ref _nextVocalOriginToken);

        lock (VocalOriginsLock)
        {
            if (!VocalOrigins.TryGetValue(caster.Guid, out var origins))
            {
                origins = [];
                VocalOrigins.Add(caster.Guid, origins);
            }

            var withOrigin = activeSpell as RulesetEffectSpellWithOrigin;

            origins.Add(new VocalOrigin(
                token,
                activeSpell?.SpellDefinition,
                activeSpell == null ? null : GetOriginSpell(activeSpell),
                withOrigin?.Mode ?? OriginMode.None,
                activeSpell?.SpellRepertoire,
                activeSpell?.UsesSpellListClassification() == true));
        }

        return new VocalOriginScope(caster.Guid, token);
    }

    internal static bool TryGetActiveSpellClassification(
        RulesetCharacter caster,
        SpellDefinition spellDefinition,
        out RulesetSpellRepertoire spellRepertoire,
        out bool useSpellListClassification)
    {
        spellRepertoire = null;
        useSpellListClassification = false;

        if (caster == null || spellDefinition == null)
        {
            return false;
        }

        lock (VocalOriginsLock)
        {
            if (TryGetCurrentVocalOrigin(caster.Guid, out var vocalOrigin) &&
                vocalOrigin.SelectedSpell == spellDefinition)
            {
                spellRepertoire = vocalOrigin.SpellRepertoire;
                useSpellListClassification = vocalOrigin.UseSpellListClassification;

                return true;
            }
        }

        var found = false;

        foreach (var activeSpell in caster.SpellsCastByMe)
        {
            if (activeSpell?.SpellDefinition != spellDefinition)
            {
                continue;
            }

            var activeRepertoire = activeSpell.SpellRepertoire;
            var activeUsesSpellList = activeSpell.UsesSpellListClassification();

            if (found &&
                (spellRepertoire != activeRepertoire ||
                 useSpellListClassification != activeUsesSpellList))
            {
                spellRepertoire = null;
                useSpellListClassification = false;

                return false;
            }

            found = true;
            spellRepertoire = activeRepertoire;
            useSpellListClassification = activeUsesSpellList;
        }

        return found;
    }

    internal static bool TryGetVocalOrigin(
        RulesetCharacter caster,
        SpellDefinition spellDefinition,
        out SpellDefinition originatingSpell,
        out OriginMode mode)
    {
        if (caster != null)
        {
            lock (VocalOriginsLock)
            {
                if (TryGetCurrentVocalOrigin(caster.Guid, out var vocalOrigin) &&
                    vocalOrigin.SelectedSpell == spellDefinition)
                {
                    originatingSpell = vocalOrigin.OriginatingSpell;
                    mode = vocalOrigin.Mode;

                    return originatingSpell != null;
                }
            }
        }

        return TryGetPendingOrigin(
            caster,
            spellDefinition,
            out originatingSpell,
            out mode);
    }

    internal static bool TryGetVocalOrigin(
        RulesetCharacter caster,
        string spellName,
        out SpellDefinition selectedSpell,
        out SpellDefinition originatingSpell,
        out OriginMode mode)
    {
        selectedSpell = null;
        originatingSpell = null;
        mode = OriginMode.None;

        if (caster == null || string.IsNullOrEmpty(spellName))
        {
            return false;
        }

        lock (VocalOriginsLock)
        {
            if (!TryGetCurrentVocalOrigin(caster.Guid, out var vocalOrigin) ||
                !string.Equals(
                    vocalOrigin.SelectedSpell?.Name,
                    spellName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            selectedSpell = vocalOrigin.SelectedSpell;
            originatingSpell = vocalOrigin.OriginatingSpell;
            mode = vocalOrigin.Mode;

            return selectedSpell != null &&
                   originatingSpell != null &&
                   mode != OriginMode.None;
        }
    }

    private static bool TryGetCurrentVocalOrigin(ulong casterGuid, out VocalOrigin vocalOrigin)
    {
        if (VocalOrigins.TryGetValue(casterGuid, out var origins) && origins.Count > 0)
        {
            vocalOrigin = origins[origins.Count - 1];

            return true;
        }

        vocalOrigin = null;

        return false;
    }

    internal static SpellDefinition GetOriginSpell(RulesetEffectSpell activeSpell)
    {
        return activeSpell is RulesetEffectSpellWithOrigin withOrigin
            ? withOrigin.OriginatingSpell ?? withOrigin.SpellDefinition
            : activeSpell.SpellDefinition;
    }

    internal static int GetResourceSlotLevel(RulesetEffectSpell activeSpell)
    {
        return activeSpell is RulesetEffectSpellWithOrigin withOrigin && withOrigin.ResourceSlotLevel > 0
            ? withOrigin.ResourceSlotLevel
            : activeSpell.SlotLevel;
    }

    public override void SerializeAttributes(IAttributesSerializer serializer, IVersionProvider versionProvider)
    {
        base.SerializeAttributes(serializer, versionProvider);

        ResourceSlotLevel = serializer.SerializeAttribute("ResourceSlotLevel", ResourceSlotLevel);
        BypassComponentsAndCastingTime =
            serializer.SerializeAttribute("BypassComponentsAndCastingTime", BypassComponentsAndCastingTime);

        var mode = (int)Mode;

        mode = serializer.SerializeAttribute("OriginMode", mode);
        Mode = (OriginMode)mode;
    }

    public override void SerializeElements(IElementsSerializer serializer, IVersionProvider versionProvider)
    {
        base.SerializeElements(serializer, versionProvider);

        OriginatingSpell =
            BaseDefinition.SerializeDatabaseReference(serializer, "OriginatingSpell", OriginatingSpell);
        OriginatingSpell ??= SpellDefinition;

        if (ResourceSlotLevel <= 0)
        {
            ResourceSlotLevel = SlotLevel;
        }
    }

    internal enum OriginMode
    {
        None,
        WishSpellReplication,
        WishAlternateEffect
    }

    private sealed class PendingOrigin(
        ulong casterGuid,
        RulesetSpellRepertoire repertoire,
        SpellDefinition selectedSpell,
        int effectSlotLevel,
        SpellDefinition originatingSpell,
        int resourceSlotLevel,
        bool bypassComponentsAndCastingTime,
        OriginMode mode)
    {
        internal readonly bool BypassComponentsAndCastingTime = bypassComponentsAndCastingTime;
        internal readonly ulong CasterGuid = casterGuid;
        internal readonly int EffectSlotLevel = effectSlotLevel;
        internal readonly OriginMode Mode = mode;
        internal readonly SpellDefinition OriginatingSpell = originatingSpell;
        internal readonly RulesetSpellRepertoire Repertoire = repertoire;
        internal readonly int ResourceSlotLevel = resourceSlotLevel;
        internal readonly SpellDefinition SelectedSpell = selectedSpell;
        internal int AddedCharges;
        internal RulesetDeviceFunction DeviceFunction;
        internal RulesetItemDevice OriginItem;
        internal int SubSpellIndex;
    }

    private sealed class PendingOriginScope(PendingOrigin previous) : IDisposable
    {
        public void Dispose()
        {
            _pendingOrigin = previous;
        }
    }

    private sealed class PendingDeviceOriginScope : IDisposable
    {
        private readonly int _addedCharges;
        private readonly RulesetDeviceFunction _deviceFunction;
        private readonly RulesetItemDevice _originItem;
        private readonly PendingOrigin _pending;
        private readonly int _subSpellIndex;

        internal PendingDeviceOriginScope(PendingOrigin pending)
        {
            _pending = pending;
            _originItem = pending.OriginItem;
            _deviceFunction = pending.DeviceFunction;
            _addedCharges = pending.AddedCharges;
            _subSpellIndex = pending.SubSpellIndex;
        }

        public void Dispose()
        {
            _pending.OriginItem = _originItem;
            _pending.DeviceFunction = _deviceFunction;
            _pending.AddedCharges = _addedCharges;
            _pending.SubSpellIndex = _subSpellIndex;
        }
    }

    private sealed class VocalOrigin(
        long token,
        SpellDefinition selectedSpell,
        SpellDefinition originatingSpell,
        OriginMode mode,
        RulesetSpellRepertoire spellRepertoire,
        bool useSpellListClassification)
    {
        internal readonly OriginMode Mode = mode;
        internal readonly SpellDefinition OriginatingSpell = originatingSpell;
        internal readonly SpellDefinition SelectedSpell = selectedSpell;
        internal readonly RulesetSpellRepertoire SpellRepertoire = spellRepertoire;
        internal readonly long Token = token;
        internal readonly bool UseSpellListClassification = useSpellListClassification;
    }

    private sealed class VocalOriginScope(ulong casterGuid, long token) : IDisposable
    {
        public void Dispose()
        {
            lock (VocalOriginsLock)
            {
                if (!VocalOrigins.TryGetValue(casterGuid, out var origins))
                {
                    return;
                }

                var index = origins.FindIndex(x => x.Token == token);

                if (index >= 0)
                {
                    origins.RemoveAt(index);
                }

                if (origins.Count == 0)
                {
                    VocalOrigins.Remove(casterGuid);
                }
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();

        public void Dispose()
        {
        }
    }
}

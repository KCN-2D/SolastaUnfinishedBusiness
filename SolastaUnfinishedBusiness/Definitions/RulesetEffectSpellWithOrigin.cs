using System;
using System.Collections.Generic;
using System.Threading;
using JetBrains.Annotations;

// ReSharper disable once CheckNamespace
internal class RulesetEffectSpellWithOrigin : RulesetEffectSpell
{
    [ThreadStatic]
    private static PendingOrigin _pendingOrigin;

    private static readonly Dictionary<ulong, VocalOrigin> VocalOrigins = [];
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
            if (activeSpell is RulesetEffectSpellWithOrigin
                {
                    OriginatingSpell: not null,
                    Mode: not OriginMode.None
                } withOrigin)
            {
                VocalOrigins[caster.Guid] = new VocalOrigin(
                    token,
                    withOrigin.SpellDefinition,
                    withOrigin.OriginatingSpell,
                    withOrigin.Mode);
            }
            else
            {
                // Starting any other magic-effect action invalidates an
                // abandoned origin context for this caster.
                VocalOrigins.Remove(caster.Guid);
            }
        }

        return new VocalOriginScope(caster.Guid, token);
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
                if (VocalOrigins.TryGetValue(caster.Guid, out var vocalOrigin) &&
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
            if (!VocalOrigins.TryGetValue(caster.Guid, out var vocalOrigin) ||
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
    }

    private sealed class PendingOriginScope(PendingOrigin previous) : IDisposable
    {
        public void Dispose()
        {
            _pendingOrigin = previous;
        }
    }

    private sealed class VocalOrigin(
        long token,
        SpellDefinition selectedSpell,
        SpellDefinition originatingSpell,
        OriginMode mode)
    {
        internal readonly OriginMode Mode = mode;
        internal readonly SpellDefinition OriginatingSpell = originatingSpell;
        internal readonly SpellDefinition SelectedSpell = selectedSpell;
        internal readonly long Token = token;
    }

    private sealed class VocalOriginScope(ulong casterGuid, long token) : IDisposable
    {
        public void Dispose()
        {
            lock (VocalOriginsLock)
            {
                if (VocalOrigins.TryGetValue(casterGuid, out var vocalOrigin) &&
                    vocalOrigin.Token == token)
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

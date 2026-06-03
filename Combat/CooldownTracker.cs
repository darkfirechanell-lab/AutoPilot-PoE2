using System;
using System.Collections.Generic;

namespace AutoPilot.Combat;

/// <summary>
/// Fonte ÚNICA de verdade para "quando usei esta skill / já posso usar outra vez".
///
/// Resolve o M3 da auditoria: o AutoMyAim tinha três sistemas de cooldown a competir (CooldownManager
/// com Stopwatch, SkillTracker com DateTime, e _lastX espalhados pelas routines). Aqui é um só,
/// por chave de string, em milissegundos de relógio real.
/// </summary>
public sealed class CooldownTracker
{
    private readonly Dictionary<string, long> _lastUse = new(StringComparer.Ordinal);

    /// <summary>Marca a skill/ação como usada agora.</summary>
    public void Mark(string key) => _lastUse[key] = DateTime.UtcNow.Ticks;

    /// <summary>True se já passaram <paramref name="cooldownMs"/> desde o último uso (ou nunca usada).</summary>
    public bool Ready(string key, int cooldownMs)
    {
        if (!_lastUse.TryGetValue(key, out var last)) return true;
        var elapsed = (DateTime.UtcNow.Ticks - last) / TimeSpan.TicksPerMillisecond;
        return elapsed >= cooldownMs;
    }

    /// <summary>Ms desde o último uso, ou long.MaxValue se nunca usada.</summary>
    public long SinceMs(string key)
    {
        if (!_lastUse.TryGetValue(key, out var last)) return long.MaxValue;
        return (DateTime.UtcNow.Ticks - last) / TimeSpan.TicksPerMillisecond;
    }

    public void Clear() => _lastUse.Clear();
}

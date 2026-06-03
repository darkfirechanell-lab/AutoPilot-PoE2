using System;
using System.Linq;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;

namespace AutoPilot.Combat;

/// <summary>
/// Leitura de buffs/debuffs de uma entidade (jogador ou alvo), de forma limpa e tolerante a falhas.
///
/// No AutoMyAim, cada routine reimplementava "tem este buff?" / "quantas charges?" com try/catch
/// próprios — código duplicado e fácil de divergir. Aqui está num só sítio. Métodos estáticos
/// porque não há estado: é só ler a memória da entidade no momento.
///
/// Convenção de retorno das charges: ausente = 0; ilegível = -1 (a routine decide o que fazer com
/// cada caso — ex.: Salvo não arranca com leitura ilegível, mas trata ausente como 0 seals).
/// </summary>
public static class BuffReader
{
    /// <summary>True se a entidade tem um buff/debuff com este nome interno (case-insensitive).</summary>
    public static bool Has(Entity entity, string buffName)
    {
        if (entity == null || string.IsNullOrEmpty(buffName)) return false;
        try
        {
            if (entity.TryGetComponent<Buffs>(out var buffs) && buffs?.BuffsList != null)
                return buffs.BuffsList.Any(b =>
                    string.Equals(b?.Name, buffName, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return false;
    }

    /// <summary>True se a entidade tem um buff cujo nome CONTÉM o fragmento (ex.: "frozen").</summary>
    public static bool HasContaining(Entity entity, string fragment)
    {
        if (entity == null || string.IsNullOrEmpty(fragment)) return false;
        try
        {
            if (entity.TryGetComponent<Buffs>(out var buffs) && buffs?.BuffsList != null)
                return buffs.BuffsList.Any(b =>
                    b?.Name?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true);
        }
        catch { }
        return false;
    }

    /// <summary>Número de charges de um buff. Ausente = 0; ilegível = -1.</summary>
    public static int Charges(Entity entity, string buffName)
    {
        if (entity == null || string.IsNullOrEmpty(buffName)) return -1;
        try
        {
            if (!entity.TryGetComponent<Buffs>(out var buffs) || buffs?.BuffsList == null)
                return -1;

            var buff = buffs.BuffsList.FirstOrDefault(b =>
                string.Equals(b?.Name, buffName, StringComparison.OrdinalIgnoreCase));
            return buff?.BuffCharges ?? 0;
        }
        catch { return -1; }
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AutoPilot.Combat.General;

/// <summary>
/// M2: cache de regex compilados por PADRÃO (string). O gate de mod do RuleEvaluator (SkillRule.
/// TargetMatchesMod) precisa de um Regex, mas a regra guarda só o texto; compilar a cada tick seria
/// caro. Aqui compila-se UMA vez por padrão e reusa-se. Padrão inválido → regex que nunca casa (não
/// rebenta o combate).
/// </summary>
internal static class ModRegexCache
{
    private static readonly Dictionary<string, Regex> _cache = new();
    private static readonly Regex _never = new("(?!)", RegexOptions.Compiled); // nunca casa

    public static Regex Get(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return _never;
        if (_cache.TryGetValue(pattern, out var rx)) return rx;
        try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
        catch { rx = _never; }
        _cache[pattern] = rx;
        return rx;
    }
}

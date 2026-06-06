using System.Text.RegularExpressions;

namespace AutoPilot.Combat.General;

/// <summary>
/// M1 (plano dos mods): tipo ÚNICO de regra de mod, partilhado pelas 3 features (targeting, gate da
/// rotação, dodge). Um ModRule = { Regex que casa o nome interno do mod, Rótulo legível, Peso }.
///
/// O mesmo avaliador (<see cref="Matches"/>) serve os 3 consumidores — não há 3 modelos diferentes
/// (decisão da auditoria). O regex é compilado UMA vez (case-insensitive); regex inválido = nunca casa
/// e o rótulo guarda o erro (como o RareModScanner), em vez de rebentar.
///
/// O significado do Peso depende do consumidor:
///   • targeting: + prioriza o alvo, − desprioriza (com limite p/ não dominar boss>rare>lixo);
///   • dodge: multiplicador de perigo de um mob que JÁ está a agir (amplifica o evento, não o cria).
/// </summary>
public sealed class ModRule
{
    /// <summary>Padrão regex que casa o nome interno do mod (ex.: <c>CorpseExploder|Volatile</c>).</summary>
    public string Pattern { get; }

    /// <summary>Rótulo legível para UI/log (ex.: "Explode cadáveres"). Se o regex for inválido, guarda o erro.</summary>
    public string Label { get; }

    /// <summary>Peso/multiplicador (significado conforme o consumidor — ver doc da classe).</summary>
    public float Weight { get; }

    private readonly Regex _regex;       // null se o pattern for inválido
    public bool IsValid => _regex != null;

    public ModRule(string pattern, string label, float weight)
    {
        Pattern = pattern ?? "";
        Weight = weight;
        try
        {
            _regex = new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            Label = string.IsNullOrWhiteSpace(label) ? Pattern : label;
        }
        catch (System.Exception e)
        {
            _regex = null; // inválido → nunca casa
            Label = $"[regex inválido: {e.Message}]";
        }
    }

    /// <summary>True se algum mod da entidade casa este regex (via cache do ModReader). Defensivo.</summary>
    public bool Matches(ExileCore2.PoEMemory.MemoryObjects.Entity entity)
        => _regex != null && ModReader.HasModMatching(entity, _regex);
}

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AutoPilot.Combat.General;

/// <summary>
/// Guarda/carrega PERFIS de configuração com nome, para o utilizador trocar de build sem reconfigurar.
/// Um perfil é um ficheiro JSON na pasta de perfis do AutoPilot. Guarda: as regras [Geral] de cada
/// skill (por NOME de memória, para resistir à auto-deteção variar entre builds) + os settings gerais
/// relevantes (Attack Range, Kiting, C1, etc.).
///
/// As skills NÃO são identificadas por posição (a barra varia) mas por nome — ao carregar, aplica-se a
/// cada skill da build atual a regra do perfil com o mesmo nome; skills sem correspondência ficam como
/// estão. Isto torna os perfis robustos entre builds diferentes.
/// </summary>
public sealed class ProfileManager
{
    // ADAPTATIVO: o caminho é dado pelo plugin (ConfigDirectory do ExileCore) — não hard-coded. Assim
    // funciona onde quer que o ExileCore esteja instalado, e para qualquer utilizador.
    private readonly string Dir;

    /// <summary>Pasta onde os perfis (.json) vivem. Usada pelo botão "Abrir pasta" da UI.</summary>
    public string Folder => Dir;

    public string LastMessage { get; private set; } = "";

    /// <param name="configDir">ConfigDirectory do plugin (adaptativo). Os perfis ficam numa subpasta.</param>
    public ProfileManager(string configDir)
    {
        Dir = Path.Combine(configDir ?? ".", "AutoPilot_Profiles");
        try { Directory.CreateDirectory(Dir); } catch { }
    }

    /// <summary>True se já existe um perfil com este nome.</summary>
    public bool Exists(string name)
    {
        try { return File.Exists(PathFor(name)); } catch { return false; }
    }

    /// <summary>Apaga o ficheiro de um perfil. Devolve true em sucesso.</summary>
    public bool Delete(string name)
    {
        try
        {
            var path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
            LastMessage = $"Perfil '{name}' apagado.";
            return true;
        }
        catch (Exception e)
        {
            LastMessage = $"Erro a apagar '{name}': {e.Message}";
            return false;
        }
    }

    /// <summary>Lista os nomes dos perfis guardados (ficheiros .json na pasta de perfis).</summary>
    public List<string> ListProfiles()
    {
        var names = new List<string>();
        try
        {
            if (Directory.Exists(Dir))
                foreach (var f in Directory.GetFiles(Dir, "*.json"))
                    names.Add(Path.GetFileNameWithoutExtension(f));
        }
        catch { }
        names.Sort();
        return names;
    }

    /// <summary>Caminho do ficheiro de um perfil (sanitiza o nome para nome de ficheiro válido).</summary>
    private string PathFor(string name)
    {
        var safe = name;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(safe)) safe = "perfil";
        return Path.Combine(Dir, safe + ".json");
    }

    /// <summary>Guarda um perfil em disco. Devolve true em sucesso.</summary>
    public bool Save(string name, ProfileData data)
    {
        try
        {
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(PathFor(name), json);
            LastMessage = $"Perfil '{name}' guardado ({data.Skills.Count} skills).";
            return true;
        }
        catch (Exception e)
        {
            LastMessage = $"Erro a guardar '{name}': {e.Message}";
            return false;
        }
    }

    /// <summary>Carrega um perfil de disco, ou null se não existe / erro.</summary>
    public ProfileData Load(string name)
    {
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path)) { LastMessage = $"Perfil '{name}' não existe."; return null; }
            var data = JsonConvert.DeserializeObject<ProfileData>(File.ReadAllText(path));
            LastMessage = data == null ? $"Perfil '{name}' ilegível." : $"Perfil '{name}' carregado.";
            return data;
        }
        catch (Exception e)
        {
            LastMessage = $"Erro a carregar '{name}': {e.Message}";
            return null;
        }
    }
}

/// <summary>Dados serializáveis de um perfil: settings gerais + regras de skill por nome.</summary>
public sealed class ProfileData
{
    // Settings gerais.
    public float AttackRange { get; set; } = 100f;
    public float ProximalRange { get; set; } = 25f;
    public float CursorJitter { get; set; } = 0f;
    public bool RequireCursorOnTarget { get; set; }
    public float CursorOnTargetTolerance { get; set; } = 35f;
    public bool UseVisibility { get; set; } = true;
    public bool PauseOnPanels { get; set; } = true;

    // Kiting.
    public bool UseDodge { get; set; }
    public float DangerRange { get; set; } = 25f;
    public float DangerThreshold { get; set; } = 3f;
    public int DodgeCooldownMs { get; set; } = 1500;

    // Motor Geral.
    public bool GeneralUseUiRules { get; set; }
    public string Routine { get; set; } = "Ice Shot";

    // Regras [Geral] de cada skill, por nome de memória.
    public List<ProfileSkill> Skills { get; set; } = new();
}

/// <summary>As regras [Geral] de uma skill, mais o que da config básica faz sentido guardar.</summary>
public sealed class ProfileSkill
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public int TapHoldMs { get; set; } = 12;

    public string UseType { get; set; } = "Tap";
    public int CooldownMs { get; set; }
    public bool AttackInPlace { get; set; }
    public string MinRarity { get; set; } = "Qualquer";
    public string MinHardness { get; set; } = "Easy";
    public bool IgnoreRangeForUnique { get; set; }
    public float MinDistance { get; set; }
    public float MaxDistance { get; set; }
    public float TargetHpMin { get; set; }
    public float TargetHpMax { get; set; } = 1f;
    public int CloseTargets { get; set; }
    public float CloseTargetsRange { get; set; } = 10f;
    public string GroundEntityPath { get; set; } = "";
    public bool SkipIfGroundActive { get; set; }
    public string TargetHasBuff { get; set; } = "";
    public string TargetMissingBuff { get; set; } = "";
    public string PlayerHasBuff { get; set; } = "";
    public string PlayerMissingBuff { get; set; } = "";
    public bool BossIgnoresPlayerMissingBuff { get; set; }
    public string ChargeBuff { get; set; } = "";
    public int ChargeMin { get; set; }
    public string AfterSkill { get; set; } = "";
    public int AfterSkillDelayMs { get; set; }
    public string ReleaseWhen { get; set; } = "Timeout";
    public string ReleaseBuffName { get; set; } = "";
    public int ReleaseAnimationStage { get; set; }
    public int ReleaseTimeoutMs { get; set; } = 500;
}

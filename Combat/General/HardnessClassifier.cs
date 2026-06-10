using System;
using System.Collections.Generic;
using AutoPilot.Detection;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Combat.General;

/// <summary>Nível de dureza de um alvo. ORDENADO: o gate usa "&gt;=" (Medium apanha Medium E Tank).</summary>
public enum TargetHardness { Easy = 0, Medium = 1, Tank = 2 }

/// <summary>
/// HP_ROTATION / Fase H1 — classifica a DUREZA de um mob pela sua vida efetiva RELATIVA à zona.
///
/// score = (MaxHP+MaxES) ÷ mediana-de-pool-dos-Rares-da-zona + ajuste de mods. 2 limiares → 3 níveis
/// (Easy/Medium/Tank). Auto-calibra por área (a mediana sobe com o tier). Cold-start (poucos Rares
/// amostrados) → mediana SINTÉTICA na mesma unidade (danoPorIceShot × tiros), para um só par de limiares
/// servir as duas vias. Ver HP_ROTATION_PLANO.md (15 decisões) e a auditoria.
///
/// FASE H1: SÓ classifica e loga — NÃO age. O gate por skill (RuleEvaluator) e o latch entram em H4.
///
/// CUSTO (anti-lag): a classificação corre 1×/tick só no alvo atual (não nos 40 mobs). A pool reusa o
/// <see cref="LifeCache"/> PARTILHADO (zero 2ª leitura). A mediana é um valor CACHEADO por área, recalc
/// no máximo 1×/seg e só quando a amostra mudou (dirty) — ler é O(1); ordenar 30 floats só no recalc raro.
/// </summary>
public sealed class HardnessClassifier
{
    // ── Config (sliders do utilizador; defaults provisórios afinados em teste) ──────────────
    public float LimiarTank { get; set; } = 2.5f;          // score >= isto → Tank
    public float LimiarMedium { get; set; } = 1.5f;        // score >= isto → Medium
    public int MinAmostras { get; set; } = 8;              // < isto numa área → cold-start (mediana sintética)
    public float DanoPorIceShot { get; set; } = 800f;      // mediana sintética = isto × TirosNumRareMediano
    public float TirosNumRareMediano { get; set; } = 12f;
    public float AjusteModPorMatch { get; set; } = 0.5f;   // +score por mod "chato" que o alvo tenha

    // Mods que tornam um mob mais chato de matar (somam ao score). Nomes internos confirmados no dump M0.
    private static readonly System.Text.RegularExpressions.Regex AnnoyingMods =
        new("RevivesMinions|LifeRegeneration|HealingNova|ReducedDamageTaken|PhysImmune|ResistAll",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Mods de INFLAÇÃO de vida — Rares com estes são EXCLUÍDOS da amostra da mediana (senão sobem a
    // mediana e fazem os normais parecer fracos). Decisão #14.
    private static readonly System.Text.RegularExpressions.Regex InflationMods =
        new("ExtraEnergyShield|EnergyShieldAura|LifeRegeneration",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private const int Window = 30;          // amostra por área (janela deslizante).
    private const long MedianTtlMs = 1000;  // recalc da mediana no máximo 1×/seg.

    private readonly LifeCache _life;

    // Amostra por área: array circular de pools de Rares (sem RingBuffer genérico — array inline).
    private sealed class Sample
    {
        public readonly float[] Pools = new float[Window];
        public int Count;          // quantos válidos (até Window)
        public int Next;           // próximo índice a escrever (circular)
        public bool Dirty = true;  // amostra mudou desde o último recalc da mediana
        public float Median;       // valor cacheado
        public long MedianAtTicks;
    }

    private readonly Dictionary<int, Sample> _byArea = new();   // areaLevel → amostra
    private readonly HashSet<uint> _sampledIds = new();         // dedup: cada mob entra na amostra 1×

    // Diagnóstico do último alvo classificado (para o log de H1).
    public string LastDebug { get; private set; } = "";

    public HardnessClassifier(LifeCache life) => _life = life;

    /// <summary>
    /// Observa um alvo (alimenta a amostra) E classifica-o. H1: o resultado é só logado.
    /// Chamar 1×/tick para o alvo atual. <paramref name="areaLevel"/> lido 1×/tick no plugin.
    /// </summary>
    public TargetHardness Classify(Entity entity, MonsterRarity rarity, int areaLevel, long nowTicks)
    {
        if (entity == null) { LastDebug = "dureza: sem alvo"; return TargetHardness.Easy; }

        // White morre logo: Easy sem medir (performance, #6). Unique = combo sempre (Tank, #7).
        if (rarity == MonsterRarity.White) { LastDebug = "dureza: White→Easy"; return TargetHardness.Easy; }
        if (rarity == MonsterRarity.Unique) { LastDebug = "dureza: Unique→Tank"; return TargetHardness.Tank; }

        if (!_life.TryGetPool(entity, nowTicks, out var pool, out var maxHp, out var maxEs))
        { LastDebug = "dureza: pool ilegível→Easy"; return TargetHardness.Easy; }

        // Alimenta a amostra (só Rares, 1× por id, sem mods de inflação). #11/#14 + dedup da auditoria.
        var coldStart = AreaCount(areaLevel) < MinAmostras;
        var sampled = false;
        if (rarity == MonsterRarity.Rare && _sampledIds.Add(entity.Id))
        {
            if (HasInflation(entity)) { /* excluído da mediana (#14) */ }
            else { Insert(areaLevel, pool, nowTicks); sampled = true; }
        }

        var median = MedianFor(areaLevel, coldStart, nowTicks);
        if (median <= 0f) { LastDebug = "dureza: mediana 0→Easy"; return TargetHardness.Easy; }

        var modAdj = HasAnnoying(entity) ? AjusteModPorMatch : 0f;
        var score = pool / median + modAdj;

        var level = score >= LimiarTank ? TargetHardness.Tank
                  : score >= LimiarMedium ? TargetHardness.Medium
                  : TargetHardness.Easy;

        // Magic no cold-start: limitado a MEDIUM (nunca Tank com baseline não-fiável). Decisão #5.
        if (coldStart && rarity == MonsterRarity.Magic && level == TargetHardness.Tank)
            level = TargetHardness.Medium;

        LastDebug = $"dureza: {rarity} hp={maxHp:F0} es={maxEs:F0} pool={pool:F0} " +
                    $"med={median:F0}{(coldStart ? "(sint)" : "")} score={score:F2} adj={modAdj:F1} → {level} " +
                    $"[T>={LimiarTank} M>={LimiarMedium} amostra={AreaCount(areaLevel)}{(sampled ? " +amostrei" : "")}]";
        return level;
    }

    // ── Amostra / mediana ──────────────────────────────────────────────────────────────────

    private int AreaCount(int areaLevel) => _byArea.TryGetValue(areaLevel, out var s) ? s.Count : 0;

    private void Insert(int areaLevel, float pool, long nowTicks)
    {
        if (!_byArea.TryGetValue(areaLevel, out var s)) { s = new Sample(); _byArea[areaLevel] = s; }
        s.Pools[s.Next] = pool;
        s.Next = (s.Next + 1) % Window;
        if (s.Count < Window) s.Count++;
        s.Dirty = true;
    }

    private float MedianFor(int areaLevel, bool coldStart, long nowTicks)
    {
        // Cold-start: mediana SINTÉTICA (mesma unidade da real → um só par de limiares). #13.
        if (coldStart) return DanoPorIceShot * TirosNumRareMediano;

        var s = _byArea[areaLevel]; // existe (coldStart=false ⇒ Count>=MinAmostras)
        if (!s.Dirty && (nowTicks - s.MedianAtTicks) / TimeSpan.TicksPerMillisecond <= MedianTtlMs)
            return s.Median; // valor cacheado (caminho quente).

        // Recalc raro: copia os válidos, ordena, tira a mediana. 30 floats = trivial.
        var n = s.Count;
        var buf = new float[n];
        Array.Copy(s.Pools, buf, n);
        Array.Sort(buf);
        s.Median = (n % 2 == 1) ? buf[n / 2] : 0.5f * (buf[n / 2 - 1] + buf[n / 2]);
        s.MedianAtTicks = nowTicks;
        s.Dirty = false;
        return s.Median;
    }

    private static bool HasInflation(Entity e) => Combat.ModReader.HasModMatching(e, InflationMods);
    private static bool HasAnnoying(Entity e) => Combat.ModReader.HasModMatching(e, AnnoyingMods);

    /// <summary>
    /// Mudança de ÁREA: limpa só o dedup de ids (os ids reciclam entre instâncias, e um id reusado não
    /// deve bloquear a amostragem do novo mob). A baseline por área (<see cref="_byArea"/>) MANTÉM-SE —
    /// acumula por area level na sessão (#12): um corredor de T15 herda o que aprendeu de T15 anteriores.
    /// </summary>
    public void OnAreaChange() => _sampledIds.Clear();

    /// <summary>Reset TOTAL (fim de sessão/plugin desligado): limpa amostra E dedup.</summary>
    public void Reset()
    {
        _byArea.Clear();
        _sampledIds.Clear();
        LastDebug = "";
    }
}

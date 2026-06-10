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
    public int MinAmostras { get; set; } = 8;              // < isto numa área → cold-start (estimativa por nível)
    public float ColdStartFactor { get; set; } = 1.0f;     // ajuste à estimativa (cobre juice/ES que a fórmula não vê)
    public float AjusteModPorMatch { get; set; } = 0.5f;   // +score por mod "chato" que o alvo tenha

    // Vida-BASE de monstro por nível de área (PoE2, fonte poe2db.tw). Pontos-âncora; interpola linear.
    // A pool de um RARE típico ≈ baseLife(nível) × 8 (Rare = +700% vida). Validado: nível 80 ≈ 31065×8
    // = 248k vs ~291k medido (resto = mods/ES, coberto por ColdStartFactor).
    // LIMITE (auditoria vuln. 2): a curva do poe2db é IRREGULAR nos níveis 68-78 (mistura de fontes), por
    // isso a estimativa aí pode errar ~30%. Foco no ENDGAME (≥80), onde a curva é suave e onde se joga; e
    // a mediana REAL corrige assim que há amostras. NÃO confiar na estimativa como precisa abaixo de ~80.
    private static readonly int[] _lvlAnchors  = {  60,    70,    80,    82,    84,    90,    95,   100 };
    private static readonly float[] _baseLife  = { 4834, 11148, 31065, 32956, 34963, 41748, 48398, 56106 };
    private const float RareLifeMult = 8.0f;   // Rare = +700% vida final (poe2db).

    /// <summary>Vida-base de monstro no nível dado (interpolação linear entre âncoras). Clampa nos extremos.</summary>
    private static float BaseLifeAt(int level)
    {
        if (level <= _lvlAnchors[0]) return _baseLife[0];
        var last = _lvlAnchors.Length - 1;
        if (level >= _lvlAnchors[last]) return _baseLife[last];
        for (var i = 1; i <= last; i++)
        {
            if (level > _lvlAnchors[i]) continue;
            var lo = _lvlAnchors[i - 1]; var hi = _lvlAnchors[i];
            var t = (float)(level - lo) / (hi - lo);
            return _baseLife[i - 1] + t * (_baseLife[i] - _baseLife[i - 1]);
        }
        return _baseLife[last];
    }

    /// <summary>Estimativa da pool de um Rare TÍPICO no nível de área (cold-start, adaptativa por tier).</summary>
    private float EstimateRarePool(int areaLevel) => BaseLifeAt(areaLevel) * RareLifeMult * ColdStartFactor;

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
    private readonly HashSet<long> _sampledKeys = new();        // dedup por (areaLevel,id): cada mob entra 1×

    // LATCH (decisão #11): classifica um alvo 1× e CONGELA o nível enquanto for o mesmo id. Evita o salto
    // de nível na transição cold-start→real (e o "Snipe a meio do canal"), e corta o hot-path (não
    // recalcula pool/mediana/score/regex 60×/seg para o mesmo Rare). Limpo quando o alvo muda/morre.
    private uint _latchedId;
    private TargetHardness _latchedLevel;
    private bool _hasLatch;

    // Diagnóstico do último alvo classificado (para o log de H1).
    public string LastDebug { get; private set; } = "";

    public HardnessClassifier(LifeCache life) => _life = life;

    /// <summary>
    /// Observa um alvo (alimenta a amostra) E classifica-o. H1: o resultado é só logado.
    /// Chamar 1×/tick para o alvo atual. <paramref name="areaLevel"/> lido 1×/tick no plugin.
    /// </summary>
    public TargetHardness Classify(Entity entity, MonsterRarity rarity, int areaLevel, long nowTicks)
    {
        if (entity == null) { _hasLatch = false; LastDebug = "dureza: sem alvo"; return TargetHardness.Easy; }

        // White morre logo: Easy sem medir (performance, #6). Unique = combo sempre (Tank, #7). Estes não
        // precisam de latch (são constantes), mas atualizam o id para o latch seguinte ser coerente.
        if (rarity == MonsterRarity.White) { _hasLatch = false; LastDebug = "dureza: White→Easy"; return TargetHardness.Easy; }
        if (rarity == MonsterRarity.Unique) { _hasLatch = false; LastDebug = "dureza: Unique→Tank"; return TargetHardness.Tank; }

        // LATCH: mesmo id já classificado? Devolve o nível CONGELADO (não recalcula nada — sem salto, sem
        // regex/mediana no hot-path). A amostragem do mob já aconteceu no 1º contacto, não se repete.
        if (_hasLatch && entity.Id == _latchedId)
        {
            LastDebug = $"dureza: {rarity} alvl={areaLevel} → {_latchedLevel} (latch id={_latchedId})";
            return _latchedLevel;
        }

        if (!_life.TryGetPool(entity, nowTicks, out var pool, out var maxHp, out var maxEs))
        { _hasLatch = false; LastDebug = "dureza: pool ilegível→Easy"; return TargetHardness.Easy; }

        // Alimenta a amostra (só Rares, 1× por (área,id), sem mods de inflação). #11/#14. Chave composta
        // (areaLevel,id) alinha o dedup com a baseline (que acumula por área) — um id reusado noutra área
        // não bloqueia, e o mesmo mob não reentra. (Corrige a vuln. 3 da auditoria.)
        var coldStart = AreaCount(areaLevel) < MinAmostras;
        var sampled = false;
        if (rarity == MonsterRarity.Rare && _sampledKeys.Add(DedupKey(areaLevel, entity.Id)))
        {
            if (HasInflation(entity)) { /* excluído da mediana (#14) */ }
            else { Insert(areaLevel, pool, nowTicks); sampled = true; }
        }

        var median = MedianFor(areaLevel, coldStart, nowTicks);
        if (median <= 0f) { _hasLatch = false; LastDebug = "dureza: mediana 0→Easy"; return TargetHardness.Easy; }

        var modAdj = HasAnnoying(entity) ? AjusteModPorMatch : 0f;
        var score = pool / median + modAdj;

        var level = score >= LimiarTank ? TargetHardness.Tank
                  : score >= LimiarMedium ? TargetHardness.Medium
                  : TargetHardness.Easy;

        // Magic no cold-start: limitado a MEDIUM (nunca Tank com baseline não-fiável). Decisão #5.
        if (coldStart && rarity == MonsterRarity.Magic && level == TargetHardness.Tank)
            level = TargetHardness.Medium;

        // CONGELA o nível para este id: enquanto for o alvo, não reavalia (estável + barato).
        _latchedId = entity.Id;
        _latchedLevel = level;
        _hasLatch = true;

        LastDebug = $"dureza: {rarity} alvl={areaLevel} hp={maxHp:F0} es={maxEs:F0} pool={pool:F0} " +
                    $"med={median:F0}{(coldStart ? "(estimativa)" : "(real)")} score={score:F2} adj={modAdj:F1} → {level} " +
                    $"[T>={LimiarTank} M>={LimiarMedium} amostra={AreaCount(areaLevel)}{(sampled ? " +amostrei" : "")}]";
        return level;
    }

    // ── Amostra / mediana ──────────────────────────────────────────────────────────────────

    private int AreaCount(int areaLevel) => _byArea.TryGetValue(areaLevel, out var s) ? s.Count : 0;

    /// <summary>Chave de dedup composta (areaLevel, id) — alinha o dedup com a baseline por área.</summary>
    private static long DedupKey(int areaLevel, uint id) => ((long)areaLevel << 32) | id;

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
        // Cold-start: estimativa por NÍVEL DE ÁREA (adapta-se ao tier sozinha, sem slider fixo). Mesma
        // unidade da mediana real → um só par de limiares. Agnóstica à build (mede vida, não a skill).
        // #13 + autopilot-must-be-build-agnostic. Assim que a área tiver MinAmostras Rares, a mediana
        // REAL toma conta (e essa já acumula por area level, #12).
        if (coldStart) return EstimateRarePool(areaLevel);

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
    /// Mudança de ÁREA: solta o latch (o alvo da área anterior morreu). A baseline por área
    /// (<see cref="_byArea"/>) e o dedup (<see cref="_sampledKeys"/>, chave composta por área) MANTÊM-SE —
    /// acumulam por area level na sessão (#12): um corredor de T15 herda o que aprendeu de T15 anteriores.
    /// Como a chave de dedup inclui o areaLevel, ids reusados noutra instância do mesmo nível não colidem
    /// com a amostragem (corrige a vuln. 3 sem perder a acumulação).
    /// </summary>
    public void OnAreaChange() => _hasLatch = false;

    /// <summary>Reset TOTAL (fim de sessão/plugin desligado): limpa amostra, dedup e latch.</summary>
    public void Reset()
    {
        _byArea.Clear();
        _sampledKeys.Clear();
        _hasLatch = false;
        LastDebug = "";
    }
}

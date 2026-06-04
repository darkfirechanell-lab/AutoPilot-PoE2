using System;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace AutoPilot.Combat.Routines;

/// <summary>
/// Rotina de STAFF (Whirling Assault / Tempest Bell / Killing Palm / Charged Staff / Rend / Hollow
/// Form / Herald of Ice). Construída sobre os mesmos sistemas da IceShotRoutine (InputQueue,
/// BuffReader, CooldownTracker, máquina de canalização) mas com a lógica desta build melee.
///
/// CICLO DA BUILD (descrito pelo utilizador 2026-06-04):
///   Killing Palm (no sino) → Power Charges → Charged Staff (mantido) → Whirling Assault carrega
///   mais forte. O Whirling Assault é o dano principal; o sino (Tempest Bell) amplifica; o Killing
///   Palm gera as cargas; o Charged Staff converte cargas em dano sustentado; o Rend é burst extra.
///
/// PRIORIDADE (do utilizador):
///   1. Manutenção (fundo): Charged Staff > Herald of Ice (sempre ligados)
///   2. Gerador: Killing Palm (quando faltam cargas ou ao colocar sino)
///   3. Dano principal: Whirling Assault (tecla mais usada, spam = filler)
///   4. Burst de chefe: Tempest Bell + Hollow Form + Rend (sequência)
///
/// ⚠️ NOMES DE BUFF/CHARGE A CONFIRMAR ⚠️
/// Os nomes internos dos buffs desta build AINDA NÃO foram capturados (o AutoPilot_buffnames.txt só
/// tem a build de arco). Os nomes abaixo são PALPITES pelas convenções do PoE2 (snake_case, ex.:
/// frenzy_charge → power_charge). Cada leitura tem FALLBACK por cooldown, por isso a rotação funciona
/// mesmo com o nome errado — só fica "cega" nesse ponto até confirmares. Para confirmar:
///   1. Liga "Mostrar Debug" nos settings e joga uns segundos com a build de staff.
///   2. Abre C:\Users\clona\Desktop\GamePoe\TestePoE\AutoPilot_buffnames.txt e procura os nomes
///      PLAYER:* que aparecem quando usas Charged Staff / Killing Palm / Rend / Hollow Form.
///   3. Substitui as constantes marcadas "A CONFIRMAR" pelos nomes reais. Ver memória
///      [[freezing-mark-mechanic]] para o padrão de como confirmámos os nomes da build de arco.
/// </summary>
public sealed class StaffRoutine : IRoutine
{
    public string Name => "Staff";

    // ── Nomes de skills (memória — preenchidos pela auto-deteção) ───────────────────────────
    // Estes são os nomes "...Player" que a barra reporta. A CONFIRMAR: corre o jogo com Debug
    // ligado e confirma no HUD (DebugSkillSlots) os nomes exatos das tuas skills.
    // ✅ Nomes CONFIRMADOS pela barra do jogador (skillName interno, 2026-06-04).
    private const string WHIRLING_ASSAULT = "WhirlingAssaultPlayer"; // ✅ confirmado
    private const string KILLING_PALM = "KillingPalmPlayer";         // ✅ confirmado
    private const string CHARGED_STAFF = "ChargedStaffPlayer";       // ✅ confirmado
    private const string TEMPEST_BELL = "TempestBellPlayer";         // ✅ confirmado
    private const string REND = "WyvernRendPlayer";                  // ✅ confirmado (era "RendPlayer")
    private const string FALLING_THUNDER = "FallingThunderPlayer";   // ✅ confirmado (skill nova)
    // ❌ Estas DUAS não apareceram na barra detetada — provavelmente a build não as usa. Ficam aqui
    // inertes: ctx.Find() devolve null e os Try... saem sem fazer nada. Se as adicionares à barra,
    // confirma o nome interno e atualiza.
    private const string HOLLOW_FORM = "HollowFormPlayer";           // A CONFIRMAR (não detetada)
    private const string HERALD_OF_ICE = "HeraldOfIcePlayer";        // A CONFIRMAR (não detetada)

    // ── Buffs/charges (nomes internos do jogo) — A CONFIRMAR ────────────────────────────────
    // Power Charges: no log da build de arco vimos "frenzy_charge" e "ice_bite" como charges. As
    // Power Charges são quase de certeza "power_charge" (convenção do PoE). Se Charges() devolver -1
    // (ilegível) ou 0 sempre, o nome está errado → cai no fallback por cooldown do Killing Palm.
    private const string POWER_CHARGE = "power_charge";              // A CONFIRMAR
    // Buff do Charged Staff ativo no jogador. Palpite: "charged_staff" / "charged_staff_buff".
    private const string CHARGED_STAFF_BUFF = "charged_staff";       // A CONFIRMAR
    // Buff do jogador enquanto Hollow Form ativo.
    private const string HOLLOW_FORM_BUFF = "hollow_form";           // A CONFIRMAR
    // Debuff do Rend no ALVO (sangramento). Palpite: "rend" / "rend_bleed".
    private const string REND_ON_ENEMY = "rend";                    // A CONFIRMAR
    // Buff do jogador concedido pelo Rend (o tal "~50% de dano" que descreveste), se existir como
    // buff no jogador em vez de debuff no alvo. Verificamos os dois.
    private const string REND_PLAYER_BUFF = "rend_damage_buff";      // A CONFIRMAR
    // Herald of Ice (reserva/aura). Este JÁ está confirmado no log: "herald_of_ice".
    private const string HERALD_OF_ICE_BUFF = "herald_of_ice";

    // ── Cooldowns internos anti-spam (ms) ───────────────────────────────────────────────────
    // Servem de fallback quando o buff não é legível, e de anti-spam quando é.
    private const int CD_WHIRLING = 50;        // filler: igual ao Ice Shot, deixa spammar
    private const int CD_KILLING_PALM = 600;   // gerador: rápido, mas não todos os ticks
    private const int CD_CHARGED_STAFF = 800;  // manutenção: re-tentar reaplicar
    private const int CD_TEMPEST_BELL = 200;   // abertura de boss; o "loop" usa o tempo do sino
    private const int CD_REND = 800;           // re-tentar reaplicar Rend
    private const int CD_HOLLOW_FORM = 1000;   // ativar Hollow Form
    private const int CD_HERALD = 3000;        // só ligar a reserva se cair
    private const int CD_FALLING_THUNDER = 300; // anti-spam; a condição real é CS ativo + charges cheias

    // Timeouts de commit (segurar a tecla até confirmar) — mesma rede de segurança do IceShot.
    private const int KILLING_PALM_COMMIT_MS = 500;
    private const int CHARGED_STAFF_COMMIT_MS = 500;
    private const int TEMPEST_BELL_COMMIT_MS = 600;
    private const int REND_COMMIT_MS = 500;
    private const int HOLLOW_FORM_COMMIT_MS = 600;
    private const int FALLING_THUNDER_COMMIT_MS = 600;

    // ── Limiares configuráveis (ligados aos settings na integração) ─────────────────────────
    /// <summary>Power Charges abaixo das quais o Killing Palm dispara para reabastecer.</summary>
    public int MinPowerCharges { get; set; } = 3;
    /// <summary>Reaplicar Rend no boss/rares (burst). Desligar para rares rápidos.</summary>
    public bool UseRend { get; set; } = true;
    /// <summary>Usar Hollow Form na abertura do boss.</summary>
    public bool UseHollowForm { get; set; } = true;
    /// <summary>Duração estimada do sino (ms) — quando expira, repõe Tempest Bell. Fallback se não
    /// conseguirmos ler o sino na cena.</summary>
    public int TempestBellDurationMs { get; set; } = 6000;
    /// <summary>Manter Charged Staff sempre ativo (reaplicar quando o buff cai). Coração da build.</summary>
    public bool MaintainChargedStaff { get; set; } = true;
    /// <summary>Usar Falling Thunder. Só dispara na janela ótima (Charged Staff ativo + charges cheias).</summary>
    public bool UseFallingThunder { get; set; } = true;
    /// <summary>Power Charges necessárias para o Falling Thunder disparar (a "janela cheia", ex.: 5).</summary>
    public int FallingThunderCharges { get; set; } = 5;

    private readonly CooldownTracker _cd = new();

    // Estado de canalização (uma máquina só, partilhada — igual ao IceShot).
    private enum Channel { None, KillingPalm, ChargedStaff, TempestBell, Rend, HollowForm, FallingThunder }
    private Channel _channel;
    private long _channelStartTicks;
    private uint _channelTargetId;

    public bool IsBusy => _channel != Channel.None;

    // Diagnóstico para o HUD debug.
    public string ComboDebug { get; private set; } = "";
    public string FillerDebug { get; private set; } = "";
    public string MaintenanceDebug { get; private set; } = "";

    public void Execute(RoutineContext ctx)
    {
        // 1. Se está a meio de algo (segurar até confirmar), continua a máquina — mesmo sem alvo.
        if (_channel != Channel.None)
        {
            ContinueChannel(ctx);
            return;
        }

        // 2. MANUTENÇÃO (fundo) — corre SEMPRE, mesmo sem alvo (o Charged Staff e a Herald são do
        //    jogador, não dependem de ter inimigo à frente). É o "sempre ligados" da tua prioridade.
        if (TryMaintainChargedStaff(ctx)) return;
        if (TryHerald(ctx)) return;

        var target = ctx.Target?.Entity;
        if (target == null || !IsAlive(target))
        {
            ComboDebug = "staff: (sem alvo)";
            return;
        }

        // Diagnóstico base.
        _rarityDebug = $"alvoRar={target.Rarity} pc={BuffReader.Charges(ctx.Game?.Player, POWER_CHARGE)}";

        // 3. Rotação por raridade do alvo.
        switch (target.Rarity)
        {
            case MonsterRarity.Unique: ExecuteBoss(ctx, target); break;
            case MonsterRarity.Rare: ExecuteElite(ctx, target); break;
            default: ExecuteClear(ctx, target); break;
        }
    }

    private string _rarityDebug = "";

    // ── Rotações ────────────────────────────────────────────────────────────────────────────

    // CLEAR (mapas): ciclo contínuo. O Charged Staff já foi tratado na manutenção (fundo).
    //   • Se faltam Power Charges → Killing Palm rápido (reset das cargas → alimenta o Charged Staff).
    //   • Senão → spam de Whirling Assault (dano principal).
    // (Herald of Ice trata das explosões em cadeia sozinha — só garantimos que está ligada.)
    private void ExecuteClear(RoutineContext ctx, Entity target)
    {
        if (TryKillingPalm(ctx, target)) return;     // só dispara se faltarem charges (ver dentro)
        if (TryFallingThunder(ctx, target)) return;  // janela ótima: Charged Staff + charges cheias
        TryFiller(ctx);                              // Whirling Assault
    }

    // ELITE (raro): como o clear, mas com Rend opcional para burst. Sem a abertura completa do boss
    // (Tempest Bell + Hollow Form) para não desperdiçar em alvos rápidos.
    private void ExecuteElite(RoutineContext ctx, Entity target)
    {
        if (TryTempestBell(ctx, target)) return;     // sino amplifica também em raros difíceis
        if (TryKillingPalm(ctx, target)) return;     // repõe cargas (sobretudo logo após o sino)
        if (UseRend && TryRend(ctx, target)) return;
        if (TryFallingThunder(ctx, target)) return;  // janela ótima: Charged Staff + charges cheias
        TryFiller(ctx);
    }

    // BOSS (único): abertura + loop, exatamente como descreveste.
    //   Abertura:  Tempest Bell → Killing Palm (no sino) → Charged Staff (já mantido) → Rend →
    //              Hollow Form → spam Whirling Assault.
    //   Loop:      quando o sino acaba → novo Tempest Bell → Killing Palm → reaplicar Rend → spam.
    // A ordem dos Try... abaixo É a prioridade: cada um só dispara se a sua condição estiver por
    // satisfazer (sino ausente, cargas em falta, Rend expirado, etc.); senão segue para o filler.
    private void ExecuteBoss(RoutineContext ctx, Entity target)
    {
        if (TryTempestBell(ctx, target)) return;            // 1. sino (e reposição no loop)
        if (TryKillingPalm(ctx, target)) return;            // 2. cargas (logo a seguir ao sino)
        // 3. Charged Staff — já garantido pela manutenção no topo do Execute.
        if (UseRend && TryRend(ctx, target)) return;        // 4. Rend (reaplica ao expirar)
        if (UseHollowForm && TryHollowForm(ctx)) return;    // 5. Hollow Form
        if (TryFallingThunder(ctx, target)) return;         // 6. Falling Thunder (janela ótima)
        TryFiller(ctx);                                     // 7. spam Whirling Assault
    }

    // ── Manutenção (fundo) ──────────────────────────────────────────────────────────────────

    // Charged Staff: o coração da build. Reaplica quando o buff cai. Se o nome do buff estiver errado
    // (Has devolve sempre false), o fallback por cooldown reaplica na mesma (de CD_CHARGED_STAFF em
    // CD_CHARGED_STAFF) — pior, mas nunca deixa de o ter. Quando confirmares o nome, fica perfeito.
    private bool TryMaintainChargedStaff(RoutineContext ctx)
    {
        if (!MaintainChargedStaff) { MaintenanceDebug = "cs: off"; return false; }

        var hasBuff = BuffReader.Has(ctx.Game?.Player, CHARGED_STAFF_BUFF);
        MaintenanceDebug = $"cs: buff={hasBuff} cd={_cd.Ready(CHARGED_STAFF, CD_CHARGED_STAFF)}";
        if (hasBuff) return false; // já ativo → não toca

        if (!_cd.Ready(CHARGED_STAFF, CD_CHARGED_STAFF)) return false;
        var s = ctx.Find(CHARGED_STAFF);
        if (s == null || !s.IsReady) return false;
        // SEGURA até o buff aparecer ou timeout (confirmação), como o Ice-Tipped no IceShot.
        BeginChannel(ctx, Channel.ChargedStaff, s.Key.Value.Key, ctx.Target?.Entity?.Id ?? 0);
        return true;
    }

    // Herald of Ice: reserva/aura. Só LIGA se não estiver ativa (o buff herald_of_ice está confirmado
    // no log). Não há spam — depois de ligada, o Has() corta. Não é dano direto, não exige alvo.
    private bool TryHerald(RoutineContext ctx)
    {
        if (BuffReader.Has(ctx.Game?.Player, HERALD_OF_ICE_BUFF)) return false;
        if (!_cd.Ready(HERALD_OF_ICE, CD_HERALD)) return false;
        var s = ctx.Find(HERALD_OF_ICE);
        if (s == null || !s.IsReady) return false;
        ctx.Skills.Tap(s.Key.Value.Key, s.TapHoldMs.Value);
        _cd.Mark(HERALD_OF_ICE);
        return true;
    }

    // ── Dano principal (filler) ─────────────────────────────────────────────────────────────

    // Whirling Assault: o spam. Igual ao Ice Shot no IceShot — dano direto, exige cursor no alvo (C1).
    private void TryFiller(RoutineContext ctx)
    {
        var s = ctx.Find(WHIRLING_ASSAULT);
        FillerDebug = $"whirl: cd={_cd.Ready(WHIRLING_ASSAULT, CD_WHIRLING)} " +
                      $"found={(s == null ? "null" : "ok")} ready={(s == null ? "-" : s.IsReady.ToString())} " +
                      $"key={(s == null ? "-" : s.Key.Value.Key.ToString())} canHit={ctx.CanHit}";

        if (!ctx.CanHit) return;                              // C1: dano direto → cursor no alvo
        if (!_cd.Ready(WHIRLING_ASSAULT, CD_WHIRLING)) return;
        if (s == null || !s.IsReady) return;
        ctx.Skills.Tap(s.Key.Value.Key, s.TapHoldMs.Value);
        _cd.Mark(WHIRLING_ASSAULT);
    }

    // ── Gerador ─────────────────────────────────────────────────────────────────────────────

    // Killing Palm: repõe Power Charges. Só dispara se faltarem cargas (lê POWER_CHARGE) — assim não
    // desperdiça quando já estás cheio. Se as charges forem ilegíveis (-1, nome errado), cai no
    // fallback: dispara por cooldown (CD_KILLING_PALM) para nunca ficares sem o gerador a funcionar.
    private bool TryKillingPalm(RoutineContext ctx, Entity target)
    {
        var charges = BuffReader.Charges(ctx.Game?.Player, POWER_CHARGE);
        // charges >= 0 = leitura fiável → só repõe se abaixo do mínimo.
        // charges == -1 = ilegível → fallback por cooldown (dispara periodicamente).
        var needByCharges = charges >= 0 && charges < MinPowerCharges;
        var fallbackByTime = charges < 0; // nome do buff provavelmente errado → modo cego
        if (!needByCharges && !fallbackByTime) return false;

        if (!_cd.Ready(KILLING_PALM, CD_KILLING_PALM)) return false;
        var s = ctx.Find(KILLING_PALM);
        if (s == null || !s.IsReady) return false;
        // SEGURA até confirmar o uso (charges sobem OU ActorSkill em uso/cooldown) ou timeout.
        _palmChargesAtStart = charges;
        BeginChannel(ctx, Channel.KillingPalm, s.Key.Value.Key, target.Id);
        return true;
    }

    private int _palmChargesAtStart;

    // Falling Thunder: nuke condicional. SÓ dispara na "janela ótima" pedida pelo utilizador —
    // Charged Staff ATIVO **e** Power Charges CHEIAS (>= FallingThunderCharges, ex.: 5). Como provável
    // gastador das charges, isto cria o ciclo: Killing Palm enche → ao chegar a 5 com CS ativo, Falling
    // Thunder dispara → charges caem → Killing Palm reabastece.
    //
    // Requer leitura FIÁVEL das duas coisas: se o nome do buff do Charged Staff ou das Power Charges
    // estiver errado (Has=false / Charges=-1), a janela nunca é reconhecida e o Falling Thunder não sai
    // (em vez de spammar à toa). É o comportamento seguro até confirmares CHARGED_STAFF_BUFF / POWER_CHARGE.
    private bool TryFallingThunder(RoutineContext ctx, Entity target)
    {
        if (!UseFallingThunder) return false;

        var csActive = BuffReader.Has(ctx.Game?.Player, CHARGED_STAFF_BUFF);
        var charges = BuffReader.Charges(ctx.Game?.Player, POWER_CHARGE);
        ThunderDebug = $"thunder: cs={csActive} pc={charges}/{FallingThunderCharges} cd={_cd.Ready(FALLING_THUNDER, CD_FALLING_THUNDER)}";

        // Janela ótima: precisa das DUAS condições com leitura fiável.
        if (!csActive) return false;
        if (charges < 0 || charges < FallingThunderCharges) return false;

        if (!_cd.Ready(FALLING_THUNDER, CD_FALLING_THUNDER)) return false;
        var s = ctx.Find(FALLING_THUNDER);
        if (s == null || !s.IsReady) return false;
        // SEGURA até confirmar o uso (ActorSkill) ou timeout — igual às outras.
        BeginChannel(ctx, Channel.FallingThunder, s.Key.Value.Key, target.Id);
        return true;
    }

    public string ThunderDebug { get; private set; } = "";

    // ── Burst de boss ───────────────────────────────────────────────────────────────────────

    // Tempest Bell: coloca o sino. Anti-reposição por presença do sino na cena seria o ideal, mas
    // detetar a entidade do sino é frágil; usamos um timer pela duração estimada (TempestBellDurationMs)
    // + cooldown anti-spam. Quando o "sino expira" (timer) volta a colocar = o loop que descreveste.
    private bool TryTempestBell(RoutineContext ctx, Entity target)
    {
        // O sino ainda está "no chão" pela nossa estimativa? Não recoloca.
        if (_cd.SinceMs(TEMPEST_BELL) < TempestBellDurationMs) return false;
        if (!_cd.Ready(TEMPEST_BELL, CD_TEMPEST_BELL)) return false;
        var s = ctx.Find(TEMPEST_BELL);
        if (s == null || !s.IsReady) return false;
        // SEGURA até confirmar o uso (ActorSkill) ou timeout.
        BeginChannel(ctx, Channel.TempestBell, s.Key.Value.Key, target.Id);
        return true;
    }

    // Rend: burst. Reaplica quando o debuff sai do alvo (ou o buff de dano sai do jogador). Fallback
    // por cooldown se nenhum dos nomes estiver certo. Confirmamos os DOIS sítios (alvo e jogador).
    private bool TryRend(RoutineContext ctx, Entity target)
    {
        var onEnemy = BuffReader.Has(target, REND_ON_ENEMY);
        var onPlayer = BuffReader.Has(ctx.Game?.Player, REND_PLAYER_BUFF);
        // Se conseguimos ler ALGUM dos dois e ele está presente → já aplicado, não recasta.
        if (onEnemy || onPlayer) return false;

        if (!_cd.Ready(REND, CD_REND)) return false;
        var s = ctx.Find(REND);
        if (s == null || !s.IsReady) return false;
        BeginChannel(ctx, Channel.Rend, s.Key.Value.Key, target.Id);
        return true;
    }

    // Hollow Form: ativa na abertura. Só liga se não estiver ativo (lê HOLLOW_FORM_BUFF; fallback CD).
    private bool TryHollowForm(RoutineContext ctx)
    {
        if (BuffReader.Has(ctx.Game?.Player, HOLLOW_FORM_BUFF)) return false;
        if (!_cd.Ready(HOLLOW_FORM, CD_HOLLOW_FORM)) return false;
        var s = ctx.Find(HOLLOW_FORM);
        if (s == null || !s.IsReady) return false;
        BeginChannel(ctx, Channel.HollowForm, s.Key.Value.Key, ctx.Target?.Entity?.Id ?? 0);
        return true;
    }

    // ── Máquina de canalização (uma só, partilhada — igual ao IceShot) ──────────────────────

    private void BeginChannel(RoutineContext ctx, Channel channel, Keys key, uint targetId)
    {
        _channel = channel;
        _channelStartTicks = DateTime.UtcNow.Ticks;
        _channelTargetId = targetId;
        ctx.Skills.Channel(key); // KeyDown contínuo
    }

    private void ContinueChannel(RoutineContext ctx)
    {
        var elapsed = (DateTime.UtcNow.Ticks - _channelStartTicks) / TimeSpan.TicksPerMillisecond;
        var target = ctx.Target?.Entity;
        var targetGone = target == null || !IsAlive(target) || target.Id != _channelTargetId;

        switch (_channel)
        {
            case Channel.KillingPalm:
            {
                // Confirma: as Power Charges subiram (gerou) OU a skill entrou em uso/cooldown.
                var charges = BuffReader.Charges(ctx.Game?.Player, POWER_CHARGE);
                var gained = charges >= 0 && _palmChargesAtStart >= 0 && charges > _palmChargesAtStart;
                var s = ctx.Find(KILLING_PALM);
                var used = s != null && (s.IsUsing || s.IsOnCooldown);
                var timeout = elapsed >= KILLING_PALM_COMMIT_MS;
                if (gained || used || timeout || targetGone) { EndChannel(ctx); _cd.Mark(KILLING_PALM); }
                return;
            }
            case Channel.ChargedStaff:
            {
                // Confirma pelo buff (charged_staff) no jogador. Não depende do alvo.
                var applied = BuffReader.Has(ctx.Game?.Player, CHARGED_STAFF_BUFF);
                var s = ctx.Find(CHARGED_STAFF);
                var used = s != null && (s.IsUsing || s.IsOnCooldown);
                var timeout = elapsed >= CHARGED_STAFF_COMMIT_MS;
                if (applied || used || timeout) { EndChannel(ctx); _cd.Mark(CHARGED_STAFF); }
                return;
            }
            case Channel.TempestBell:
            {
                var s = ctx.Find(TEMPEST_BELL);
                var used = s != null && (s.IsUsing || s.IsOnCooldown);
                var timeout = elapsed >= TEMPEST_BELL_COMMIT_MS;
                if (used || timeout || targetGone) { EndChannel(ctx); _cd.Mark(TEMPEST_BELL); }
                return;
            }
            case Channel.Rend:
            {
                var applied = (target != null && BuffReader.Has(target, REND_ON_ENEMY))
                              || BuffReader.Has(ctx.Game?.Player, REND_PLAYER_BUFF);
                var s = ctx.Find(REND);
                var used = s != null && (s.IsUsing || s.IsOnCooldown);
                var timeout = elapsed >= REND_COMMIT_MS;
                if (applied || used || timeout || targetGone) { EndChannel(ctx); _cd.Mark(REND); }
                return;
            }
            case Channel.HollowForm:
            {
                var applied = BuffReader.Has(ctx.Game?.Player, HOLLOW_FORM_BUFF);
                var s = ctx.Find(HOLLOW_FORM);
                var used = s != null && (s.IsUsing || s.IsOnCooldown);
                var timeout = elapsed >= HOLLOW_FORM_COMMIT_MS;
                if (applied || used || timeout) { EndChannel(ctx); _cd.Mark(HOLLOW_FORM); }
                return;
            }
            case Channel.FallingThunder:
            {
                // Confirma pelo ActorSkill (em uso/cooldown = saiu) ou timeout.
                var s = ctx.Find(FALLING_THUNDER);
                var used = s != null && (s.IsUsing || s.IsOnCooldown);
                var timeout = elapsed >= FALLING_THUNDER_COMMIT_MS;
                if (used || timeout || targetGone) { EndChannel(ctx); _cd.Mark(FALLING_THUNDER); }
                return;
            }
        }
    }

    private void EndChannel(RoutineContext ctx)
    {
        ctx.Skills.Release(); // KeyUp da tecla canalizada
        _channel = Channel.None;
        _channelTargetId = 0;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static bool IsAlive(Entity e)
    {
        try { return e is { IsValid: true, IsAlive: true, IsDead: false }; }
        catch { return false; }
    }

    public void Reset()
    {
        _channel = Channel.None;
        _channelTargetId = 0;
        _cd.Clear();
    }
}

# Plano — Build Monge Leveling (Holo Focus / Bells) no motor Geral

> Build nova do user (12 Jun): Monge de leveling. Ascendência **Holo Focus** cria **Bells**
> automaticamente em combate. O ciclo: Culling Palm bate no Bell (crítico garantido) → gera Power
> Charges → Falling Thunder gasta-as com bónus. Rend = sustain. Verinn's Assault = filler.
>
> **NÃO COMEÇAR sem aprovação.** Faseado, testa entre cada.

## MECÂNICA (confirmada pelo user)
- **Bell**: NÃO se casta. O Holo Focus (ascendência) cria Bells AUTOMÁTICOS em combate, perto do
  jogador/inimigos. São ENTIDADES no chão, "primed for stun", crítico garantido a quem os acerta.
  Duram pouco; somem se não interagidos. São a "bateria" do ciclo.
- **Culling Palm**: bate no Bell → crítico → GERA Power Charges. SÓ no Bell (não em mob normal).
- **Falling Thunder**: gasta as Power Charges (>= 3) com bónus. Dano de culminação.
- **Rend**: sustain (buff de dano lightning); reaplica se o buff vai expirar.
- **Verinn's Assault**: filler (dano base entre cooldowns).

## HIERARQUIA (prioridade — geração de charges ANTES do dano, pedido do user)
```
Rend            P100  — sustain (Player SEM buff_rend, se a expirar)
Culling Palm    P90   — bate no BELL → gera charges (CRÍTICO: antes do Thunder)
Falling Thunder P80   — gasta charges (Buff de charges=power_charge, Charges mín=3)
Verinn Assault  P10   — filler (sem gates)
```
Regra de ouro do user: o Culling (gera) TEM de ter prioridade acima do Thunder (gasta), senão o
Thunder gasta e fica sem charges → ciclo parte.

## O QUE O MOTOR JÁ FAZ (config, ~70%)
- Prioridades ✓
- Falling Thunder: `ChargeBuff=power_charge` + `ChargeMin=3` ✓ (RuleEvaluator:123)
- Verinn filler: sem gates ✓
- Rend: `Player SEM buff`=buff_rend ✓
- Power charges: `power_charge` confirmado no buffnames.txt do user.

## O QUE É CÓDIGO NOVO — a peça crítica
**Culling Palm mirar o BELL (entidade != mob).** O motor mira só o `_currentTarget` (o mob). O Bell é
outra entidade. Precisa:
1. **Detetar Bells** — entidades no chão. Reusar o `GroundEntityTracker` (já procura por path em
   MiscellaneousObjects). PRECISA do PATH do Bell (descobrir no Dev plugin, como o tornado). O
   Holo Focus/Bell pode estar noutro EntityType — confirmar.
2. **Mirar o Bell** — campo novo no SkillRule tipo "AimAtGroundEntity=<path>": quando a regra dispara,
   o cursor aponta ao Bell mais próximo (não ao mob). Código novo no motor + AimController.
3. **Bell mais próximo** — se há vários, o mais perto do jogador (dica do user).
4. **Sem Bell → Culling não dispara** (não há o que bater).

## FASES
- **F0 (descobrir):** o user entra com a build → nomes reais das skills (CullingPalm? KillingPalm?
  FallingThunder? VerinnAssault?) + o PATH do Bell (entidade, via Dev) + o buff do Rend + confirmar
  `power_charge` conta as cargas. SEM isto não dá para o preset.
- **F1 (config):** preset no Geral — prioridades + Thunder(charges) + Verinn + Rend. Culling mira o
  MOB (provisório, não gera charges ainda mas testa o resto). Testa o ciclo base.
- **F2 (código, a peça crítica):** Culling mira o BELL. Campo "AimAtGroundEntity" + deteção do Bell
  (GroundEntityTracker) + AimController aponta ao Bell. Testa: Culling bate no Bell → charges sobem →
  Thunder dispara.
- **F3 (opcional):** weapon swap para o Rend (se estiver no outro set); Life Drain / sustain forçado.

## RISCOS
- **Mirar entidade != mob** é novo no motor — a parte mais incerta (toca no AimController).
- O Bell **dura pouco** — o plugin tem de mirar RÁPIDO (latência do aim vs vida do Bell). Pode falhar
  se o aim for lento.
- O path/EntityType do Bell — desconhecido até ver no jogo. Pode não ser MiscellaneousObjects.
- Crítico no Bell tem de REGISTAR antes do Thunder (dica do user: Thunder precisa do crit do Culling).

## ⭐ DESCOBERTA (IceMonk, C:/Users/clona/Desktop/GamePoe/IceMonk) — Bell por BUFF, não por entidade!
O plugin IceMonk (build Ice Strike de outro dev, ExilePrecision) deteta o estado do Bell por um BUFF
no player: `tempest_bell_combo_tracker` com `BuffCharges == 4` → o combo do sino está pronto → usa o
Bell. NÃO mira a entidade Bell. Isto pode ELIMINAR a parte crítica/arriscada (aim-ao-Bell):
- **Forma 1 (plano original):** mirar a entidade Bell — código novo no AimController, timing apertado, risco alto.
- **Forma 2 (IceMonk):** ler um buff-tracker no player — JÁ é o que o motor faz (gate de buff/charges). Simples.
RECOMENDAÇÃO: quando o user tiver a build montada, CONFIRMAR no buffnames.txt/Dev se há um buff-tracker
(holo_focus_*/`*bell*`/`*charge*`) no PLAYER durante o combate. SE houver → Forma 2 (config, sem aim-ao-Bell).
SE não → Forma 1. NÃO construir o aim-ao-Bell antes de confirmar isto (evita refazer).
Outras ideias-ouro do IceMonk (reaproveitar): priority queue por raridade (DetermineElite/Normal); GATE DE
MANA por skill (`currentMana > skill.Cost` — o motor NÃO tem isto, vale adicionar); HasFreezingMark +
HasNearbyFreezingMark (mark no alvo OU num mob perto, raio 30). NOTA: IceMonk é Ice Strike/Hand of Chayula,
NÃO Falling Thunder — skills diferentes da build do user; só a ESTRUTURA/mecânica do Bell é reaproveitável.

## DETETAR O BELL (investigado 12 Jun — ReAgent dá a forma, não o Bell pronto)
O ReAgent NÃO tem nada de Bell, MAS o `NearbyMonsterInfo` mostra a forma: lê `EntityType.Monster` e
expõe `Entity.Path`/`Entity.Metadata`/`GridPosition` de cada um. O Bell é "primed for stun" (alvejável)
→ provavelmente `EntityType.Monster` (NÃO MiscellaneousObjects como o tornado). Logo:
- Detetar = filtrar os Monster pelo PATH do Bell (forma do ReAgent). PATH desconhecido até ver no Dev.
- CONSEQUÊNCIA (vuln 3 da auditoria): se o Bell é Monster, ele entra no TARGETING NORMAL como um mob →
  tem de ser EXCLUÍDO do targeting de raros (senão o aim mira o Bell em vez do raro) E tratado como
  alvo-só-do-Culling. Isto é mais código no EntityCache (filtro) além do aim-secundário.
- F0 TEM de confirmar no Dev: o Path EXATO do Bell + o EntityType (Monster vs outro).

## DEPENDÊNCIAS
Reusa: GroundEntityTracker (deteção por path), o motor Geral (prioridades/charges/buffs), AimController
(mirar). Ver [[staffroutine-build]] (a StaffRoutine dedicada tem KillingPalm/ChargedStaff/FallingThunder/
Rend já com nomes confirmados — fonte dos nomes), [[combate-estado-11jun]], [[plan-before-coding]].

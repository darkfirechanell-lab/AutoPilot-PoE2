# Plano — Múltiplas regras por skill (v2, pós-auditoria + investigação)

> **Objetivo do utilizador:** cada skill poder ter VÁRIAS regras para momentos diferentes (normais,
> normais+, mágicos, mágicos+, raros, raros+, boss). UI: um toggle "Regra extra" revela um 2º bloco de
> opções LIMPAS para definir a 2ª regra. Caso concreto que motivou: Barrage = 2 formas (Medium sem
> frozen; Tank/boss com frozen).
>
> **NÃO COMEÇAR sem aprovação.** Cada fase: isolada, compila 0 erros, commit próprio, o utilizador testa.

---

## O QUE A INVESTIGAÇÃO PROVOU (factos do código, não suposições)

| Camada | Suporta N regras/skill? | Evidência |
|---|---|---|
| Motor `GeneralRoutine.Execute` | ✅ SIM | `foreach rule in _ordered` itera todas; `ctx.Find(name)` dá a tecla |
| Preset embutido | ✅ SIM | já corre 2× Barrage + 3× Mark |
| `SkillDetector.Sync` | ✅ TOLERA 2 slots mesmo nome | linha 111-112: só remove se a skill não existe; 2 "Barrage" ambos religam |
| **`CooldownTracker`** | ❌ chave = `SkillName` | CooldownTracker.cs:15 — 2 regras mesmo nome COLIDEM no cooldown/chain |
| UI `ContentNode<SkillSlot>` | ❌ 1 slot/skill, campos planos | item de ContentNode não tem precedente de sub-classe/ContentNode aninhado renderizável |
| Pontes mapper/preset/perfil | ❌ 1↔1 | 30 campos `[Geral]` por skill, em 5 sítios |

**Padrão externo (ExiledBot, forum confirmado):** regra liga-se à TECLA/slot, não ao nome único; prioridade
decide. Identidade (tecla) ≠ regras (N).

**Risco de UI confirmado:** NÃO há precedente de sub-classe nem ContentNode aninhado DENTRO de um item de
ContentNode em nenhum plugin. `CustomIconSettings` usa RenderMethod ImGui manual; `SkillSetting` (AutoMyAim)
só tem nodes planos. → Tanto "RuleConfig sub-classe" como "ContentNode aninhado" são NÃO-PROVADOS.

---

## AS 3 VULNERABILIDADES DA AUDITORIA (obrigatórias)

1. **SPOF de cooldown/chain:** `_cd` e `AfterSkill` usam `SkillName` como chave. 2 regras "Barrage" colidem
   → uma não dispara, e o `AfterSkill=Barrage` do Snipe não sabe qual âncora. QUEBRA o combo que isto quer
   consertar. → **RuleId por regra** (não por nome).
2. **Dimensionamento:** 30 campos × 5 sítios = ~150 edições por cópia, e só p/ 1 regra extra; a visão real
   (4 momentos) multiplicaria. → NÃO duplicar campos; extrair config reutilizável.
3. **Ordem:** se duplicar campos primeiro, a Solução N-regras obriga a refazer + 2ª migração. → fundação
   primeiro.

---

## DECISÃO DE UI (resolvida pela investigação)

`RuleConfig` como sub-classe dentro do SkillSlot = risco não-provado (não renderiza garantido). Logo a UI da
regra-2 NÃO é uma sub-classe aninhada. **Duas opções viáveis, ambas com campos PLANOS no SkillSlot:**

- **Opção A (campos planos prefixados):** os campos da regra-2 são propriedades planas `Extra_*` no
  SkillSlot, com `[ConditionalDisplay(IsExtraVisible)]` (padrão JÁ provado pelo "Mostrar config"). Feio no
  código (campos repetidos) mas RENDERIZA garantido. Não escala para N regras.
- **Opção B (painel ImGui custom):** um `RenderMethod` no SkillSlot desenha a lista de regras à mão (como o
  PickIt/CustomIcons fazem). Escala para N regras, UI limpa, MAS é ImGui manual (mais código de render,
  fora do sistema de nodes → o save/load tem de ser tratado à parte).

→ **Decisão de produto PENDENTE** (ver fim). A fundação (F1) é igual nas duas; só F2 diverge.

---

## FASES

### F1 — FUNDAÇÃO: RuleId por regra (corrige o SPOF do cooldown). INVISÍVEL ao utilizador.
**Objetivo:** cooldown e chaining passam a ser por REGRA, não por nome. Comportamento idêntico (1 regra/skill
ainda), mas o motor fica pronto para 2+ regras do mesmo nome sem colisão.

- F1.1 — `SkillRule.RuleId` (string única: ex. `SkillName + "#" + índice` na lista). Default = SkillName
  (1 regra → id == nome → retrocompat total).
- F1.2 — `GeneralRoutine`: `_cd.Ready/Mark/SinceMs` e o estado de hold (`_holdRule`) passam a usar
  `rule.RuleId` em vez de `rule.SkillName`. O `ctx.Find` CONTINUA por SkillName (a tecla é da skill).
- F1.3 — `AfterSkill` chaining: continua a referir uma skill-âncora, mas o `SinceMs` da âncora passa a
  agregar todas as regras dessa skill (ou referir um RuleId). DECIDIR: âncora por nome (mais simples, o
  Snipe espera "qualquer Barrage") vs por RuleId (preciso). Para o combo do user, "qualquer Barrage" chega.
- **Teste F1:** com 1 regra/skill, o combo IceShot (Tornado→Barrage→Snipe) funciona EXATAMENTE como hoje.
  Zero mudança visível. Valida que o RuleId não partiu nada.

### F2 — UI da regra-2 (depende da decisão A vs B)
**Opção A:** +toggle `HasExtraRule` + os campos da regra-2 (planos, `Extra_*`, ConditionalDisplay). O mapper
gera a 2ª SkillRule (RuleId `#1`) se o toggle estiver ligado. UI revela bloco limpo (pedido do user).
**Opção B:** RenderMethod custom desenha N regras; cada uma um RuleConfig serializado à parte.
- **Teste F2:** Barrage com regra-1 (Medium, sem frozen) + regra-2 (Tank, frozen) → no boss não-congelado
  só sai no combo certo; o Snipe encadeia bem (graças ao RuleId da F1).

### F3 — (FUTURO, opção B) N regras por skill
Lista dinâmica de RuleConfig por skill (normais/mágicos/raros/boss). Só se a Opção B for escolhida; a F1+F2
já deixam o motor pronto.

---

## MAPEAMENTOS (a regra dos N sítios — onde o campo TEM de atravessar)
RuleId/regra-2 atravessam: SkillRule, (UI), SkillRuleMapper, ProfileSkill, BuildProfileData (save),
ApplyProfileToSettings (load), PresetApplier. Checklist único — nenhum escapa (senão a regra extra não
persiste, como aconteceu com a dureza).

## MIGRAÇÃO
Settings antigos (1 regra) → RuleId default = SkillName → carregam idênticos. 1 migração só (se F1 vier 1º).

## RISCOS
- F1 mexe no coração do motor (cooldown/chain) — é onde o combo vive. Teste F1 tem de provar combo idêntico
  ANTES de F2. É a fase mais delicada (mas invisível).
- F2 Opção A = render garantido mas não escala; Opção B = escala mas ImGui manual (save/load à parte).

## ALTERNATIVA IMEDIATA (zero código, destrava hoje)
Preset embutido (GeneralUseUiRules OFF) já corre 2 regras de Barrage. Build de gelo certa já. A F1-F2 é
para a EDIÇÃO-NA-UI de múltiplas regras.

---

## ORDEM FINAL
1. **F1 — RuleId por regra** (corrige o SPOF do cooldown; invisível; combo idêntico). Bloqueia F2.
2. **Decisão A vs B** (produto: bloco-plano-simples vs painel-N-regras).
3. **F2 — UI da regra-2** (toggle revela; mapper gera 2ª regra; 7 mapeamentos).
4. **F3 — (futuro, só B) N regras.**

Ver [[combatroutine-dynamic-targeting]], [[hardness-feature-built]], [[exiledbot-combat-model-reference]].

# ROTACAO_RICA_PLANO — Routine Geral universal (todas as classes/builds) — v2 pós-auditoria

> Data: 10 Jun 2026 (v2 — emendas da auditoria aplicadas). Origem: pesquisa ReAgent (exCore2/
> PoE2), SimulationCraft APL/Hekili, RotationSolver (conceitos), ExiledBot2 + dump por reflexão
> da API do ExileCore2.dll local (`api_dump.txt`, ferramenta em `ApiDump/`). Complementa o
> HP_ROTATION_PLANO (dureza) — não o substitui; a H1 (HardnessClassifier) continua o caminho dela.
>
> PRINCÍPIO 1 (igual ao SkillRule): todo o campo novo tem default que NÃO FILTRA. Cada fase
> compila e é testável sozinha; nada muda comportamento até o utilizador configurar.
>
> PRINCÍPIO 2 — MODO SOMBRA (obrigatório, da auditoria): todo o gate novo nasce a LOGAR a decisão
> sem bloquear (padrão BaselineRecorder/ActionLog). Só é promovido a "enforcing" depois de uma
> sessão de jogo sem falsos positivos. Motivo: leituras de memória já fraudaram nesta codebase
> (`ActorSkill.Stats` duplicate-key; `SkillBar.Skills` partido nesta patch) — um gate errado para
> o combate EM SILÊNCIO e o utilizador culpa a build.
>
> PRINCÍPIO 3 — ORÇAMENTO DE PERFORMANCE: cada fase declara que leituras novas faz por-tick vs
> por-sync (a sincronização de skills é a cada 30 ticks por custo; o profiling interno "prof:"
> mede). Leituras caras (DeployedObjects, timers de buff) entram no cache por-tick existente
> (BuffReader/EntityCache) ou no ciclo de sync — nunca leitura crua por regra por tick.

## Fontes da API (confirmado no dump, custo de leitura ~zero)
- `ActorSkill`: `WeaponSetBinding`, `Cost/EsCost/LifeCost`, `CanBeUsed`, `AllowedToCast`,
  `RemainingUses/TotalUses`, `DeployedObjects`, `IsChanneling`, `CastTime`
- `Actor`: `DeployedObjects`, `isMoving`, `isAttacking`
- `Life` (player): `HPPercentage/MPPercentage/ESPercentage`, `Ward`, reservas; (mob): `Invulnerable`
- `Stats`: `ActiveWeaponSetIndex`
- `Buffs`: `TryGetBuff` → Buff com timer (TimeLeft)
- `IngameUIElements`: `OpenLeftPanel/OpenRightPanel`, `FullscreenPanels`, `ChatBox`,
  `FocusedInputElement` (melhor que ChatBox p/ "está a escrever")
- `ServerData`: `Latency`, `MonstersRemaining`, `NearestPlayers`
- `IngameData`: `GetPathfindingValueAt`, `GetTerrainHeightAt`, `RawTerrainTargetingData`
- Components não usados: `GroundEffect`, `Charges` (flasks), `Shrine`, `StateMachine`, `Targetable`

---

## R0 — Fundações (fazer ANTES de adicionar campos; reforçado pela auditoria)
- **R0.1 Versão no perfil**: campo `Version` no JSON do ProfileManager + migração tolerante
  (campo em falta = default). Sem isto, cada fase nova parte perfis antigos em silêncio.
- **R0.2 HUD "porquê"**: ESTENDER o CombatHud existente (não criar painel novo) — última razão
  de bloqueio por skill, vinda do `reason` que o RuleEvaluator já devolve (debug toggle). ~Meio
  dia. Multiplica a velocidade de afinar tudo o resto.
- **R0.3 Gate global de UI**: suprimir input do motor quando `FullscreenPanels`/`OpenLeftPanel`/
  `OpenRightPanel`/`FocusedInputElement` (chat a escrever)/escape abertos. REGRA: ao suprimir,
  soltar primeiro TODAS as teclas premidas (lição V0.8 — nunca deixar tecla presa).
- **R0.4 Skip de alvo invulnerável**: `Life.Invulnerable` no TargetSelector/RuleEvaluator
  (fases de boss). Nasce em modo sombra (Princípio 2).
- **R0.5 Protocolo de preempção de input** (NOVO — vulnerabilidade fatal #2): prioridades fixas
  no InputQueue/SkillExecutor: **Pânico > Reação > Rotação**. Preemptar = soltar todas as teclas
  premidas → só depois agir. R3.3 e R5.x ficam BLOQUEADOS até isto existir.
- **R0.6 Checklist/teste de plumbing de campos** (NOVO — vulnerabilidade fatal #3): cada campo
  toca 5 pontos (SkillRule + SkillSlot + SkillRuleMapper + UI + perfil). Criar verificação por
  reflexão (propriedades do SkillRule vs Mapper) que falha à compilação/arranque se um campo
  ficar meio-ligado; no mínimo, checklist documentada e seguida.

## R1 — Gates baratos no SkillRule (entra como UM LOTE ÚNICO, não campo a campo)
- **R1.1 Vitals do player**: `PlayerHpMinPercent/MaxPercent`, `PlayerManaMinPercent`,
  `PlayerEsMinPercent` (defaults 0/1 = não filtra). Desbloqueia skills defensivas/panic.
- **R1.2 Gate de recurso automático** (sem config): motor salta skill se `CanBeUsed=false` ou
  custo (`Cost/EsCost/LifeCost`) > recurso atual. MODO SOMBRA primeiro — é a leitura com maior
  risco de fraude nesta patch.
- **R1.3 Listas OR nos buffs**: `TargetHasBuff = "frozen|chilled"` (mesma sintaxe regex/pipe do
  `TargetMatchesMod`). Mexe direto na dor frozen-vs-chilled.
- **R1.4 `ChargeMax`**: banda de charges (min já existe).
- **R1.5 `OnlyWhileMoving` / `OnlyWhileStationary`**: via `Actor.isMoving` do player.
- Leituras: vitals/isMoving por-tick (baratas, cache); custo/CanBeUsed no ciclo de sync.

## R2 — Flasks (PROMOVIDO da antiga R5.1 — auditoria: só depende de R1.1, valor máximo)
- **R2.1 Regras de flask**: life flask por HP% do player, utility por uptime (component
  `Charges` + `PlayerFlaskBuffs`). É o uso nº 1 do ReAgent/BYOR. Passa pelo InputQueue com a
  prioridade do R0.5 — nunca input direto.

## R3 — Buffs com TEMPO (antiga R2)
- **R3.1 BuffReader lê TimeLeft**: `TryGetBuff` + timer do Buff (entra no cache por-tick atual).
- **R3.2 `RefreshBeforeExpireMs`**: recasta buff quando faltam < X ms (player e alvo).
  GUARDA-CHUVA anti-spam (auditoria): intervalo mínimo de recast obrigatório (respeita
  `CooldownMs` da regra; buffs que reportam TimeLeft=0/infinito constante NÃO disparam refresh).

## R4 — Modos & sequências (antiga R3; ideias SimC/Hekili)
- **R4.1 `ActiveInModes`**: flags Danger/Elite/Normal por regra (default: todos). Rotação de
  boss vs clear vs emergência SEM perfis separados.
- **R4.2 Sequências nomeadas**: `ComboGroup` + `ComboStep` (A→B→C). REGRAS DE ABORTO definidas
  já (auditoria): aborta em morte do alvo, mudança de modo, timeout configurável; ao abortar,
  soltar teclas (R0.5). `AfterSkill` passa a caso especial documentado de sequência de 2 —
  mantém-se por compatibilidade, UI aponta para sequências como caminho novo.
- **R4.3 Slot de reação**: skill "dispara quando DangerDetector ativa", fura prioridade via
  R0.5 (Reação > Rotação). DEPENDE de R0.5.

## R5 — Cobertura de classes (antiga R4)
- **R5.1 Minions/totems**: `MinDeployed` — recasta se `ActorSkill.DeployedObjects.Count < N`.
  Maior buraco de cobertura (Witch/totem não funcionam sem isto). Leitura no ciclo de sync.
- **R5.2 Munição/usos**: gate `RemainingUses > 0` + regra "reload" (Mercenary/crossbow).
- **R5.3 Weapon sets**: gate por `WeaponSetBinding` vs `ActiveWeaponSetIndex` (+ pseudo-skill
  "trocar de set" — decisão de produto pendente).
- **R5.4 Densidade à volta do PLAYER**: `CloseTargetsAround = Target|Player` + raridade mínima
  na contagem (ClusterEngine já tem a infra).
- **R5.5 `PlayerHasAilment`**: lista OR de ailments do player → skills de cleanse.
- ~~Shapeshift~~ → CORTADO do caminho crítico (auditoria): vira **SPIKE paralelo** de
  investigação (como muda a barra em memória ao trocar de forma). Só volta ao plano com
  resultado do spike.

## R6 — Emergência (resto da antiga R5)
- **R6.1 Pânico**: ação a HP < X% — usar skill de escape configurada (via R0.5, prioridade
  máxima). Logout TCP (estilo ReAgent Disconnect) é DECISÃO DE PRODUTO — perguntar antes.

## R7 — Perfis ricos
- **R7.1 Auto-load por personagem**: mapa `PlayerName → último perfil`. DEBOUNCE obrigatório
  (auditoria): só com `InGame && !IsLoading`, nunca trocar em combate (PlayerName vem vazio em
  loading screens → troca espúria).
- **R7.2 Templates por arquétipo**: SÓ 2 para já (auditoria): **Arco** (generalizar
  IceShotPreset) e **Minions** (maior buraco). Restantes quando houver builds dessas em uso.
- **R7.3 Export/import por string**: JSON→base64 no clipboard (inclui `Version` do R0.1; o
  mapeamento por NOME de skill já torna o import robusto entre utilizadores).

## R8 — Terreno (grande; projeto à parte)
- **R8.1 LoS real no RayCaster**: `RawTerrainTargetingData` em vez de heurística.
- **R8.2 Kiting com walkability**: DodgeController consulta `GetPathfindingValueAt`.
- **R8.3 Evitar `GroundEffect`**: degens no chão entram no DangerDetector/dodge.

## R9 — Polish (diferido pela auditoria; nada aqui desbloqueia outra fase)
- **R9.1 Delays adaptativos ao ping**: multiplicar delays por `ServerData.Latency` com CLAMP
  (lê 0/lixo entre zonas).
- **R9.2 Alertas**: regra → aviso no HUD + som (`SoundController`).
- **R9.3 Auto-switch de perfil por área** (`AreaName`/peaceful) — opt-in; decisão de produto.

---

## Ordem e porquê (v2)
R0 (fundações + preempção + sombra + checklist) → R1 (lote único de gates) → R2 (flasks — valor
imediato) → R3 (uptime de buffs) → R4 (rotação rica) → R5 (classes) → R6 (emergência) → R7
(perfis) → R8 (terreno) → R9 (polish). R7.1/R7.2 podem adiantar-se — só dependem de R0.
Spike de shapeshift corre em paralelo quando conveniente.

## Regras de aceitação por fase (auditoria)
1. Compila 0 erros; defaults não mudam comportamento (Princípio 1).
2. Gates novos: 1 sessão em modo sombra sem falsos positivos antes de enforcing (Princípio 2).
3. "prof:" sem pico novo atribuível à fase (Princípio 3).
4. Campo novo passa a verificação de plumbing do R0.6.

## Decisões de produto pendentes (perguntar antes de construir)
1. R5.3: incluir ação de auto-swap de weapon set, ou só gate?
2. R6.1: logout TCP de pânico entra? (risco/recompensa de hardcore)
3. R9.3: auto-switch de perfil por área — comportamento surpresa; opt-in?
4. Quando retomar a H1 do HP_ROTATION_PLANO em relação a este plano.

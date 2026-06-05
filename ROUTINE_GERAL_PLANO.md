# Plano Mestre de Melhorias do AutoPilot (v2 — pós-auditoria)

> Baseado na investigação do AutoMyAim + ExiledBot 2 v0.9.4d (ver memórias
> `exiledbot-combat-model-reference`, `autopilot-must-be-build-agnostic`).
> **v2: reescrito após auditoria crítica.** Correções aplicadas: (1) modelo de dados PRIMEIRO,
> (2) critério de equivalência por baseline de logs, (3) encadeamento temporal entre skills no modelo,
> (4) Kiting diferido para fora do caminho crítico.
> **NÃO COMEÇAR a construir até o utilizador aprovar.** Cada fase: isolada, compila 0 erros, commit
> próprio, e o UTILIZADOR TESTA antes da seguinte (protocolo a pente fino). Tudo opt-in: o IceShot e o
> combate atual continuam a funcionar durante TODA a construção.

O AutoPilot tem de servir QUALQUER build (requisito firme). Tudo é agnóstico à build, exceto o preset
de gelo (que é só uma configuração de exemplo).

---

## PRINCÍPIO CENTRAL DA AUDITORIA: o MODELO DE DADOS é a fundação

A Routine Geral NÃO é "mais um bloco" — é a **substituição do núcleo** de combate. Logo, o modelo de
skill enriquecido vem **PRIMEIRO**, e tudo o resto (shift-ao-atacar, raridade-alvo, condições) passa a
ser **campos desse modelo**, não código solto. Construir features no `IceShotRoutine` antigo seria
escrevê-las duas vezes (retrabalho garantido). Por isso a ordem foi invertida.

### Impedance mismatch a ter em conta (ExiledBot vs AutoPilot)
O ExiledBot **move-se na grid**; o AutoPilot **mira um cursor no ecrã**. Parâmetros do skills.ini que
assumem movimento (is_movement, kiting na grid) não transferem direto. O modelo do AutoPilot reusa as
CONDIÇÕES (raridade, buffs, cargas, recursos, distância) mas a ação é sempre "tap/hold de tecla +
cursor no alvo", não "andar para".

---

## FASE 0 — Quick wins triviais (commit único, sem fase própria)

Não merecem sub-projeto; são edições pontuais no arranque:
- **Expandir filtro de imunidade** (`EntityCache.InvulnBuffFragments`): juntar `divine_shrine`,
  `cannot_be_damaged_by_things_outside_radius`, `cannot_be_damaged_for_`, `cannot_be_damaged_by_enemies`.
  CUIDADO: validar cada um como fizemos com o Proximal (alguns são condicionais/temporários — não
  filtrar para sempre se for só temporário). É adicionar strings a um array existente.
- Teste: combate normal não muda; mobs com esses mods deixam de desperdiçar tiros.

---

## FASE 1 — MODELO DE DADOS (a fundação) [tem de vir antes de tudo]

Enriquecer o `SkillSlot` com TODAS as regras, e um avaliador puro. SEM mudar a rotação ativa ainda —
o `IceShotRoutine` continua a correr. Esta fase só ADICIONA o modelo e prova que ele consegue
EXPRIMIR a rotação de gelo, sem a substituir.

**1.1 — Classe de regras (`SkillRule` / campos no `SkillSlot`):**
- Tipo: `Tap` | `Hold até condição` | `Buff` (sem checks de alvo) | `Persistente`.
- Prioridade (maior = primeiro) + Cooldown próprio (ms).
- **`AttackInPlace` (bool)** ← o shift-ao-atacar vira um CAMPO da skill, não um bloco separado.
- Alcance: min/max distância ao alvo. **Flag `IgnoreRangeForUnique`** (bosses sempre alvo).
- Raridade do alvo: Todos | Magic+ | Rare+ | Só Unique | Só Normal.
- Cargas: min/max de power/frenzy/endurance.
- Recursos do player: min/max vida% / mana% / ES% (defaults que não filtram).
- Alvo TEM / NÃO TEM buff (nome) ; Player TEM / NÃO TEM buff (nome) ; Charges de buff X >= N.
- HP do alvo < / > X% (culling).
- `close_targets` (N) + `close_targets_range` (AoE/packs).
- **Encadeamento temporal (CRÍTICO — corrige a falsa equivalência do anti-corte):**
  `AfterSkill` (nome) + `AfterSkillDelayMs` = "só dispara N ms DEPOIS de a skill X ter saído".
  Isto modela o combo Barrage→Snipe corretamente (o Snipe entra DURANTE a janela de empower, não
  "depois da animação acabar" como o ExiledBot). O `BarrageCommitMs` é um caso deste mecanismo.
- Confirmação do hold (soltar quando): buff aparece (alvo/player) | charges baixam | skill em
  uso/cooldown (ActorSkill) | stage de animação >= N | timeout.

**1.2 — Avaliador puro `Evaluate(ctx, slot) -> bool`** (todas as condições em AND). Função sem efeitos
colaterais, testável isoladamente.

**Teste da Fase 1:** com o IceShot ainda ativo, log paralelo "o que o avaliador DECIDIRIA" para cada
skill, e confirmar que bate com o que o IceShot faz. Zero mudança de comportamento (o avaliador só
observa, não age).

---

## FASE 2 — BASELINE DE EQUIVALÊNCIA (o critério de aceitação) [antes do motor]

A auditoria apontou que "validar que o motor == IceShot" é subjetivo. Definir o critério **agora**,
ANTES de construir o motor:

- Gravar o `AutoPilot_actions.txt` do **IceShot atual** em 3 cenários fixos: (a) pack de lixo,
  (b) Rare que congela, (c) Boss/Unique. Guardar como **baseline** (ficheiro à parte).
- **Critério binário:** o motor geral PASSA se produzir a **mesma sequência de teclas, na mesma ordem,
  dentro de ±1 tick** nos mesmos cenários. Não é "parece igual" — é a sequência de TAP/HOLD/RELEASE a
  bater com o baseline.
- Isto usa os logs que JÁ existem (ActionLog). Sem infra nova.

---

## FASE 3 — MOTOR GENÉRICO (`GeneralRoutine : IRoutine`) [o principal]

Agora sim, o motor. Corre LADO A LADO com IceShot/Staff (o dropdown "Rotina de combate" já existe —
adicionar a opção "Geral"). O IceShot **não se toca**.

**3.1 — `GeneralRoutine`:** ordena as skills ativas por prioridade, e para cada uma chama o avaliador
da Fase 1. Tap/Hold conforme o tipo. Anti-corte = prioridades distintas + cooldown + o **encadeamento
temporal** (`AfterSkill`/`AfterSkillDelayMs`) da Fase 1.
**3.2 — Máquina de hold** lê a condição de soltar da config (reusa a máquina de canal atual).
**3.3 — Shift-ao-atacar** consumido do campo `AttackInPlace` (já no modelo da Fase 1).
**3.4 — UI:** expor as regras no menu de cada skill (ConditionalDisplay já em uso).

**Teste de cada sub-fase:** correr o motor nos 3 cenários e comparar com o **baseline da Fase 2**.
Passa = sequência igual ±1 tick. Falha = ver onde divergiu (o log diz).

---

## FASE 4 — PRESET DE GELO + APOSENTAR IceShot [fecho do projeto]

- **4.1 Preset "Ice Shot (Deadeye gelo)":** botão que preenche os SkillSlots com as regras do gelo
  (incluindo o encadeamento Barrage→Snipe via AfterSkill/AfterSkillDelayMs).
- **4.2 Validação final:** o motor com o preset tem de PASSAR o baseline da Fase 2 nos 3 cenários.
  Critério binário, não subjetivo. Se passa → o motor geral exprime a rotação real testada.
- **4.3** O IceShotRoutine fica como referência/preset; o motor geral passa a ser o caminho recomendado.
  (Não apagar o IceShot enquanto o motor não estiver provado em jogo pelo utilizador.)

---

## DIFERIDO — Kiting / Wiggle (sobrevivência) [FORA do caminho crítico]

A auditoria recomendou tirar isto do âmbito atual: mexe em MOVIMENTO (o mais arriscado), conflitua com
o controlo manual do utilizador, e **não bloqueia nada do resto**. Fica documentado para DEPOIS de o
motor geral estar provado. Quando se fizer: tudo desligado por defeito, só age com aim ativo, fases
mínimas (dodge roll de emergência → escape → wiggle → adaptive/boss-circling/dodge-reativo). Modelo do
ExiledBot [kiting] como referência.

---

## ORDEM FINAL (corrigida pela auditoria)

0. Quick wins (filtros de imunidade) — commit trivial.
1. **MODELO DE DADOS** + avaliador — a fundação (shift e condições viram campos aqui).
2. **BASELINE de equivalência** — gravar os logs do IceShot como critério binário.
3. **MOTOR genérico** — validado contra o baseline a cada sub-fase.
4. **PRESET de gelo** + validação final + aposentar IceShot.
— (diferido) Kiting/Wiggle, só depois do motor provado.

**Porque esta ordem:** o modelo primeiro elimina o retrabalho (shift e targeting são campos, não
sub-projetos); o baseline torna o "está igual?" falsificável; o encadeamento temporal no modelo evita
partir o combo de gelo; o Kiting diferido encurta o caminho crítico. Nada se parte sem o utilizador
ver e testar.

# Plano v2 — Melhorias por MODS de Monstro (pós-auditoria)

> Investigação: ver memória `monster-mods-reference`. Leitura-chave:
> `entity.GetComponent<ObjectMagicProperties>()?.Mods` → `List<string>` de mods internos. O AutoPilot
> JÁ usa `ObjectMagicProperties` (Rarity em EntityCache.ReadRarity / RuleEvaluator.SafeRarity).
>
> **v2: reescrito após auditoria crítica.** 4 correções aplicadas:
> (1) o DUMP é um GATE BLOQUEANTE — nada se constrói até os dados estarem provados;
> (2) um único modelo `ModRule` partilhado pelas 3 features (não 3 modelos);
> (3) M1+M2 fundidas com hierarquia explícita (targeting decide QUEM, rotação decide O QUÊ);
> (4) no Dodge, o mod AMPLIFICA um evento (mob a atacar), não cria perigo de um mob estático.
>
> **NÃO COMEÇAR a construir até o utilizador aprovar.** Cada fase: isolada, compila 0 erros, commit
> próprio, o utilizador TESTA antes da seguinte. Tudo aditivo/opt-in — o combate atual nunca se parte.

---

## PRINCÍPIO CENTRAL DA AUDITORIA: validar os DADOS antes de construir nada

Todo o projeto depende de uma premissa NÃO verificada: que `Mods` está populado, tem nomes string
LEGÍVEIS, e contém os mods que interessam (explosão/proj./volatile) com nome identificável. O próprio
"Dump nearby mods" do RareModScanner é a admissão de que ninguém sabe os nomes sem os descobrir. Por
isso o dump deixa de ser um extra e passa a ser o **GATE que decide se as fases seguintes são viáveis**.

---

## FASE M0 — DUMP COMO GATE BLOQUEANTE (validar os dados primeiro)

**Objetivo:** descobrir, com dados REAIS do teu jogo, se os mods são utilizáveis — ANTES de construir
qualquer lógica em cima deles. Leitura DIRETA (sem cache ainda — otimização prematura cortada).

- **M0.1 — Botão "[Geral] Dump mods perto" + acumulador:** lê `ObjectMagicProperties.Mods` de cada
  monstro perto (leitura simples, sem cache) e escreve para `AutoPilot_modnames.txt`: por monstro, a
  raridade + nome + lista de mods. Acumula nomes únicos já vistos (como o `AutoPilot_buffnames.txt`).
- **GATE DE DECISÃO (bloqueante):** com o teu output real, confirmar 3 coisas. Só se TODAS passarem é
  que M1/M2/M3 avançam:
  (a) os mods aparecem para a raridade que interessa (Rare/Unique — não só não-vazio para um tipo);
  (b) os nomes são STRINGS legíveis (não hashes/IDs);
  (c) os mods perigosos que queres (explosão/proj./volatile/on-death) estão lá com nome identificável.
- **SE o gate FALHAR:** M3 (dodge por mods) é inviável pela via dos mods → fica só com o sinal de
  evento (Action) que já temos; M1/M2 limitam-se aos mods que existirem. Replanear conforme o output.
- **Teste M0:** carregar o dump perto de Rares/Uniques → ler o ficheiro JUNTOS e decidir o gate. Zero
  mudança no combate.

---

## FASE M1 — Modelo unificado `ModRule` + leitura cacheada (a fundação, SÓ depois do gate)

**Objetivo (só se M0 passou):** UM único tipo de regra de mod partilhado pelas features, e a leitura
agora cacheada (já sabemos que vale a pena).

- **M1.1 — `ModReader`** (Combat/, irmão do BuffReader): `GetMods(Entity)` com cache por-tick
  (invalidada por `NewTick()` junto com o BuffReader) + `HasModMatching(entity, regex)`. Leitura
  defensiva, lista vazia em erro.
- **M1.2 — `ModRule` (tipo ÚNICO, partilhado):** `{ Regex, Rótulo, Peso }`. O mesmo tipo serve os 3
  consumidores (rotação, targeting, dodge) — não 3 modelos diferentes. Avaliador único:
  `Matches(entity)`. Compila o regex uma vez (case-insensitive); regex inválido = não casa + erro no
  rótulo (como o RareModScanner).
- **Teste M1:** sem UI ainda — log de observação "que mods deste alvo casariam com esta ModRule de
  teste". Zero mudança no combate.

---

## FASE M2 — Mods no TARGETING + na ROTAÇÃO, com hierarquia (M1+M2 antigas FUNDIDAS)

**Objetivo:** as duas features que tocam o mesmo dado, desenhadas JUNTAS com hierarquia explícita,
porque interagem (a auditoria provou: o targeting escolhe o alvo ANTES da rotação correr).

**HIERARQUIA (regra de ouro): o TARGETING decide QUEM; a ROTAÇÃO decide O QUÊ contra esse alvo.**
A rotação só vê o alvo que o targeting deixou passar. Peso negativo num mod ESCONDE o alvo da rotação.

- **M2.1 — Targeting por mod (WeightEngine):** lista de `ModRule` de targeting; o peso de cada
  `TrackedEntity` soma o `Peso` das ModRules que casam (via ModReader). Positivo = priorizar; negativo
  = despriorizar. **LIMITE:** o delta é limitado para NÃO dominar a prioridade boss>rare>lixo (testar
  contra o baseline de targeting / linha `pick:`).
- **M2.2 — Gate de mod na rotação (RuleEvaluator):** `SkillRule.TargetMatchesMod` (regex, vazio =
  ignora) — usar uma skill só contra o alvo (que o targeting JÁ escolheu) se ele casa o mod. Usa o
  mesmo `ModRule.Matches`.
- **M2.3 — UI + perfis:** submenu "Mods" com a lista de ModRules de targeting (ContentNode, como as
  Skills) + o campo `[Geral]` de mod por skill. ProfileData guarda ambos.
- **TESTE CONJUNTO (não isolado — interagem):** confirmar (linha `pick:`) que um Rare com mod sobe/desce
  na escolha SEM partir boss>rare>lixo, E que a skill com gate de mod só dispara contra o alvo escolhido
  que casa. Documentar: ativar peso negativo num mod esconde esse alvo da rotação.

---

## FASE M3 — Dodge inteligente: o mod AMPLIFICA o evento (não cria perigo)

**Objetivo:** o Dodge fica mais inteligente — MAS corrigindo a falsa equivalência da auditoria. Um mod
é uma propriedade ESTÁTICA (potencial), não um evento iminente. Um Rare "volatile" parado é inofensivo;
só é perigoso quando MORRE/ATACA.

**FÓRMULA CORRETA:** perigo = (mob a atacar perto, via `Actor.Action=="UsingAbility"` que o
DangerDetector já lê) × (multiplicador SE esse mob tem mod perigoso). **O mod AMPLIFICA o perigo de um
mob que JÁ ESTÁ A AGIR; nunca cria perigo de um mob estático.** Sem isto, o dodge fugiria
permanentemente de qualquer Rare com mod mau (o medo recorrente do utilizador).

- **M3.1 — Lista de mods perigosos (`ModRule` com peso):** regex + multiplicador. **Defaults VAZIOS**
  até o dump (M0) dar os nomes reais — não inventar defaults antes de os ver.
- **M3.2 — DangerDetector:** mantém o "mob a atacar perto" que já tem; multiplica o score DESSE mob
  pelo peso das ModRules perigosas que ele casa. Opcional: alargar um pouco o alcance de perigo só para
  mobs com mod perigoso JÁ a agir.
- **M3.3 — UI:** na secção Kiting, a lista de mods perigosos (ModRule). Tudo opt-in (o Dodge já tem o
  toggle).
- **Teste M3:** perto de um Rare com mod perigoso A ATACAR → score `danger:` mais alto (dodge mais
  cedo) que perto de lixo a atacar. Perto do mesmo Rare PARADO → NÃO dispara dodge (prova que o mod
  amplifica o evento, não cria perigo).

---

## ORDEM FINAL (corrigida pela auditoria)

0. **M0 — DUMP como GATE** — validar os dados reais. BLOQUEIA tudo o resto até passar.
1. **M1 — ModReader + ModRule único** — fundação (cache + modelo partilhado), só depois do gate.
2. **M2 — Mods no targeting + rotação (fundidas)** — hierarquia targeting→rotação, teste conjunto.
3. **M3 — Dodge: mod amplifica evento** — não cria perigo de mob estático.

**Porquê:** o dump-como-gate evita construir sobre dados inexistentes (a maior falha apanhada); o
`ModRule` único elimina 3 modelos redundantes; fundir M1+M2 elimina um ciclo de teste e mapeia a
interação; o "mod amplifica evento" evita o dodge em pânico permanente. Cada fase opt-in e testada; o
combate atual nunca se parte.

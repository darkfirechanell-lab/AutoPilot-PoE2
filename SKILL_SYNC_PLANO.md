# Plano — Deteção de skills só quando MUDAM (otimização, v2 pós-auditoria)

> **Origem:** ideia do utilizador (re-detetar ao fechar a UI de skills/G). A auditoria mostrou uma
> abordagem MAIS SIMPLES e MAIS ROBUSTA que cobre todos os casos (G, weapon-swap, árvore, efeitos) num só
> mecanismo: **usar o HASH de skills que o SkillDetector JÁ calcula** como gatilho do trabalho caro.
>
> **NÃO COMEÇAR a construir até o utilizador aprovar.** Compila 0 erros, commit próprio, o utilizador
> TESTA. A deteção é CRÍTICA (se partir, o plugin não usa skills) — testar com cuidado.

---

## PROBLEMA (medido com CERTEZA, 6 Jun) + DESCOBERTA da auditoria

Profiling interno: `sync` = 99ms (1×, ao entrar na zona) + ~5ms periódico + overhead constante do RelinkLive.

**A causa real, confirmada no código (SkillDetector.Sync, linhas 53-56):**
```
var equipped = ResolveEquipped(actorSkills);          // ← CARO, corre SEMPRE
var hash = ComputeHash(equipped, shortcuts);
if (!force && hash == _lastHash) { RelinkLive(slots, equipped); return; }  // reusa equipped (bom)
```
- O `Sync` JÁ reusa o `equipped` no RelinkLive (passa-o como argumento) — isso está bem.
- **MAS o `ResolveEquipped` (a operação cara: `.Where().GroupBy(Id2).OrderByDescending().ThenBy().Select()`)
  corre SEMPRE na linha 53, mesmo quando o hash não mudou.** É o desperdício central.
- **PIOR:** o `RelinkLive` chamado no TickInner (todos os ticks) é a sobrecarga `RelinkLive(slots)` SEM
  passar equipped → lá dentro faz `equipped ??= ResolveEquipped(...)` → refaz a operação cara TODOS os
  ticks. (Confirmado em SkillDetector.cs:92-94.)

**Conclusão da auditoria:** o núcleo NÃO é "quando correr o Sync" — é **NÃO refazer `ResolveEquipped`
quando as skills não mudaram**. E o SkillDetector já tem a ferramenta para saber se mudaram: o `_lastHash`.

---

## A SOLUÇÃO (auditada): gatilho por HASH, uma fonte única de "equipped"

Em vez de detetar UIs específicas (G), usa-se a mudança do HASH das skills como gatilho — cobre G,
weapon-swap (2 sets do PoE2!), árvore de passivas, efeitos de jogo: QUALQUER mudança, num só mecanismo.

**Princípio:** um único `ResolveEquipped` por tick NO MÁXIMO (não evitável de todo, porque o hash precisa
dele), mas o trabalho PESADO a jusante (reconstruir a lista de slots, atribuir teclas) só corre quando o
hash muda. E o RelinkLive deixa de chamar um SEGUNDO `ResolveEquipped` — reusa o do Sync.

---

## FASE S0 — MEDIÇÃO (verificar a premissa antes de otimizar) [da auditoria]

**Objetivo:** responder a 2 perguntas que decidem o desenho, ANTES de mexer. Só medição/log.

- **S0.1 — Quanto custa o `ResolveEquipped` isolado?** Cronometrá-lo à parte (não o Sync inteiro) e logar.
  Confirma se ele é mesmo o grosso do custo (a auditoria diz que sim; medir confirma).
- **S0.2 — Os objetos `ActorSkill` sobrevivem entre ticks?** Guardar a lista `equipped` num tick e no
  seguinte verificar se ainda são válidos (`Address != 0`, ler `.CanBeUsed` sem rebentar). Decide se dá
  para CACHEAR a lista (reusar N ticks) ou se tem de ser re-resolvida (e aí só quando o hash muda).
- **GATE S0:** com os dados, escolher entre as 2 vias da S1 (cache de equipped vs resolve-só-quando-muda).

---

## FASE S1 — Uma fonte ÚNICA de "equipped" por tick, trabalho pesado só quando o hash muda

**Objetivo (o núcleo, era a antiga S2):** eliminar o `ResolveEquipped` DUPLICADO (Sync + RelinkLive) e o
trabalho pesado quando nada mudou.

- **S1.1 — RelinkLive deixa de re-resolver:** o TickInner passa a fazer `ResolveEquipped` UMA vez e a
  passar o resultado ao RelinkLive (que já aceita o parâmetro `equipped`). Acaba o 2º ResolveEquipped/tick.
- **S1.2 — Trabalho pesado gated pelo hash:** computar o hash (barato, sobre o equipped já resolvido); se
  == `_lastHash`, só RelinkLive (re-liga refs vivas, barato); se mudou, fazer a reconstrução completa de
  slots + teclas. (É o que o Sync já faz — falta é o TickInner usar este caminho em vez de chamar
  RelinkLive cru que re-resolve.)
- **S1.3 — SE S0.2 disse que os objetos sobrevivem entre ticks:** cachear `equipped` e só re-resolver a
  cada N ticks (ex.: 10) OU quando um check barato sinalizar mudança. Reduz ainda mais. SENÃO: manter
  resolve-por-tick mas só 1× (já é metade do custo atual).
- **Teste S1:** `prof: sync` por-tick cai para perto do custo de 1 ResolveEquipped (ou menos com cache);
  trocar gema (G) / weapon-set → skills re-detetadas; skills NÃO se perdem num mapa inteiro.

---

## FASE S2 — (se ainda valer) Reduzir a frequência do ResolveEquipped

**Só se S1 não chegar.** Se o ResolveEquipped por-tick ainda pesar, espaçá-lo: corrê-lo a cada N ticks
(ex.: 5-10) em vez de todos. Entre resoluções, o RelinkLive usa a última lista (se S0.2 permitir) ou
salta (as refs aguentam alguns ticks). O hash continua a apanhar mudanças no próximo resolve.

- **Teste S2:** medir o ganho adicional no `prof:`; confirmar que um atraso de N ticks a detetar uma
  troca de skill é aceitável (N pequeno = ~100ms, imperceptível).

---

## FASE S3 — Gatilhos explícitos OPCIONAIS (refinamento, não essencial)

Com o gatilho-por-hash, isto é só "fazer o check de hash imediatamente" em momentos óbvios, para a
deteção ser instantânea em vez de esperar o próximo ciclo:

- Ao FECHAR o SkillPanel (G) — a ideia original do utilizador, agora como aceleração (não como único
  gatilho). `IngameUi.SkillPanel.IsVisible` (confirmado existir via GemRecommender).
- Já cobertos pelo hash de qualquer forma; estes só tornam a deteção imediata. Opcional.

---

## ORDEM FINAL (corrigida pela auditoria)

0. **S0 — Medição** — custo do ResolveEquipped + validade dos ActorSkill entre ticks. Decide o desenho.
1. **S1 — Fonte única de equipped + pesado-só-quando-hash-muda** — O NÚCLEO (era a antiga S2). Elimina o
   ResolveEquipped duplicado e o trabalho à toa.
2. **S2 — (se preciso) espaçar o ResolveEquipped** — frequência menor.
3. **S3 — (opcional) gatilho imediato no fecho do G** — a ideia original como aceleração.

**As 3 correções da auditoria, aplicadas:**
1. ORDEM invertida — o núcleo é o RelinkLive/ResolveEquipped (antiga S2), não "quando correr o Sync"
   (antiga S1). S1 sozinha não resolvia o overhead constante.
2. PREMISSA verificada primeiro (S0): a validade dos ActorSkill entre ticks decide se dá para cachear —
   medir antes de construir, não assumir.
3. WEAPON-SWAP coberto — o gatilho-por-hash apanha a troca de weapon-set (sem UI), que o gatilho "fecho do
   G" deixava de fora (skills erradas até ao fallback). O hash é uma rede que cobre TUDO, não opcional.

**Porquê é mais simples E mais robusto:** um só mecanismo (hash) cobre G + weapon-swap + árvore + efeitos,
em vez de detetar cada UI; o trabalho caro só corre quando algo muda; reusa o `_lastHash`/`ResolveEquipped`/
`RelinkLive` que já existem. Ver [[autopilot-next-session-dureza]].

**Nota:** o pico de 99ms da 1ª deteção (entrada na zona) é inerente (resolve tudo do zero 1×). Acontece no
load, não num pack — aceitável.

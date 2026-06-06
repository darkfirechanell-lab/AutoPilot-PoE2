# Plano — Otimizar o Rebuild do EntityCache (pico de 14ms em packs grandes)

> **Pedido do utilizador: estudar BEM antes de mexer** — o Rebuild é o coração do targeting (corre a cada
> tick, processa TODOS os mobs). Mexer à pressa é arriscado. Este plano é o estudo + opções, sem código.
>
> **NÃO COMEÇAR a construir até o utilizador aprovar.** Cada fase: medição primeiro, compila 0 erros,
> commit próprio, o utilizador TESTA.

---

## ESTUDO: o que o Rebuild faz e ONDE estão os 14ms

`EntityCache.Rebuild()` (a cada tick): limpa a lista, lê os monstros de `ValidEntitiesByType[Monster]`, e
POR CADA mob faz:
1. `Vector2.Distance` — **barato** (matemática pura).
2. `IsValidTarget(entity, dist)` — **caro** (leituras de memória do jogo):
   - `IsValid/IsAlive/IsDead/IsTargetable/IsHidden/IsHostile` (6 flags)
   - `entity.Path` (string) + loop sobre ExcludedPrefixes
   - `entity.Stats` (dicionário de stats — leitura pesada) + TryGetValue
   - `BuffsBlockTarget` → lê o componente `Buffs` + percorre a `BuffsList` (a leitura MAIS cara)
3. `ReadRarity(entity)` — lê o componente `ObjectMagicProperties` (memória).

**Conclusão do estudo:** o custo é **O(n) × leituras-de-memória-por-mob**, onde as mais caras são `Buffs`
(BuffsList) e `Stats`. Num pack de 60+ mobs, são ~60× (Buffs + Stats + ObjectMagicProperties + Path) por
tick. É por isso que pica a 14ms só em packs grandes — escala com o nº de mobs.

**O que JÁ está otimizado (não tocar):** os buffs já se leem 1×/mob (BuffsBlockTarget unificado). Bom.

---

## PRINCÍPIO: reduzir TRABALHO POR MOB e/ou Nº DE MOBS processados — sem perder alvos válidos

Há 3 vias, da mais segura à mais agressiva. O estudo prefere começar pela mais segura.

---

## FASE R0 — MEDIÇÃO FINA (saber o custo REAL por sub-operação) [gate]

**Objetivo:** antes de otimizar, medir QUAL sub-leitura domina (Buffs? Stats? Rarity? Path?) e quantos
mobs entram no loop. Não adivinhar qual cortar. Só log.

- **R0.1 — Logar nº de mobs no `source`** (quantos o loop processa) e quantos passam o filtro.
- **R0.2 — Cronometrar as sub-partes** do IsValidTarget num tick de pico (flags vs Path vs Stats vs Buffs)
  — ou, mais simples, medir IsValidTarget vs ReadRarity em separado.
- **GATE R0:** com os dados, confirmar: (a) o custo escala mesmo com nº de mobs; (b) qual sub-leitura
  domina. Decide qual fase seguinte aplicar.

---

## FASE R1 — ORDENAR os checks do mais barato ao mais caro (early-out) [mais segura]

**Objetivo:** o IsValidTarget já faz early-out, mas a ordem pode pôr leituras caras antes de baratas.
Reordenar para que as caras (Buffs, Stats) só corram se as baratas (flags, distância) passarem.

- **R1.1 — Distância primeiro:** mobs além do Attack Range vão ter peso 0 no WeightEngine de qualquer
  forma. Se filtrar por distância LOGO no Rebuild (antes de ler Buffs/Stats), os mobs longe nem pagam as
  leituras caras. CUIDADO: o DangerDetector e o ClusterEngine podem precisar de mobs um pouco além do
  Attack Range — confirmar o alcance máximo de QUALQUER consumidor antes de cortar (senão parte o dodge/
  cluster). Usar o MAIOR alcance entre AttackRange/DangerRange/cluster como limite.
- **R1.2 — Flags baratas antes de Path/Stats/Buffs:** já está (linhas 145-146 antes). Confirmar que Stats
  e Buffs são mesmo os últimos.
- **Teste R1:** `prof: rebuild` cai em packs grandes (mobs longe deixam de pagar Buffs/Stats); NENHUM alvo
  válido perto desaparece; o dodge e o cluster continuam a funcionar (não cortar mobs que eles usam).

---

## FASE R2 — CACHE de validade por-id (não reler tudo de mobs já vistos) [a estudar]

**Objetivo:** muitos mobs persistem entre ticks. Algumas propriedades NÃO mudam (Path, Rarity são fixas
para um id). Cacheá-las evita reler `ObjectMagicProperties`/Path a cada tick.

- **R2.1 — Cache de Rarity e Path por id** (mudam? NÃO para um mob vivo). Ler 1× e reusar.
  - PREMISSA A VERIFICAR (como no SKILL_SYNC): os ids são estáveis entre ticks e a cache não fica com
    lixo de mobs mortos (limpar como o WeightEngine.PruneCache já faz).
- **R2.2 — O que NÃO cachear:** IsAlive/IsTargetable/Buffs MUDAM (mob morre, fica imune, etc.) — esses
  têm de reler. Só cachear o IMUTÁVEL (Rarity, Path).
- **Teste R2:** medir o ganho; confirmar que mobs que mudam de estado (morrem, ficam imunes) são
  reavaliados corretamente (não ficam "presos" na cache).

---

## FASE R3 — (agressiva, só se preciso) LIMITAR mobs processados [último recurso]

**Objetivo:** se mesmo assim picar, processar só os N mobs mais perto (o targeting só quer o melhor alvo;
mobs muito longe são irrelevantes). MAS isto pode afetar o cluster (densidade) e o danger.

- **R3.1 — Pré-filtro por distância barata** (só Vector2.Distance, sem leituras) → ordenar/cortar aos N
  mais perto → só esses pagam IsValidTarget. RISCO: o cluster/danger podem querer a contagem total.
- **Teste R3:** confirmar que cortar mobs longe NÃO muda a escolha de alvo nem parte o cluster/dodge.

---

## ORDEM FINAL

0. **R0 — Medição fina** — qual sub-leitura domina + nº de mobs. Gate.
1. **R1 — Reordenar + filtro de distância cedo** — a mais segura; mobs longe não pagam leituras caras.
2. **R2 — Cache do imutável (Rarity/Path) por id** — se R1 não chegar; premissa verificada 1º.
3. **R3 — Limitar nº de mobs** — último recurso; cuidado com cluster/danger.

**Cuidado transversal (o que torna o Rebuild perigoso de mexer):** o snapshot do Rebuild alimenta o
targeting, o cluster, o danger/dodge E o auto-dump. Qualquer corte (distância, nº de mobs) tem de
respeitar o MAIOR alcance de TODOS os consumidores — senão parte uma feature silenciosamente. Por isso a
ordem começa pela reordenação (não corta nada) e só corta mobs no fim, com teste.

Ver [[autopilot-next-session-dureza]] (diagnóstico: rebuild=14ms em packs grandes).

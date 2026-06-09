# Plano — Otimizar o Rebuild do EntityCache (v2 pós-auditoria; pico de 14ms em packs grandes)

> **v2 (8 Jun): 4 emendas de auditoria.** (1) NÃO trocar Vector2.Distance pelo DistancePlayer (unidades);
> (2) R0 inventaria o alcance de cada consumidor (decide viabilidade do filtro de distância); (3) ordem
> R1↔R2 invertida — CACHE primeiro (segura, garantida), filtro de distância depois (condicional); (4)
> micro-medição da premissa antes de cachear (lição do SKILL_SYNC). R3 cortada do plano.

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

## IDEIAS DO AutoMyAim (investigado a pedido do utilizador, 8 Jun)

Comparámos com o EntityScanner.cs do AutoMyAim (a base do AutoPilot). 2 diferenças importantes que
confirmam e melhoram este plano:

1. **A ideia a roubar é a ORDEM, não a função de distância.** O AutoMyAim filtra por distância ANTES de
   qualquer leitura cara → mobs longe são descartados de imediato, nem pagam Stats/Buffs. Nós lemos
   Stats/Buffs de TODOS (incluindo longe). → o ganho vem de **filtrar primeiro, ler caro depois**.
   ⚠️ EMENDA AUDITORIA: NÃO substituir o nosso `Vector2.Distance` pelo `entity.DistancePlayer` do
   framework — podem ser unidades DIFERENTES (mundo vs grid, momento de atualização, altura). Se a unidade
   diferir, TODOS os limiares (AttackRange=60, DangerRange=25, Proximal=25) ficam calibrados na unidade
   errada → aim/dodge/proximal com distâncias erradas. Manter o nosso `Vector2.Distance` (barato, já
   calibrado) como o filtro; só mudar a ORDEM (distância → filtro → leitura cara).
2. **Separa em FASES:** distância (barato) → leitura cara só nos candidatos. Nós fazemos tudo num loop.

NOTA: o AutoMyAim NÃO lê Buffs no scan (só Stats.CannotBeDamaged) — nós lemos Buffs (BuffsBlockTarget)
para o Proximal Tangibility e imunidade. Essa leitura é nossa e é a mais cara; filtrar por distância antes
dela dá-nos MAIS ganho que ao AutoMyAim — MAS só se houver alcance a cortar (ver o inventário na R0).

---

## FASE R0 — MEDIÇÃO FINA + INVENTÁRIO DE ALCANCES [gate] [EMENDA AUDITORIA]

**Objetivo:** antes de otimizar, medir (a) QUAL sub-leitura domina e (b) — CRÍTICO — qual o alcance REAL
de cada consumidor do snapshot. Sem (b), a R1 não pode ser desenhada (pode partir o cluster/dodge em
silêncio ou não cortar nada). Só log/inventário, zero mudança de combate.

- **R0.1 — Logar nº de mobs no `source`** (quantos o loop processa) e quantos passam o filtro.
- **R0.2 — Cronometrar as sub-partes** do IsValidTarget (flags vs Path vs Stats vs Buffs) ou, mais simples,
  IsValidTarget vs ReadRarity em separado, num tick de pico.
- **R0.3 — INVENTÁRIO DE ALCANCES (decide a viabilidade da R1):** mapear, no código, a distância MÁXIMA a
  que cada consumidor lê o snapshot:
  - WeightEngine: `MaxDistance` = AttackRange (60) → mobs além ficam com peso 0.
  - DangerDetector: `DangerRange` (25).
  - ClusterEngine: **percorre o snapshot TODO sem limite de distância?** Se sim, a R1 (filtro por
    distância) NÃO pode cortar mobs sem partir o cluster → R1 fica INVIÁVEL e o plano usa só a R2.
  - auto-dump: itera os Monsters todos (mas é opt-in/diagnóstico, não bloqueia).
  Determinar o MAIOR desses alcances. Só se houver mobs ALÉM desse máximo é que a R1 corta algo.
- **GATE R0:** confirmar (a) o custo escala com nº de mobs; (b) qual sub-leitura domina; (c) **há alcance a
  cortar?** (= existem mobs no source além do maior alcance dos consumidores). Se NÃO houver, R1 é inútil →
  ir direto à R2. Os dados decidem a ordem.

---

## FASE R1 — CACHE do IMUTÁVEL por id (a MAIS segura e ganho garantido) [EMENDA: era a R2]

**Objetivo (o núcleo, agora 1º):** Rarity e Path NÃO mudam para um mob vivo, mas são relidos da memória a
CADA tick para o mesmo mob. Cacheá-los por id evita reler `ObjectMagicProperties`/Path a cada frame. NÃO
altera o que os consumidores veem (só evita reler o mesmo) → seguro, e dá ganho mesmo sem cortar mobs.

- **R1.0 — MICRO-MEDIÇÃO antes de cachear (lição do SKILL_SYNC S0):** logar se algum id REAPARECE com
  Rarity/Path DIFERENTE (= id reciclado por um mob novo, OU raridade que muda em fase de boss). Só cachear
  o que for COMPROVADAMENTE imutável. Decide o desenho da cache.
- **R1.1 — Cache `{id → (Rarity, Path, Address)}`:** a chave é o id, MAS guarda também o `Address` como
  discriminador anti-reciclagem — se o mesmo id reaparecer com Address diferente, é um mob NOVO → invalida
  e relê. Limpa ids mortos (como o WeightEngine.PruneCache já faz).
- **R1.2 — O que NÃO cachear:** IsAlive/IsTargetable/IsHidden/Buffs/Stats MUDAM (mob morre, fica imune,
  ganha Proximal) → reler SEMPRE. Só cachear o imutável validado na R1.0 (Rarity, Path).
- **Teste R1:** `prof: rebuild` cai (Rarity/Path deixam de se reler); mobs que mudam de estado (morrem,
  ficam imunes) são reavaliados corretamente; um id reciclado não devolve dados do mob antigo (Address).

---

## FASE R2 — FILTRAR por distância antes da leitura cara (SÓ se a R0 provar alcance a cortar) [EMENDA: era a R1]

**Objetivo:** mobs longe não pagam as leituras caras (Buffs/Stats). Mas SÓ se a R0.3 (inventário) provar
que existem mobs ALÉM do maior alcance dos consumidores. Se o cluster usa o snapshot todo, esta fase é
INVIÁVEL — saltar.

- **R2.1 — Ordem: distância (nosso `Vector2.Distance`, barato) → flags baratas → SÓ ENTÃO Stats/Buffs.**
  Mobs além do maior-alcance-dos-consumidores: adicionar ao snapshot SEM ler Buffs/Stats (com peso 0
  garantido), ou não adicionar de todo se nenhum consumidor os usa. NÃO trocar pelo DistancePlayer (emenda).
- **Teste R2:** `prof: rebuild` cai mais em packs grandes; NENHUM alvo válido perto desaparece; o dodge e o
  cluster continuam a funcionar (confirmar contra o inventário da R0.3).

---

## FASE R3 — (NÃO planeada; só se R1+R2 provarem-se insuficientes) LIMITAR nº de mobs [EMENDA: cortada]

Cortada do plano como fase. A S1 do SKILL_SYNC mostrou que uma cache bem feita resolve sozinha. A R3
(processar só os N mais perto) é a mais arriscada (afeta cluster/danger) e provavelmente desnecessária.
Só reconsiderar se a medição, depois de R1+R2, provar que ainda pica — e aí com o seu próprio gate.

---

## ORDEM FINAL (pós-auditoria: R1↔R2 invertidas, R3 cortada)

0. **R0 — Medição fina + INVENTÁRIO de alcances** — qual leitura domina + há alcance a cortar? Gate.
1. **R1 — Cache do imutável (Rarity/Path) por id** — a MAIS segura, ganho garantido, não altera o que os
   consumidores veem. Com micro-medição anti-reciclagem (Address) primeiro.
2. **R2 — Filtrar por distância antes das leituras caras** — SÓ se a R0.3 provar alcance a cortar; senão
   inviável (cluster usa o snapshot todo). Manter o nosso Vector2.Distance.
3. **R3 — (cortada)** — só se R1+R2 não chegarem, com gate próprio.

**As 4 EMENDAS da auditoria (no plano):** (1) NÃO substituir Vector2.Distance pelo DistancePlayer (unidades
podem diferir → limiares errados); (2) R0 inventaria o alcance REAL de cada consumidor (decide se a R2 é
viável); (3) ordem invertida — cache (R1) é mais segura que filtro de distância (R2), e vem 1º; (4) R2/R1
têm micro-medição da premissa antes de cachear (lição do SKILL_SYNC: medir, não assumir).

**Cuidado transversal:** o snapshot alimenta targeting + cluster + danger/dodge + auto-dump. A cache (R1)
é segura porque não muda o que veem. O filtro de distância (R2) só é seguro se respeitar o MAIOR alcance
de todos — por isso depende do inventário da R0.3.

Ver [[autopilot-next-session-dureza]] (diagnóstico: rebuild=14ms em packs grandes).

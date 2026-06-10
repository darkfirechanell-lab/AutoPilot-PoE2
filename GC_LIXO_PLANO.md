# Plano — Reduzir pauses de GC no Rebuild (v2 pós-auditoria)

> **Contexto:** o pico de 16ms no Rebuild é um FREEZE GLOBAL (todas as secções — rebuild/routine/target/
> aim — picam no MESMO tick). O Rebuild nosso normal = 45us. Logo o pico NÃO é o nosso código por mob; é
> uma pausa do thread inteiro → suspeita: GC (.NET para tudo p/ limpar memória) OU o jogo PoE2 a engasgar.
>
> **Auditoria REJEITOU o plano original (object pool).** 3 razões: (1) premissa "é GC" não provada;
> (2) pool corrompe `_currentTarget` (guarda ref a item de `_monsters` entre ticks → pool reescreve →
> ataca alvo errado); (3) há solução mais simples (struct) que evita o pool. Este plano corrige isso.
>
> **NÃO COMEÇAR código de otimização até o GATE H0 provar que é GC.** Cada fase: medir, compila 0 erros,
> commit, testar.

---

## FASE G0 — GATE: PROVAR que é GC (bloqueante) [da auditoria]

**Objetivo:** confirmar que o pico de 16ms coincide com uma corrida do GC. Se NÃO for GC → todo o plano
é cancelado (seria otimizar problema inexistente). Zero código de otimização.

- **G0.1 — Cruzar pico com gc_delta:** o PerfWatchdog JÁ regista `gc0_delta/gc1_delta/gc2_delta` no
  perf.log. Quando houver um tick com `max_tick_ms` alto (~16ms), ver se nesse intervalo o gc_delta
  SOBE (gen0 sobe muito / gen1/gen2 sobe = pause maior). Normal observado: gc0~55, gc1~40, gc2~4 por
  amostra. Um spike de gc no tick do freeze = GC confirmado.
- **GATE G0:** SE o gc_delta NÃO sobe no tick do pico → NÃO é GC (é o jogo nativo / loading) → CANCELAR
  o plano; o pico é externo e não-otimizável pelo nosso lado. SE sobe → é GC → G1 avança.
- **Nota:** este gate é dados que JÁ existem (perf.log). Só ler, não construir.

---

## FASE G1 — Achar a MAIOR fonte de lixo (não assumir que é o TrackedEntity) [da auditoria]

**Objetivo (só se G0 = GC):** o `new TrackedEntity` por mob é PEQUENO. A auditoria aponta suspeitos
maiores: as **strings de debug** (`$"..."` interpolado a CADA tick com WriteLogs/ShowDebug ligado) geram
muito mais lixo que 40 structs. Medir antes de otimizar a coisa errada.

- **G1.1 — Teste A/B simples:** jogar com "Gravar logs" e "Mostrar texto" DESLIGADOS num pack grande, vs
  ligados. Se o pico de 16ms desaparece com logs desligados → a fonte de lixo são as STRINGS de debug,
  não o TrackedEntity. (As linhas `prof:`/`skillsync:`/`rebuildcache:` são interpolação pesada por tick.)
- **G1.2 — Inventário de alocações por-tick no Rebuild/Tick:** `new TrackedEntity` (40/tick), `new List<>`
  no ResolveEquipped (já cacheado pela S1), strings de debug, `_staleIds` (reusado, ok). Confirmar qual
  domina.
- **GATE G1:** decide o alvo da otimização: strings de debug (G2a) vs TrackedEntity (G2b).

---

## FASE G2a — (se as strings dominam) Reduzir lixo de debug [mais provável + mais simples]

- **G2a.1 — Só construir a string de debug quando MUDA / com throttle.** O DebugLog já só GRAVA quando
  muda, MAS a string é CONSTRUÍDA (`$"..."`) a cada tick antes de comparar. Construir só a cada N ms, ou
  só quando ShowDebug/WriteLogs ligado E o estado mudou. Elimina o grosso do lixo sem tocar no combate.
- **Teste G2a:** com logs ligados, o pico de 16ms cai; gc_delta baixa; combate igual.

---

## FASE G2b — (se o TrackedEntity domina) struct, NÃO object pool [da auditoria: struct > pool]

**Objetivo:** eliminar a alocação heap do TrackedEntity SEM object pool (que corromperia `_currentTarget`).
Tornar `TrackedEntity` um `struct` (value type) → vive na lista, zero alocação heap, zero GC, e copia por
VALOR (resolve o problema do `_currentTarget` automaticamente — uma cópia não é reescrita pelo scan).

- **PRÉ-REQUISITO (SPOF do `_currentTarget`):** com struct, `_currentTarget = SelectTarget(...)` guarda uma
  CÓPIA (não ref ao item da lista). O scan do tick seguinte reescreve a LISTA, não a cópia → `_currentTarget`
  mantém os dados do alvo. ✓ Resolve o SPOF que rejeitou o pool.
- **G2b.1 — Verificar os consumidores que MUTAM a lista durante o tick:** `WeightEngine`/`ClusterEngine`
  fazem `monsters[i].Weight *= x` e `m.Weight = ...`. Com `List<struct>` e acesso por ÍNDICE (`monsters[i].Weight = x`)
  isto FUNCIONA (modifica em-place). MAS via `IReadOnlyList<TrackedEntity>` ou `foreach (var m in ...)`
  a struct é COPIADA → a escrita perde-se. CRÍTICO: confirmar que todos os que escrevem Weight usam
  índice (`monsters[i].X`), não `foreach var m`. (WeightEngine.Apply usa `foreach (var m in monsters) m.Weight=...`
  — isto QUEBRA com struct. Tem de mudar para `for` com índice.)
- **G2b.2 — Mudar `init`→`set`:** com struct, os campos precisam de set. Reset COMPLETO no scan (Entity,
  Distance, Rarity, Weight) — campo esquecido = dados stale.
- **Teste G2b:** gc_delta cai; targeting CORRETO (alvo certo, sticky funciona); pesos aplicados (não
  perdidos por cópia de struct).
- **AVISO:** struct toca em MUITOS sítios (todos os que mutam Weight). Mais arriscado que G2a. Só se G1
  provar que o TrackedEntity é mesmo a fonte (improvável vs strings).

---

## ORDEM FINAL

0. **G0 — GATE: provar GC** (ler perf.log gc_delta no tick do pico). Se não-GC → CANCELAR tudo.
1. **G1 — Achar a maior fonte de lixo** (teste A/B logs on/off). Provavelmente strings, não TrackedEntity.
2. **G2a — Reduzir lixo de debug** (se strings) — simples, sem risco de combate.
3. **G2b — struct (NÃO pool)** (se TrackedEntity) — resolve o SPOF do `_currentTarget` por cópia-de-valor,
   MAS toca em todos os que mutam Weight (WeightEngine usa `foreach`, tem de virar `for` índice).

**As 3 emendas da auditoria:** (1) GATE de confirmação GC antes de código; (2) medir a MAIOR fonte de
lixo (strings prováveis, não TrackedEntity); (3) se for o TrackedEntity → STRUCT (copia por valor, resolve
o `_currentTarget`) em vez de object POOL (que reescreveria o objeto pooled guardado em `_currentTarget` →
alvo errado). Object pool CORTADO do plano.

**Provável desfecho:** G0 confirma GC leve, G1 mostra que são as strings de debug, G2a resolve com
throttle de string. O TrackedEntity (G2b) provavelmente nem é preciso. Se o pico for o jogo nativo, G0
cancela tudo — e aceita-se que 16ms ocasional é externo (o que era NOSSO, os buffs, já está resolvido:
3.3ms→71us).

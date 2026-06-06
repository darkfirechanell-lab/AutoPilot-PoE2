# Plano — Rotação adaptada à DUREZA do mob (v6: 3 níveis, pós-auditoria)

> **v6 (2026-06-06): 3 emendas de auditoria aplicadas** — (1) score unificado baseline/fallback via mediana
> sintética; (2) Magic limitado a MEDIUM no cold-start; (3) protótipo no motor GERAL, não na IceShot (que
> ramifica por raridade e não comporta gate por skill). + 3 ajustes de eficiência (fases de medição
> fundidas; latch só ao agir; reusar cache de Life). Ver "ORDEM FINAL" no fim para o resumo das emendas.

> **Ideia do utilizador, fechada em 15 decisões de design (2026-06-06).** A rotação decide o que usar pela
> DUREZA do mob (vida efetiva relativa à zona), não só pela raridade. 3 níveis: TANK / MEDIUM / EASY.
>
> - **TANK** → combo completo (frozen → Tornado → Barrage → Snipe); se não matar → rotação normal (recongela).
> - **MEDIUM** → Barrage + Tornado (sempre que disponível) + rotação normal. SEM Snipe.
> - **EASY** → só rotação normal (Ice Shot + Mark + Ice-Tipped). Suficiente.
>
> **NÃO COMEÇAR a construir até o utilizador aprovar este plano.** Cada fase: isolada, compila 0 erros,
> commit próprio, o utilizador TESTA antes da seguinte. Tudo aditivo/opt-in — o combate atual nunca se parte.

---

## MODELO CENTRAL: score de dureza + "dureza mínima" por skill

Cada mob recebe um **score de dureza** (número numa escala contínua):

```
score = (pool efetiva do mob ÷ mediana de pool dos Rares da zona) + ajuste dos mods
        pool efetiva = MaxHP + MaxES (somados igual)
```

O score cai num de **3 níveis**, por 2 limiares configuráveis:
```
score >= limiarTank    → TANK
score >= limiarMedium  → MEDIUM
senão                  → EASY
```

Cada **skill** tem um campo **"Dureza mínima"** (Easy / Medium / Tank). A skill só é usada se o nível do
alvo for >= a sua dureza mínima. Config da build do utilizador:

| Skill | Dureza mín. | EASY | MEDIUM | TANK |
|-------|-------------|:---:|:---:|:---:|
| Ice Shot | Easy | ✓ | ✓ | ✓ |
| Mark | Easy | ✓ | ✓ | ✓ |
| Ice-Tipped | Easy | ✓ | ✓ | ✓ |
| Barrage | Medium | — | ✓ | ✓ |
| Tornado | Medium | — | ✓ | ✓ |
| Snipe | Tank | — | — | ✓ |

→ Resultado = exatamente os 3 pacotes do utilizador. Configurar = 1 campo por skill (escala p/ qualquer build).

---

## AS 15 DECISÕES DE DESIGN (fechadas — a implementação NÃO as reabre)

1. **Pool** = MaxHP + MaxES, somados igual (peso 1:1). Mob ES-based é tão duro como um de muita vida.
2. **Vida MÁXIMA** só (não o HP atual). A dureza é fixa por mob → estável, combina com o latch.
3. **Vida manda; mods AJUSTAM** (somam/subtraem ao score; não fazem override).
4. **Métrica RELATIVA** = pool ÷ mediana de pool dos Rares da zona (auto-calibra por tier/nível).
5. **MAGIC entra na escala** pela vida, medido pela MESMA mediana (a dos Rares). Magic juiced → MEDIUM/TANK.
   MAS: no COLD-START (baseline do tier ainda sem ≥ N rares), o Magic é limitado a MEDIUM no MÁXIMO —
   nunca aciona TANK (Snipe canalizado) com base num fallback não-fiável. Só com baseline real é que um
   Magic pode chegar a TANK. Limita o erro do início ao caso BARATO (MEDIUM=+Barrage), nunca ao caro
   (TANK=Snipe desperdiçado). [EMENDA AUDITORIA 2]
6. **WHITE** = sempre EASY (não se avalia a vida — performance; e morre logo).
7. **BOSS (Unique)** = combo SEMPRE (fora da escala; ignora o score).
8. **FROZEN continua obrigatório** para o combo. A dureza é um gate A MAIS, não substitui o frozen.
9. **Distância** não interfere (já tratada pelo Attack Range e pelo CanHit).
10. **TANK "se não matar"** → quando o alvo descongela e está vivo, volta à rotação normal (recongela).
    É o comportamento atual (o combo não fica preso à espera de frozen).
11. **LATCH por Entity.Id** — classifica o mob UMA vez no 1º contacto; não reavalia a meio do combate.
12. **Baseline ACUMULA por area level na SESSÃO** (memória, não disco). Um corredor T15 herda o que
    aprendeu de mapas T15 anteriores. Cai no fallback só no 1º mapa de cada tier.
13. **COLD-START = fallback com MEDIANA SINTÉTICA (não uma escala diferente).** Enquanto a zona/tier tem
    < N rares amostrados, NÃO se usa um score de unidade diferente — usa-se a MESMA fórmula `pool ÷ mediana`,
    mas com uma `medianaSintetica = danoPorIceShot × tirosNumRareMediano` (ambos sliders). Assim o score
    do fallback e o da baseline estão na MESMA unidade (giram à volta de 1.0 p/ um rare mediano), e UM só
    par de limiares (`limiarTank`/`limiarMedium`) serve as duas vias. CRÍTICO: sem isto, os limiares só
    podiam estar calibrados para uma via e o cold-start dava lixo (que o latch cristalizava). [EMENDA AUDITORIA 1]
14. **Mods de INFLAÇÃO de vida excluídos da mediana** (via ModReader/M1) — senão sobem a mediana e fazem
    os normais parecer fracos. Nomes confirmados: `ExtraEnergyShield|EnergyShieldAura|LifeRegeneration`.
15. **Config = "Dureza mínima" por skill** (1 campo Easy/Medium/Tank por skill).

---

## NOTAS TÉCNICAS (factos verificados — a implementação não adivinha)

- **Pool:** `entity.GetComponent<Life>()` → `MaxHP` + `MaxES` (confirmado em CharacterData-PoE2). Reusar o
  padrão de cache de Life por id do `WeightEngine.ReadHpFraction` (TTL 500ms) — não duplicar no hot-path.
- **Nível da zona:** `GameController.Game.IngameState.Data.CurrentAreaLevel` (int). Ler 1x/tick.
- **Mods:** `ModReader` (M1, JÁ FEITO E VALIDADO) — leitura cacheada por-tick. ModRule p/ os ajustes/exclusões.
- **Combo atual:** mantém-se intacto (Tornado→Barrage→Snipe, frozen, CanHit). A dureza é um gate adicional.

---

## FASE H0 — INSTRUMENTAÇÃO + GATE (medir antes de decidir)

**Objetivo:** com dados REAIS, confirmar que se consegue ler tudo e que os limiares separam bem. Zero
mudança no combate.

- **H0.1 — Logar por alvo:** area level, raridade, MaxHP, MaxES, `pool`, mediana-da-zona (quando houver),
  `score = pool/mediana`. Reusa o debug atual.
- **H0.2 — Acumular amostra de Rares por area level** (só observar; ainda não classifica).
- **GATE H0 (bloqueante):** confirmar: (a) `CurrentAreaLevel` lê-se; (b) `pool` (HP+ES) lê-se p/ Rare/
  Magic e o ES soma; (c) a mediana SEPARA visivelmente um durão de um fraco; (d) os Magic aparecem na
  amostra de vida (p/ a decisão #5 funcionar). Só então H1 avança.
- **Teste H0:** 2-3 mapas com o log; ler juntos e ESCOLHER `limiarTank`, `limiarMedium`, `N mínimo de
  amostras` e `danoPorIceShot` inicial. Decisão de produto do utilizador.

---

## FASE H1 — MEDIÇÃO COMPLETA (pool + baseline + nível) NUM SÓ CICLO [EFICIÊNCIA AUDITORIA]

> As 3 fases antigas (pool / baseline / classificador) liam os MESMOS dados no mesmo ponto — separá-las
> dava 2 ciclos de teste in-game redundantes. Fundidas: uma fase que lê, aprende e CALCULA o nível, mas
> SÓ LOGA (não age). Sem latch ainda (classificar é uma divisão barata; o latch só importa quando há
> consequências — entra em H4). Um único teste valida tudo. Zero mudança no combate.

- **H1.1 — Pool efetiva (reusar o cache do WeightEngine):** estender `WeightEngine.ReadHpFraction` (que já
  lê `Life` com cache por id, TTL 500ms) para EXPOR também os absolutos `MaxHP+MaxES`. NÃO criar uma 2ª
  leitura de Life no TrackedEntity (seriam duas leituras do mesmo componente por tick). pool ≤ 0 = desconhecido.
- **H1.2 — Area level:** `GameController.Game.IngameState.Data.CurrentAreaLevel`, 1x/tick.
- **H1.3 — `ZoneHpBaseline`** (em memória, na sessão): `dict<int areaLevel, janela deslizante de pools de
  Rares>` (ex.: últimos 30). Mediana a pedido (robusta a outliers). Acumula por area level (#12) — corredores
  herdam o tier. SÓ Rares entram na amostra (White/Magic não — não contaminam). Excluir da amostra os Rares
  com mods de inflação de vida via ModReader (regex `ExtraEnergyShield|EnergyShieldAura|LifeRegeneration`, #14).
- **H1.4 — Cálculo do nível (sem latch, só log):** para o alvo atual, `score = pool/mediana + ajusteMods`
  (ou, no cold-start, `pool/medianaSintetica`, #13). Ajuste de mods soma (ex.: `RevivesMinions|
  LifeRegeneration|HealingNova` = +X; vida manda, #3). Magic limitado a MEDIUM no cold-start (#5). Aplica
  os 2 limiares → nível. ESCREVE no debug; NÃO age.
- **GATE H1 (bloqueante):** confirmar com o teu output: (a) pool (HP+ES) lê-se p/ Rare/Magic, ES soma;
  (b) area level lê-se; (c) a mediana estabiliza e SEPARA durão de fraco; (d) o nível calculado bate com a
  tua intuição (um magico juiced sai MEDIUM/TANK; um rare fraco sai EASY). Só então H4 (agir) avança.
- **Teste H1:** 2-3 mapas com o log; ler juntos; AFINAR `limiarTank`, `limiarMedium`, `N mínimo`,
  `danoPorIceShot`, `tirosNumRareMediano`. Edge: Magic/Rare ES-based mostra pool alta com HP baixo;
  invulnerabilidade temporária (Proximal/Temporal Bubble) não afeta (classifica por pool MÁXIMA).

---

## FASE H4 — APLICAR NO MOTOR GERAL (não na IceShot) [EMENDA AUDITORIA 3]

**Porquê o Geral e NÃO a IceShot:** a IceShotRoutine ramifica por RARIDADE (`switch(target.Rarity)` →
ExecuteBoss/ExecuteElite/ExecuteClear), com sequências hard-coded em cada caminho — NÃO tem um ponto único
onde "as skills passam por um gate". Enxertar "dureza mínima por skill" lá obrigaria a reescrever os 3
métodos E entra em contradição direta com a #5 (Magic→TANK, mas o switch manda todo Magic para
ExecuteClear que nem chama Tornado/Barrage). O motor GERAL, ao contrário, JÁ É "lista de SkillRule + gates
no RuleEvaluator" — adicionar um gate de dureza é ADITIVO e trivial, e o SkillRule é onde a #15 já queria
o campo. Logo, o protótipo é no Geral; a IceShot fica intacta como rede de segurança.

- **H4.1 — Campo `MinHardness` no SkillRule** (enum Easy/Medium/Tank; default Easy = não filtra).
- **H4.2 — Gate no RuleEvaluator:** a regra só passa se `nívelDoAlvo >= rule.MinHardness`. SOMA-SE aos
  gates existentes (frozen via TargetHasBuff, CanHit, raridade) — todos têm de passar (#8, #9). Boss →
  trata-se à parte: Unique força nível TANK (combo sempre, #7); White → EASY (#6).
- **H4.3 — UI por skill + sliders globais:** campo "Dureza mínima" em cada SkillSlot ([Geral]) +
  `limiarTank`, `limiarMedium`, `N mínimo`, `danoPorIceShot`, `tirosNumRareMediano`. Resumo no debug:
  "nível alvo=TANK | EASY=IceShot,Mark MEDIUM=+Barrage,Tornado TANK=+Snipe".
- **H4.4 — Preset Ice Shot atualizado:** o IceShotPreset.Build() passa a definir MinHardness por regra
  (Snipe=Tank; Tornado/Barrage=Medium; resto=Easy) — reproduz a regra do utilizador no Geral.
- **Teste H4:** com a routine "Geral" + preset, contra Rare/Magic: tanky→combo (se frozen); médio→Barrage+
  Tornado; fraco→só normal; boss→combo sempre; Magic juiced sobe (mas não a TANK no cold-start, #5); SEM
  canais abortados (latch). Confirmar a #10 (TANK que não morre volta à normal ao descongelar). A IceShot
  continua a funcionar como antes (não foi tocada).

---

## FASE H5 — (DIFERIDA / OPCIONAL) Levar a dureza à IceShotRoutine

Só se o utilizador quiser a feature TAMBÉM na routine IceShot dedicada (não só na Geral). Implica
reescrever ExecuteBoss/ExecuteElite/ExecuteClear para consultarem o nível de dureza — trabalho maior e
arriscado (mexe no que funciona). Provavelmente DESNECESSÁRIO: se o Geral com preset reproduz a rotação,
o utilizador pode simplesmente usar o Geral. Decidir depois de H4 provada.

---

## ORDEM FINAL (pós-auditoria: 3 emendas + 3 ajustes de eficiência)

1. **H1 — MEDIÇÃO FUNDIDA + GATE** — pool (HP+ES, reusando cache do WeightEngine) + area level + baseline
   por zona + cálculo do nível, mas SÓ LOGA (sem latch, sem agir). Um só ciclo de teste. BLOQUEIA H4 até
   o gate passar (nível bate com a intuição; mediana separa; Magic juiced sobe).
2. **H4 — APLICAR NO MOTOR GERAL** — campo `MinHardness` no SkillRule + gate no RuleEvaluator + latch por
   id (agora que há consequências) + UI por skill + preset atualizado. A IceShot fica intacta.
3. **H5 — (opcional/diferido)** — levar a dureza à IceShotRoutine só se o utilizador quiser (implica
   reescrever os 3 Execute* dela; provavelmente desnecessário se o Geral reproduz a rotação).

**As 3 EMENDAS da auditoria (obrigatórias, já no plano):**
1. Fallback usa MEDIANA SINTÉTICA → score na mesma unidade da baseline → um só par de limiares (senão o
   cold-start era lixo cristalizado pelo latch).
2. Magic limitado a MEDIUM no cold-start → erro do início fica no caso barato, nunca no Snipe desperdiçado.
3. Protótipo no motor GERAL (não na IceShot) → a IceShot ramifica por raridade e não comporta gate por
   skill sem reescrita; o Geral já é "SkillRule + gates" e o campo é aditivo.

**Os 3 AJUSTES de eficiência (já no plano):** H1+H2+H3 fundidas (1 ciclo de teste em vez de 3); sem latch
na medição (só em H4, quando há consequências); reusar o cache de Life do WeightEngine (não 2ª leitura).

**Porquê é sólido (não superficial):** uma só escala de dureza, na mesma unidade em ambas as vias (emenda
1); decide pela VIDA para Magic E Rare (#4, #5) com o erro do cold-start contido (emenda 2); auto-calibra
por tier/nível (#4, #12); o latch evita saltos e canais abortados (#11); reusa SkillRule/ModReader/cache
de Life; é um gate ADITIVO no Geral, com a IceShot intacta como rede de segurança (emenda 3). Cada fase
opt-in e testada.

Ver [[hp-based-rotation-idea]], [[archnemesis-mods-reference]], [[mods-gate-m0-passed]],
[[frozen-vs-chilled-combo-gate]], [[autopilot-must-be-build-agnostic]].

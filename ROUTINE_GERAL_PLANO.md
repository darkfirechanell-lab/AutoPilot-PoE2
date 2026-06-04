# Routine Geral Configurável — Plano

Objetivo: substituir a rotação fixa de gelo (`IceShotRoutine`) por um **motor genérico** que
funciona para **qualquer build**. O utilizador configura tudo pela UI (menus simples, sem escrever
código). A build de gelo atual passa a ser **um preset de exemplo**, não código fixo.

---

## 1. Modelo de dados — o que cada skill ganha na UI

Cada `SkillSlot` (já tem Tecla / Prioridade / Tap Hold ms) ganha um submenu **"Regras de Uso"**:

### Modo de uso
- **Tipo:** `Tap` | `Hold até condição` (dropdown)
  - Tap = um toque (ex.: Ice Shot, Barrage).
  - Hold até condição = segura a tecla até a condição de confirmação acontecer (ex.: Mark até ao
    debuff, Salvo até os seals baixarem, Snipe até ao stage de release).
- **Cooldown interno (ms):** anti-spam por skill (já existe como conceito; passa a ser por skill).

### Quando usar (condições) — TODAS têm de ser verdadeiras (AND)
Cada condição é uma linha opcional com um dropdown + campo. Default = "ignorar" (não filtra).

1. **Raridade do alvo:** `Qualquer` | `Normal+` | `Magic+` | `Rare+` | `Só Unique/Boss`
   (dropdown — ex.: Barrage só em Rare+).
2. **Alvo TEM buff/debuff:** caixa de texto com nome interno (ex.: `frozen`). Vazio = ignora.
3. **Alvo NÃO tem buff/debuff:** caixa de texto (ex.: `freezing_mark` → só marca se não marcado).
4. **Player TEM buff:** caixa de texto (ex.: ignora se já tens o buff).
5. **Player NÃO tem buff:** caixa de texto (ex.: Ice-Tipped só se não tens `shearing_bolts`).
6. **Charges do player >= N:** nome do buff + N (ex.: `skill_seals` >= 10 para o Salvo).
7. **HP do alvo:** `< X%` | `> X%` | ignorar (ex.: culling só com HP < 10%).
8. **Distância ao alvo:** `< X` | `> X` | ignorar.

### Confirmação do "hold até condição" (quando Tipo = Hold)
Dropdown **"Soltar quando":**
- `Buff aparece no alvo` (+ nome) — ex.: Mark.
- `Buff aparece no player` (+ nome) — ex.: Ice-Tipped.
- `Charges do player baixam` (+ nome) — ex.: Salvo (seals consumidos).
- `Skill em uso/cooldown (ActorSkill)` — ex.: Tornado.
- `Stage de animação >= N` (+ N) — ex.: Snipe stage 21.
- `Timeout (ms)` — rede de segurança, sempre presente.

---

## 2. O motor (genérico, agnóstico à build)

`GeneralRoutine : IRoutine` substitui o switch de raridade fixo:

```
Execute(ctx):
  se está em hold ativo → continua o hold (solta quando a condição de confirmação acontece) → return
  ordena as skills ativas por Prioridade (maior primeiro)
  para cada skill, por ordem:
    se passa TODAS as condições (raridade, buffs, charges, hp, dist, cooldown, CanHit p/ dano):
      se Tipo=Tap → tap, marca cooldown, return
      se Tipo=Hold → começa o hold, return
  (nenhuma disparou → não faz nada este tick)
```

- **Uma máquina de hold partilhada** (como a atual), mas a condição de soltar vem da config da skill,
  não de código fixo por skill.
- **C1 (cursor no alvo)** aplica-se às skills marcadas como "dano" (flag por skill, ou às que têm
  condição de alvo). Mantém o que já temos.

---

## 3. Como a build de gelo vira PRESET

Um botão **"Carregar preset: Ice Shot (Deadeye gelo)"** preenche os SkillSlots com as regras atuais:
- Ice Shot: Tap, prioridade baixa (filler), sem condições.
- Barrage: Tap, prioridade alta, alvo Rare+, alvo TEM `frozen`, cooldown 2000.
- Snipe: Hold até stage 21, alvo Rare+, alvo TEM `frozen`, depois do Barrage (commit ms).
- Mark: Hold até `freezing_mark` no alvo, alvo NÃO tem `freezing_mark`; fora do boss player NÃO tem
  `freezing_mark_damage_buff`.
- Ice-Tipped: Hold até `shearing_bolts` no player; player NÃO tem `shearing_bolts`.
- Salvo: Hold até `skill_seals` baixar; player charges `skill_seals` >= 10.
- Tornado: Hold até ActorSkill confirmar; cooldown (boss vs normal).

Assim provamos que o motor genérico consegue exprimir a rotação real testada — sem perder nada.

---

## 4. Construção POR PARTES (cada uma testada como A/B/C)

- **P1 — Modelo de condições:** classe `SkillRule` + avaliador `Evaluate(ctx, slot)` (raridade, tem/não
  tem buff, charges, hp, dist, cooldown). SEM UI ainda; testado com valores fixos = igual ao IceShot.
- **P2 — Motor genérico:** `GeneralRoutine` que ordena por prioridade e usa o avaliador. Corre LADO A
  LADO com o IceShot (toggle "usar motor geral"), para comparar sem partir o que funciona.
- **P3 — Hold configurável:** a máquina de hold lê a condição de soltar da config.
- **P4 — UI:** expor as regras no menu de cada skill (dropdowns/checkboxes/caixas de texto).
- **P5 — Preset de gelo:** botão que preenche tudo; validar que o motor geral == IceShot no jogo.
- **P6 — Aposentar o IceShot:** quando o motor geral provar ser igual ou melhor, o IceShot fica só
  como referência/preset.

Em cada parte: reler letra a letra, compilar 0 erros, commit isolado, utilizador testa, só depois a
seguinte. O IceShot **continua a funcionar** durante toda a construção (motor geral é opt-in até P6).

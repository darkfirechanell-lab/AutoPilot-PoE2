# AutoPilot

Plugin de automação de combate para **Path of Exile 2** via [ExileCore2](https://github.com/exCore2/ExileCore2).

Reconstruído de raiz a partir do conceito do AutoMyAim, com melhorias próprias.

## Funcionalidades

- **Targeting dinâmico** — 3 modos que mudam conforme o contexto, com histerese (anti-tremedeira):
  - **Danger** — muitos mobs colados → foca o mais perto para limpar a ameaça
  - **Elite** — há Rare/Unique no alcance → foca os elites
  - **Normal** — clear eficiente (sticky + cluster)
- **Deteção automática de skills e teclas** — incluindo botões do rato (LMB/MMB/RMB)
- **Aim** — mira o centro do corpo do alvo, com confinamento e suavização opcionais
- **Filtro de invulnerabilidade** — ignora clones de boss imunes (por stat e por buff)
- **Routine IceShot** — Snipe (release no stage de animação), Freezing Salvo (hold até seals), Freezing Mark, combo Barrage→Snipe
- **HUD de debug** — estado de combate, animação e buffs ao vivo

## Estrutura

| Pasta | Conteúdo |
|-------|----------|
| `Input/` | InputQueue (sem bloqueio), SkillExecutor |
| `Detection/` | EntityCache (1 scan/tick), TrackedEntity |
| `Targeting/` | Modos dinâmicos, pesos, clustering espacial, raycast, seleção de alvo |
| `Aiming/` | AimController |
| `Combat/` | AnimationReader, BuffReader, SkillDetector, routines |
| `Hud/` | CombatHud |
| `Settings/` | Configuração |

## Build

Requer a variável de ambiente `exileCore2Package` a apontar para a pasta do HUD (onde estão `ExileCore2.dll` e `GameOffsets2.dll`).

```
dotnet build -c Debug
```

O HUD compila automaticamente o que está em `Plugins/Source`.

namespace AutoPilot.Combat;

/// <summary>
/// Uma rotina de combate (ex.: IceShot). Recebe o contexto do tick e decide que skills usar.
///
/// Contrato mínimo: as routines de canalização (Snipe/Salvo/Mark) precisam de continuar a sua
/// máquina de estados MESMO sem alvo (para soltar a tecla com segurança) — por isso o plugin
/// pergunta <see cref="IsBusy"/> antes de decidir saltar a routine por falta de alvo.
/// </summary>
public interface IRoutine
{
    /// <summary>Nome para o menu de seleção de rotina.</summary>
    string Name { get; }

    /// <summary>
    /// True se a routine está a meio de algo que NÃO pode ser interrompido por troca de alvo
    /// (canalizar Snipe, segurar Salvo/Mark). Enquanto true, o plugin deixa-a continuar.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>Executa um passo da rotação com o contexto deste tick.</summary>
    void Execute(RoutineContext ctx);

    /// <summary>Larga qualquer tecla presa e reseta o estado interno (paragem/área/desligar).</summary>
    void Reset();
}

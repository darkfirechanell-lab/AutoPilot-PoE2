using System;
using System.IO;
using System.Text;

namespace AutoPilot;

/// <summary>
/// Log de ERROS dedicado — a rede de segurança para apanhar bugs escondidos e crashes internos.
///
/// Ao contrário do <see cref="DebugLog"/> (que mostra o ESTADO e sobrescreve), este:
///   • faz APPEND (nunca apaga) — um erro perdido nunca mais se vê;
///   • grava IMEDIATAMENTE (sem throttle) — um crash não pode esperar pelo flush seguinte;
///   • escreve a mensagem + stack trace + um carimbo de tempo, num ficheiro à parte.
///
/// Está SEMPRE ligado (não depende de toggle): erros são raros e queremos sempre apanhá-los. Anti-spam:
/// não repete a MESMA assinatura de erro em rajada (senão um erro por-tick enchia o ficheiro num
/// segundo) — conta as repetições e só reescreve quando a assinatura muda.
/// </summary>
public static class ErrorLog
{
    private const string FixedPath = @"C:\Users\clona\Desktop\GamePoe\TestePoE\AutoPilot_errors.txt";

    private static string _lastSignature = "";
    private static int _repeatCount;

    /// <summary>
    /// Marca uma nova sessão no topo do ficheiro e trunca-o se tiver crescido demais (append infinito
    /// ao longo de muitas sessões). Chamar no arranque do plugin (Initialise).
    /// </summary>
    public static void StartSession()
    {
        try
        {
            // Se o ficheiro passou de ~1 MB, recomeça (mantém só uma marca). Erros antigos já foram vistos.
            if (File.Exists(FixedPath) && new FileInfo(FixedPath).Length > 1_000_000)
                File.WriteAllText(FixedPath, "", Encoding.UTF8);

            File.AppendAllText(FixedPath,
                $"\n========== SESSÃO {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========\n", Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>Regista um erro (contexto + exceção). Nunca rebenta o jogo.</summary>
    public static void Report(string context, Exception ex)
    {
        try
        {
            var signature = $"{context}|{ex?.GetType().Name}|{ex?.Message}";

            // Mesmo erro em rajada: conta, não escreve linha nova a cada tick.
            if (signature == _lastSignature)
            {
                _repeatCount++;
                // De vez em quando (a cada 100 repetições) deixa uma marca, para se ver que continua.
                if (_repeatCount % 100 != 0) return;
            }
            else
            {
                _lastSignature = signature;
                _repeatCount = 0;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"==== [{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context} ====");
            if (_repeatCount > 0) sb.AppendLine($"(repetido {_repeatCount}x desde a 1ª ocorrência)");
            if (ex != null)
            {
                sb.AppendLine($"{ex.GetType().Name}: {ex.Message}");
                sb.AppendLine(ex.StackTrace ?? "(sem stack trace)");
                if (ex.InnerException != null)
                    sb.AppendLine($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            sb.AppendLine();

            File.AppendAllText(FixedPath, sb.ToString(), Encoding.UTF8);
        }
        catch { /* nunca rebenta o jogo por causa do log de erros */ }
    }

    /// <summary>Regista um aviso simples (sem exceção) — para situações suspeitas mas não fatais.</summary>
    public static void Warn(string context, string message)
    {
        try
        {
            File.AppendAllText(FixedPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] AVISO {context}: {message}\n", Encoding.UTF8);
        }
        catch { }
    }
}

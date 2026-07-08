namespace ProcioneMGR.Data;

/// <summary>
/// Stadio del ciclo di vita di un modello nel registry (Fase 2). Progressione tipica:
/// <c>Staging → Challenger → Champion</c>, con uscita a <c>Retired</c>. Vincolo di dominio: un solo
/// <see cref="Champion"/> per (Symbol, Timeframe). Persistito come stringa (come gli altri enum del
/// dominio), così il valore è leggibile a DB e stabile rispetto ai riordini dell'enum.
/// </summary>
public enum ModelStage
{
    /// <summary>Appena addestrato/salvato: candidato non ancora in gioco.</summary>
    Staging = 0,

    /// <summary>In valutazione contro il Champion in carica.</summary>
    Challenger = 1,

    /// <summary>Modello attivo per la sua coppia (Symbol, Timeframe). Unico.</summary>
    Champion = 2,

    /// <summary>Ritirato (superato da uno migliore o degradato per drift). Non più eleggibile.</summary>
    Retired = 3,
}

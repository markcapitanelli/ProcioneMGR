using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D-03, Fase 1 PRD-RISANAMENTO] L'invariante selezione/holdout come politica unica
/// (<see cref="PipelineDateRanges.Validate"/>): prima viveva SOLO nel salvataggio della UI, e una
/// configurazione nata altrove (pre-controllo, SQL a mano, tool) girava con l'holdout sovrapposto
/// alla selezione — ogni numero "out-of-sample" contaminato in silenzio. Ora la stessa funzione
/// blocca il form E l'avvio del run.
/// </summary>
public sealed class PipelineRangeValidationTests
{
    private static PipelineDateRanges Ranges(string selFrom, string selTo, string holdFrom, string holdTo) => new()
    {
        SelectionFrom = DateTime.Parse(selFrom),
        SelectionTo = DateTime.Parse(selTo),
        HoldoutFrom = DateTime.Parse(holdFrom),
        HoldoutTo = DateTime.Parse(holdTo),
    };

    [Fact]
    public void ValidRanges_PassValidation()
    {
        Assert.Null(Ranges("2026-01-01", "2026-04-30", "2026-05-01", "2026-06-30").Validate());
    }

    [Fact]
    public void HoldoutTouchingSelectionEnd_IsAllowed()
    {
        // Il confine esatto (holdout che inizia NEL giorno in cui finisce la selezione) e' il
        // pattern del default della UI (SelectionTo == HoldoutFrom): resta valido.
        Assert.Null(Ranges("2026-01-01", "2026-04-30", "2026-04-30", "2026-06-30").Validate());
    }

    [Fact]
    public void OverlappingHoldout_IsRejected()
    {
        var error = Ranges("2026-01-01", "2026-04-30", "2026-03-01", "2026-06-30").Validate();
        Assert.NotNull(error);
        Assert.Contains("sovrapposti", error);
    }

    [Theory]
    [InlineData("2026-04-30", "2026-01-01", "2026-05-01", "2026-06-30")] // selezione invertita
    [InlineData("2026-01-01", "2026-04-30", "2026-06-30", "2026-05-01")] // holdout invertito
    public void InvertedOrEmptyWindows_AreRejected(string sf, string st, string hf, string ht)
    {
        Assert.NotNull(Ranges(sf, st, hf, ht).Validate());
    }

    [Fact]
    public void DefaultInstance_IsRejected_NotSilentlyAccepted()
    {
        // Il caso reale del rischio D-03: DateRangesJson corrotto o vuoto deserializza in
        // new PipelineDateRanges() (tutte le date a MinValue). Deve essere BOCCIATO qui, con un
        // messaggio chiaro, non lasciato arrivare agli stage dove fallirebbe in modo opaco.
        Assert.NotNull(new PipelineDateRanges().Validate());
    }
}

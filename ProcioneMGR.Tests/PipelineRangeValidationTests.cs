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

    // --- [J2] Finestre relative (scorrevoli) --------------------------------------------------

    private static PipelineDateRanges Rolling(int holdoutDays = 120, int selectionDays = 365) => new()
    {
        RollingHoldoutDays = holdoutDays,
        RollingSelectionDays = selectionDays,
    };

    [Fact]
    public void Rolling_ResolvesAgainstNow_WithCorrectGeometry()
    {
        var now = new DateTime(2026, 8, 25, 14, 37, 42, DateTimeKind.Utc);
        var r = Rolling().Resolve(now);

        // L'ancora è troncata al minuto: due risoluzioni nello stesso minuto = stessa finestra.
        Assert.Equal(new DateTime(2026, 8, 25, 14, 37, 0, DateTimeKind.Utc), r.HoldoutTo);
        Assert.Equal(r.HoldoutTo.AddDays(-120), r.HoldoutFrom);
        // Stessa invariante D-03 delle date fisse: selezione e holdout mai sovrapposti.
        Assert.Equal(r.HoldoutFrom, r.SelectionTo);
        Assert.Equal(r.SelectionTo.AddDays(-365), r.SelectionFrom);
        Assert.Null(r.Validate());
    }

    [Fact]
    public void Rolling_TwoResolutions_ProduceDifferentWindows()
    {
        // Il difetto che J2 chiude: 90 esecuzioni sulla STESSA finestra congelata. Due «adesso»
        // diversi devono produrre due finestre diverse.
        var ranges = Rolling();
        var primo = ranges.Resolve(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
        var secondo = ranges.Resolve(new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(primo.HoldoutTo, secondo.HoldoutTo);
        Assert.Equal(24, (secondo.HoldoutTo - primo.HoldoutTo).TotalDays, precision: 5);
    }

    [Fact]
    public void Absolute_Resolve_IsIdentity()
    {
        var fixedRanges = Ranges("2026-01-01", "2026-04-30", "2026-05-01", "2026-06-30");
        Assert.Same(fixedRanges, fixedRanges.Resolve(DateTime.UtcNow));
    }

    [Fact]
    public void Rolling_HalfSpecified_IsRejected()
    {
        // Metà relativa e metà no è un errore dichiarato, non un default silenzioso.
        Assert.NotNull(new PipelineDateRanges { RollingHoldoutDays = 120 }.Validate());
        Assert.NotNull(new PipelineDateRanges { RollingSelectionDays = 365 }.Validate());
    }

    [Fact]
    public void Rolling_HoldoutUnderAWeek_IsRejected()
    {
        Assert.NotNull(Rolling(holdoutDays: 6).Validate());
        Assert.Null(Rolling(holdoutDays: 7).Validate());
    }

    [Fact]
    public void Rolling_HoldoutMonths_ComesFromTheDeclaredDays_NotFromDefaultDates()
    {
        // Su una config relativa NON risolta le assolute sono ai default: (default − default) = 0
        // giorni avrebbe escluso il run dalla coda candidati in silenzio.
        var unresolved = Rolling(holdoutDays: 122);
        Assert.NotNull(unresolved.HoldoutMonths());
        Assert.Equal(unresolved.HoldoutMonths(), unresolved.Resolve(DateTime.UtcNow).HoldoutMonths());
    }

    [Fact]
    public void Rolling_HoldoutAge_IsZero_NotTwoThousandYears()
    {
        Assert.Equal(0, Rolling().HoldoutAgeDays(DateTime.UtcNow));
    }
}

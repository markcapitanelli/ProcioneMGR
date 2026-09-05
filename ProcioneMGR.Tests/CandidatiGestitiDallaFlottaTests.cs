using ProcioneMGR.Data;
using ProcioneMGR.Services.Fleet;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Revisione 2026-09-03/04] <b>Quale riga «Assign» brucia un candidato, e per quanto.</b>
///
/// <para>Prima ogni riga «Assign» contava, anche i rifiuti del gate: in dry-run la coda si svuotava
/// in poche ore e il no-op diceva «tutti già schierati». Togliendoli tutti, però, un candidato che
/// il braccio non può eseguire (ensemble multi-gamba, corsia non autorizzata) restava in testa
/// alla FIFO e bloccava gli altri per quattordici giorni. La regola sta nel mezzo, ed è qui.</para>
/// </summary>
public class CandidatiGestitiDallaFlottaTests
{
    private static readonly DateTime Ora = new(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(DecisionOutcome.Applied, false, true, null, 0, true)]
    [InlineData(DecisionOutcome.Intended, false, false, null, 0, true)]
    [InlineData(DecisionOutcome.Unknown, false, false, "esito ignoto", 0, true)]      // intento chiuso dalla riconciliazione
    [InlineData(DecisionOutcome.Unknown, false, false, null, 0, false)]               // binario vecchio senza esito: non è uno schieramento
    [InlineData(DecisionOutcome.Failed, false, false, "bracket non derivabile", 1, true)]
    [InlineData(DecisionOutcome.Failed, false, false, "bracket non derivabile", 30, false)]   // dopo 24 ore si ritenta
    [InlineData(DecisionOutcome.Refused, false, false, null, 1, true)]                // rifiuto a dry-run spento: la coda avanza
    [InlineData(DecisionOutcome.Refused, false, false, null, 30, false)]
    [InlineData(DecisionOutcome.Refused, true, false, null, 1, false)]                // in DRY-RUN nulla brucia
    [InlineData(DecisionOutcome.Noted, false, false, null, 0, false)]
    public void LaREGOLA_rigaPERriga(string outcome, bool dryRun, bool applied, string? error, int oreFa, bool atteso)
    {
        Assert.Equal(atteso, FleetStateReader.ContaComeGestito(outcome, dryRun, applied, error, Ora.AddHours(-oreFa), Ora));
    }

    /// <summary>
    /// <b>Il caso che ha motivato la correzione</b>: DryRun=true e GreyAutoDeploy=true, ogni tick
    /// produce un «Assign [dry-run]» rifiutato. Nessuno di quei rifiuti deve bruciare il candidato:
    /// chi spegne il dry-run dopo giorni di osservazione deve trovare la coda intera.
    /// </summary>
    [Fact]
    public void INdryRUN_novantaseiRIFIUTI_nonBRUCIANOilCANDIDATO()
    {
        for (var tick = 0; tick < 96; tick++)
        {
            Assert.False(FleetStateReader.ContaComeGestito(DecisionOutcome.Refused, dryRun: true, applied: false, error: null,
                Ora.AddMinutes(-15 * tick), Ora));
        }
    }

    // ------------------------------------------------------------------ [2026-09-05]

    /// <summary>
    /// <b>[K14-bis] Un artifact della ri-applica gestisce il run solo se ha APPLICATO.</b> Misurato il
    /// 2026-09-05: la ri-applica non aveva mai applicato nulla in trenta giorni, ma ogni suo scarto
    /// («solo 1 simboli distinti») marcava «gia' schierati» i grigi di quel run per la flotta, e
    /// Composite ADA/USDT 5m (mediana K57 3,48) era sparito dalla coda del braccio automatico.
    /// Payload illeggibile o senza il campo = gestito: non sapere non allarga il permesso.
    /// </summary>
    [Theory]
    [InlineData("{\"Applied\":true,\"Message\":\"applicato\"}", true)]
    [InlineData("{\"Applied\":false,\"Message\":\"Candidato scartato: solo 1 simboli distinti (minimo 2).\"}", false)]
    [InlineData("{\"Applied\":false,\"Message\":\"Run senza ensemble applicabile: nessuna azione.\"}", false)]
    [InlineData("{\"Message\":\"senza campo\"}", true)]
    [InlineData("{\"Applied\":\"si\"}", true)]
    [InlineData("non e' json", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void LaDecisioneDellaRiapplica_GestisceSoloSeHaApplicato(string? payload, bool gestisce)
        => Assert.Equal(gestisce, FleetStateReader.DecisioneCheGestisce(payload));

    /// <summary>
    /// <b>[K61-bis] Quale riga puo' bruciare un candidato:</b> le «Assign» (come prima) e le «Retire»
    /// RIFIUTATE — che sono i rifiuti di sostituzione col RunId del candidato. Senza, un rimpiazzo
    /// respinto dalla guardia dei duplicati restava in testa alla lista ordinata per mediana e
    /// veniva riproposto e rifiutato a ogni tick, per sempre.
    /// </summary>
    [Theory]
    [InlineData("Assign", DecisionOutcome.Applied, true)]
    [InlineData("Assign", DecisionOutcome.Refused, true)]
    [InlineData("Retire", DecisionOutcome.Refused, true)]
    [InlineData("Retire", DecisionOutcome.Applied, false)]
    [InlineData("Retire", DecisionOutcome.Failed, false)]
    [InlineData("ProposeGrey", DecisionOutcome.Applied, false)]
    [InlineData("Blocked", DecisionOutcome.Noted, false)]
    public void LeRigheCheBrucianoUnCandidato(string kind, string outcome, bool brucia)
        => Assert.Equal(brucia, FleetStateReader.RigaCheBrucia(kind, outcome));
}

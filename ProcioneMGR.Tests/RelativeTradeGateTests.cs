using ProcioneMGR.Services.Pipeline.Stages;

namespace ProcioneMGR.Tests;

/// <summary>
/// [J6, PRD autonomia-operativa 2026-08-25] Il gate del conteggio trade relativo alla frequenza.
/// Il fatto che lo motiva: il gate assoluto (20) bocciava 67 chiavi distinte TUTTE in guadagno
/// (Sharpe medio 1,12) chiedendo ai 4h una frequenza che non hanno; e il massimo Sharpe holdout
/// dell'archivio (3,19 su 17 trade) non ha mai ricevuto un DSR perché fermato lì. Questi test
/// pinnano: la frazione che ALZA soltanto (il pavimento di potenza non si scavalca mai — la
/// trappola auto-referenziale), il sotto-consegnare colto, la frazione a zero = comportamento
/// storico identico, e l'atteso non derivabile che ricade sul pavimento senza inventare numeri.
/// </summary>
public class RelativeTradeGateTests
{
    [Fact]
    public void FrazioneZero_ComportamentoStorico_SoloPavimento()
    {
        var (required, origin) = HoldoutValidationStage.RequiredHoldoutTrades(
            minTrades: 20, fraction: 0m, selectionTrades: 300, selectionMonths: 12m, holdoutMonths: 4m);
        Assert.Equal(20, required);
        Assert.Empty(origin);
    }

    [Fact]
    public void CandidatoRado_PagaIlPavimento_NonLaFrazione()
    {
        // 2,3 trade/mese in selezione, 4 mesi di holdout ⇒ atteso ~9,2; frazione 0,5 ⇒ 5.
        // Il pavimento (10) vince: uno Sharpe su 5 trade è un aneddoto, non una stima.
        var (required, origin) = HoldoutValidationStage.RequiredHoldoutTrades(
            minTrades: 10, fraction: 0.5m, selectionTrades: 28, selectionMonths: 12m, holdoutMonths: 4m);
        Assert.Equal(10, required);
        Assert.Empty(origin); // il requisito è il pavimento: nessuna origine relativa da dichiarare
    }

    [Fact]
    public void CandidatoFrequente_LaFrazioneAlzaIlRequisito()
    {
        // 30 trade/mese in selezione, 4 mesi di holdout ⇒ atteso 120; frazione 0,3 ⇒ 36 > 20:
        // un candidato che ne consegna 20 sta sotto-consegnando rispetto al proprio ritmo.
        var (required, origin) = HoldoutValidationStage.RequiredHoldoutTrades(
            minTrades: 20, fraction: 0.3m, selectionTrades: 360, selectionMonths: 12m, holdoutMonths: 4m);
        Assert.Equal(36, required);
        Assert.Contains("attesi dal ritmo di selezione", origin);
    }

    [Fact]
    public void LaFrazioneNonAbbassaMai_IlPavimentoNonSiScavalca()
    {
        // La trappola auto-referenziale pinnata: qualunque frequenza minuscola, il requisito non
        // scende mai sotto il pavimento.
        for (var selectionTrades = 1; selectionTrades <= 24; selectionTrades += 3)
        {
            var (required, _) = HoldoutValidationStage.RequiredHoldoutTrades(
                minTrades: 10, fraction: 0.5m, selectionTrades, selectionMonths: 12m, holdoutMonths: 4m);
            Assert.True(required >= 10, $"con {selectionTrades} trade di selezione il requisito è sceso a {required}");
        }
    }

    [Theory]
    [InlineData(0, 12, 4)]     // zero trade in selezione: atteso non derivabile
    [InlineData(300, 0, 4)]    // finestra di selezione nulla
    public void AttesoNonDerivabile_RicadeSulPavimento_SenzaInventare(int selTrades, int selMonths, int holdMonths)
    {
        var (required, origin) = HoldoutValidationStage.RequiredHoldoutTrades(
            minTrades: 20, fraction: 0.5m, selTrades, selMonths, holdMonths);
        Assert.Equal(20, required);
        Assert.Empty(origin);
    }

    [Fact]
    public void HoldoutSenzaFinestra_RicadeSulPavimento()
    {
        var (required, _) = HoldoutValidationStage.RequiredHoldoutTrades(
            minTrades: 20, fraction: 0.5m, selectionTrades: 300, selectionMonths: 12m, holdoutMonths: null);
        Assert.Equal(20, required);
    }

    [Fact]
    public void IlRejectReason_ConservaLaPortaDellaFasciaGrigia()
    {
        // Il prefisso «Solo » è la porta della fascia grigia (GreyZone.ShortWindowRejectPrefix):
        // il messaggio costruito dal gate deve continuare a cominciare così.
        var (required, origin) = HoldoutValidationStage.RequiredHoldoutTrades(
            minTrades: 20, fraction: 0.3m, selectionTrades: 360, selectionMonths: 12m, holdoutMonths: 4m);
        var reason = $"Solo 12 trade in holdout (< {required}{origin})";
        Assert.StartsWith(ProcioneMGR.Services.Pipeline.GreyZone.ShortWindowRejectPrefix, reason);
    }
}

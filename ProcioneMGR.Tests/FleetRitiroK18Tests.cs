namespace ProcioneMGR.Tests;

/// <summary>
/// [K18, PRD autonomia-piena — Fase 2, 2026-08-31] <b>Numeratore e denominatore dalla stessa
/// storia.</b>
///
/// <para><c>GetPerformanceAsync(from:)</c> filtrava SOLO la lista dei trade; lo Sharpe restava
/// quello della curva di equity intera. Chi chiedeva «come è andata questa gamba da quando la
/// guardo» otteneva un conteggio ancorato e uno Sharpe no — due storie diverse dentro lo stesso
/// verdetto. Il chiamante è esattamente il ritiro di flotta, e il commento di
/// <c>FleetStateReader</c> prometteva un ancoraggio che qui non avveniva: «Trade e Sharpe si
/// ancorano allo stesso primo avvistamento dell'identità».</para>
///
/// <para>Oggi non mordeva perché il criterio Sharpe non arriva mai a esprimersi (pretende 20 trade
/// che a 2,2-3,7 al mese arrivano fra 5 e 9 mesi). Ma un difetto che non morde <i>ancora</i> è un
/// difetto che morderà il giorno in cui il criterio funziona — cioè il giorno in cui ci si fida
/// del suo verdetto.</para>
///
/// <para>Guardiano di sorgente, come per K15: la proprietà vive dentro un metodo che per essere
/// esercitato davvero richiede un motore vivo, un database e una serie di candele, e un
/// refactoring potrebbe rimuovere il filtro senza che nessun test se ne accorga.</para>
/// </summary>
public sealed class FleetRitiroK18Tests
{
    [Fact]
    public void LoSharpeAncorato_usaLaCurvaFILTRATAda_from()
    {
        var sorgente = File.ReadAllText(Path.Combine(
            Procione.Platform.RepoRoot, "ProcioneMGR", "Services", "Trading", "TradingEngine.cs"));

        // Il filtro c'è, ed è sullo stesso `from` che filtra i trade.
        Assert.Contains("from is DateTime ancora ? equity.Where(e => e.Timestamp >= ancora).ToList() : equity", sorgente);
        // E non è rimasta in giro la versione che ignorava la finestra.
        Assert.DoesNotContain("SharpeRatio = Statistics.SharpeRatio(equity, ppy),", sorgente);
    }

    [Fact]
    public void IlCommentoDiFleetStateReader_NonPromettePIUdiQuantoIlMotoreFa()
    {
        // La promessa e il fatto devono stare nello stesso posto: il commento del lettore di flotta
        // dichiara l'ancoraggio, e da oggi il motore lo mantiene. Se qualcuno togliesse il filtro,
        // il test sopra cade e questo resta a dire perché importava.
        var lettore = File.ReadAllText(Path.Combine(
            Procione.Platform.RepoRoot, "ProcioneMGR", "Services", "Fleet", "FleetStateReader.cs"));

        Assert.Contains("GetPerformanceAsync(from: firstSeen", lettore);
    }
}

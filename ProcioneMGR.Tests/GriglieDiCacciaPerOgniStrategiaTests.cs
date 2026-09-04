using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-04, richiesta del proprietario: «la caccia su tutte le strategie, anche le nuove»]
/// <b>Una strategia nella fabbrica è una strategia cacciata — a patto che abbia una griglia.</b>
///
/// <para>La caccia enumera <see cref="IStrategyFactory.Prototypes"/> quando la configurazione non
/// elenca strategie (è il caso di tutte le configurazioni in rotazione: il parametro
/// <c>strategies</c> dello stage Discovery è vuoto), quindi una strategia nuova entra da sola.
/// Ma <see cref="StrategyDiscoveryEngine.DefaultRanges"/> è uno <c>switch</c> sul nome con un
/// ramo di default vuoto: una strategia registrata nella fabbrica e dimenticata qui verrebbe
/// «cacciata» con zero parametri, cioè non cacciata, senza che nessuna superficie lo dica. Questo
/// guardiano fa rosso alla prima strategia nuova senza griglia.</para>
/// </summary>
public class GriglieDiCacciaPerOgniStrategiaTests
{
    [Fact]
    public void OGNIstrategiaDELLAfabbrica_haUNAgrigliaDIcaccia()
    {
        var fabbrica = new StrategyFactory();
        Assert.NotEmpty(fabbrica.Prototypes);

        var senzaGriglia = fabbrica.Prototypes
            .Where(p => StrategyDiscoveryEngine.DefaultRanges(p.Name).Count == 0)
            .Select(p => p.Name)
            .ToList();

        Assert.True(senzaGriglia.Count == 0,
            $"Strategie nella fabbrica SENZA griglia di caccia (aggiungere un ramo a StrategyDiscoveryEngine.DefaultRanges): {string.Join(", ", senzaGriglia)}");
    }

    /// <summary>Il nullo: un nome che non esiste non ha griglia, e non esplode.</summary>
    [Fact]
    public void UNnomeIGNOTO_nonHAgriglia()
        => Assert.Empty(StrategyDiscoveryEngine.DefaultRanges("StrategiaCheNonEsiste"));

    /// <summary>E ogni prototipo si istanzia dalla fabbrica col proprio nome: i due elenchi non divergono.</summary>
    [Fact]
    public void OGNIprototipo_siCREAdalPROPRIOnome()
    {
        var fabbrica = new StrategyFactory();
        foreach (var p in fabbrica.Prototypes)
        {
            var creata = fabbrica.Create(p.Name);
            Assert.Equal(p.Name, creata.Name);
        }
    }
}

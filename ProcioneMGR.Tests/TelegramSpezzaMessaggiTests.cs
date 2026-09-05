using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-05] Telegram rifiuta con HTTP 400 un testo oltre 4.096 caratteri, e il digest giornaliero
/// li supera: il 5/09 alle 08:26 è fallito, ha ritentato dopo 15 minuti ed è fallito di nuovo. Lo
/// spezzettamento è puro e si prova da solo.
/// </summary>
public class TelegramSpezzaMessaggiTests
{
    /// <summary>IL NULLO: un testo corto resta un messaggio solo, identico.</summary>
    [Theory]
    [InlineData("ciao")]
    [InlineData("")]
    public void TestoCORTO_restaUNmessaggio(string testo)
        => Assert.Equal([testo], TelegramNotifier.Spezza(testo));

    /// <summary>Esattamente al limite: ancora un messaggio solo.</summary>
    [Fact]
    public void TestoALlimite_restaUNmessaggio()
    {
        var testo = new string('x', TelegramNotifier.MaxCaratteri);
        Assert.Single(TelegramNotifier.Spezza(testo));
    }

    /// <summary>Oltre il limite: ogni parte sta sotto il limite di Telegram e nulla va perso.</summary>
    [Fact]
    public void TestoLUNGO_partiSOTTOilLIMITE_eNULLAsiPERDE()
    {
        var righe = Enumerable.Range(0, 400).Select(i => $"corsia {i}: Sharpe 0,{i:D2}, {i} trade, DD {i % 9}%");
        var testo = string.Join('\n', righe);
        Assert.True(testo.Length > 3 * TelegramNotifier.MaxCaratteri);

        var parti = TelegramNotifier.Spezza(testo);

        Assert.True(parti.Count >= 3);
        Assert.All(parti, p => Assert.True(p.Length <= TelegramNotifier.TagliaParte, $"parte di {p.Length} caratteri"));
        // Nessuna riga spezzata a metà: ogni parte inizia con «corsia ».
        Assert.All(parti, p => Assert.StartsWith("corsia ", p, StringComparison.Ordinal));
        // Ricomponendo si ritrova ogni riga, una volta sola.
        var ricomposto = string.Join('\n', parti);
        Assert.Equal(400, ricomposto.Split('\n').Length);
    }

    /// <summary>Senza un a-capo utile si taglia secco, senza andare in ciclo né perdere caratteri.</summary>
    [Fact]
    public void SenzaACAPO_siTAGLIAsecco()
    {
        var testo = new string('y', 10_000);
        var parti = TelegramNotifier.Spezza(testo);

        Assert.All(parti, p => Assert.True(p.Length <= TelegramNotifier.TagliaParte));
        Assert.Equal(10_000, parti.Sum(p => p.Length));
    }
}

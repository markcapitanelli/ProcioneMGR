using ProcioneMGR.Services.Config;
using ProcioneMGR.Services.Monitoring.Drift;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I6] Le soglie dei rilevatori di deriva diventano amministrabili — e quindi validabili.
///
/// Fino al 2026-08-18 vivevano SOLO nei default di <see cref="DriftThresholds"/>: nessuna chiave,
/// nessun pannello, si cambiavano <b>ricompilando</b>. Era una violazione diretta del mandato
/// «tutto amministrabile da UI» del 2026-08-09, e pesava perché quei numeri sono la prassi generica
/// del settore, non una misura fatta su serie finanziarie.
///
/// <para>La regola che conta è quella sull'<b>ordine</b> delle soglie: una soglia di <i>alert</i>
/// più permissiva di quella di <i>warning</i> rende l'alert irraggiungibile. Un livello di allarme
/// che non può scattare è peggio di un livello assente, perché <i>sembra</i> esserci — è la stessa
/// classe dell'avviso EEA/MiCA con la condizione «non connesso», che lo escludeva dall'unico caso
/// per cui era stato scritto.</para>
/// </summary>
public class DriftThresholdsConfigTests
{
    /// <summary>
    /// <b>La regola numero uno</b>: una regola che rifiuta la configurazione di fabbrica è un bug
    /// della regola, non della configurazione.
    /// </summary>
    [Fact]
    public void IDefaultDiFabbrica_PassanoLaValidazione()
        => Assert.Null(AdminConfigRules.Validate(new DriftMonitorOptions()));

    /// <summary>
    /// <b>Il controllo di livello 2</b>: la sottosezione nuova non cambia il comportamento. Un POCO
    /// costruito senza nominare le soglie deve avere ESATTAMENTE i valori che il codice usava prima
    /// che fossero configurabili — altrimenti «configurabile» avrebbe significato «cambiate».
    /// </summary>
    [Fact]
    public void SenzaConfigurazione_LeSoglieSonoQuelleDiPrima()
    {
        var t = new DriftMonitorOptions().Thresholds;
        var storiche = new DriftThresholds();

        Assert.Equal(storiche.PsiBins, t.PsiBins);
        Assert.Equal(storiche.PsiWarning, t.PsiWarning);
        Assert.Equal(storiche.PsiAlert, t.PsiAlert);
        Assert.Equal(storiche.KsPValueWarning, t.KsPValueWarning);
        Assert.Equal(storiche.KsPValueAlert, t.KsPValueAlert);
        Assert.Equal(storiche.PageHinkleyDelta, t.PageHinkleyDelta);
        Assert.Equal(storiche.PageHinkleyWarning, t.PageHinkleyWarning);
        Assert.Equal(storiche.PageHinkleyAlert, t.PageHinkleyAlert);
        Assert.Equal(storiche.MinObservations, t.MinObservations);
    }

    /// <summary>PSI: l'alert più permissivo del warning non scatterebbe mai.</summary>
    [Fact]
    public void PsiAlertPiuPermissivoDelWarning_ERifiutato()
        => Assert.NotNull(AdminConfigRules.Validate(
            new DriftMonitorOptions { Thresholds = new DriftThresholds { PsiWarning = 0.3, PsiAlert = 0.1 } }));

    /// <summary>
    /// KS lavora al contrario: l'alert dev'essere il p-value PIÙ PICCOLO. È l'inversione che si
    /// sbaglia più facilmente proprio perché le altre due famiglie vanno nel verso opposto.
    /// </summary>
    [Fact]
    public void KsAlertMenoStringenteDelWarning_ERifiutato()
        => Assert.NotNull(AdminConfigRules.Validate(
            new DriftMonitorOptions { Thresholds = new DriftThresholds { KsPValueWarning = 0.01, KsPValueAlert = 0.05 } }));

    [Fact]
    public void PageHinkleyAlertPiuPermissivoDelWarning_ERifiutato()
        => Assert.NotNull(AdminConfigRules.Validate(
            new DriftMonitorOptions { Thresholds = new DriftThresholds { PageHinkleyWarning = 50, PageHinkleyAlert = 25 } }));

    [Theory]
    [InlineData(1)]   // meno di 2 bin: il PSI non ha una distribuzione da confrontare
    [InlineData(0)]
    public void PsiConTroppiPochiBin_ERifiutato(int bins)
        => Assert.NotNull(AdminConfigRules.Validate(
            new DriftMonitorOptions { Thresholds = new DriftThresholds { PsiBins = bins } }));

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void PValueKsFuoriDaZeroUno_ERifiutato(double p)
        => Assert.NotNull(AdminConfigRules.Validate(
            new DriftMonitorOptions { Thresholds = new DriftThresholds { KsPValueWarning = p, KsPValueAlert = p } }));

    /// <summary>
    /// Il pavimento sulle osservazioni è lo stesso di <c>RecentCandles</c>: sotto le 20 un test di
    /// distribuzione non significa niente, e due pavimenti diversi per la stessa domanda
    /// darebbero due verdetti sulla stessa feature.
    /// </summary>
    [Fact]
    public void OsservazioniMinimeSottoIlPavimento_ERifiutato()
        => Assert.NotNull(AdminConfigRules.Validate(
            new DriftMonitorOptions { Thresholds = new DriftThresholds { MinObservations = 5 } }));

    /// <summary>Uguali va bene: è severo, non incoerente — l'alert scatta esattamente col warning.</summary>
    [Fact]
    public void SoglieUguali_SonoAmmesse()
        => Assert.Null(AdminConfigRules.Validate(new DriftMonitorOptions
        {
            Thresholds = new DriftThresholds
            {
                PsiWarning = 0.2, PsiAlert = 0.2,
                KsPValueWarning = 0.05, KsPValueAlert = 0.05,
                PageHinkleyWarning = 30, PageHinkleyAlert = 30,
            },
        }));
}

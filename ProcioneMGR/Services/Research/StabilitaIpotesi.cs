using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Research;

/// <summary>
/// [K57, PRD autonomia-piena — Fase 4, 2026-09-02] <b>La stabilità di un'ipotesi fra rimisurazioni:
/// informazione già pagata che nessuno usava.</b>
/// </summary>
/// <param name="Misure">Quante volte la STESSA ipotesi è stata rivalutata (identità esatta, parametri compresi).</param>
/// <param name="Mediana">La stima centrale — quella che sopravvive a una notte fortunata.</param>
/// <param name="Massimo">Il valore più alto osservato: è quello che oggi guida la proposta.</param>
/// <param name="Ampiezza">Massimo − minimo. È la larghezza del ventaglio, non una deviazione standard: si legge senza formule.</param>
public sealed record StabilitaIpotesi(int Misure, decimal Mediana, decimal Massimo, decimal Ampiezza)
{
    /// <summary>
    /// Rimisurazioni minime per poter dire qualcosa. Sotto, non si giudica: due o tre finestre
    /// adiacenti non descrivono una distribuzione, e un gate costruito su di esse sarebbe rumore
    /// travestito da prova. È la stessa soglia di <c>ExpectationEvidence</c>, per la stessa ragione.
    /// </summary>
    public const int MinMisurePerGiudicare = 5;

    /// <summary>
    /// [Soglia MISURATA, non scelta.] Un'ipotesi è stabile se il ventaglio delle sue rimisurazioni
    /// non supera la propria mediana — cioè <c>Ampiezza ≤ Mediana × 1,0</c>.
    ///
    /// <para><b>Perché relativa e non assoluta.</b> Un'ampiezza di 1,0 su una mediana di 0,6 è un
    /// ventaglio più largo del valore; la stessa ampiezza su una mediana di 3,0 è ordinaria. Una
    /// soglia assoluta punirebbe le ipotesi migliori proprio per essere migliori.</para>
    ///
    /// <para><b>Perché esattamente 1,0.</b> Misurato il 2026-09-02 sulle 324 chiavi giudicabili del
    /// motore corrente: il rapporto <c>ampiezza / mediana</c> ha mediana <b>0,57</b>. La soglia a
    /// 1,0 sta quindi intorno al 73° percentile — taglia il quarto peggiore (87 chiavi sopra la
    /// mediana di 0,5 diventano 64) senza toccare il grosso. A 0,5 ne resterebbero 35, cioè si
    /// butterebbe più della metà di ciò che passa; a 2,0 ne resterebbero 79, cioè quasi nulla
    /// cambierebbe.</para>
    /// </summary>
    public const decimal MaxAmpiezzaSuMediana = 1.0m;

    public bool Giudicabile => Misure >= MinMisurePerGiudicare;

    /// <summary>
    /// Vero = il ventaglio è più largo della mediana. <b>Non è «cattiva»</b>: è «non misurata
    /// abbastanza bene da poterla proporre». Con mediana ≤ 0 il rapporto non è interpretabile (il
    /// segno capovolgerebbe il significato) e si dichiara instabile: un'ipotesi che oscilla intorno
    /// allo zero non è un'ipotesi.
    /// </summary>
    public bool Instabile => Giudicabile && (Mediana <= 0m || Ampiezza > Mediana * MaxAmpiezzaSuMediana);

    /// <summary>
    /// La differenza fra ciò che l'ipotesi PROMETTE oggi (il massimo, che è quello che guida la
    /// proposta) e ciò che regge (la mediana). Misurato: <b>24 chiavi su 111</b> passano la soglia
    /// di 0,5 <i>solo</i> col massimo.
    /// </summary>
    public decimal Fortuna => Massimo - Mediana;

    public string Racconto => !Giudicabile
        ? $"{Misure} rimisurazioni: sotto le {MinMisurePerGiudicare} necessarie, non si giudica la stabilità"
        : Instabile
            ? $"instabile: {Misure} rimisurazioni da {Mediana - (Ampiezza - Fortuna):F2} a {Massimo:F2} "
              + $"(ventaglio {Ampiezza:F2} contro una mediana di {Mediana:F2})"
            : $"stabile: {Misure} rimisurazioni, mediana {Mediana:F2}, ventaglio {Ampiezza:F2}";
}

public interface IStabilitaReader
{
    /// <summary>
    /// La stabilità delle chiavi indicate, dalle rivalutazioni presenti in archivio. Le chiavi senza
    /// abbastanza misure non compaiono: assenza = «non lo so», mai «va bene».
    /// </summary>
    Task<IReadOnlyDictionary<string, StabilitaIpotesi>> ReadAsync(
        IReadOnlyCollection<string> candidateKeys, CancellationToken ct = default);
}

/// <summary>
/// [K57] <b>Ogni caccia rimisura la stessa ipotesi 13-16 volte, e nessuno guardava il ventaglio.</b>
///
/// <para><b>Il fatto.</b> Le finestre di selezione e holdout scorrono, quindi la stessa identica
/// ipotesi — stessa strategia, stesso simbolo, stesso timeframe, <i>stessi parametri</i> — viene
/// rivalutata a ogni giro su dati leggermente diversi. Misurato il 2026-09-02 sul motore corrente,
/// ampiezza mediana del ventaglio per configurazione:</para>
/// <code>
/// cfg 17 (4h)  : 168 chiavi, 14 misure mediane, ventaglio 0,752
/// cfg 18 (1h)  :  96 chiavi, 16 misure mediane, ventaglio 0,616
/// cfg 19 (5m)  :  24 chiavi, 13 misure mediane, ventaglio 0,534
/// cfg 20 (15m) :  36 chiavi, 13 misure mediane, ventaglio 0,398
/// </code>
///
/// <para>Il cancello dello Sharpe holdout sta a <b>0,5</b>. Un ventaglio di 0,4-0,75 su una soglia
/// di 0,5 significa che, per una fetta consistente delle ipotesi, <b>passare o non passare dipende
/// da quale notte si guarda</b>. E la fascia grigia ordina per Sharpe, quindi propone per
/// costruzione la notte in cui il ventaglio è al massimo.</para>
///
/// <para><b>Quanto pesa, misurato.</b> Su 324 chiavi giudicabili: <b>111</b> passano la soglia col
/// massimo, <b>87</b> con la mediana. Le <b>24</b> di differenza passano <i>solo</i> perché
/// esisteva una notte fortunata — il 22% di ciò che oggi viene proposto.</para>
///
/// <para><b>Ed è informazione GRATUITA</b>: nessun calcolo nuovo, nessun run in più. Le misure sono
/// già in <c>ResearchCandidates</c>, pagate col budget di caccia e finora buttate a ogni giro
/// tenendo solo l'ultima riga. È la radice di K54, attaccata dove nasce invece che a valle.</para>
/// </summary>
public sealed class StabilitaReader(IDbContextFactory<ApplicationDbContext> dbFactory) : IStabilitaReader
{
    public async Task<IReadOnlyDictionary<string, StabilitaIpotesi>> ReadAsync(
        IReadOnlyCollection<string> candidateKeys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidateKeys);
        if (candidateKeys.Count == 0) return new Dictionary<string, StabilitaIpotesi>(StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var chiavi = candidateKeys.Distinct(StringComparer.Ordinal).ToList();

        // Si legge SOLO lo Sharpe holdout: è il numero su cui il cancello decide, ed è quello che
        // la fascia grigia ordina. Nessun filtro sul motore che ha prodotto la riga — filtrarlo qui
        // significherebbe scegliere quale storia raccontare; a monte la finestra è già limitata da
        // chi chiama.
        var righe = await db.ResearchCandidates.AsNoTracking()
            .Where(c => chiavi.Contains(c.CandidateKey))
            .Select(c => new { c.CandidateKey, c.HoldoutSharpe })
            .ToListAsync(ct);

        return righe
            .GroupBy(r => r.CandidateKey, StringComparer.Ordinal)
            .Select(g => (g.Key, Stat: Calcola([.. g.Select(x => x.HoldoutSharpe)])))
            .Where(x => x.Stat.Giudicabile)
            .ToDictionary(x => x.Key, x => x.Stat, StringComparer.Ordinal);
    }

    /// <summary>Puro: si prova senza database.</summary>
    public static StabilitaIpotesi Calcola(IReadOnlyList<decimal> misure)
    {
        ArgumentNullException.ThrowIfNull(misure);
        if (misure.Count == 0) return new StabilitaIpotesi(0, 0m, 0m, 0m);

        var ordinate = misure.OrderBy(v => v).ToList();
        var m = ordinate.Count / 2;
        // Mediana e non media: una singola notte anomala non deve spostare la stima centrale — è
        // esattamente il difetto che si sta correggendo, e prenderlo dall'altra parte non aiuta.
        var mediana = ordinate.Count % 2 == 1 ? ordinate[m] : (ordinate[m - 1] + ordinate[m]) / 2m;
        return new StabilitaIpotesi(ordinate.Count, mediana, ordinate[^1], ordinate[^1] - ordinate[0]);
    }
}

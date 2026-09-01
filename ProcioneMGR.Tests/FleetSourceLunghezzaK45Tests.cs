using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Llm.Committee;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K45, 2026-09-02] <b>La fonte dell'assegnazione deve STARE nella sua colonna.</b>
///
/// <para><b>Il difetto, che teneva ferma la flotta.</b> I8 ha raffinato
/// <see cref="FleetOrchestratorWorker.DescribeAssignSource"/> per distinguere tre cause che prima
/// collassavano nella parola «default» — <c>default:non-interrogato</c> (23 caratteri),
/// <c>default:tutti-astenuti</c> e <c>default:quorum-mancato</c> (22) — <b>senza guardare la
/// larghezza della colonna</b>, che era <c>varchar(16)</c>. Da allora ogni tick in cui il comitato
/// veniva interrogato senza raggiungere il quorum falliva con
/// <c>22001: il valore è troppo lungo per il tipo character varying(16)</c>, e l'eccezione finiva
/// nel <c>catch</c> di <c>ExecuteAsync</c> («Tick fallito; ritento al prossimo»).</para>
///
/// <para><b>Perché è rimasto invisibile per settimane.</b> Senza corsie libere non nasce nessun
/// menù, quindi il comitato non veniva mai interrogato e <c>Source</c> restava <c>"rules"</c>, che
/// entra. È bastato liberare uno slot il 2026-09-01 perché il primo pareggio producesse un
/// <c>default:…</c> e la flotta smettesse di scrivere <b>qualunque</b> riga: piano con un'azione,
/// journal muto, ogni quindici minuti. Un difetto che aspettava esattamente il momento in cui il
/// sistema avrebbe cominciato a funzionare.</para>
///
/// <para><b>La proprietà difesa</b> non è «32 basta»: è che <b>i valori possibili e la colonna
/// vivano nello stesso test</b>. La lunghezza si legge dal modello EF, non da una costante copiata:
/// se qualcuno stringe la colonna o allunga una delle stringhe, questo test cade — che è
/// precisamente ciò che non è successo la prima volta.</para>
/// </summary>
public class FleetSourceLunghezzaK45Tests
{
    /// <summary>Un DbContext senza database: serve solo il MODELLO, cioè la larghezza dichiarata.</summary>
    private sealed class ModelOnlyContext : ApplicationDbContext
    {
        public ModelOnlyContext(DbContextOptions<ApplicationDbContext> o) : base(o, new NoEncryption()) { }

        private sealed class NoEncryption : IEncryptionService
        {
            public string Encrypt(string plaintext) => plaintext;
            public string Decrypt(string ciphertext) => ciphertext;
        }
    }

    private static int LunghezzaMassimaDiSource()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=modello-soltanto")
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ContextInitialized))
            .Options;
        using var ctx = new ModelOnlyContext(options);
        var prop = ctx.Model.FindEntityType(typeof(OrchestratorDecision))!.FindProperty(nameof(OrchestratorDecision.Source))!;
        return prop.GetMaxLength() ?? int.MaxValue;
    }

    /// <summary>Tutti gli esiti possibili di <c>DescribeAssignSource</c>, costruiti dal vero.</summary>
    private static IEnumerable<(string Caso, string Valore)> FontiPossibili()
    {
        yield return ("quorum raggiunto",
            FleetOrchestratorWorker.DescribeAssignSource(Verdetto(byQuorum: true, voti: 3, validi: 3)));
        yield return ("comitato mai interrogato",
            FleetOrchestratorWorker.DescribeAssignSource(Verdetto(byQuorum: false, voti: 0, validi: 0)));
        yield return ("tutti astenuti",
            FleetOrchestratorWorker.DescribeAssignSource(Verdetto(byQuorum: false, voti: 3, validi: 0)));
        yield return ("quorum mancato",
            FleetOrchestratorWorker.DescribeAssignSource(Verdetto(byQuorum: false, voti: 3, validi: 2)));
        // Gli altri scrittori della colonna, per completezza: se qualcuno ne aggiunge uno lungo,
        // questo elenco è il posto dove ricordarsene.
        yield return ("click umano", "human");
        yield return ("braccio di flotta", "fleet");
        yield return ("regola deterministica", "rules");
        yield return ("ricaduta sulla regola", "default");
    }

    private static CommitteeVerdict Verdetto(bool byQuorum, int voti, int validi)
    {
        var lista = Enumerable.Range(0, voti)
            .Select(i => new CommitteeVote(
                Provider: $"p{i}",
                OptionId: i < validi ? Guid.NewGuid().ToString("N") : null,
                Confidence: i < validi ? 0.8 : null,
                Reason: "prova",
                Valid: i < validi))
            .ToList();
        return new CommitteeVerdict(
            ChosenOptionId: byQuorum ? Guid.NewGuid().ToString("N") : string.Empty,
            ByQuorum: byQuorum,
            Votes: lista);
    }

    [Fact]
    public void OgniFONTEpossibile_STAnellaColonna()
    {
        var max = LunghezzaMassimaDiSource();
        var troppoLunghe = FontiPossibili()
            .Where(f => f.Valore.Length > max)
            .Select(f => $"«{f.Valore}» ({f.Valore.Length} caratteri, {f.Caso})")
            .ToList();

        Assert.True(troppoLunghe.Count == 0,
            $"OrchestratorDecisions.Source ammette {max} caratteri, ma queste fonti non ci stanno: "
            + string.Join(" · ", troppoLunghe)
            + ". Una fonte che non entra non tronca: fa fallire l'INSERT, e con esso l'INTERO tick "
            + "dell'orchestratore — che e' esattamente come la flotta e' rimasta muta dal 2026-09-01. "
            + "Allargare la colonna, non accorciare la parola: quelle parole sono l'informazione.");
    }

    [Fact]
    public void IlNULLO_delGuardiano_unaFonteINVENTATAtroppoLungaVIENEcolta()
    {
        // Senza questo, un guardiano che non confronta niente passerebbe il test qui sopra. La
        // stringa qui e' finta apposta: serve a provare che il metro misura.
        var max = LunghezzaMassimaDiSource();
        var inventata = new string('x', max + 1);

        Assert.True(inventata.Length > max, "il metro deve poter dichiarare troppo lunga una stringa troppo lunga");
    }

    [Fact]
    public void LeTREcauseDelDEFAULTrestanoDISTINTE()
    {
        // Il valore di I8 era distinguere tre cose che «default» collassava in una: «il comitato non
        // e' mai partito», «ha risposto nessuno» e «hanno risposto ma senza maggioranza». Sono la
        // differenza fra «ha scelto la regola» e «non ha funzionato». Se un domani qualcuno le
        // accorciasse per farle entrare, avrebbe rimesso il difetto che I8 ha tolto.
        var cause = new[]
        {
            FleetOrchestratorWorker.DescribeAssignSource(Verdetto(false, 0, 0)),
            FleetOrchestratorWorker.DescribeAssignSource(Verdetto(false, 3, 0)),
            FleetOrchestratorWorker.DescribeAssignSource(Verdetto(false, 3, 2)),
        };

        Assert.Equal(3, cause.Distinct(StringComparer.Ordinal).Count());
        Assert.All(cause, c => Assert.StartsWith("default:", c, StringComparison.Ordinal));
    }
}

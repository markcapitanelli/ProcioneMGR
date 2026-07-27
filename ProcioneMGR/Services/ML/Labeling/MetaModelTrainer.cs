using Microsoft.ML;
using Microsoft.ML.Data;

namespace ProcioneMGR.Services.ML.Labeling;

// =============================================================================================
//  [C4, chiusura] Il MODELLO che mancava al meta-labeling.
//
//  Fin qui MetaLabeler sapeva costruire i campioni ("questo segnale ha funzionato?") e valutare
//  un filtro, ma le probabilità arrivavano da fuori: nessuno le produceva. Qui si addestra il
//  meta-modello vero e si ottengono probabilità OUT-OF-FOLD — cioè ogni campione è giudicato da
//  un modello che non l'ha mai visto.
//
//  TRE VINCOLI, tutti figli di come le etichette triple-barrier sono fatte:
//
//  1. PURGA OBBLIGATORIA, non opzionale. L'etichetta della barra i si risolve fino alla barra
//     i+orizzonte: se un campione di test cade dentro quella finestra, il modello ha già visto il
//     futuro. La finestra di purga è quindi almeno l'orizzonte della barriera verticale, e il
//     valore di default lo deriva da lì invece di lasciarlo scegliere a caso.
//  2. PESI DI CAMPIONE. Le etichette si sovrappongono; addestrare a peso uguale darebbe più voce
//     ai periodi affollati. I pesi di unicità media di MetaLabeler entrano nel training.
//  3. NIENTE PROBABILITÀ IN-SAMPLE. Un meta-modello valutato sui dati su cui è stato addestrato
//     mostra sempre un miglioramento: è il modo più rapido di illudersi. Qui l'unica uscita è
//     out-of-fold.
// =============================================================================================

/// <summary>Riga di addestramento del meta-modello: feature, etichetta binaria, peso.</summary>
internal sealed class MetaRow
{
    public float[] Features { get; set; } = [];
    public bool Label { get; set; }
    public float Weight { get; set; } = 1f;
}

internal sealed class MetaPrediction
{
    public float Probability { get; set; }
}

/// <summary>Parametri dell'addestramento del meta-modello.</summary>
public sealed class MetaModelConfig
{
    /// <summary>Numero di fold della cross-validation purgata.</summary>
    public int Folds { get; set; } = 5;

    /// <summary>
    /// Barre di purga fra train e test. Se lasciato a zero viene derivato dall'orizzonte della
    /// barriera verticale — che è il minimo corretto, non una preferenza.
    /// </summary>
    public int PurgeWindow { get; set; }

    /// <summary>Barre di embargo dopo il fold di test.</summary>
    public int EmbargoPeriods { get; set; }

    /// <summary>Foglie e iterazioni del classificatore ad alberi.</summary>
    public int NumberOfLeaves { get; set; } = 16;
    public int NumberOfTrees { get; set; } = 60;

    /// <summary>Seed: l'addestramento dev'essere riproducibile come tutto il resto della piattaforma.</summary>
    public int Seed { get; set; } = 42;
}

/// <summary>Esito dell'addestramento out-of-fold.</summary>
public sealed record MetaModelResult(
    IReadOnlyList<double> OutOfFoldProbabilities,
    int Folds,
    int SamplesScored,
    int PurgeWindowUsed)
{
    /// <summary>True se ogni campione ha ricevuto una probabilità da un modello che non l'ha visto.</summary>
    public bool IsComplete => OutOfFoldProbabilities.All(p => p >= 0);
}

/// <summary>Addestra il meta-modello e produce probabilità out-of-fold.</summary>
public interface IMetaModelTrainer
{
    MetaModelResult TrainOutOfFold(
        IReadOnlyList<MetaLabelSample> samples,
        IReadOnlyList<float[]> features,
        TripleBarrierConfig barrierConfig,
        MetaModelConfig? config = null);
}

/// <inheritdoc cref="IMetaModelTrainer"/>
public sealed class MetaModelTrainer(IPurgedTimeSeriesCv? cv = null) : IMetaModelTrainer
{
    private readonly IPurgedTimeSeriesCv _cv = cv ?? new PurgedTimeSeriesCv();

    public MetaModelResult TrainOutOfFold(
        IReadOnlyList<MetaLabelSample> samples,
        IReadOnlyList<float[]> features,
        TripleBarrierConfig barrierConfig,
        MetaModelConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(features);
        if (features.Count != samples.Count)
            throw new ArgumentException("Le feature devono essere allineate per indice ai campioni.", nameof(features));

        config ??= new MetaModelConfig();
        barrierConfig ??= new TripleBarrierConfig();

        // Vincolo 1: la purga non può essere più corta della finestra su cui l'etichetta si risolve.
        var purge = config.PurgeWindow > 0
            ? Math.Max(config.PurgeWindow, barrierConfig.VerticalBarrierBars)
            : barrierConfig.VerticalBarrierBars;

        var featureCount = features.Count > 0 ? features[0].Length : 0;
        var probabilities = Enumerable.Repeat(-1.0, samples.Count).ToList();

        if (samples.Count < config.Folds * 2 || featureCount == 0)
        {
            // Campione troppo piccolo per una CV onesta: si dichiara, non si ripiega su in-sample.
            return new MetaModelResult(probabilities, 0, 0, purge);
        }

        // Entrambe le classi devono esistere, altrimenti il classificatore non ha nulla da separare.
        if (samples.All(s => s.WasProfitable) || samples.All(s => !s.WasProfitable))
        {
            return new MetaModelResult(probabilities, 0, 0, purge);
        }

        var rows = new MetaRow[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            rows[i] = new MetaRow
            {
                Features = features[i],
                Label = samples[i].WasProfitable,
                // Vincolo 2: il peso di unicità entra nell'addestramento.
                Weight = (float)Math.Max(1e-6, samples[i].Weight),
            };
        }

        var splits = _cv.Split(samples.Count, config.Folds, purge, config.EmbargoPeriods);
        var scored = 0;

        foreach (var split in splits)
        {
            var trainRows = split.TrainIndices.Select(i => rows[i]).ToList();
            // Un fold il cui train ha una sola classe non produce un modello utilizzabile: si salta,
            // lasciando quei campioni senza probabilità invece di inventarne una.
            if (trainRows.Count < 10 || trainRows.All(r => r.Label) || trainRows.All(r => !r.Label)) continue;

            var ml = new MLContext(seed: config.Seed);
            var trainView = CreateView(ml, trainRows, featureCount);

            var pipeline = ml.BinaryClassification.Trainers.FastTree(
                labelColumnName: nameof(MetaRow.Label),
                featureColumnName: nameof(MetaRow.Features),
                exampleWeightColumnName: nameof(MetaRow.Weight),
                numberOfLeaves: config.NumberOfLeaves,
                numberOfTrees: config.NumberOfTrees);

            var model = pipeline.Fit(trainView);

            var testRows = split.TestIndices.Select(i => rows[i]).ToList();
            var testView = CreateView(ml, testRows, featureCount);
            var predicted = ml.Data.CreateEnumerable<MetaPrediction>(
                model.Transform(testView), reuseRowObject: false).ToList();

            for (var k = 0; k < split.TestIndices.Count && k < predicted.Count; k++)
            {
                probabilities[split.TestIndices[k]] = predicted[k].Probability;
                scored++;
            }
        }

        return new MetaModelResult(probabilities, splits.Count, scored, purge);
    }

    private static IDataView CreateView(MLContext ml, IEnumerable<MetaRow> rows, int featureCount)
    {
        var schema = SchemaDefinition.Create(typeof(MetaRow));
        schema[nameof(MetaRow.Features)].ColumnType = new VectorDataViewType(NumberDataViewType.Single, featureCount);
        return ml.Data.LoadFromEnumerable(rows, schema);
    }
}

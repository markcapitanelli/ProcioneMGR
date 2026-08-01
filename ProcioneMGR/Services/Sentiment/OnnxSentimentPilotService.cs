using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Data;
using ProcioneMGR.Data;
using ProcioneMGR.Services.AltData;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>Esito dell'addestramento del pilota ONNX, con le misure che il pannello dichiara.</summary>
public sealed record OnnxPilotTrainResult(
    bool Success,
    string Message,
    int Rows,
    int NonZeroLabels,
    double HoldoutRmse,
    double ParityMaxAbsDiff,
    int ParityChecked,
    string ModelPath);

/// <summary>
/// Addestra il pilota ONNX del sentiment (PRD-ONNX-SENTIMENT-PILOT, Livello 1) con una filiera
/// 100% C#: notizie testuali già in archivio → etichette deboli dal lessico
/// (<see cref="KeywordSentimentScorer"/>) → vettori <see cref="HashingTextVectorizer"/> →
/// regressione lineare ML.NET (SDCA) → export ONNX (<c>ConvertToOnnx</c>) → verifica di PARITÀ
/// fra le predizioni ML.NET e l'inferenza ONNX Runtime attraverso lo scorer REALE.
///
/// <para><b>Onestà dichiarata</b>: il Livello 1 è una DISTILLAZIONE del lessico — il suo scopo è
/// provare la filiera di inferenza locale (export, caricamento, parità, integrazione col gate IC),
/// non battere il lessico in segnale. La generalizzazione oltre le 25 parole (n-grammi
/// co-occorrenti) è possibile ma va MISURATA nel pannello di confronto, mai presunta. Il Livello 2
/// (modello pre-addestrato esterno) resta gated dall'esito di questo pilota.</para>
///
/// <para>Se la parità fallisce oltre la tolleranza il modello esportato viene ELIMINATO: un
/// modello che inferisce diverso da come è stato addestrato è peggio di nessun modello.</para>
/// </summary>
public sealed class OnnxSentimentPilotService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<SentimentOptions> options,
    KeywordSentimentScorer keyword,
    OnnxSentimentScorer onnxScorer,
    ILogger<OnnxSentimentPilotService> logger)
{
    /// <summary>Oltre questa differenza assoluta fra ML.NET e ONNX Runtime la parità è fallita.</summary>
    internal const double ParityTolerance = 1e-3;

    private const int MinRows = 50;

    private sealed class PilotRow
    {
        [VectorType(HashingTextVectorizer.Dimension)]
        public float[] Features { get; set; } = [];
        public float Label { get; set; }
    }

    private sealed class PilotPrediction
    {
        public float Score { get; set; }
    }

    public async Task<OnnxPilotTrainResult> TrainAsync(CancellationToken ct)
    {
        var modelPath = onnxScorer.ResolvedModelPath;

        // 1) Notizie TESTUALI in archivio (le fonti strutturali — calendario, retail — hanno
        //    punteggi override, non testo libero: fuori dal training come sono fuori dallo scoring).
        List<(string Title, string? Summary, DateTime When)> texts;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            texts = (await db.AltDataPoints.AsNoTracking()
                    .Where(a => a.Category != nameof(NewsCategory.EconomicCalendar)
                             && a.Category != nameof(NewsCategory.RetailSentiment))
                    .OrderBy(a => a.TimestampUtc)
                    .Select(a => new { a.Title, a.Summary, a.TimestampUtc })
                    .ToListAsync(ct))
                .Select(a => (a.Title, a.Summary, a.TimestampUtc))
                .ToList();
        }

        if (texts.Count < MinRows)
        {
            return new OnnxPilotTrainResult(false,
                $"Servono almeno {MinRows} notizie testuali in archivio (ora: {texts.Count}). Sincronizza più a lungo e riprova.",
                texts.Count, 0, 0, 0, 0, modelPath);
        }

        // 2+3) Etichette deboli dal lessico + vettorizzazione (stesso codice dell'inferenza).
        var rows = new List<PilotRow>(texts.Count);
        foreach (var (title, summary, _) in texts)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(new PilotRow
            {
                Features = HashingTextVectorizer.Vectorize(title, summary),
                Label = (float)keyword.Score(title, summary),
            });
        }
        var nonZero = rows.Count(r => r.Label != 0f);

        // 4) Split temporale: l'ultimo 20% (mai meno di 10 righe) resta fuori dal training — il
        //    RMSE dichiarato è fuori campione, non un self-report in-sample.
        var holdoutCount = Math.Max(10, rows.Count / 5);
        var trainRows = rows.Take(rows.Count - holdoutCount).ToList();
        var holdoutRows = rows.Skip(rows.Count - holdoutCount).ToList();
        var holdoutTexts = texts.Skip(rows.Count - holdoutCount).ToList();

        // 5) Regressione lineare (SDCA): l'unico trainer necessario su un ingresso già-vettoriale,
        //    ed è nella famiglia coperta dall'export ONNX di ML.NET. Iperparametri ESPLICITI: con
        //    32k dimensioni sparse e poche migliaia di righe, la regolarizzazione auto-stimata dei
        //    default schiaccia i pesi a zero e il modello collassa sulla costante (misurato: stessa
        //    predizione su un titolo rialzista e uno ribassista).
        var ml = new MLContext(seed: 42);
        var trainView = ml.Data.LoadFromEnumerable(trainRows);
        var pipeline = ml.Regression.Trainers.Sdca(
            labelColumnName: nameof(PilotRow.Label),
            featureColumnName: nameof(PilotRow.Features),
            l2Regularization: 1e-6f,
            l1Regularization: 0f,
            maximumNumberOfIterations: 200);
        var model = pipeline.Fit(trainView);

        // 6) RMSE fuori campione con ML.NET (il riferimento della parità).
        var engine = ml.Model.CreatePredictionEngine<PilotRow, PilotPrediction>(model);
        var mlPredictions = holdoutRows.Select(r => engine.Predict(r).Score).ToList();
        var rmse = Math.Sqrt(holdoutRows.Zip(mlPredictions, (r, p) => Math.Pow(p - r.Label, 2)).Average());

        // 7) Export ONNX su file temporaneo, poi swap atomico sul percorso configurato. Si
        //    esporta il SOLO output "Score": senza potatura il grafo dichiara anche "Label" come
        //    input (era una colonna dell'IDataView) e l'inferenza dovrebbe alimentare un'etichetta
        //    che a inferenza non esiste.
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        var tempPath = modelPath + ".tmp";
        using (var fs = File.Create(tempPath))
        {
            ml.Model.ConvertToOnnx(model, trainView, fs, "Score");
        }
        File.Move(tempPath, modelPath, overwrite: true);
        onnxScorer.Reload();

        // 8) PARITÀ end-to-end attraverso lo scorer REALE (titolo→vettore→ONNX Runtime), contro le
        //    predizioni ML.NET sugli stessi testi: è il livello 1 dello standard di verifica — il
        //    riferimento indipendente è il framework che ha addestrato il modello. Clamp su
        //    entrambi i lati (lo scorer blocca in [-1,1] per contratto).
        var parityChecked = Math.Min(200, holdoutRows.Count);
        double maxDiff = 0;
        for (var i = 0; i < parityChecked; i++)
        {
            var viaOnnx = (double)await onnxScorer.ScoreAsync(holdoutTexts[i].Title, holdoutTexts[i].Summary, ct);
            var viaMlNet = Math.Clamp((double)mlPredictions[i], -1d, 1d);
            maxDiff = Math.Max(maxDiff, Math.Abs(viaOnnx - viaMlNet));
        }

        // La causa va fotografata PRIMA di eliminare il file: rivalutare IsAvailable dopo il
        // delete direbbe sempre "non si carica" anche quando il problema era la parità (successo
        // davvero alla prima stesura — la classe di difetto del Filone E, in casa propria).
        var wasAvailable = onnxScorer.IsAvailable;
        if (!wasAvailable || maxDiff > ParityTolerance)
        {
            var reason = !wasAvailable
                ? $"il modello esportato non si carica in ONNX Runtime ({onnxScorer.LastLoadError ?? "file assente al momento del caricamento"})"
                : $"parità FALLITA (max |Δ| {maxDiff:E2} > {ParityTolerance:E0} su {parityChecked} testi)";
            File.Delete(modelPath);
            onnxScorer.Reload();
            logger.LogError("Pilota ONNX scartato: {Reason}. File eliminato.", reason);
            return new OnnxPilotTrainResult(false,
                $"Modello scartato: {reason}. Un modello che inferisce diverso da come è stato addestrato è peggio di nessun modello.",
                rows.Count, nonZero, rmse, maxDiff, parityChecked, modelPath);
        }

        logger.LogInformation(
            "Pilota ONNX addestrato: {Rows} notizie ({NonZero} con etichetta ≠0), RMSE holdout {Rmse:F4}, parità max |Δ| {MaxDiff:E2} su {Checked} testi, modello in {Path}.",
            rows.Count, nonZero, rmse, maxDiff, parityChecked, modelPath);

        return new OnnxPilotTrainResult(true,
            $"Modello addestrato su {trainRows.Count} notizie e verificato: RMSE fuori campione {rmse:F4} " +
            $"(etichette deboli dal lessico), parità ML.NET↔ONNX Runtime max |Δ| {maxDiff:E2} su {parityChecked} testi. " +
            "Ricorda: è una distillazione del lessico — il valore di segnale si giudica nel confronto qui sotto, non qui.",
            rows.Count, nonZero, rmse, maxDiff, parityChecked, modelPath);
    }
}

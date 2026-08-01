# PRD — Pilota di inferenza locale ONNX per il sentiment (2026-08)

*Nasce dal PDF di ricerca esterna "Architetture AI per Trading in C#" (2026-08-01), che proponeva
ONNX Runtime come pilastro MLOps dell'intera piattaforma. Il confronto punto-per-punto (in
`ROADMAP.md`, sezione «Layer AI multi-provider») ha ridimensionato la proposta: il local-first qui
c'è già più forte (tutto si addestra E inferisce in C#, zero step Python — scelta deliberata del
bivio QLIB-4 contro TorchSharp), e la cornice HFT/GPU del PDF non si applica a una piattaforma
intraday/swing su REST. Quello che resta di GENUINO è stretto: la capacità di eseguire in-process
un modello in formato ONNX, provata su UN caso a basso rischio — lo scorer del sentiment. Documento
separato da PRD-AI-MULTIPROVIDER perché NON è un consumatore di `ILlmClient` (principio §1.5 di
quel PRD: sarebbe un'eccezione confusiva dentro un documento che vieta canali paralleli).*

## §1 — Principî

1. **Filiera 100% C#** (vincolo del proprietario): si addestra in ML.NET dentro l'app, si esporta
   con `ConvertToOnnx`, si inferisce con `Microsoft.ML.OnnxRuntime` (CPU) in-process. Niente
   Python in nessun punto, nemmeno in addestramento — più forte del "local-first" del PDF, che
   presume training Python→export.
2. **La parte testuale NON sta nel modello.** `HashingTextVectorizer` (token → FNV-1a → conteggi
   L2-normalizzati) è codice C# condiviso fra training e inferenza: la parità del "tokenizer" è
   garantita per costruzione, non da un vocabolario da tenere allineato. Un tokenizer subword
   sbagliato produce punteggi plausibili ma errati — peggio di un crash. MAI `string.GetHashCode()`
   (randomizzato per processo): FNV-1a è fisso per sempre, con un test-àncora sul valore noto.
3. **La parità è il gate di pubblicazione del modello.** Dopo ogni export, le predizioni ML.NET e
   l'inferenza ONNX Runtime (attraverso lo scorer REALE, titolo→vettore→sessione) si confrontano
   sugli stessi testi: oltre 1e-3 di scarto il file viene ELIMINATO. Un modello che inferisce
   diverso da come è stato addestrato è peggio di nessun modello.
4. **Onestà sul valore**: il Livello 1 è una DISTILLAZIONE del lessico (etichette deboli dal
   `KeywordSentimentScorer`). Il suo scopo è provare la filiera, non battere il lessico; l'eventuale
   generalizzazione (n-grammi co-occorrenti oltre le 25 parole) si MISURA nel pannello di confronto
   di /sentiment (stesso `FactorEvaluator` di ogni fattore), mai si presume.
5. **Perimetro stretto dichiarato**: la dipendenza OnnxRuntime serve SOLO lo scorer sentiment. Non
   diventa la pipeline ML di default (i predittori restano ML.NET/C#-puro); un secondo consumatore
   ONNX, se mai, giustificherà un seam condiviso — non prima (stessa regola del rifiuto del wrapper
   FxStreet: una classe che non aggiunge comportamento è duplicazione).

## §2 — Livello 1 (ESEGUITO 2026-08-01)

| Pezzo | Cosa | Dove |
|---|---|---|
| Pacchetti | `Microsoft.ML.OnnxConverter 0.23.0` (export) + `Microsoft.ML.OnnxRuntime 1.22.1` (inferenza CPU) — prima dipendenza nativa di inferenza del repo, dichiarata nel csproj | `ProcioneMGR.csproj` |
| Vettorizzatore | `HashingTextVectorizer` (unigrammi+bigrammi, 2^15 dimensioni, FNV-1a, L2) | `Services/Sentiment/HashingTextVectorizer.cs` |
| Trainer | `OnnxSentimentPilotService`: notizie testuali in archivio → etichette deboli dal lessico → SDCA lineare (iperparametri espliciti: i default auto-regolarizzano a zero su 32k dimensioni — misurato) → split temporale 80/20 → RMSE fuori campione → export col SOLO output "Score" → parità → swap atomico | `Services/Sentiment/OnnxSentimentPilotService.cs` |
| Scorer | `OnnxSentimentScorer : ISentimentScorer`: `InferenceSession` singleton, introspezione di input/output (mai nomi cablati), input float residui alimentati a zero, `LastLoadError` per la UI, fallback al lessico se il modello manca o l'inferenza fallisce | `Services/Sentiment/OnnxSentimentScorer.cs` |
| UI | pannello «3. Scorer del sentiment» in /sentiment: addestra, stato modello, scorer attivo, confronto IC | `Components/Pages/Sentiment.razor` |
| Artefatto | `Sentiment:OnnxModelPath` (default `models/sentiment-pilot.onnx`), cartella **gitignored**: un binario addestrato sui propri dati non appartiene a un repo pubblico | `.gitignore` |

**Verifica (i 4 livelli)**: (1) parità ML.NET↔ORT come riferimento indipendente, nei test E a ogni
addestramento dal pannello; (2) il corpus sintetico dei test è stato corretto quando il fit passava
da scorciatoie degeneri (token unici per riga) — lezione registrata sotto; (3) test d'integrazione
con Postgres reale (Testcontainers) sull'intero giro treno→esporta→carica→confronta; (4) verifica
in browser sull'app vera (addestramento dal pannello + confronto).

**Due lezioni dal collaudo, da ricordare:**
1. *Il grafo esportato dichiara come input OGNI colonna dell'IDataView*, inclusa la Label che a
   inferenza non esiste: senza potatura (`ConvertToOnnx(..., "Score")`) ORT pretende un'etichetta
   per fare inferenza. Lo scorer resta comunque robusto (input residui a zero).
2. *Un fallimento di parità veniva riferito come "il modello non si carica"*: la causa era
   fotografata DOPO `File.Delete`+`Reload`. La classe «controlli che rassicurano» del Filone E, in
   casa propria: la causa si fotografa PRIMA di distruggere l'evidenza.

## §3 — Livello 2 (GATED, non impegnato)

Modello pre-addestrato esterno (classe FinBERT o transformer compatto ~10M parametri): richiede
`Microsoft.ML.Tokenizers` + verifica di parità del tokenizer contro il riferimento del modello,
licenza di ridistribuzione verificata PER modello, download pin-su-URL+checksum (mai nel repo).
Si apre SOLO se il Livello 1 dimostra nel pannello di confronto un IC che giustifichi lo sforzo
— e comunque il gate per l'uso live resta quello di ogni fattore.

## §4 — Non-obiettivi

- ONNX come formato/pipeline di default della piattaforma (contraddirebbe QLIB-4).
- GPU/TensorRT/DirectML: nessun bisogno HFT; CPU basta e avanza per uno scorer di notizie.
- RL per l'esecuzione ordini (respinto due volte: QLIB-5, audit tensortrade) e ibridi
  LSTM/Transformer per predizione direzionale (10 conferme negative della classe): il PDF li
  propone, questa piattaforma li ha già misurati.
- Committare modelli addestrati nel repo (pubblico).

using System.Text.Json;

namespace Procione;

/// <summary>
/// La traduzione da testo degli strumenti esterni a dati.
///
/// Sta in una classe a parte, e senza alcun effetto collaterale, per una ragione precisa: e' il
/// punto in cui una plancia puo' mentire in silenzio. Un separatore letto male, un BOM non tolto,
/// un campo mancante trattato come zero, e il quadro diventa verde su una piattaforma rotta — o
/// rosso su una sana, che e' il modo di far smettere la gente di guardarlo. Qui dentro non si
/// parla con nessuno: si trasformano stringhe, e si possono quindi provare contro esempi noti.
/// </summary>
internal static class Parsing
{
    /// <summary>Righe di <c>docker ps -a --format ...</c>, separate da TAB.</summary>
    public static List<Container> Containers(string uscita)
    {
        var lista = new List<Container>();
        foreach (var riga in uscita.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = riga.TrimEnd('\r').Split('\t');
            if (c.Length >= 3)
                lista.Add(new Container(c[0].Trim(), c[1].Trim(), c[2].Trim(),
                                        c.Length > 3 ? c[3].Trim() : "",
                                        c.Length > 4 ? c[4].Trim() : ""));
        }
        return lista;
    }

    /// <summary>Righe del jsonpath dei pod: <c>ns|nome|fase|riavvii|pronto|creazione</c>.</summary>
    public static List<Pod> Pods(string uscita)
    {
        var lista = new List<Pod>();
        foreach (var riga in uscita.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = riga.TrimEnd('\r').Split('|');
            if (c.Length < 6 || c[1].Length == 0) continue;
            lista.Add(new Pod(
                c[0], c[1], c[2],
                // Il conteggio puo' MANCARE (container non ancora avviato). Si tratta come 0, non
                // come ignoto: al giro dopo, quando esistera', un valore diverso fara' semplicemente
                // rifare il tunnel. E' la stessa scelta di ensure-trading-portforward.ps1.
                int.TryParse(c[3], out var r) ? r : 0,
                c[4] == "true",
                DateTimeOffset.TryParse(c[5], out var d) ? d : DateTimeOffset.UtcNow));
        }
        return lista;
    }

    /// <summary>A quale URL kubectl crede di dover parlare per il nostro cluster.</summary>
    public static string? KubeServer(string kubeconfigJson, string nomeCluster)
    {
        try
        {
            using var doc = JsonDocument.Parse(kubeconfigJson);
            if (!doc.RootElement.TryGetProperty("clusters", out var clusters)) return null;
            foreach (var c in clusters.EnumerateArray())
            {
                if (c.TryGetProperty("name", out var n) && n.GetString() == nomeCluster &&
                    c.TryGetProperty("cluster", out var cl) && cl.TryGetProperty("server", out var s))
                    return s.GetString();
            }
        }
        // JsonException = testo non JSON. InvalidOperationException = JSON valido ma di forma
        // sbagliata (radice array, null, o uno scalare): TryGetProperty LANCIA, e senza questa
        // riga `procione stato` morirebbe con uscita 3 invece di dire «contesto assente» — cioe'
        // la plancia tacerebbe proprio sul guasto che deve raccontare.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return null;
    }

    /// <summary>
    /// Contenuto del marcatore di un tunnel.
    ///
    /// <c>Set-Content -Encoding utf8</c> di Windows PowerShell 5.1 antepone il BOM: senza toglierlo
    /// il confronto con l'identita' del pod fallirebbe SEMPRE, e la plancia griderebbe «STANTIO»
    /// su tunnel perfettamente sani — un allarme costante, cioe' nessun allarme.
    /// </summary>
    public static string? Marker(string? contenutoGrezzo)
    {
        if (contenutoGrezzo is null) return null;
        var t = contenutoGrezzo.Trim().TrimStart('﻿').Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>Righe <c>nome|stato|ultimaEsecuzione|esito</c> prodotte dal frammento PowerShell.</summary>
    public static List<TaskInfo> ScheduledTasks(string uscita)
    {
        var lista = new List<TaskInfo>();
        foreach (var riga in uscita.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = riga.TrimEnd('\r').Split('|');
            if (c.Length >= 2 && c[0].Length > 0)
                lista.Add(new TaskInfo(c[0], c[1], c.Length > 2 ? c[2] : "", c.Length > 3 ? c[3] : ""));
        }
        return lista;
    }

    /// <summary>
    /// Esiti "buoni" di un'attivita' pianificata. 267011 = mai eseguita, 267009 = in esecuzione
    /// adesso: nessuno dei due e' un fallimento, e trattarli come tali riempirebbe di giallo una
    /// macchina appena configurata.
    ///
    /// Si legge in <c>long</c>, non in <c>int</c>. Gli esiti in forma HRESULT senza segno —
    /// 2147942401 (0x80070001) e simili — traboccano da Int32: con <c>int.TryParse</c> la lettura
    /// falliva e il ripiego dichiarava l'attivita' SANA. Un'attivita' fallita che risulta a posto
    /// e' precisamente il controllo che rassicura e basta.
    /// </summary>
    public static bool TaskResultIsFine(string? lastResult)
    {
        // Vuoto = ignoto (attivita' appena creata, informazioni non disponibili): non si grida.
        if (string.IsNullOrWhiteSpace(lastResult)) return true;
        // Non vuoto ma illeggibile e' un'anomalia: meglio un giallo da guardare che un verde falso.
        if (!long.TryParse(lastResult, out var rc)) return false;
        return rc is 0 or 267011 or 267009;
    }

    /// <summary>L'esito come lo si mostra: esadecimale se e' un numero, testuale altrimenti.</summary>
    public static string TaskResultLabel(string? lastResult) =>
        long.TryParse(lastResult, out var rc) ? $"0x{rc:X8}" : $"'{lastResult}'";

    /// <summary>Eta' dell'ultimo giro del worker di sync, dal payload di /health dell'ingestion.</summary>
    public static string HeartbeatAge(string corpo)
    {
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.TryGetProperty("ageSeconds", out var a) && a.TryGetDouble(out var s))
                return $"ultimo giro del worker {Ui.Age(TimeSpan.FromSeconds(s))} fa";
        }
        catch (JsonException) { }
        return "in salute";
    }

    /// <summary>"Up 34 minutes" → "34 minutes"; il resto si lascia com'e'.</summary>
    public static string ContainerUptime(string dockerStatus) =>
        dockerStatus.StartsWith("Up ", StringComparison.Ordinal) ? dockerStatus[3..] : dockerStatus;

    /// <summary>Prima colonna di stato di <c>kubectl get nodes --no-headers</c>.</summary>
    public static string? NodeStatus(string uscita)
    {
        var riga = uscita.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (riga is null) return null;
        var campi = riga.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return campi.Length > 1 ? campi[1] : null;
    }
}

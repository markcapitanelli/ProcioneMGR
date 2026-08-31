using Procione;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K5b, PRD autonomia-piena — aggiunto il 2026-08-31 da un incidente misurato]
/// <b>Il supervisore deve rialzarsi da solo anche quando a ucciderlo non è un guasto.</b>
///
/// <para>Il fatto: alle 14:38 di quel giorno un installer Microsoft ha usato il <b>Restart
/// Manager</b> — il meccanismo con cui Windows chiude le applicazioni per liberare i file durante
/// un aggiornamento — e ha terminato la plancia a metà bring-up. Esito 1, nessuna riga di log,
/// nessuna eccezione: non un crash, una chiusura richiesta dall'esterno. L'attività aveva già
/// <c>-RestartCount 3</c> e <b>non è intervenuta</b>: il Task Scheduler non ha contato quella morte
/// come un fallimento. La piattaforma è rimasta giù venti minuti e sarebbe rimasta giù fino al
/// logon successivo, cioè finché non se ne fosse accorto un umano — che è esattamente la
/// definizione di non autonoma.</para>
///
/// <para>La correzione non prova a indovinare quali morti il Task Scheduler consideri fallimenti:
/// aggiunge un trigger che riprova <b>sempre</b>, e si affida a <c>-MultipleInstances IgnoreNew</c>
/// per rendere l'avvio idempotente. Nel caso normale — supervisore vivo — il risveglio è un no-op
/// deciso da Windows, non dal nostro codice.</para>
///
/// <para>Si prova il TESTO generato, non la registrazione: toccare il Task Scheduler della macchina
/// dentro una suite di test sarebbe una modifica di sistema mascherata da verifica. Il rischio vero
/// del PowerShell generato è la sintassi, e quella si controlla col parser di PowerShell stesso —
/// un riferimento indipendente dal nostro codice.</para>
/// </summary>
public class PlanciaRisveglioTests
{
    private const string EseguibileFinto = @"C:\Users\proci\Desktop\ProgettoP\tools\Procione\bin\Release\net10.0\procione.exe";

    [Fact]
    public void Script_RegistraDueTrigger_LogonPiuRisveglio()
    {
        var s = Tasks.BuildRegisterScript(EseguibileFinto);

        Assert.Contains("New-ScheduledTaskTrigger -AtLogOn", s);
        Assert.Contains("$risveglio.Repetition", s);
        // Il punto che il difetto ha reso concreto: i trigger devono arrivare a Register-ScheduledTask
        // TUTTI E DUE. Un trigger solo è il comportamento di prima, e non si rialza.
        Assert.Contains("-Trigger @($alLogon, $risveglio)", s);
    }

    [Fact]
    public void Script_IlRisveglioUsaLaCadenzaDichiarata()
    {
        var s = Tasks.BuildRegisterScript(EseguibileFinto);

        Assert.Equal(10, Tasks.RisveglioMinuti);
        Assert.Contains($"-RepetitionInterval (New-TimeSpan -Minutes {Tasks.RisveglioMinuti})", s);
        // Ripetizione per un giorno intero su un trigger GIORNALIERO: è l'idioma che si ri-arma da
        // solo ogni notte. Una durata "infinita" (TimeSpan::MaxValue) ha storia di stranezze.
        Assert.Contains("-RepetitionDuration (New-TimeSpan -Days 1)", s);
        Assert.Contains("New-ScheduledTaskTrigger -Daily -At '00:00'", s);
    }

    [Fact]
    public void Script_LAvvioResta_IDEMPOTENTE_e_senza_tetto_di_tempo()
    {
        var s = Tasks.BuildRegisterScript(EseguibileFinto);

        // Senza IgnoreNew il risveglio ogni 10 minuti diventerebbe una fabbrica di supervisori.
        Assert.Contains("-MultipleInstances IgnoreNew", s);
        // Senza questo Windows ucciderebbe il supervisore dopo 72 ore, in silenzio.
        Assert.Contains("-ExecutionTimeLimit (New-TimeSpan -Seconds 0)", s);
    }

    [Fact]
    public void Script_NonPuntaMaiAUnWorktree()
    {
        // La lezione del 2026-08-17: un'attività che punta a un worktree muore con
        // `git worktree remove`, in silenzio. Sei notti di backup perse.
        var s = Tasks.BuildRegisterScript(EseguibileFinto);
        Assert.DoesNotContain(@"\.claude\worktrees\", s);
    }

    [Fact]
    public void Script_EsintatticamenteValido_secondoPowerShellStesso()
    {
        if (!OperatingSystem.IsWindows()) return; // il Task Scheduler e' la piattaforma della plancia

        var s = Tasks.BuildRegisterScript(EseguibileFinto);
        var file = Path.Combine(Path.GetTempPath(), $"procione-registrazione-prova-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, s);
        try
        {
            // Il parser di PowerShell come riferimento INDIPENDENTE: non esegue nulla (nessun task
            // viene registrato), dice solo se il testo che generiamo è PowerShell valido. È il
            // controllo che mancava: un errore di sintassi nel codice generato si sarebbe scoperto
            // solo il giorno di una ri-registrazione, cioè nel momento peggiore.
            var r = Proc.Capture("powershell",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                 "$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + file + "',[ref]$null,[ref]$e); " +
                 "if ($e -and $e.Count -gt 0) { $e | ForEach-Object { $_.Message }; exit 1 } else { exit 0 }"],
                timeoutMs: 60000);

            Assert.True(r.Code == 0, $"PowerShell non sa leggere lo script generato:\n{r.Out}\n{r.Err}");
        }
        finally
        {
            try { File.Delete(file); } catch { /* il file di prova non deve far fallire il test */ }
        }
    }
}

using ProcioneMGR.Services.Admin;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-05] <b>/admin/backup legge il supervisore della plancia, non un task che non esiste
/// più.</b> Dal 2026-08-23 il backup notturno è un lavoro di <c>procione servizio</c>; la pagina
/// cercava ancora «ProcioneMGR Backup DB» nel Task Scheduler e dichiarava «NON REGISTRATA» con
/// quattordici dump sani, consigliando un <c>-Register</c> che avrebbe creato un secondo backup.
/// </summary>
public sealed class SupervisorJobProbeTests
{
    private static readonly DateTimeOffset Ora = new(2026, 9, 5, 18, 45, 0, TimeSpan.FromHours(2));

    private const string Vivo = """
        {"Pid": 16192, "Started": "2026-09-05T14:18:00.103573+02:00", "Heartbeat": "2026-09-05T18:40:27.0704837+02:00",
         "Repo": "C:\\Users\\proci\\Desktop\\ProgettoP",
         "Jobs": [
           {"Name": "veglia", "LastRun": "2026-09-05T18:38:35.6715593+02:00", "LastCode": 0, "LastSummary": "Watchdog : backup   OK", "RunningSince": null, "Enabled": true},
           {"Name": "backup", "LastRun": "2026-09-05T03:31:38.4610056+02:00", "LastElapsedSeconds": 95.13, "LastCode": 0,
            "LastSummary": "rimosso backup oltre 14 giorni: procionemgr-20260821-033011.dump", "RunningSince": null, "ConsecutiveFailures": 0, "Enabled": true}
         ]}
        """;

    [Fact]
    public void ConIlSupervisoreVivo_IlLavoroBackupEsisteEDiceLUltimoEsito()
    {
        var s = SupervisorJobProbe.Parse(Vivo, Ora);

        Assert.NotNull(s);
        Assert.True(s.Queryable);
        Assert.True(s.Exists);
        Assert.Equal("acceso", s.State);
        Assert.Equal(0, s.LastResult);
        // Confronto in UTC e al secondo: il fuso della macchina che esegue il test non deve contare.
        Assert.NotNull(s.LastRunLocal);
        var ultima = s.LastRunLocal.Value.ToUniversalTime();
        Assert.Equal(new DateTime(2026, 9, 5, 1, 31, 38, DateTimeKind.Utc), ultima.AddTicks(-(ultima.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Contains("14 giorni", s.Message, StringComparison.Ordinal);
        Assert.Contains("supervisore", s.Source, StringComparison.Ordinal);
    }

    /// <summary>Un supervisore che non batte da un'ora non fa partire nulla: il lavoro «esiste» ma va detto che è fermo, col rimedio.</summary>
    [Fact]
    public void ConIlBattitoStantio_IlLavoroEDichiaratoFermo_ColRimedio()
    {
        var s = SupervisorJobProbe.Parse(Vivo, Ora.AddHours(1));

        Assert.NotNull(s);
        Assert.Equal("supervisore FERMO", s.State);
        Assert.Contains("procione servizio", s.Message, StringComparison.Ordinal);
        Assert.Equal(0, s.LastResult);
    }

    [Fact]
    public void LavoroDisabilitato_EDisabled()
    {
        var s = SupervisorJobProbe.Parse(Vivo.Replace("\"ConsecutiveFailures\": 0, \"Enabled\": true", "\"ConsecutiveFailures\": 0, \"Enabled\": false"), Ora);
        Assert.NotNull(s);
        Assert.Equal("Disabled", s.State);
    }

    [Fact]
    public void UnEsitoNonZero_ArrivaAllaPagina()
    {
        var s = SupervisorJobProbe.Parse(Vivo.Replace("\"LastElapsedSeconds\": 95.13, \"LastCode\": 0", "\"LastElapsedSeconds\": 95.13, \"LastCode\": 1"), Ora);
        Assert.NotNull(s);
        Assert.Equal(1, s.LastResult);
    }

    /// <summary>Senza il lavoro, o senza un JSON del supervisore, si risponde null: il chiamante ripiega sul Task Scheduler invece di inventare.</summary>
    [Theory]
    [InlineData("{\"Jobs\": [{\"Name\": \"veglia\"}]}")]
    [InlineData("{\"Pid\": 1}")]
    [InlineData("[1,2,3]")]
    [InlineData("non e' json")]
    [InlineData("")]
    public void SenzaIlLavoro_OSenzaJson_Null(string json)
        => Assert.Null(SupervisorJobProbe.Parse(json, Ora));

    /// <summary>La pagina: senza supervisore e senza task, l'avviso nomina il rimedio giusto (il supervisore), non solo -Register.</summary>
    [Fact]
    public void LAvvisoSenzaNulla_NominaIlSupervisore()
    {
        var warnings = DatabaseBackupService.BuildWarnings("C:\\x", new ScheduledTaskStatus(Queryable: true, Exists: false), new BackupOptions());
        var w = Assert.Single(warnings);
        Assert.Contains("procione servizio", w, StringComparison.Ordinal);
        Assert.Contains("mai i due insieme", w, StringComparison.Ordinal);
    }

    [Fact]
    public void LAvvisoConSupervisoreFermo_LoDice()
    {
        var task = SupervisorJobProbe.Parse(Vivo, Ora.AddHours(2))!;
        var warnings = DatabaseBackupService.BuildWarnings("C:\\x", task, new BackupOptions());
        Assert.Contains(warnings, w => w.Contains("supervisore della plancia non batte", StringComparison.Ordinal));
    }
}

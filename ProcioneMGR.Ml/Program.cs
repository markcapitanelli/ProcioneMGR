using ProcioneMGR.Ml;

// Npgsql "legacy timestamp behavior": identico al monolite/ingestion. Va impostato PRIMA di
// costruire qualunque data source Npgsql. Sta qui e non in MlHost.Build perché è uno switch globale
// di processo: appartiene all'entry point, non alla costruzione dell'host.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Tutto il wiring (endpoint Kestrel compresi) sta in MlHost.Build, così che i test possano farlo
// partire su Kestrel vero: vedi MlHost.
var app = MlHost.Build(args);

app.Run();

// Esposto per i test di integrazione (WebApplicationFactory + GrpcChannel).
public partial class Program;

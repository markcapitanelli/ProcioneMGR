using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    private readonly IEncryptionService _encryption;

    // L'IEncryptionService viene iniettato da DI (anche a design-time via
    // DesignTimeDbContextFactory) per alimentare l'EncryptedStringConverter.
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, IEncryptionService encryption)
        : base(options)
    {
        _encryption = encryption;
    }

    /// <summary>Dati di mercato time-series. Tabella ad alto volume, separata da Identity.</summary>
    public DbSet<OhlcvData> OhlcvData => Set<OhlcvData>();

    /// <summary>Credenziali API degli exchange, cifrate a riposo.</summary>
    public DbSet<ExchangeCredential> ExchangeCredentials => Set<ExchangeCredential>();

    /// <summary>Watchlist globale: serie mantenute aggiornate dal worker in background.</summary>
    public DbSet<TrackedSeries> TrackedSeries => Set<TrackedSeries>();

    /// <summary>Configurazioni di strategia salvate per-utente.</summary>
    public DbSet<SavedStrategy> SavedStrategies => Set<SavedStrategy>();

    /// <summary>Stato dell'ensemble (riga singola, JSON).</summary>
    public DbSet<EnsembleState> EnsembleStates => Set<EnsembleState>();

    /// <summary>Storico rebalancing dell'ensemble.</summary>
    public DbSet<EnsembleRebalanceHistory> EnsembleRebalanceHistory => Set<EnsembleRebalanceHistory>();

    /// <summary>Modelli di market regime addestrati (K-means).</summary>
    public DbSet<ProcioneMGR.Services.Regime.RegimeModel> RegimeModels => Set<ProcioneMGR.Services.Regime.RegimeModel>();

    /// <summary>Modelli di previsione dei rendimenti (Lineare/RF/LightGBM) salvati per-utente.</summary>
    public DbSet<SavedMlModel> SavedMlModels => Set<SavedMlModel>();

    /// <summary>Fattori alpha "minati" (formulaic alpha mining) salvati per-utente.</summary>
    public DbSet<SavedFactor> SavedFactors => Set<SavedFactor>();

    /// <summary>Dati alternativi (notizie RSS con categoria/sentiment).</summary>
    public DbSet<AltDataPoint> AltDataPoints => Set<AltDataPoint>();

    /// <summary>Configurazioni di pagina per-utente: preset con nome + ultima configurazione usata.</summary>
    public DbSet<UserPageConfig> UserPageConfigs => Set<UserPageConfig>();

    // --- Trading (Fase 8) ---
    public DbSet<ProcioneMGR.Services.Trading.Order> Orders => Set<ProcioneMGR.Services.Trading.Order>();
    public DbSet<ProcioneMGR.Services.Trading.OpenPosition> OpenPositions => Set<ProcioneMGR.Services.Trading.OpenPosition>();
    public DbSet<ProcioneMGR.Services.Trading.TradeRecord> TradeRecords => Set<ProcioneMGR.Services.Trading.TradeRecord>();
    public DbSet<ProcioneMGR.Services.Trading.TradingEngineState> TradingEngineStates => Set<ProcioneMGR.Services.Trading.TradingEngineState>();
    public DbSet<ProcioneMGR.Services.Trading.TradingAuditLog> TradingAuditLogs => Set<ProcioneMGR.Services.Trading.TradingAuditLog>();

    /// <summary>Piani di esecuzione live "a fette" (TWAP/VWAP/Iceberg) in corso/storici, per corsia.</summary>
    public DbSet<ProcioneMGR.Services.Trading.ExecutionJob> ExecutionJobs => Set<ProcioneMGR.Services.Trading.ExecutionJob>();

    // --- Autonomous Pipeline ---
    public DbSet<ProcioneMGR.Services.Pipeline.PipelineConfiguration> PipelineConfigurations => Set<ProcioneMGR.Services.Pipeline.PipelineConfiguration>();
    public DbSet<ProcioneMGR.Services.Pipeline.PipelineRun> PipelineRuns => Set<ProcioneMGR.Services.Pipeline.PipelineRun>();
    public DbSet<ProcioneMGR.Services.Pipeline.PipelineArtifact> PipelineArtifacts => Set<ProcioneMGR.Services.Pipeline.PipelineArtifact>();

    // --- Experiment tracking (generalizzato: backtest/sweep/training/discovery/pipeline) ---
    public DbSet<ProcioneMGR.Services.Experiments.ExperimentRun> ExperimentRuns => Set<ProcioneMGR.Services.Experiments.ExperimentRun>();
    public DbSet<ProcioneMGR.Services.Experiments.ExperimentArtifact> ExperimentArtifacts => Set<ProcioneMGR.Services.Experiments.ExperimentArtifact>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // IMPORTANTISSIMO: lasciare che Identity configuri le sue tabelle
        // (AspNetUsers, AspNetRoles, ...). Restano nettamente separate dalle
        // tabelle di mercato/credenziali qui sotto.
        base.OnModelCreating(builder);

        builder.Entity<OhlcvData>(entity =>
        {
            entity.ToTable("OhlcvData");

            entity.HasKey(e => e.Id);

            // Precisione esplicita per i prezzi: PostgreSQL la onora come numeric(18,8).
            entity.Property(e => e.Open).HasPrecision(18, 8);
            entity.Property(e => e.High).HasPrecision(18, 8);
            entity.Property(e => e.Low).HasPrecision(18, 8);
            entity.Property(e => e.Close).HasPrecision(18, 8);
            entity.Property(e => e.Volume).HasPrecision(28, 8);

            // Indice composto UNIVOCO per le query time-series tipiche:
            //   WHERE Symbol = @s AND Timeframe = @tf AND TimestampUtc BETWEEN @a AND @b
            // L'ordine delle colonne (alta -> bassa selettivita') e' deliberato.
            // L'unicita' impedisce candele duplicate in fase di ingestione.
            entity.HasIndex(e => new { e.Symbol, e.Timeframe, e.TimestampUtc })
                  .IsUnique()
                  .HasDatabaseName("IX_OhlcvData_Symbol_Timeframe_TimestampUtc");
        });

        var encryptedConverter = new EncryptedStringConverter(_encryption);

        builder.Entity<ExchangeCredential>(entity =>
        {
            entity.ToTable("ExchangeCredentials");

            entity.HasKey(e => e.Id);

            // Enum salvato come stringa leggibile ("Binance"/"Bitget") invece di int.
            entity.Property(e => e.ExchangeName)
                  .HasConversion<string>()
                  .HasMaxLength(16);

            entity.Property(e => e.Label).HasMaxLength(64).IsRequired();

            // SEGRETI CIFRATI A RIPOSO (AES-256-GCM via converter).
            entity.Property(e => e.ApiKey).HasConversion(encryptedConverter).IsRequired();
            entity.Property(e => e.ApiSecret).HasConversion(encryptedConverter).IsRequired();
            entity.Property(e => e.Passphrase).HasConversion(encryptedConverter); // nullable: il converter non viene invocato sui null

            // Relazione 1-a-molti con l'utente; cancellando l'utente si cancellano le credenziali.
            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Le credenziali si interrogano sempre per utente.
            entity.HasIndex(e => e.UserId);
        });

        builder.Entity<TrackedSeries>(entity =>
        {
            entity.ToTable("TrackedSeries");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Exchange).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Timeframe).HasMaxLength(8).IsRequired();
            entity.Property(e => e.LastSyncStatus).HasMaxLength(256);

            // Una sola voce per combinazione: niente serie tracciate duplicate.
            entity.HasIndex(e => new { e.Exchange, e.Symbol, e.Timeframe })
                  .IsUnique()
                  .HasDatabaseName("IX_TrackedSeries_Exchange_Symbol_Timeframe");
        });

        builder.Entity<SavedStrategy>(entity =>
        {
            entity.ToTable("SavedStrategies");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(64).IsRequired();
            entity.Property(e => e.StrategyName).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ParametersJson).IsRequired();
            entity.Property(e => e.OptimizationSharpe).HasPrecision(18, 6);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId);
        });

        builder.Entity<SavedMlModel>(entity =>
        {
            entity.ToTable("SavedMlModels");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ModelType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Timeframe).HasMaxLength(8).IsRequired();
            entity.Property(e => e.FactorsJson).IsRequired();
            entity.Property(e => e.ModelBytes).IsRequired();

            // Registry / ciclo di vita (Fase 2): enum come stringa leggibile, default Staging.
            entity.Property(e => e.Stage).HasConversion<string>().HasMaxLength(16).HasDefaultValue(ModelStage.Staging);
            entity.Property(e => e.RetiredReason).HasMaxLength(256);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId);
            // Query calda del registry: il Champion attivo per (Symbol, Timeframe).
            entity.HasIndex(e => new { e.Symbol, e.Timeframe, e.Stage });
        });

        builder.Entity<SavedFactor>(entity =>
        {
            entity.ToTable("SavedFactors");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Name).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Expression).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Timeframe).HasMaxLength(8).IsRequired();

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId);
        });

        builder.Entity<UserPageConfig>(entity =>
        {
            entity.ToTable("UserPageConfigs");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.PageKey).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(64).IsRequired(); // "" = ultima configurazione usata
            entity.Property(e => e.ConfigJson).IsRequired();

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Una sola riga per (utente, pagina, nome): l'upsert del PageConfigStore si appoggia qui.
            entity.HasIndex(e => new { e.UserId, e.PageKey, e.Name }).IsUnique();
        });

        builder.Entity<AltDataPoint>(entity =>
        {
            entity.ToTable("AltDataPoints");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Source).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Url).HasMaxLength(1024);
            entity.Property(e => e.Category).HasMaxLength(32);
            entity.Property(e => e.DedupeKey).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.SentimentScore).HasPrecision(5, 4);

            // Una sola volta per fonte+articolo: evita duplicati fra sync successive dello stesso feed.
            entity.HasIndex(e => e.DedupeKey).IsUnique();
            entity.HasIndex(e => e.TimestampUtc);
        });

        builder.Entity<EnsembleRebalanceHistory>(entity =>
        {
            entity.ToTable("EnsembleRebalanceHistory");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Reason).HasMaxLength(32);
            entity.HasIndex(e => e.Timestamp);
        });

        builder.Entity<ProcioneMGR.Services.Regime.RegimeModel>(entity =>
        {
            entity.ToTable("RegimeModels");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExchangeName).HasMaxLength(16);
            entity.Property(e => e.Symbol).HasMaxLength(32);
            entity.Property(e => e.Timeframe).HasMaxLength(8);
            // Una query frequente: ultimo modello attivo per (exchange, symbol, timeframe).
            entity.HasIndex(e => new { e.ExchangeName, e.Symbol, e.Timeframe, e.IsActive });
        });

        builder.Entity<ProcioneMGR.Services.Trading.Order>(e =>
        {
            e.ToTable("Orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Side).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.MarketType).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => x.ClientOrderId).IsUnique(); // idempotenza
        });

        builder.Entity<ProcioneMGR.Services.Trading.OpenPosition>(e =>
        {
            e.ToTable("OpenPositions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Side).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => x.PositionId).IsUnique();
        });

        builder.Entity<ProcioneMGR.Services.Trading.TradeRecord>(e =>
        {
            e.ToTable("TradeRecords");
            e.HasKey(x => x.Id);
            e.Property(x => x.Side).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.MarketType).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => x.ClosedAtUtc);
            // Copre la query del monitor di decadimento (ultimi N trade per gamba, ORDER BY ClosedAtUtc DESC).
            e.HasIndex(x => new { x.StrategyId, x.ClosedAtUtc });
        });

        builder.Entity<ProcioneMGR.Services.Trading.TradingEngineState>(e =>
        {
            e.ToTable("TradingEngineStates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.MarketType).HasConversion<string>().HasMaxLength(8);
        });

        builder.Entity<ProcioneMGR.Services.Trading.TradingAuditLog>(e =>
        {
            e.ToTable("TradingAuditLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(32);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => x.TimestampUtc);
        });

        builder.Entity<ProcioneMGR.Services.Trading.ExecutionJob>(e =>
        {
            e.ToTable("ExecutionJobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.StrategyId).HasMaxLength(64);
            e.Property(x => x.PositionId).HasMaxLength(64);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Side).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.MarketType).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Algorithm).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            // Il worker interroga i job Running per corsia; la chiusura posizione cerca per PositionId.
            e.HasIndex(x => new { x.LaneId, x.Status });
            e.HasIndex(x => x.PositionId);
        });

        builder.Entity<ProcioneMGR.Services.Pipeline.PipelineConfiguration>(e =>
        {
            e.ToTable("PipelineConfigurations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(1024);
            e.Property(x => x.CreatedBy).HasMaxLength(64);
            e.Property(x => x.ExchangeName).HasMaxLength(16);
            e.Property(x => x.ExecutionMode).HasMaxLength(16);
            e.Property(x => x.Schedule).HasMaxLength(64);
            e.HasIndex(x => x.CreatedBy);
        });

        builder.Entity<ProcioneMGR.Services.Pipeline.PipelineRun>(e =>
        {
            e.ToTable("PipelineRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Trigger).HasMaxLength(16);
            e.HasIndex(x => x.ConfigurationId);
            e.HasIndex(x => x.StartedAt);
        });

        builder.Entity<ProcioneMGR.Services.Pipeline.PipelineArtifact>(e =>
        {
            e.ToTable("PipelineArtifacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.StageName).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.HasIndex(x => x.RunId);
        });

        builder.Entity<ProcioneMGR.Services.Experiments.ExperimentRun>(e =>
        {
            e.ToTable("ExperimentRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.CreatedBy).HasMaxLength(64);
            e.Property(x => x.Symbol).HasMaxLength(32);
            e.Property(x => x.Timeframe).HasMaxLength(8);
            e.Property(x => x.ParametersHash).HasMaxLength(64);
            // Query tipiche della UI: elenco per tipo e per data, e riconoscimento config identiche.
            e.HasIndex(x => new { x.Kind, x.StartedAt });
            e.HasIndex(x => x.ParametersHash);
        });

        builder.Entity<ProcioneMGR.Services.Experiments.ExperimentArtifact>(e =>
        {
            e.ToTable("ExperimentArtifacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.KindTag).HasMaxLength(32);
            e.HasIndex(x => x.RunId);
        });

        // --- Adattamenti specifici PostgreSQL ---
        if (Database.IsNpgsql())
        {
            // Tutte le date della piattaforma sono UTC "naive" (nessun offset memorizzato). Il default
            // di Npgsql (`timestamp with time zone`) rifiuterebbe una DateTime con Kind=Unspecified
            // (quella che si ottiene da un date-picker o da una lettura senza Kind), spezzando le
            // scritture runtime. Mappando a `timestamp without time zone` si elimina un'intera classe
            // di errori di Kind e si conserva la semantica "naive UTC" del DB già deployato.
            //
            // I DateTimeOffset (es. IdentityUser.LockoutEnd) sono gestiti dallo switch legacy di
            // Npgsql (vedi NpgsqlCompatibility): con quello attivo mappano a `timestamp without time
            // zone`, come il DB già deployato. Lo switch va attivato al CARICAMENTO dell'assembly
            // (ModuleInitializer) perché a design-time `Program.cs` non viene eseguito in tempo — senza
            // ciò il differ di `migrations add` vedeva un falso ALTER su LockoutEnd (model "with" vs
            // snapshot "without") e andava in NullReferenceException.
            foreach (var entityType in builder.Model.GetEntityTypes())
            {
                foreach (var prop in entityType.GetProperties())
                {
                    if (prop.ClrType == typeof(DateTime) || prop.ClrType == typeof(DateTime?))
                    {
                        prop.SetColumnType("timestamp without time zone");
                    }
                }
            }
        }
    }
}

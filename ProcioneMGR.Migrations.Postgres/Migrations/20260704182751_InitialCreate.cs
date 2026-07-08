using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AltDataPoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SymbolsJson = table.Column<string>(type: "text", nullable: false),
                    SentimentScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    DedupeKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AltDataPoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp without time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnsembleRebalanceHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AllocationsJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnsembleRebalanceHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnsembleStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "text", nullable: false),
                    StatusJson = table.Column<string>(type: "text", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnsembleStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OhlcvData",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    High = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Volume = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OhlcvData", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenPositions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    PositionId = table.Column<string>(type: "text", nullable: false),
                    StrategyId = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric", nullable: true),
                    TakeProfit = table.Column<decimal>(type: "numeric", nullable: true),
                    TrailingStopPercent = table.Column<decimal>(type: "numeric", nullable: true),
                    BestPriceSinceEntry = table.Column<decimal>(type: "numeric", nullable: true),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    UnrealizedPnl = table.Column<decimal>(type: "numeric", nullable: false),
                    UnrealizedPnlPercent = table.Column<decimal>(type: "numeric", nullable: false),
                    ExchangeOrderId = table.Column<string>(type: "text", nullable: true),
                    Leverage = table.Column<int>(type: "integer", nullable: false),
                    LiquidationPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    MarginBalance = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenPositions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    OrderId = table.Column<string>(type: "text", nullable: false),
                    ClientOrderId = table.Column<string>(type: "text", nullable: false),
                    PositionId = table.Column<string>(type: "text", nullable: false),
                    StrategyId = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FilledPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    FilledQuantity = table.Column<decimal>(type: "numeric", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FilledAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ExchangeOrderId = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Mode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    MarketType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Leverage = table.Column<int>(type: "integer", nullable: false),
                    ManuallyConfirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExchangeName = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UniverseJson = table.Column<string>(type: "text", nullable: false),
                    DateRangesJson = table.Column<string>(type: "text", nullable: false),
                    StagesJson = table.Column<string>(type: "text", nullable: false),
                    InitialCapital = table.Column<decimal>(type: "numeric", nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    ExecutionMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Schedule = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ScheduleEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ContextSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    StageSummariesJson = table.Column<string>(type: "text", nullable: false),
                    Conclusion = table.Column<string>(type: "text", nullable: false),
                    RecommendationJson = table.Column<string>(type: "text", nullable: false),
                    ErrorLog = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegimeModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExchangeName = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TrainedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TrainingDataFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TrainingDataTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    NumberOfRegimes = table.Column<int>(type: "integer", nullable: false),
                    CentroidsJson = table.Column<string>(type: "text", nullable: false),
                    FeatureScalingJson = table.Column<string>(type: "text", nullable: false),
                    RegimeProfilesJson = table.Column<string>(type: "text", nullable: false),
                    SilhouetteScore = table.Column<double>(type: "double precision", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegimeModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackedSeries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Exchange = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradeRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    PositionId = table.Column<string>(type: "text", nullable: false),
                    StrategyId = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    Pnl = table.Column<decimal>(type: "numeric", nullable: false),
                    PnlPercent = table.Column<decimal>(type: "numeric", nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Duration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    ExitReason = table.Column<string>(type: "text", nullable: true),
                    Mode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    MarketType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Leverage = table.Column<int>(type: "integer", nullable: false),
                    WasLiquidated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradingAuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Details = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    Mode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TradingEngineStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    MarketType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Leverage = table.Column<int>(type: "integer", nullable: false),
                    IsRunning = table.Column<bool>(type: "boolean", nullable: false),
                    ExchangeName = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Timeframe = table.Column<string>(type: "text", nullable: false),
                    TotalCapital = table.Column<decimal>(type: "numeric", nullable: false),
                    AvailableCapital = table.Column<decimal>(type: "numeric", nullable: false),
                    RealizedPnl = table.Column<decimal>(type: "numeric", nullable: false),
                    PeakEquity = table.Column<decimal>(type: "numeric", nullable: false),
                    DailyPnl = table.Column<decimal>(type: "numeric", nullable: false),
                    DailyAnchorUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastOrderUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IsEmergencyStopped = table.Column<bool>(type: "boolean", nullable: false),
                    EmergencyStopReason = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradingEngineStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserPasskeys",
                columns: table => new
                {
                    CredentialId = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserPasskeys", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_AspNetUserPasskeys_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ExchangeName = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApiKey = table.Column<string>(type: "text", nullable: false),
                    ApiSecret = table.Column<string>(type: "text", nullable: false),
                    Passphrase = table.Column<string>(type: "text", nullable: true),
                    IsTestnet = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExchangeCredentials_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedMlModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TrainingDataFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TrainingDataTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ForwardHorizon = table.Column<int>(type: "integer", nullable: false),
                    FactorsJson = table.Column<string>(type: "text", nullable: false),
                    ModelBytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    TrainRowCount = table.Column<int>(type: "integer", nullable: false),
                    TrainCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedMlModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedMlModels_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedStrategies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StrategyName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IsOptimized = table.Column<bool>(type: "boolean", nullable: false),
                    OptimizationDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    OptimizationSharpe = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedStrategies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedStrategies_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AltDataPoints_DedupeKey",
                table: "AltDataPoints",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AltDataPoints_TimestampUtc",
                table: "AltDataPoints",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserPasskeys_UserId",
                table: "AspNetUserPasskeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnsembleRebalanceHistory_Timestamp",
                table: "EnsembleRebalanceHistory",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeCredentials_UserId",
                table: "ExchangeCredentials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OhlcvData_Symbol_Timeframe_TimestampUtc",
                table: "OhlcvData",
                columns: new[] { "Symbol", "Timeframe", "TimestampUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenPositions_PositionId",
                table: "OpenPositions",
                column: "PositionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders",
                column: "ClientOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedAtUtc",
                table: "Orders",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_RunId",
                table: "PipelineArtifacts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineConfigurations_CreatedBy",
                table: "PipelineConfigurations",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_ConfigurationId",
                table: "PipelineRuns",
                column: "ConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_StartedAt",
                table: "PipelineRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RegimeModels_ExchangeName_Symbol_Timeframe_IsActive",
                table: "RegimeModels",
                columns: new[] { "ExchangeName", "Symbol", "Timeframe", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedMlModels_UserId",
                table: "SavedMlModels",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedStrategies_UserId",
                table: "SavedStrategies",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedSeries_Exchange_Symbol_Timeframe",
                table: "TrackedSeries",
                columns: new[] { "Exchange", "Symbol", "Timeframe" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradeRecords_ClosedAtUtc",
                table: "TradeRecords",
                column: "ClosedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TradeRecords_StrategyId_ClosedAtUtc",
                table: "TradeRecords",
                columns: new[] { "StrategyId", "ClosedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradingAuditLogs_TimestampUtc",
                table: "TradingAuditLogs",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AltDataPoints");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserPasskeys");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "EnsembleRebalanceHistory");

            migrationBuilder.DropTable(
                name: "EnsembleStates");

            migrationBuilder.DropTable(
                name: "ExchangeCredentials");

            migrationBuilder.DropTable(
                name: "OhlcvData");

            migrationBuilder.DropTable(
                name: "OpenPositions");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "PipelineArtifacts");

            migrationBuilder.DropTable(
                name: "PipelineConfigurations");

            migrationBuilder.DropTable(
                name: "PipelineRuns");

            migrationBuilder.DropTable(
                name: "RegimeModels");

            migrationBuilder.DropTable(
                name: "SavedMlModels");

            migrationBuilder.DropTable(
                name: "SavedStrategies");

            migrationBuilder.DropTable(
                name: "TrackedSeries");

            migrationBuilder.DropTable(
                name: "TradeRecords");

            migrationBuilder.DropTable(
                name: "TradingAuditLogs");

            migrationBuilder.DropTable(
                name: "TradingEngineStates");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}

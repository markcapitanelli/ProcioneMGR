# Dockerfile UNIFICATO per tutte le immagini ProcioneMGR (Fasi 0-2b microservizi).
# Un solo stage "build" condiviso compila il monolite UNA volta (i publish successivi nello
# stesso layer riusano gli obj/bin già prodotti), poi 6 target runtime leggeri:
#   --target procionemgr            (monolite Blazor Server)
#   --target procionemgr-ingestion  (microservizio ingestione OHLCV)
#   --target procionemgr-ml         (microservizio inferenza ML, gRPC — Fase 2a)
#   --target procionemgr-trading    (microservizio trading, gRPC — Fase 2b)
#   --target strategyhunter         (tool CLI batch, K8s Job)
#   --target dbbackup               (tool CLI backup, K8s CronJob — include pg_dump/pg_restore)
# La configurazione reale (MasterKey, password Postgres) NON entra nelle immagini: vedi
# .dockerignore; a runtime va fornita via variabili d'ambiente o volume montato.
# NB: tutti i target runtime usano aspnet (non il runtime base): anche i tool CLI ereditano il
# FrameworkReference Microsoft.AspNetCore.App dal ProjectReference a ProcioneMGR (SDK Web).

# --- Build stage condiviso ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore separato dai sorgenti per sfruttare la cache layer sui cambi di solo codice.
COPY ProcioneMGR/ProcioneMGR.csproj ProcioneMGR/
COPY ProcioneMGR.Contracts/ProcioneMGR.Contracts.csproj ProcioneMGR.Contracts/
COPY ProcioneMGR.Ingestion/ProcioneMGR.Ingestion.csproj ProcioneMGR.Ingestion/
COPY ProcioneMGR.Ml/ProcioneMGR.Ml.csproj ProcioneMGR.Ml/
COPY ProcioneMGR.Trading/ProcioneMGR.Trading.csproj ProcioneMGR.Trading/
COPY tools/DbBackup/DbBackup.csproj tools/DbBackup/
COPY tools/StrategyHunter/StrategyHunter.csproj tools/StrategyHunter/
RUN dotnet restore ProcioneMGR/ProcioneMGR.csproj \
 && dotnet restore ProcioneMGR.Ingestion/ProcioneMGR.Ingestion.csproj \
 && dotnet restore ProcioneMGR.Ml/ProcioneMGR.Ml.csproj \
 && dotnet restore ProcioneMGR.Trading/ProcioneMGR.Trading.csproj \
 && dotnet restore tools/DbBackup/DbBackup.csproj \
 && dotnet restore tools/StrategyHunter/StrategyHunter.csproj

COPY ProcioneMGR/ ProcioneMGR/
COPY ProcioneMGR.Contracts/ ProcioneMGR.Contracts/
COPY ProcioneMGR.Ingestion/ ProcioneMGR.Ingestion/
COPY ProcioneMGR.Ml/ ProcioneMGR.Ml/
COPY ProcioneMGR.Trading/ ProcioneMGR.Trading/
COPY tools/DbBackup/ tools/DbBackup/
COPY tools/StrategyHunter/ tools/StrategyHunter/

# Publish in sequenza nello stesso layer: ProcioneMGR viene COMPILATO UNA VOLTA (dal primo
# publish) e riusato dagli altri. I satelliti NON devono ereditare gli appsettings del
# monolite (config bleed): la loro configurazione arriva da env/Secret.
RUN dotnet publish ProcioneMGR/ProcioneMGR.csproj -c Release -o /out/procionemgr --no-restore \
 && dotnet publish ProcioneMGR.Ingestion/ProcioneMGR.Ingestion.csproj -c Release -o /out/procionemgr-ingestion --no-restore \
 && dotnet publish ProcioneMGR.Ml/ProcioneMGR.Ml.csproj -c Release -o /out/procionemgr-ml --no-restore \
 && dotnet publish ProcioneMGR.Trading/ProcioneMGR.Trading.csproj -c Release -o /out/procionemgr-trading --no-restore \
 && dotnet publish tools/DbBackup/DbBackup.csproj -c Release -o /out/dbbackup --no-restore \
 && dotnet publish tools/StrategyHunter/StrategyHunter.csproj -c Release -o /out/strategyhunter --no-restore \
 && rm -f /out/procionemgr-ingestion/appsettings.Development.json /out/procionemgr-ingestion/appsettings.Production.json \
          /out/procionemgr-ml/appsettings.Development.json /out/procionemgr-ml/appsettings.Production.json \
          /out/procionemgr-trading/appsettings.Development.json /out/procionemgr-trading/appsettings.Production.json \
          /out/dbbackup/appsettings.*.json /out/strategyhunter/appsettings.*.json

# --- Target: monolite ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS procionemgr
WORKDIR /app
COPY --from=build /out/procionemgr .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ProcioneMGR.dll"]

# --- Target: microservizio ingestione ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS procionemgr-ingestion
WORKDIR /app
COPY --from=build /out/procionemgr-ingestion .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ProcioneMGR.Ingestion.dll"]

# --- Target: microservizio ml (inferenza gRPC, Fase 2a) ---
# Due porte, a differenza degli altri target: 8080 gRPC (h2c, solo HTTP/2) e 8081 /health
# (HTTP/1.1, per le probe di Kubernetes). Le porte le apre ConfigureKestrel in Program.cs, che ha
# la precedenza su ASPNETCORE_URLS: la variabile qui sarebbe ignorata e quindi fuorviante.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS procionemgr-ml
WORKDIR /app
COPY --from=build /out/procionemgr-ml .
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "ProcioneMGR.Ml.dll"]

# --- Target: microservizio trading (comandi gRPC + worker delle 3 lane, Fase 2b) ---
# Unico target che a runtime riceve la MASTER KEY reale (Security__MasterKey via Secret
# trading-secrets): gli serve per decifrare le credenziali exchange e firmare gli ordini
# Testnet/Live. La chiave NON è nell'immagine — arriva solo da env a runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS procionemgr-trading
WORKDIR /app
COPY --from=build /out/procionemgr-trading .
# Niente ASPNETCORE_URLS: gli endpoint sono configurati in Program.cs via ConfigureKestrel, che ha
# la precedenza e renderebbe questa variabile solo fuorviante. 8080 = gRPC (h2c, HTTP/2 esplicito:
# in chiaro Kestrel servirebbe HTTP/1.1 e ogni RPC fallirebbe), 8081 = /health in HTTP/1.1 per le
# probe di Kubernetes (che non parlano HTTP/2).
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "ProcioneMGR.Trading.dll"]

# --- Target: StrategyHunter (K8s Job) ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS strategyhunter
WORKDIR /app
COPY --from=build /out/strategyhunter .
# ConnectionStrings__PostgresConnection via Secret; la fase (ingest|discover|...) via args.
ENTRYPOINT ["dotnet", "StrategyHunter.dll"]
CMD ["discover"]

# --- Target: DbBackup (K8s CronJob) ---
# postgresql-client fornisce pg_dump/pg_restore, richiesti da DatabaseBackupHelper.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS dbbackup
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/dbbackup .
# BACKUP_DIR va montato su un volume/PVC a runtime; ConnectionStrings__PostgresConnection via Secret.
ENTRYPOINT ["dotnet", "DbBackup.dll"]
CMD ["backup"]

FROM mcr.microsoft.com/dotnet/sdk:6.0 as build-env
ARG versionNumber=0.0.1
WORKDIR /src/NodeDBSyncer
COPY NodeDBSyncer/*.csproj .
RUN dotnet restore

WORKDIR /src/WalletServer
COPY WalletServer/*.csproj .
RUN dotnet restore

WORKDIR /src
COPY . .

WORKDIR /src/NodeDBSyncer
RUN dotnet publish -c Release -o /syncer /p:Version=${versionNumber}
WORKDIR /src/WalletServer
RUN dotnet publish -c Release -o /api /p:Version=${versionNumber}

FROM mcr.microsoft.com/dotnet/runtime:6.0-alpine as syncer
WORKDIR /backend/syncer
COPY --from=build-env /syncer .
ENTRYPOINT ["dotnet", "NodeDBSyncer.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine as api
WORKDIR /backend/api
COPY --from=build-env /api .
EXPOSE 80
ENTRYPOINT ["dotnet", "WalletServer.dll"]
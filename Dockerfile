FROM mcr.microsoft.com/dotnet/sdk:6.0 as build-env
ARG versionNumber=0.0.1
WORKDIR /src
COPY . .
RUN dotnet publish WalletServer/WalletServer.csproj -c Release -o /api /p:Version=${versionNumber}

FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine as api
WORKDIR /backend/api
COPY --from=build-env /api .
EXPOSE 80
ENTRYPOINT ["dotnet", "WalletServer.dll"]

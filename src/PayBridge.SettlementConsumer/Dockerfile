# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/PayBridge.Common/PayBridge.Common.csproj", "src/PayBridge.Common/"]
COPY ["src/PayBridge.SettlementConsumer/PayBridge.SettlementConsumer.csproj", "src/PayBridge.SettlementConsumer/"]
RUN dotnet restore "src/PayBridge.SettlementConsumer/PayBridge.SettlementConsumer.csproj"

COPY src/PayBridge.Common/ src/PayBridge.Common/
COPY src/PayBridge.SettlementConsumer/ src/PayBridge.SettlementConsumer/

RUN dotnet publish "src/PayBridge.SettlementConsumer/PayBridge.SettlementConsumer.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "PayBridge.SettlementConsumer.dll"]
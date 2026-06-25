# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["src/PayBridge.Common/PayBridge.Common.csproj", "src/PayBridge.Common/"]
COPY ["src/PayBridge.FraudStub/PayBridge.FraudStub.csproj", "src/PayBridge.FraudStub/"]
RUN dotnet restore "src/PayBridge.FraudStub/PayBridge.FraudStub.csproj"

COPY src/PayBridge.Common/ src/PayBridge.Common/
COPY src/PayBridge.FraudStub/ src/PayBridge.FraudStub/

RUN dotnet publish "src/PayBridge.FraudStub/PayBridge.FraudStub.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "PayBridge.FraudStub.dll"]
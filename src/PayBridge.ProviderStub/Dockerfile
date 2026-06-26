# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/PayBridge.Common/PayBridge.Common.csproj", "src/PayBridge.Common/"]
COPY ["src/PayBridge.ProviderStub/PayBridge.ProviderStub.csproj", "src/PayBridge.ProviderStub/"]
RUN dotnet restore "src/PayBridge.ProviderStub/PayBridge.ProviderStub.csproj"

COPY src/PayBridge.Common/ src/PayBridge.Common/
COPY src/PayBridge.ProviderStub/ src/PayBridge.ProviderStub/

RUN dotnet publish "src/PayBridge.ProviderStub/PayBridge.ProviderStub.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "PayBridge.ProviderStub.dll"]
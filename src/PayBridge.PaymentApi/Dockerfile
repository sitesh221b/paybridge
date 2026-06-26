# syntax=docker/dockerfile:1.7
# Multi-stage build: compile in SDK image, run in slim runtime image.
# Smaller final image, no compiler tools shipped to production.

# --- Build stage ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only csproj files first for layer-cache friendliness.
# If only source changes (not deps), restore is cached.
COPY ["src/PayBridge.Common/PayBridge.Common.csproj", "src/PayBridge.Common/"]
COPY ["src/PayBridge.PaymentApi/PayBridge.PaymentApi.csproj", "src/PayBridge.PaymentApi/"]
RUN dotnet restore "src/PayBridge.PaymentApi/PayBridge.PaymentApi.csproj"

# Now copy the rest of the source and publish
COPY src/PayBridge.Common/ src/PayBridge.Common/
COPY src/PayBridge.PaymentApi/ src/PayBridge.PaymentApi/

RUN dotnet publish "src/PayBridge.PaymentApi/PayBridge.PaymentApi.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# --- Runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "PayBridge.PaymentApi.dll"]
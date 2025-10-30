# Base runtime (Debian)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
# Render espone la porta in $PORT. Imposta anche un default per esecuzioni locali.
ENV PORT=8080
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

# (Opzionale ma consigliato) esegui come utente non-root
RUN useradd -m appuser && chown -R appuser /app
USER appuser

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["NextStakeWebApp.csproj", "."]
RUN dotnet restore "./NextStakeWebApp.csproj"
COPY . .
RUN dotnet build "./NextStakeWebApp.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Publish stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./NextStakeWebApp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final image
FROM base AS final
WORKDIR /app
# assicurati che l'utente abbia permessi sui file pubblicati
USER root
COPY --from=publish /app/publish ./
RUN chown -R appuser /app
USER appuser

# (IMPORTANTE: ENV è già definita nella stage base, quindi non serve ripeterla qui)
ENTRYPOINT ["dotnet", "NextStakeWebApp.dll"]

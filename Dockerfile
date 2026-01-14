# Base runtime (Debian)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 \
    libfreetype6 \
    libpng16-16 \
    libharfbuzz0b \
    && rm -rf /var/lib/apt/lists/*

ENV PORT=8080
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

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
USER root
COPY --from=publish /app/publish ./
RUN chown -R appuser /app
USER appuser

ENTRYPOINT ["dotnet", "NextStakeWebApp.dll"]

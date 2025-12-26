FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /app

EXPOSE 8080

ENV ASPNETCORE_ENVIRONMENT=Development
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_USE_POLLING_FILE_WATCHER=true

RUN mkdir -p /app/keys

# Restore only once for speed
COPY DataMigration.csproj .
RUN dotnet restore

# Do NOT copy source here (volume will handle it)
ENTRYPOINT ["dotnet", "watch", "run", "--no-launch-profile", "--urls", "http://0.0.0.0:8080"]

# Build from the repository root:  docker build -f deploy/Dockerfile .

# 1. Frontend
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# 2. API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY server/UniSchedule.Api/UniSchedule.Api.csproj UniSchedule.Api/
RUN dotnet restore UniSchedule.Api/UniSchedule.Api.csproj
COPY server/UniSchedule.Api/ UniSchedule.Api/
RUN dotnet publish UniSchedule.Api/UniSchedule.Api.csproj -c Release -o /out --no-restore /p:UseAppHost=false

# 3. Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /out ./
COPY --from=web /web/dist ./wwwroot
# The data volume (SQLite file + data protection keys) is owned by the non-root "app" user.
RUN mkdir -p /app/data && chown app:app /app/data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Data__Path=/app/data/schedule.db
EXPOSE 8080
VOLUME /app/data
ENTRYPOINT ["dotnet", "UniSchedule.Api.dll"]

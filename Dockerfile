# syntax=docker/dockerfile:1

# ValkyrieID container image: a framework-dependent app on the chiseled ASP.NET Core runtime (no shell, no package
# manager, non-root). Build for both architectures with:
#   docker buildx build --platform linux/amd64,linux/arm64 -t valkyrieid .
# The build stages run on the build machine's platform; only the final stage is per target architecture.

# Admin UI (architecture-independent static files).
FROM --platform=$BUILDPLATFORM node:26-alpine AS admin-ui
WORKDIR /src/src/ValkyrieID.AdminUI
COPY src/ValkyrieID.AdminUI/package.json src/ValkyrieID.AdminUI/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/ValkyrieID.AdminUI/ ./
# Writes to ../ValkyrieID.Server/wwwroot/admin (angular.json).
RUN npm run build

# Server.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
WORKDIR /src

# Restore in its own layer, from the project and lock files only (locked mode: packages.lock.json must match).
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ValkyrieID.Core/ValkyrieID.Core.csproj src/ValkyrieID.Core/packages.lock.json src/ValkyrieID.Core/
COPY src/ValkyrieID.Data/ValkyrieID.Data.csproj src/ValkyrieID.Data/packages.lock.json src/ValkyrieID.Data/
COPY src/ValkyrieID.Data.Sqlite/ValkyrieID.Data.Sqlite.csproj src/ValkyrieID.Data.Sqlite/packages.lock.json src/ValkyrieID.Data.Sqlite/
COPY src/ValkyrieID.Data.PostgreSql/ValkyrieID.Data.PostgreSql.csproj src/ValkyrieID.Data.PostgreSql/packages.lock.json src/ValkyrieID.Data.PostgreSql/
COPY src/ValkyrieID.Server/ValkyrieID.Server.csproj src/ValkyrieID.Server/packages.lock.json src/ValkyrieID.Server/
RUN dotnet restore src/ValkyrieID.Server --locked-mode

COPY src/ src/
COPY --from=admin-ui /src/src/ValkyrieID.Server/wwwroot/admin src/ValkyrieID.Server/wwwroot/admin/

# Portable (RID-less) publish, so one build serves every architecture; no apphost, since it would be built for the
# build platform. Native libraries for other platforms are removed afterwards.
RUN dotnet publish src/ValkyrieID.Server -c Release -o /app --no-restore -p:SkipAdminUI=true -p:UseAppHost=false \
    && test -f /app/wwwroot/admin/index.html \
    && case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; esac \
    && find /app/runtimes -mindepth 1 -maxdepth 1 -type d ! -name "$rid" ! -name linux ! -name unix -exec rm -rf {} + \
    && mkdir -p /data

# Runtime: chiseled with ICU and time zone data (-extra), running as the non-root "app" user (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /data /data

# No secrets here: Issuer__Url and MasterKey__Current must be supplied at run time.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Database__Provider=Sqlite \
    Database__ConnectionString="Data Source=/data/valkyrieid.db"

USER 1654
EXPOSE 8080
VOLUME /data

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD ["dotnet", "/app/valkyrieid.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "/app/valkyrieid.dll"]

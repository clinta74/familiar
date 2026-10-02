# Familiar server image.
#
#   docker build -t familiar-server --build-arg VERSION=0.1.0 --build-arg REVISION=$(git rev-parse HEAD) .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Build configuration first. No csproj declares a TargetFramework or a package version - they come
# from these root files - so restoring without them fails with a misleading NETSDK1013. And
# .editorconfig is build input (EnforceCodeStyleInBuild), so the image compiles under the same
# rules as CI and a developer machine.
COPY ["global.json", "Directory.Build.props", "Directory.Packages.props", ".editorconfig", "./"]

# Project files on their own, so editing source doesn't invalidate the restore layer.
COPY ["src/Familiar.Domain/Familiar.Domain.csproj", "src/Familiar.Domain/"]
COPY ["src/Familiar.Persistence/Familiar.Persistence.csproj", "src/Familiar.Persistence/"]
COPY ["src/Familiar.Server/Familiar.Server.csproj", "src/Familiar.Server/"]
RUN dotnet restore "src/Familiar.Server/Familiar.Server.csproj"

COPY src/ src/

# No git inside the image, so the version arrives as build arguments or not at all. The defaults
# say 0.0.0/unknown rather than claiming to be a release.
ARG VERSION=0.0.0
ARG REVISION=unknown

RUN dotnet publish "src/Familiar.Server/Familiar.Server.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    -p:Version="$VERSION" \
    -p:SourceRevisionId="$REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl for the health check; dumb-init so SIGTERM reaches dotnet and shutdown is graceful.
RUN apt-get update \
    && apt-get install -y --no-install-recommends dumb-init curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

# The base image's own non-root user (uid 1654). Don't create one: uid 1000 is already `ubuntu`.
USER $APP_UID

# What the aspnet base image listens on, and what the client image's BACKEND_ORIGIN expects.
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --retries=3 --start-period=20s \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["/usr/bin/dumb-init", "--"]
CMD ["dotnet", "Familiar.Server.dll"]

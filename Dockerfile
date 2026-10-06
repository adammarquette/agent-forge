# syntax=docker/dockerfile:1
# Multi-stage build for AgentForge.Api
# Railway auto-detects this Dockerfile at the repo root and uses it for the service build.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy central build/package management + solution first for better layer caching.
# .editorconfig is required at build time: it marks EF-generated migrations as generated code, which
# suppresses the analyzers (CA1861 etc.) that would otherwise fail `dotnet publish` under
# warnings-as-errors. Omitting it here (while local/CI builds have the full repo) silently broke the
# Railway build once the first generated migration landed.
COPY Directory.Build.props Directory.Packages.props AgentForge.slnx .editorconfig ./
COPY src/ src/

RUN dotnet restore src/AgentForge.Api/AgentForge.Api.csproj
RUN dotnet publish src/AgentForge.Api/AgentForge.Api.csproj \
    -c Release \
    --no-restore \
    -o /app/publish

# The eval gate, run against THIS image's own source, so the image carries the per-category results of
# the build it is: the sidecar publishes them as agentforge_eval_* series and AgentForgeEvalCategoryRegression
# evaluates them in whatever environment runs the image (observability/README.md). Exit 1 is the gate
# blocking, and it does not fail the image build: the image carries the failed run and publishes
# agentforge_eval_run_passed 0, which AgentForgeEvalGateFailed fires on. The per-category rule alone
# would not - it sees only a >5-point drop, not a safety rubric one case under its floor, an orphaned
# baseline or a population check. Failing the build here instead would leave both rules unable to fire
# from any image this file built. Exit 2 means the eval assets themselves are broken and no results were
# written, which does fail it.
FROM build AS evals
COPY tests/AgentForge.Evals/ tests/AgentForge.Evals/
COPY evals/ evals/
RUN rm -f evals/results.json \
    && { dotnet run --project tests/AgentForge.Evals/AgentForge.Evals.csproj -c Release -- evals; rc=$?; \
         [ "$rc" -le 1 ] && [ -s evals/results.json ] || { echo "eval run exited $rc without results" >&2; exit 1; }; }

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=evals /src/evals/results.json /src/evals/baseline.json ./evals/

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

# Railway injects PORT at runtime; bind Kestrel to it (default 8080 for local docker run).
ENTRYPOINT ["/bin/sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet AgentForge.Api.dll"]

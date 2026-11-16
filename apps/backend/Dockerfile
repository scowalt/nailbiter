FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

COPY backend/backend.csproj backend/
COPY backend.shared/backend.shared.csproj backend.shared/
COPY backend.fsharp/backend.fsharp.fsproj backend.fsharp/
RUN dotnet restore backend/backend.csproj

COPY . .
RUN dotnet publish backend/backend.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
RUN apt-get update \
    && apt-get install -y --no-install-recommends --only-upgrade openssl libssl3t64 apt libapt-pkg6.0t64 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .

ARG SOURCE_REVISION=
ENV SourceRevision=${SOURCE_REVISION}
ENV ASPNETCORE_URLS=http://+:5093
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5093
ENTRYPOINT ["dotnet", "backend.dll"]

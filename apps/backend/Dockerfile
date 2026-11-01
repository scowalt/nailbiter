ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY backend/backend.csproj backend/
COPY backend.shared/backend.shared.csproj backend.shared/
COPY backend.fsharp/backend.fsharp.fsproj backend.fsharp/
RUN dotnet restore backend/backend.csproj

COPY . .
RUN dotnet publish backend/backend.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
RUN apt-get update \
    && apt-get install -y --no-install-recommends --only-upgrade openssl libssl3t64 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5093
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5093
ENTRYPOINT ["dotnet", "backend.dll"]

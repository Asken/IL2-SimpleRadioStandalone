# Build from the repository root (the server compiles sources from IL2-SR-Common):
#   docker build -f IL2-SRS-Server-Web/Dockerfile -t il2-srs-server .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore with the project's sources present: the SDK only adds Blazor's framework scripts
# (_framework/blazor.web.js) when it sees the .razor files at restore time.
COPY IL2-SRS-Server-Web/ IL2-SRS-Server-Web/
RUN dotnet restore IL2-SRS-Server-Web/IL2-SRS-Server-Web.csproj

COPY IL2-SR-Common/ IL2-SR-Common/
RUN dotnet publish IL2-SRS-Server-Web/IL2-SRS-Server-Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV SRS_DATA_DIR=/data \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0

RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME /data

COPY --from=build /app .

# Admin UI and API
EXPOSE 8080/tcp
# SRS clients: the same port over TCP (control) and UDP (voice); change with SRS_SERVER_PORT
EXPOSE 6002/tcp 6002/udp

USER $APP_UID
ENTRYPOINT ["dotnet", "IL2-SRS-Server.dll"]

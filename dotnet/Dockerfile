# Build a partir da pasta dotnet/:  docker build -t inovabens .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Inovabens.Core/Inovabens.Core.csproj src/Inovabens.Core/
COPY src/Inovabens.Web/Inovabens.Web.csproj src/Inovabens.Web/
RUN dotnet restore src/Inovabens.Web/Inovabens.Web.csproj
COPY src src
# sem --no-restore: o pacote com o blazor.web.js só entra quando o restore enxerga os .razor
RUN dotnet publish src/Inovabens.Web/Inovabens.Web.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
# QuestPDF (PDFs) usa fontconfig e precisa de alguma fonte; o Npgsql carrega a libgssapi ao conectar
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /out ./
USER app
EXPOSE 8080
# a cada subida: aplica as migrações pendentes, garante os dados-base e sobe o sistema
ENTRYPOINT ["dotnet", "Inovabens.Web.dll"]

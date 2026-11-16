# Build e publicação em estágios: a imagem final não carrega o SDK.
# O build roda na arquitetura de QUEM constrói e compila para a de DESTINO.
# Sem o --platform, gerar uma imagem arm64 num runner amd64 cairia em emulação
# QEMU e o publish passaria de dezenas de minutos.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src

# Os .csproj primeiro, sozinhos: o restore vira camada de cache e só refaz
# quando uma dependência muda, não a cada alteração de código.
COPY src/PosTech.Contratos/*.csproj src/PosTech.Contratos/
COPY src/PosTech.Mensageria/*.csproj src/PosTech.Mensageria/
COPY src/PosTech.Saga/*.csproj src/PosTech.Saga/
COPY src/PosTech.Persistencia/*.csproj src/PosTech.Persistencia/
COPY src/PosTech.Os.Api/*.csproj src/PosTech.Os.Api/
RUN dotnet restore src/PosTech.Os.Api/PosTech.Os.Api.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/PosTech.Os.Api/PosTech.Os.Api.csproj -c Release -o /app --no-restore -a $TARGETARCH

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Usuário sem privilégio. A imagem do .NET já traz o `app`; rodar como root
# dentro do cluster é risco sem contrapartida.
USER app

COPY --from=build --chown=app:app /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

ENTRYPOINT ["dotnet", "PosTech.Os.Api.dll"]

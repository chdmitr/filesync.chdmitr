# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY src/*.csproj .
RUN dotnet restore -r linux-musl-x64

COPY src .

RUN dotnet publish --no-restore -c Release -o /app -r linux-musl-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true

# Runtime
FROM alpine:3.22.2 AS runtime

WORKDIR /app

RUN apk upgrade --no-cache && apk add --no-cache icu-libs tzdata

RUN addgroup -S appgroup -g 431 && adduser -S -u 431 -G appgroup appuser
USER appuser

COPY --from=build --chown=appuser:appgroup /app ./

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080

ENTRYPOINT ["./FileSyncService"]

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY EduApi.csproj ./
RUN dotnet restore "EduApi.csproj"

COPY . .
RUN dotnet publish "EduApi.csproj" -c Release -o /app/publish --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

EXPOSE 10000

COPY --from=build /app/publish .

# Render sets $PORT at runtime; default to 10000 for local `docker run`.
ENV PORT=10000
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:$PORT dotnet EduApi.dll"]

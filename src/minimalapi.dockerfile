FROM mcr.microsoft.com/dotnet/sdk:6.0 AS sdk
WORKDIR /app

ARG CONFIGURATION
COPY src/MinimalApi/bin/${CONFIGURATION}/net6.0/ out

FROM mcr.microsoft.com/dotnet/aspnet:6.0
WORKDIR /app
COPY --from=sdk /app/out .
ENTRYPOINT ["dotnet", "MinimalApi.dll"]
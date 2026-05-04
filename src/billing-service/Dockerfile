FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /app
COPY src/billing-service/billing-service.csproj .
RUN dotnet restore
COPY src/billing-service/ .
RUN dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
EXPOSE 8004
ENTRYPOINT ["dotnet", "billing-service.dll"]

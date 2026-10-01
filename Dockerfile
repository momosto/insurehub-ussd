FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json InsureHub.Ussd.sln ./
COPY src/Ussd.Gateway/Ussd.Gateway.csproj src/Ussd.Gateway/
RUN dotnet restore src/Ussd.Gateway/Ussd.Gateway.csproj
COPY src/ src/
RUN dotnet publish src/Ussd.Gateway/Ussd.Gateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
USER app
ENV ASPNETCORE_URLS=http://+:5300 DOTNET_gcServer=0
EXPOSE 5300
ENTRYPOINT ["dotnet", "Ussd.Gateway.dll"]

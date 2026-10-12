FROM mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.23@sha256:3f9c03432d664163a90d20e3ed0a3784d0aa82c1b9cbd1a7dd4609fede95669e AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet build -c Release --no-restore
RUN dotnet publish -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-alpine3.23@sha256:23c1e50c547546cb1192b84369ecfc6fa227cb447b17fee57456bd178962c213 AS final
WORKDIR /app
COPY --from=build /app .
EXPOSE 8088
ENTRYPOINT ["dotnet", "OpcUaKb.HostedAgent.dll"]

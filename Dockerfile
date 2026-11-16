FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY global.json Directory.Build.props version.json ./
COPY src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj src/OpcUaKb.Pipeline/
RUN dotnet restore src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj
COPY src/OpcUaKb.Pipeline/ src/OpcUaKb.Pipeline/
RUN dotnet publish src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.15@sha256:80a51954ac70925e5783cb1ed35129a4243dcb1cddef20066b67346e17db284d
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OpcUaKb.Pipeline.dll"]

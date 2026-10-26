FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY global.json Directory.Build.props version.json ./
COPY src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj src/OpcUaKb.Pipeline/
RUN dotnet restore src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj
COPY src/OpcUaKb.Pipeline/ src/OpcUaKb.Pipeline/
RUN dotnet publish src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.14@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OpcUaKb.Pipeline.dll"]

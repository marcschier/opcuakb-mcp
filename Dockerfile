FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY global.json Directory.Build.props version.json ./
COPY src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj src/OpcUaKb.Pipeline/
RUN dotnet restore src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj
COPY src/OpcUaKb.Pipeline/ src/OpcUaKb.Pipeline/
RUN dotnet publish src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.12@sha256:b89586dc17781f25531909993658aa8161205ae38b8cec8847df4a8221a403d5
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OpcUaKb.Pipeline.dll"]

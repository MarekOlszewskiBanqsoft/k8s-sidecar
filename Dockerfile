# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY src/K8sSidecar/K8sSidecar.csproj ./K8sSidecar/
RUN dotnet restore ./K8sSidecar/K8sSidecar.csproj
COPY src/K8sSidecar/*.cs ./K8sSidecar/
RUN dotnet publish ./K8sSidecar/K8sSidecar.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
LABEL org.opencontainers.image.source=https://github.com/kiwigrid/k8s-sidecar
LABEL org.opencontainers.image.description="K8s sidecar image to collect configmaps and secrets as files"
LABEL org.opencontainers.image.licenses=MIT
WORKDIR /app
COPY --from=build /app/publish .
# Use the nobody user's numeric UID/GID to satisfy MustRunAsNonRoot PodSecurityPolicies
# https://kubernetes.io/docs/concepts/policy/pod-security-policy/#users-and-groups
USER 65534:65534
ENTRYPOINT ["dotnet", "K8sSidecar.dll"]

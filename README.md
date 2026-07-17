# 7Record

7Record is a Windows-first screen recorder and non-destructive smart editor for software tutorials, product demos, courses, and screen-led vlogging.

## Prerequisites

- .NET SDK 10.0.302 or a compatible patch
- Visual Studio with Windows application development tools
- Windows App SDK 2.3 runtime
- FFmpeg 8.1 or later

## Build

```powershell
dotnet restore SevenRecord.slnx
dotnet build SevenRecord.slnx --configuration Debug
```

## Test

```powershell
dotnet test SevenRecord.slnx --configuration Debug
```

The production architecture and prototype gates are documented in `docs/research/windows-architecture.md`.

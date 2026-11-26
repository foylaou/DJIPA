# 產生免安裝的單一執行檔：Windows\publish\現場擴音.exe（內含 .NET，不需另外安裝）
dotnet publish "$PSScriptRoot\DJIPA.Windows.csproj" -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PSScriptRoot\publish"

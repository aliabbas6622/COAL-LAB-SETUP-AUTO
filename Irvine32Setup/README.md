# Irvine32 Setup Visual Studio Extension

This VSIX automatically asks for Irvine32 when Visual Studio opens a solution. It also adds **Tools > Configure Irvine32** if you need to change the location later.

1. Open a solution containing one or more C++ `.vcxproj` projects.
2. Run the command and select the folder containing `Irvine32.inc` and `Irvine32.lib`.
3. The extension updates MASM include paths and linker library paths/dependencies for every configuration in each C++ project.

Build from a Visual Studio Developer PowerShell with:

```powershell
dotnet restore
dotnet build -c Release
dotnet msbuild -t:CreateVsixContainer -p:Configuration=Release
```

The generated `.vsix` is placed in `bin\Release` and can be installed by double-clicking it. Visual Studio may need to reload the solution after configuration.
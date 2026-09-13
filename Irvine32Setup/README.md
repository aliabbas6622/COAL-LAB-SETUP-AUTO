# Irvine32 Setup Visual Studio Extension

A powerful Visual Studio extension that fully automates configuring the **Irvine32** library and **MASM** (Microsoft Macro Assembler) environment for COAL (Computer Organization and Assembly Language) lab students and instructors.

---

## Key Features

1. **Auto-Detection of Irvine32**:
   - Automatically searches standard locations (`C:\Irvine`, `C:\Irvine32`, `C:\Irvine32-master`, `D:\Irvine`, user Downloads, solution directory, and environment variables).
   - If found, prompts to use it immediately without forcing manual folder navigation.

2. **Automatic Download & Install**:
   - If Irvine32 is not installed or detected on the machine, the extension offers to download and extract the library automatically to `C:\Irvine`.

3. **Non-Intrusive Smart Auto-Load**:
   - Detects when a solution with unconfigured C++ projects is opened.
   - Ignores non-C++ projects and solutions that are already configured.

4. **Automatic MASM Build Customization Injection**:
   - Checks whether MASM build customizations (`masm.props` and `masm.targets`) are enabled in the `.vcxproj`.
   - If missing (e.g. freshly created Empty C++ Project), it automatically injects them so `.asm` files assemble properly out-of-the-box.

5. **x86 (Win32) Platform Auto-Verification**:
   - Irvine32 is strictly a 32-bit library. When projects default to `x64`, the extension detects the mismatch and offers to switch the active solution configuration to `x86` with one click.

6. **Add Irvine32 Assembly Starter File**:
   - Solution Explorer Right-Click context menu command: **Add Irvine32 Assembly File (.asm)**.
   - Generates a tested MASM boilerplate template with greeting message, arithmetic demo, and `call DumpRegs`.
   - Automatically adds the file as a MASM document in the `.vcxproj` and opens it in the editor.

7. **Flexible Menu Access**:
   - Access via **Tools > Configure Irvine32...**
   - Access directly from Solution Explorer by right-clicking any C++ project node.

---

## How to Build

Build from a Visual Studio Developer PowerShell:

```powershell
dotnet restore
dotnet build -c Release
dotnet msbuild -t:CreateVsixContainer -p:Configuration=Release
```

The generated `.vsix` is placed in `bin\Release\Irvine32Setup.vsix`. Double-click it to install it into Visual Studio 2022.
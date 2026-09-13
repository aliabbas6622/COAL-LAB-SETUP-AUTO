# COAL-LAB-SETUP-AUTO

Automated Visual Studio environment configuration for **Computer Organization and Assembly Language (COAL)** labs using **MASM** and the **Irvine32** library.

---

## What's Included

* **[`Irvine32Setup`](file:///Irvine32Setup)**: Visual Studio Extension (VSIX) that automates:
  * Irvine32 library path auto-detection and setup
  * Automatic MASM build customization injection (`masm.props`, `masm.targets`)
  * Auto-verification and one-click platform switching to **x86 / Win32**
  * Solution Explorer context menu to create ready-to-run **Irvine32 Assembly Files (`.asm`)**
  * Automatic library downloader if Irvine32 is missing
* **[`Project1`](file:///Project1)**: Sample reference project with a tested loop assembly program ([`new.asm`](file:///Project1/new.asm)).

---

## Quick Start (Using the Extension)

1. Double-click `Irvine32Setup\bin\Release\Irvine32Setup.vsix` to install it into Visual Studio 2022.
2. Open any C++ solution or create a new Empty C++ Project.
3. In Solution Explorer, right-click the project:
   * Select **Configure Irvine32...** to configure include and library paths.
   * Select **Add Irvine32 Assembly File (.asm)** to create a boilerplate MASM file ready to build and run!
